using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

/// <summary>
/// 文字起こし結果(TranscriptItem)のキューを消費し、翻訳サービスを呼び出して結果を通知する
/// ワーカーループ。AudioPipeline.csの責務分割の3つ目として切り出したもの。
///
/// ロジック自体は元のAudioPipeline.RunTranslationWorkerAsyncと一切変更していない。
/// キューのTryRead/WaitToReadAsyncの使い分け、queueLockでの排他制御(WriteTranscriptItem側の
/// drop判定と同じロックを共有する必要がある)、キャンセル済み項目のスキップ処理など、
/// タイミングに関わる箇所は元の実装をそのまま踏襲している。
///
/// イベント通知は、呼び出し元(AudioPipeline)が自身のpublicイベントへそのまま中継できるように
/// AudioPipelineの対応イベントと同じ形(Action&lt;T&gt;)で公開している。
/// </summary>
public sealed class TranslationWorker
{
    private readonly ChannelReader<TranscriptItem> _reader;
    private readonly object _queueLock;
    private readonly ITranslationService _translationService;
    private readonly Stopwatch _pipelineClock;
    private readonly LatencyTracker _latencyTracker;

    /// <summary>停止操作で既にキャンセル済みのため翻訳を試みずスキップした項目のId</summary>
    public event System.Action<long>? TranscriptItemSkipped;
    public event System.Action<TranslatedTextEventArgs>? TranslatedTextReceived;
    public event System.Action<string>? TranslationErrorOccurred;
    public event System.Action<LatencyMeasurement>? LatencyMeasured;

    /// <param name="reader">Whisperワーカーが書き込むTranscriptItemのキュー(読み取り専用ビュー)</param>
    /// <param name="queueLock">WriteTranscriptItem側のdrop判定と共有する排他ロック。
    /// producer/consumer間の競合を無くしdrop数を正確に計測するため、呼び出し元と同じ
    /// ロックオブジェクトを渡す必要がある。</param>
    /// <param name="translationService">翻訳サービス。nullの場合はキューを読み飛ばし続ける(翻訳無効時の挙動)</param>
    /// <param name="pipelineClock">パイプライン全体で共有する経過時間計測用のStopwatch</param>
    /// <param name="latencyTracker">遅延計算用</param>
    public TranslationWorker(
        ChannelReader<TranscriptItem> reader,
        object queueLock,
        ITranslationService? translationService,
        Stopwatch pipelineClock,
        LatencyTracker latencyTracker)
    {
        _reader = reader;
        _queueLock = queueLock;
        // 呼び出し元(既存のテスト含む)がnullを渡す互換性は維持しつつ、内部ではNullTranslationService
        // (IsEnabled=false)に正規化することで、以降のロジックがnullチェックではなくIsEnabledで
        // 「翻訳せず文字起こしのみ」を判定できるようにする
        _translationService = translationService ?? NullTranslationService.Instance;
        _pipelineClock = pipelineClock;
        _latencyTracker = latencyTracker;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            // WriteTranscriptItem側のdrop判定と同じロックの下でTryReadすることで、
            // producer(WriteTranscriptItem)とconsumer(このワーカー)間の競合を無くし、
            // drop数を正確に計測できるようにする(WriteSegment/RunWhisperWorkerAsyncと同じ設計)。
            TranscriptItem? item;
            bool got;
            lock (_queueLock)
            {
                got = _reader.TryRead(out item);
            }

            if (!got)
            {
                bool more;
                try
                {
                    more = await _reader.WaitToReadAsync(CancellationToken.None);
                }
                catch
                {
                    break;
                }
                if (!more) break;
                continue;
            }

            if (!_translationService.IsEnabled) continue;

            if (cancellationToken.IsCancellationRequested)
            {
                // 停止操作で既にキャンセル済み。キューに残っていた項目は翻訳を試みず、
                // 「処理遅延によりスキップ」と同じ扱いでプレースホルダーを解消する
                TranscriptItemSkipped?.Invoke(item!.Id);
                continue;
            }

            var result = await _translationService.TranslateAsync(item!.Text, cancellationToken);
            var translationCompletedAt = _pipelineClock.Elapsed;

            if (result.Text != null)
            {
                TranslatedTextReceived?.Invoke(new TranslatedTextEventArgs(item.Id, result.Text, item.SegmentStartTime, item.SegmentEndTime));
            }
            else if (result.ErrorMessage != null)
            {
                // DeepL/Ollamaのエラーはこれまでコンソールに出すだけでUIに一切出ていなかった。
                // WPFアプリとして配布した場合、通常ユーザーはコンソールを見ないため、
                // 「なぜか訳文が出ない」状態のまま気づけなかった。ここでStatusへ通知する。
                TranslationErrorOccurred?.Invoke(result.ErrorMessage);

                // 失敗時もIdだけを載せてイベントを発火させる(Text=null)。
                // これによりUI側は「この区間は翻訳に失敗した」とIdで認識でき、
                // 訳文側リストにプレースホルダーを表示することで原文/訳文の対応がズレるのを防げる。
                TranslatedTextReceived?.Invoke(new TranslatedTextEventArgs(item.Id, null, item.SegmentStartTime, item.SegmentEndTime));
            }

            // 遅延計測: 発話終了(SegmentEndTime)を基準に、Whisper完了までの時間・
            // 翻訳完了までの時間・トータルの遅延を算出して通知する(計算自体はLatencyTrackerに委譲)
            var measurement = _latencyTracker.Measure(item, translationCompletedAt);
            LatencyMeasured?.Invoke(measurement);
        }
    }
}
