/// <summary>
/// 1区間ぶんの遅延計算(Whisper所要時間・翻訳所要時間・累積遅延)を担当する。
///
/// AudioPipeline.csの責務分割の第一歩として切り出したもの。この処理はロックやキューの
/// 排他制御に関与しない純粋な計算+ログ出力のみで、AudioPipeline側の状態(_pipelineClock等)を
/// 直接参照しないため、他の部分(キュー管理・キャンセル処理など)と比べて安全に分離できる。
/// </summary>
public sealed class LatencyTracker
{
    /// <summary>
    /// item(Whisper完了時点の情報)とtranslationCompletedAt(翻訳完了時点のパイプライン経過時間)から
    /// LatencyMeasurementを算出し、診断ログ(Logger.LogMetric)にも記録する。
    /// 呼び出し元(AudioPipeline)は、戻り値を使ってLatencyMeasuredイベントを発火させる
    /// (イベント発火自体はAudioPipelineの公開APIの一部のため、ここでは行わない)。
    /// </summary>
    public LatencyMeasurement Measure(TranscriptItem item, System.TimeSpan translationCompletedAt)
    {
        var whisperDuration = item.WhisperCompletedAt - item.SegmentEndTime;
        var translationDuration = translationCompletedAt - item.WhisperCompletedAt;
        var totalLag = translationCompletedAt - item.SegmentEndTime;
        var measurement = new LatencyMeasurement(item.Id, whisperDuration, translationDuration, totalLag);

        Logger.LogMetric("Latency",
            ("id", item.Id),
            ("whisper_ms", (int)whisperDuration.TotalMilliseconds),
            ("translation_ms", (int)translationDuration.TotalMilliseconds),
            ("total_lag_ms", (int)totalLag.TotalMilliseconds));

        return measurement;
    }
}
