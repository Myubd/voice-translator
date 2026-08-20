using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace LoopbackRecorder.Tests;

/// <summary>
/// TranslationWorker(翻訳ワーカーループ)の単体テスト。
/// 実際のDeepL/Ollama通信は行わず、ITranslationServiceのフェイク実装で
/// 成功・失敗・呼び出されないことを検証する。
/// </summary>
public class TranslationWorkerTests
{
    /// <summary>戻り値を固定で返すだけのフェイク。呼び出し回数と最後に渡されたテキストを記録する。</summary>
    private sealed class FakeTranslationService : ITranslationService
    {
        private readonly TranslationResult _result;
        public int CallCount { get; private set; }
        public string? LastText { get; private set; }

        public FakeTranslationService(TranslationResult result) => _result = result;

        public Task<TranslationResult> TranslateAsync(string text, CancellationToken cancellationToken)
        {
            CallCount++;
            LastText = text;
            return Task.FromResult(_result);
        }
    }

    private static TranscriptItem MakeItem(long id = 1) => new()
    {
        Id = id,
        Text = "こんにちは",
        SegmentStartTime = TimeSpan.FromSeconds(1),
        SegmentEndTime = TimeSpan.FromSeconds(2),
        WhisperCompletedAt = TimeSpan.FromSeconds(2.1)
    };

    private static ChannelReader<TranscriptItem> MakeReaderWith(params TranscriptItem[] items)
    {
        var channel = Channel.CreateUnbounded<TranscriptItem>();
        foreach (var item in items) channel.Writer.TryWrite(item);
        channel.Writer.Complete(); // これ以上書き込みが無いことを示す → WaitToReadAsyncはキュー消化後にfalseを返す
        return channel.Reader;
    }

    [Fact]
    public async Task 翻訳成功時はTranslatedTextReceivedとLatencyMeasuredが発火する()
    {
        var reader = MakeReaderWith(MakeItem());
        var service = new FakeTranslationService(new TranslationResult("Hello", null));
        var worker = new TranslationWorker(reader, new object(), service, new Stopwatch(), new LatencyTracker());

        var translated = new List<TranslatedTextEventArgs>();
        var latencies = new List<LatencyMeasurement>();
        worker.TranslatedTextReceived += translated.Add;
        worker.LatencyMeasured += latencies.Add;

        await worker.RunAsync(CancellationToken.None);

        Assert.Single(translated);
        Assert.Equal("Hello", translated[0].Text);
        Assert.Equal(1, service.CallCount);
        Assert.Equal("こんにちは", service.LastText);
        Assert.Single(latencies);
    }

    [Fact]
    public async Task 翻訳失敗時はエラー通知とText_null付きの通知が両方発火する()
    {
        var reader = MakeReaderWith(MakeItem());
        var service = new FakeTranslationService(new TranslationResult(null, "DeepL timeout"));
        var worker = new TranslationWorker(reader, new object(), service, new Stopwatch(), new LatencyTracker());

        var errors = new List<string>();
        var translated = new List<TranslatedTextEventArgs>();
        worker.TranslationErrorOccurred += errors.Add;
        worker.TranslatedTextReceived += translated.Add;

        await worker.RunAsync(CancellationToken.None);

        Assert.Single(errors);
        Assert.Equal("DeepL timeout", errors[0]);
        Assert.Single(translated);
        Assert.Null(translated[0].Text); // 原文/訳文の対応がズレないよう、失敗時もText=nullでイベント自体は発火する
    }

    [Fact]
    public async Task 既にキャンセル済みの場合は翻訳を呼ばずスキップ通知のみ発火する()
    {
        var reader = MakeReaderWith(MakeItem(id: 7));
        var service = new FakeTranslationService(new TranslationResult("Hello", null));
        var worker = new TranslationWorker(reader, new object(), service, new Stopwatch(), new LatencyTracker());

        var skipped = new List<long>();
        var translated = new List<TranslatedTextEventArgs>();
        worker.TranscriptItemSkipped += skipped.Add;
        worker.TranslatedTextReceived += translated.Add;

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // 開始前から既にキャンセル済みの状態を再現

        await worker.RunAsync(cts.Token);

        Assert.Equal(new long[] { 7 }, skipped);
        Assert.Empty(translated);
        Assert.Equal(0, service.CallCount); // 翻訳自体は試みられない
    }

    [Fact]
    public async Task 翻訳サービスがnullの場合はキューを読み飛ばしイベントを発火しない()
    {
        var reader = MakeReaderWith(MakeItem());
        var worker = new TranslationWorker(reader, new object(), translationService: null, new Stopwatch(), new LatencyTracker());

        var translated = new List<TranslatedTextEventArgs>();
        var latencies = new List<LatencyMeasurement>();
        worker.TranslatedTextReceived += translated.Add;
        worker.LatencyMeasured += latencies.Add;

        await worker.RunAsync(CancellationToken.None);

        Assert.Empty(translated);
        Assert.Empty(latencies);
    }

    [Fact]
    public async Task 空のキューはWriterのCompleteで正常終了する()
    {
        var reader = MakeReaderWith(); // 何も書き込まずCompleteのみ
        var service = new FakeTranslationService(new TranslationResult("Hello", null));
        var worker = new TranslationWorker(reader, new object(), service, new Stopwatch(), new LatencyTracker());

        // ハングせず完了することを確認(タイムアウトで検知)
        var task = worker.RunAsync(CancellationToken.None);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(task, completed);
        Assert.Equal(0, service.CallCount);
    }

    /// <summary>
    /// freshness-based drop(P0-4)の検証。_pipelineClockは開始していないため常に
    /// Elapsed=TimeSpan.Zeroを返す。「現在時刻(0)から見て発話終了が5秒前だった」という状況を、
    /// SegmentEndTimeに負の値を設定することで模擬する(TimeSpanは負の値を許容するため、
    /// このテスト用途では実用上問題ない)。
    /// </summary>
    [Fact]
    public async Task 発話終了からの経過が閾値を超えている場合は翻訳APIを呼ばずスキップ通知する()
    {
        var staleItem = new TranscriptItem
        {
            Id = 42,
            Text = "遅れてきた発話",
            SegmentStartTime = TimeSpan.FromSeconds(-6),
            SegmentEndTime = TimeSpan.FromSeconds(-5), // 「現在(0)」から5秒前に発話が終わったことにする
            WhisperCompletedAt = TimeSpan.FromSeconds(-4.9)
        };
        var reader = MakeReaderWith(staleItem);
        var service = new FakeTranslationService(new TranslationResult("Hello", null));
        // 3秒より古い発話は翻訳しない、という設定
        var worker = new TranslationWorker(reader, new object(), service, new Stopwatch(), new LatencyTracker(),
            maxLatency: TimeSpan.FromSeconds(3));

        var skipped = new List<long>();
        var translated = new List<TranslatedTextEventArgs>();
        worker.TranscriptItemSkipped += skipped.Add;
        worker.TranslatedTextReceived += translated.Add;

        await worker.RunAsync(CancellationToken.None);

        Assert.Equal(new long[] { 42 }, skipped);
        Assert.Empty(translated);
        Assert.Equal(0, service.CallCount); // 翻訳API自体が呼ばれていないことが重要(DeepLの無駄な課金/レート消費を避けるため)
    }

    [Fact]
    public async Task maxLatencyを指定しない場合はどんなに古い発話でも従来通り翻訳される()
    {
        // maxLatency省略時は既定でTimeSpan.MaxValueになり、機能自体が無効化される
        // (以前までの「常に全部処理する」挙動と完全互換であることの確認)
        var veryStaleItem = new TranscriptItem
        {
            Id = 99,
            Text = "とても遅れてきた発話",
            SegmentStartTime = TimeSpan.FromSeconds(-101),
            SegmentEndTime = TimeSpan.FromSeconds(-100),
            WhisperCompletedAt = TimeSpan.FromSeconds(-99.9)
        };
        var reader = MakeReaderWith(veryStaleItem);
        var service = new FakeTranslationService(new TranslationResult("Hello", null));
        var worker = new TranslationWorker(reader, new object(), service, new Stopwatch(), new LatencyTracker()); // maxLatency省略

        var translated = new List<TranslatedTextEventArgs>();
        worker.TranslatedTextReceived += translated.Add;

        await worker.RunAsync(CancellationToken.None);

        Assert.Single(translated);
        Assert.Equal(1, service.CallCount);
    }
}
