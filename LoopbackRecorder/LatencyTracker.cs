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
    /// item(Whisper完了時点の情報)・dequeuedAt(このワーカーが実際に翻訳呼び出しを始めた時点の
    /// パイプライン経過時間)・translationCompletedAt(翻訳完了時点のパイプライン経過時間)から
    /// LatencyMeasurementを算出し、診断ログ(Logger.LogMetric)にも記録する。
    /// 呼び出し元(AudioPipeline)は、戻り値を使ってLatencyMeasuredイベントを発火させる
    /// (イベント発火自体はAudioPipelineの公開APIの一部のため、ここでは行わない)。
    ///
    /// dequeuedAtを別途受け取るのは、「Whisper完了からこのワーカーが処理を始めるまでの待ち時間
    /// (queueWaitDuration)」と「実際の翻訳API呼び出しにかかった時間(translationCallDuration)」を
    /// 分離するため。翻訳ワーカーは1本の直列ループなので、前の項目の処理(DeepL失敗時のOllamaへの
    /// フォールバック呼び出しを含む)が長引くと、次の項目はキューでその分待たされる。これを
    /// 「翻訳にかかった時間」として一括りにすると、遅延の原因が単発の重い処理なのかキューの詰まりなのか
    /// 見分けられなくなるため、ここで明示的に分ける。
    /// </summary>
    public LatencyMeasurement Measure(TranscriptItem item, System.TimeSpan dequeuedAt, System.TimeSpan translationCompletedAt)
    {
        var whisperDuration = item.WhisperCompletedAt - item.SegmentEndTime;
        var queueWaitDuration = dequeuedAt - item.WhisperCompletedAt;
        var translationCallDuration = translationCompletedAt - dequeuedAt;
        var totalLag = translationCompletedAt - item.SegmentEndTime;
        var measurement = new LatencyMeasurement(item.Id, whisperDuration, queueWaitDuration, translationCallDuration, totalLag);

        Logger.LogMetric("Latency",
            ("id", item.Id),
            ("whisper_ms", (int)whisperDuration.TotalMilliseconds),
            ("queue_wait_ms", (int)queueWaitDuration.TotalMilliseconds),
            ("translation_call_ms", (int)translationCallDuration.TotalMilliseconds),
            ("total_lag_ms", (int)totalLag.TotalMilliseconds));

        return measurement;
    }
}
