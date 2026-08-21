using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace LoopbackRecorder.Tests;

/// <summary>
/// 「音声fixture → VAD → (Whisperの代わりに決定的なマッピング) → 重複除去 → 翻訳(モック)」
/// までを、実際のAudioPipeline構成要素(VoiceActivitySegmenter/SegmentDeduplicator/
/// TranslationWorker)を実際に組み合わせて通しで検証する統合テスト。
///
/// 【なぜWhisper自体は含まないか】
/// Whisper.net(whisper.cpp)は実際のONNX/GGMLモデルファイル(数百MB)とネイティブ推論ランタイムに
/// 依存しており、CIやこのテストプロジェクト単体では現実的に用意できない。そのため、
/// このテストでは「VADが検出した各発話区間に対し、Whisperが決定的なテキストを返した」という
/// 前提でTranscriptItemを組み立てることで、Whisperそのものを除いた前後の配線
/// (VAD→重複除去→翻訳キュー→翻訳ワーカー→UIイベント)が正しく機能することを検証する。
/// この配線こそが、各コンポーネント単体のテストでは検出できない「繋ぎ込みのバグ」
/// (Id不一致・イベント未発火・重複除去の絶対時刻計算違いなど)の主な発生源となるため、
/// 統合テストとして最も価値がある部分だと判断した。
///
/// AudioPipeline.RunWhisperWorkerAsync内の実装(760行目付近)と同じ手順を踏襲している:
/// VadSegmentResult → (Whisper) → text → SegmentDeduplicator.IsDuplicate → TranscriptItem → Channel
/// </summary>
public class PipelineIntegrationTests
{
    // 実アプリのAudioPipelineと同じ値(16kHz, 30ms=480サンプル/チャンク)。
    private const int SampleRate = 16000;
    private const int ChunkSamples = 480;

    private static float[] SilenceChunk() => new float[ChunkSamples];

    /// <summary>VoiceActivitySegmenterTestsと同じ矩形波生成(RMS=amplitudeちょうどになる)</summary>
    private static float[] LoudChunk(float amplitude)
    {
        var chunk = new float[ChunkSamples];
        for (int i = 0; i < chunk.Length; i++)
        {
            chunk[i] = (i % 2 == 0) ? amplitude : -amplitude;
        }
        return chunk;
    }

    private static VoiceActivitySegmenter CreateSegmenter() => new(
        sampleRate: SampleRate,
        chunkSamples: ChunkSamples,
        silenceChunksToEndSpeech: 3,
        minSpeechChunks: 2,
        maxSpeechChunks: 100,
        prerollChunks: 2,
        forcedSplitOverlapChunks: 1,
        sileroDetector: null)
    {
        EnergyThreshold = 0.3f,
        HysteresisRatio = 0.5f,
    };

    /// <summary>戻り値を差し替えられるフェイク翻訳サービス。呼ばれた順にテキストを記録する。</summary>
    private sealed class FakeTranslationService : ITranslationService
    {
        private readonly Func<string, TranslationResult> _respond;
        public List<string> ReceivedTexts { get; } = new();

        public FakeTranslationService(Func<string, TranslationResult> respond) => _respond = respond;

        public Task<TranslationResult> TranslateAsync(string text, CancellationToken cancellationToken)
        {
            ReceivedTexts.Add(text);
            return Task.FromResult(_respond(text));
        }
    }

    /// <summary>
    /// AudioPipeline.RunWhisperWorkerAsyncの該当部分(769〜783行目)を模した、
    /// 「VAD区間→(Whisperの代わりの決定的テキスト)→重複除去→TranscriptItem生成」処理。
    /// 実際のSegmentDeduplicatorインスタンスをそのまま使うことで、重複除去ロジック自体は
    /// 本物の実装で検証される。
    /// </summary>
    private static TranscriptItem? TryBuildTranscriptItem(
        SegmentDeduplicator deduplicator, VadSegmentResult segment, long id, string text)
    {
        // 簡略化のため、Whisperの相対タイムスタンプ(result.Start/End)は
        // 区間全体(0〜区間長)を返したものとして扱う
        var absoluteStart = segment.StartTime;
        var absoluteEnd = segment.EndTime;

        if (deduplicator.IsDuplicate(text, absoluteStart, absoluteEnd)) return null;

        return new TranscriptItem
        {
            Id = id,
            Text = text,
            SegmentStartTime = absoluteStart,
            SegmentEndTime = absoluteEnd,
            WhisperCompletedAt = absoluteEnd + TimeSpan.FromMilliseconds(200), // Whisper処理に200ms要したと仮定
        };
    }

    [Fact]
    public async Task 無音を挟んだ2発話が検出されそれぞれ翻訳される()
    {
        var segmenter = CreateSegmenter();
        var deduplicator = new SegmentDeduplicator();
        var segments = new List<VadSegmentResult>();
        long chunkCount = 0;

        void Feed(float[] chunk)
        {
            chunkCount++;
            var t = TimeSpan.FromSeconds((double)(chunkCount * ChunkSamples) / SampleRate);
            var result = segmenter.ProcessChunk(chunk, ChunkSamples, t);
            if (result != null) segments.Add(result);
        }

        // 無音 → 発話1(5チャンク) → 無音(終了検出分含む) → 発話2(5チャンク) → 無音でフラッシュ
        for (int i = 0; i < 5; i++) Feed(SilenceChunk());
        for (int i = 0; i < 5; i++) Feed(LoudChunk(0.8f));
        for (int i = 0; i < 5; i++) Feed(SilenceChunk());
        for (int i = 0; i < 5; i++) Feed(LoudChunk(0.8f));
        for (int i = 0; i < 5; i++) Feed(SilenceChunk());

        Assert.Equal(2, segments.Count); // 音声fixtureからVADが2発話を検出できていることをまず確認

        // Whisperの代わりに区間ごとに決定的なテキストを割り当てる(区間0→"Hello", 区間1→"World")
        var texts = new[] { "Hello", "World" };
        var transcriptItems = new List<TranscriptItem>();
        for (int i = 0; i < segments.Count; i++)
        {
            var item = TryBuildTranscriptItem(deduplicator, segments[i], id: i + 1, texts[i]);
            Assert.NotNull(item); // 実際に別発話なので重複除去には引っかからないはず
            transcriptItems.Add(item!);
        }

        var channel = Channel.CreateUnbounded<TranscriptItem>();
        foreach (var item in transcriptItems) channel.Writer.TryWrite(item);
        channel.Writer.Complete();

        var fakeService = new FakeTranslationService(text => TranslationResult.Success(text.ToUpperInvariant()));
        var worker = new TranslationWorker(channel.Reader, new object(), fakeService, new Stopwatch(), new LatencyTracker());

        var translated = new List<TranslatedTextEventArgs>();
        var latencies = new List<LatencyMeasurement>();
        worker.TranslatedTextReceived += translated.Add;
        worker.LatencyMeasured += latencies.Add;

        await worker.RunAsync(CancellationToken.None);

        // 2発話とも翻訳サービスまで届き、正しい訳文・Id・遅延計測がそれぞれ得られていること
        Assert.Equal(new[] { "Hello", "World" }, fakeService.ReceivedTexts);
        Assert.Equal(2, translated.Count);
        Assert.Equal(1, translated[0].Id);
        Assert.Equal("HELLO", translated[0].Text);
        Assert.Equal(2, translated[1].Id);
        Assert.Equal("WORLD", translated[1].Text);
        Assert.Equal(2, latencies.Count);
    }

    [Fact]
    public async Task 強制分割のオーバーラップを模した重複区間は翻訳まで届かない()
    {
        var deduplicator = new SegmentDeduplicator();

        // AudioPipelineの15秒強制分割と同様、ほぼ同じ音声区間(時間的に重なる)をWhisperが
        // 2回「同じ文字列」で文字起こししてしまったケースを再現する
        var segment1 = new VadSegmentResult(new List<float>(), TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(3));
        var segment2 = new VadSegmentResult(new List<float>(), TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(5.5));

        var item1 = TryBuildTranscriptItem(deduplicator, segment1, id: 1, "同じ発話です");
        var item2 = TryBuildTranscriptItem(deduplicator, segment2, id: 2, "同じ発話です");

        Assert.NotNull(item1);
        Assert.Null(item2); // 文字列一致+時間重なりのため重複除去され、そもそもTranscriptItemが作られない

        var channel = Channel.CreateUnbounded<TranscriptItem>();
        channel.Writer.TryWrite(item1!);
        channel.Writer.Complete();

        var fakeService = new FakeTranslationService(text => TranslationResult.Success(text));
        var worker = new TranslationWorker(channel.Reader, new object(), fakeService, new Stopwatch(), new LatencyTracker());

        var translated = new List<TranslatedTextEventArgs>();
        worker.TranslatedTextReceived += translated.Add;

        await worker.RunAsync(CancellationToken.None);

        // 重複除去された分は翻訳サービスに一度も渡らない(APIコスト・表示の二重化を防ぐ)
        Assert.Single(fakeService.ReceivedTexts);
        Assert.Single(translated);
        Assert.Equal(1, translated[0].Id);
    }

    [Fact]
    public async Task 翻訳失敗を含む複数発話でもIdの対応関係が崩れない()
    {
        var segmenter = CreateSegmenter();
        var deduplicator = new SegmentDeduplicator();
        var segments = new List<VadSegmentResult>();
        long chunkCount = 0;

        void Feed(float[] chunk)
        {
            chunkCount++;
            var t = TimeSpan.FromSeconds((double)(chunkCount * ChunkSamples) / SampleRate);
            var result = segmenter.ProcessChunk(chunk, ChunkSamples, t);
            if (result != null) segments.Add(result);
        }

        // 3発話を無音区切りで生成
        for (int burst = 0; burst < 3; burst++)
        {
            for (int i = 0; i < 5; i++) Feed(SilenceChunk());
            for (int i = 0; i < 5; i++) Feed(LoudChunk(0.8f));
        }
        for (int i = 0; i < 5; i++) Feed(SilenceChunk());

        Assert.Equal(3, segments.Count);

        var texts = new[] { "発話1", "発話2", "発話3" };
        var transcriptItems = segments
            .Select((seg, i) => TryBuildTranscriptItem(deduplicator, seg, id: i + 1, texts[i])!)
            .ToList();

        var channel = Channel.CreateUnbounded<TranscriptItem>();
        foreach (var item in transcriptItems) channel.Writer.TryWrite(item);
        channel.Writer.Complete();

        // 2件目(発話2)だけ翻訳を失敗させる(DeepL/Ollamaのタイムアウト等を想定)
        var fakeService = new FakeTranslationService(text =>
            text == "発話2" ? TranslationResult.Failure("simulated timeout") : TranslationResult.Success($"訳:{text}"));
        var worker = new TranslationWorker(channel.Reader, new object(), fakeService, new Stopwatch(), new LatencyTracker());

        var translated = new List<TranslatedTextEventArgs>();
        var errors = new List<string>();
        worker.TranslatedTextReceived += translated.Add;
        worker.TranslationErrorOccurred += errors.Add;

        await worker.RunAsync(CancellationToken.None);

        // 失敗した回もイベント自体は(Text=nullで)発火するため、原文側と訳文側のIdの対応が
        // ズレない(MainWindow側がIdで対応付けている前提を、実際のTranslationWorkerで検証)
        Assert.Equal(3, translated.Count);
        Assert.Equal("訳:発話1", translated[0].Text);
        Assert.Null(translated[1].Text);
        Assert.Equal("訳:発話3", translated[2].Text);
    }

    /// <summary>
    /// 複数の翻訳ワーカーを同じキューに対して並列実行しても全項目が重複なく処理されることを検証する。
    /// AudioPipelineがTranslationWorkerCount本のTranslationWorkerを同じChannel.Reader/queueLockに
    /// 対して同時に起動する構成(GitHubレビューP1「翻訳Workerが1本なので翻訳が律速になる」への対応)を
    /// そのまま模している。各ワーカーのキュー読み出し(TryRead)はqueueLockで排他されるため、
    /// 複数ワーカーが同時に起動しても、同じ項目が2回処理されたり項目が失われたりしないはず。
    /// </summary>
    [Fact]
    public async Task 複数の翻訳ワーカーを同じキューに対して並列実行しても全項目が重複なく処理される()
    {
        var items = Enumerable.Range(1, 6)
            .Select(id => new TranscriptItem
            {
                Id = id,
                Text = $"発話{id}",
                SegmentStartTime = TimeSpan.FromSeconds(id),
                SegmentEndTime = TimeSpan.FromSeconds(id + 1),
                WhisperCompletedAt = TimeSpan.FromSeconds(id + 1.1)
            })
            .ToList();

        var channel = Channel.CreateUnbounded<TranscriptItem>();
        foreach (var item in items) channel.Writer.TryWrite(item);
        channel.Writer.Complete();

        // 1件あたり100ms要する翻訳サービスを想定(DeepL/Ollamaの実際のAPI呼び出し時間の模擬)
        var fakeService = new SlowFakeTranslationService(delay: TimeSpan.FromMilliseconds(100));
        var queueLock = new object();

        const int workerCount = 3;
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => new TranslationWorker(channel.Reader, queueLock, fakeService, new Stopwatch(), new LatencyTracker()))
            .ToList();

        var translated = new ConcurrentBag<TranslatedTextEventArgs>();
        foreach (var worker in workers)
        {
            worker.TranslatedTextReceived += args => translated.Add(args);
        }

        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(workers.Select(w => w.RunAsync(CancellationToken.None)));
        stopwatch.Stop();

        // 6項目×100msを3ワーカーで並列処理すれば理論上は約200ms(6/3×100ms)で終わるはず。
        // 直列(1ワーカー)なら600msかかる。CI環境のスケジューリング揺らぎを考慮し、
        // 理論値ちょうどでは判定せず、緩めの上限で「並列に処理されたこと」自体を検証する。
        // (閾値調整の経緯: 当初400msだったが、GitHub Actionsの共有ランナーが混んでいる時間帯に
        // 419msで実測し、閾値ぎりぎりで失敗した。並列/直列を区別できればよいテストの目的からすると
        // 400msという閾値自体が厳しすぎたため、直列の600msとは明確に区別できる範囲で500msまで緩めた)
        Assert.True(stopwatch.ElapsedMilliseconds < 500,
            $"並列実行されていれば500ms未満で終わるはず(実測: {stopwatch.ElapsedMilliseconds}ms)");

        // 重複なく・欠落なく、全6項目がちょうど1回ずつ処理されたこと
        Assert.Equal(6, fakeService.CallCount);
        Assert.Equal(6, translated.Count);
        Assert.Equal(
            items.Select(i => i.Id).OrderBy(x => x),
            translated.Select(t => t.Id).OrderBy(x => x));
    }

    /// <summary>指定した遅延の後に成功を返すフェイク。呼び出し回数はInterlockedでスレッドセーフに数える
    /// (複数のTranslationWorkerから同時に呼ばれることを前提としたテスト専用実装)。</summary>
    private sealed class SlowFakeTranslationService : ITranslationService
    {
        private readonly TimeSpan _delay;
        private int _callCount;
        public int CallCount => _callCount;
        public bool IsEnabled => true;

        public SlowFakeTranslationService(TimeSpan delay) => _delay = delay;

        public Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<TranslationResult> TranslateAsync(string text, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            await Task.Delay(_delay, cancellationToken);
            return TranslationResult.Success($"訳:{text}");
        }
    }
}
