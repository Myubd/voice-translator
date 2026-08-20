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
        var dequeuedAt = TimeSpan.FromSeconds(13.0);              // さらに300ms後にワーカーが処理開始(=キュー待ち300ms)
        var translationCompletedAt = TimeSpan.FromSeconds(13.5); // さらに500ms後に翻訳呼び出し完了

        var tracker = new LatencyTracker();
        var result = tracker.Measure(item, dequeuedAt, translationCompletedAt);

        Assert.Equal(42, result.Id);
        Assert.Equal(TimeSpan.FromSeconds(0.7), result.WhisperDuration);
        Assert.Equal(TimeSpan.FromSeconds(0.3), result.QueueWaitDuration);
        Assert.Equal(TimeSpan.FromSeconds(0.5), result.TranslationCallDuration);
        Assert.Equal(TimeSpan.FromSeconds(1.5), result.TotalLag); // 0.7 + 0.3 + 0.5
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

        var result = new LatencyTracker().Measure(item, dequeuedAt: TimeSpan.FromSeconds(5), translationCompletedAt: TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.Zero, result.WhisperDuration);
        Assert.Equal(TimeSpan.Zero, result.QueueWaitDuration);
        Assert.Equal(TimeSpan.Zero, result.TranslationCallDuration);
        Assert.Equal(TimeSpan.Zero, result.TotalLag);
    }

    [Fact]
    public void キュー待ち時間と翻訳呼び出し時間が独立して計測される()
    {
        // フォールバック機能(DeepL失敗→Ollama)の追加で、翻訳ワーカーが1件を処理している間、
        // 次の項目はキューで待たされる時間が長くなりうる。この「キュー待ち」と「実際のAPI呼び出し」を
        // 混同しないことが本テストの主眼(GitHubレビューのP0-3指摘に対応)。
        var item = new TranscriptItem
        {
            Id = 99,
            Text = "詰まっているケース",
            SegmentStartTime = TimeSpan.Zero,
            SegmentEndTime = TimeSpan.FromSeconds(1),
            WhisperCompletedAt = TimeSpan.FromSeconds(1.1)
        };
        // 前の項目がDeepL(15秒)→Ollama(30秒)フォールバックで詰まり、この項目は45秒近く待たされた、
        // という極端なケースを想定
        var dequeuedAt = TimeSpan.FromSeconds(46.0);
        var translationCompletedAt = TimeSpan.FromSeconds(46.8); // 実際の翻訳呼び出し自体は800msで完了

        var result = new LatencyTracker().Measure(item, dequeuedAt, translationCompletedAt);

        Assert.True(result.QueueWaitDuration > TimeSpan.FromSeconds(40),
            "キュー待ちが大部分を占めるケースで、QueueWaitDurationに正しく反映されるべき");
        Assert.Equal(TimeSpan.FromSeconds(0.8), result.TranslationCallDuration);
    }
}
