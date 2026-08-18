using System;

/// <summary>
/// Whisperの文字起こし結果に対する重複除去を担当する。
///
/// AudioPipeline.csの責務分割の2つ目として切り出したもの。
/// 「直前に受理(重複ではないと判定)した発話」の文字列と実際の音声時間範囲だけを状態として持ち、
/// check-and-set(重複判定+状態更新)をロック内でアトミックに行う、比較的閉じた責務のため
/// 分離しやすい。ロジック自体は元のAudioPipeline実装から一切変更していない。
/// </summary>
public sealed class SegmentDeduplicator
{
    private readonly object _lock = new();

    private string? _lastText;

    // 直前に受理(重複ではないと判定)された発話の、実際の音声時間範囲(絶対時刻)。
    //
    // 以前は「文字列完全一致 + 壁時計で3秒以内」を重複除去の条件にしていたが、
    // これだと「同じ短い発話(例: "Yes.")が数秒後に本当にもう一度行われた」という正当なケースまで
    // 誤って握りつぶしてしまうことがあった。実際に重複が起きるのは主にWhisperがセグメント境界
    // (強制15秒分割+300msオーバーラップ)付近で、ほぼ同じ音声区間を2回文字起こしして同じ文を
    // 出力してしまうケースのため、文字列一致に加えて「実際の音声時間が重なっているか」も
    // 条件にすることで、時間的に離れた同一文言の別発話を誤除去しないようにする。
    private (TimeSpan Start, TimeSpan End)? _lastRange;

    /// <summary>
    /// 新しい発話(text, absoluteStart, absoluteEnd)が直前に受理した発話と重複するかを判定する。
    /// 重複でなければ内部状態を更新し、以降の判定の基準にする
    /// (判定と状態更新をロック内でアトミックに行うことで、並行呼び出し時に2つの発話が
    /// 両方「重複でない」と判定されてしまう競合を防ぐ)。
    /// </summary>
    /// <returns>直前に受理した発話と文字列が完全一致し、かつ音声区間が重なっている場合はtrue</returns>
    public bool IsDuplicate(string text, TimeSpan absoluteStart, TimeSpan absoluteEnd)
    {
        lock (_lock)
        {
            bool textMatches = text == _lastText;
            bool timeOverlaps = _lastRange.HasValue
                && absoluteStart < _lastRange.Value.End
                && absoluteEnd > _lastRange.Value.Start;
            bool isDuplicate = textMatches && timeOverlaps;
            if (!isDuplicate)
            {
                _lastText = text;
                _lastRange = (absoluteStart, absoluteEnd);
            }
            return isDuplicate;
        }
    }

    /// <summary>
    /// 新しいセッション(RunAsyncの再実行)開始時に状態をクリアする。
    /// 音声時間は各セッション開始時に0から再スタートするため、前回セッションの状態が
    /// 残っていると「新セッション開始直後なのに、たまたま音声時間が前回セッション終盤の
    /// 範囲と重なり誤って重複と判定される」ことがありうるため、必ず呼び出す必要がある。
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _lastText = null;
            _lastRange = null;
        }
    }
}
