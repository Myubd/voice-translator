using System;
using Xunit;

namespace LoopbackRecorder.Tests;

/// <summary>
/// LatencyTracker(遅延計算ロジック)の単体テスト。
/// 純粋な時間計算のみを検証する(Logger.LogMetric呼び出し自体はファイルI/Oを伴うため、
/// ここでは戻り値のLatencyMeasurementが正しいかだけを見る)。
/// </summary>
public class LatencyTrackerTests
{
    [Fact]
    public void 各所要時間と累積遅延が発話終了時刻を基準に正しく算出される()
    {
        var item = new TranscriptItem
        {
            Id = 42,
            Text = "テスト",
            SegmentStartTime = TimeSpan.FromSeconds(10),
            SegmentEndTime = TimeSpan.FromSeconds(12),      // 発話終了(基準点)
            WhisperCompletedAt = TimeSpan.FromSeconds(12.7) // 発話終了から700ms後にWhisper完了
        };
        var translationCompletedAt = TimeSpan.FromSeconds(13.5); // さらに800ms後に翻訳完了

        var tracker = new LatencyTracker();
        var result = tracker.Measure(item, translationCompletedAt);

        Assert.Equal(42, result.Id);
        Assert.Equal(TimeSpan.FromSeconds(0.7), result.WhisperDuration);
        Assert.Equal(TimeSpan.FromSeconds(0.8), result.TranslationDuration);
        Assert.Equal(TimeSpan.FromSeconds(1.5), result.TotalLag); // 0.7 + 0.8
    }

    [Fact]
    public void Whisperと翻訳が発話終了と同時刻に完了した場合は遅延0になる()
    {
        var item = new TranscriptItem
        {
            Id = 1,
            Text = "即時",
            SegmentStartTime = TimeSpan.Zero,
            SegmentEndTime = TimeSpan.FromSeconds(5),
            WhisperCompletedAt = TimeSpan.FromSeconds(5)
        };

        var result = new LatencyTracker().Measure(item, translationCompletedAt: TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.Zero, result.WhisperDuration);
        Assert.Equal(TimeSpan.Zero, result.TranslationDuration);
        Assert.Equal(TimeSpan.Zero, result.TotalLag);
    }
}
