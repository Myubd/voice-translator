using System;

/// <summary>
/// VAD確定済みの1発話区間。
/// StartTime/EndTimeはパイプライン開始からの経過時間(壁時計ベース)で、
/// 実際にその発話が「いつ」話されたかを表す。従来はSRT生成時に1行=固定4秒という
/// 実際の発話時間を無視した表示になっていたため、この情報を持たせることで
/// 正確なタイムスタンプでのSRT生成や、区間ごとの遅延計測を可能にする。
/// </summary>
public sealed class SpeechSegment
{
    /// <summary>実行中(1回のRunAsync)で一意な連番。原文/訳文/エクスポートの対応付けに使う</summary>
    public required long Id { get; init; }
    public required float[] Samples { get; init; }
    public required TimeSpan StartTime { get; init; }
    public required TimeSpan EndTime { get; init; }
}

/// <summary>Whisperによる文字起こし結果1件。翻訳ワーカーへ渡すために必要な情報一式を持つ。</summary>
public sealed class TranscriptItem
{
    public required long Id { get; init; }
    public required string Text { get; init; }
    public required TimeSpan SegmentStartTime { get; init; }
    public required TimeSpan SegmentEndTime { get; init; }
    /// <summary>この文字起こし結果が確定した(Whisper処理が完了した)パイプライン経過時間。遅延計測に使う</summary>
    public required TimeSpan WhisperCompletedAt { get; init; }
}

/// <summary>
/// UIへ渡す原文受信イベントの引数。Idと実際の発話時刻を持つ。
/// </summary>
public sealed record OriginalTextEventArgs(long Id, string Text, TimeSpan SegmentStartTime, TimeSpan SegmentEndTime);

/// <summary>
/// UIへ渡す訳文受信イベントの引数。
/// 翻訳が失敗した場合もText=nullでこのイベント自体は発火させることで、
/// 「翻訳が失敗した回だけ訳文側リストに追加されず、以降ずっと原文とインデックスがズレる」
/// という不具合を防ぐ(UI側はIdで対応付ける)。
/// </summary>
public sealed record TranslatedTextEventArgs(long Id, string? Text, TimeSpan SegmentStartTime, TimeSpan SegmentEndTime);

/// <summary>
/// 1区間ぶんの遅延計測結果。「発話が終わった瞬間」を基準(0)として、
/// Whisper・翻訳それぞれにかかった時間と、翻訳完了までの累積遅延を表す。
/// UIで「現在何秒遅れているか」を表示するために使う。
/// </summary>
public sealed record LatencyMeasurement(
    long Id,
    TimeSpan WhisperDuration,
    TimeSpan TranslationDuration,
    TimeSpan TotalLag);

/// <summary>
/// パイプライン内の2つのキュー(音声セグメント→Whisper待ち / 文字起こし結果→翻訳待ち)の
/// 現在の滞留件数。LatencyMeasuredと同じタイミングで発火することで、
/// 「遅延の数値は大きいが、キューは詰まっていない(=単に1件が重い)」のか
/// 「キュー自体が詰まっている(=処理速度が追いついていない)」のかを切り分けられるようにする。
/// (TODO: 「パイプラインのlatency計測・Observable化」への対応の一部。Capture/VAD段の
/// 個別タイムスタンプ計測は影響範囲が大きいため、今回はキュー長の可視化のみを対象とした)
/// </summary>
public sealed record PipelineQueueStatus(int SegmentQueueLength, int TranscriptQueueLength);
