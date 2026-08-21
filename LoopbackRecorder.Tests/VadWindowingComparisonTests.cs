using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit;

namespace LoopbackRecorder.Tests;

/// <summary>
/// Silero VADの「アプリ既定方式(480サンプルhop、576サンプル連続スライディングウィンドウ)」と
/// 「公式仕様どおりの方式(512サンプルhop、64サンプル文脈、非重複ウィンドウ)」を、
/// 同じ実音声に対して実測比較する(GitHubレビューP0-1「Silero VADの入力方式を最優先で再検証」対応)。
///
/// コードレビューの時点では「実用上の精度に大きな影響は無いと考えられるが、モデルが学習時に
/// 見ていない入力分布(重複96サンプル vs 公式64サンプル)を与え続けている」という理論上の懸念に
/// とどまっていた。このテストは、その懸念を実測で白黒つけるためのもの。
///
/// 【手動オプトイン】PipelineEndToEndTestsと同じ設計。TestData/*.wavが無い環境では何もせず
/// 終了する(CIを壊さない)。Whisperモデルは不要(VADの確率曲線だけを比較するため)。
///
/// 【regression test化(レビュー対応)】導入当初はConsole.WriteLineで差分を出力するだけで、
/// 閾値を超えてもテストは常に成功していた(=将来VAD実装を変更して確率曲線が大きく崩れても
/// 検知できない)。そのため下記の許容閾値(<see cref="MaxAllowedMeanAbsDiff"/>等)を超えた場合は
/// テストを失敗させるようにした。閾値は本ドキュメント作成時点の実測値
/// (docs/vad-windowing-comparison.md: 平均差0.0115・境界差最大30ms)に対して、TTS以外の音声
/// (小声・早口・ノイズ等)でもある程度のブレを許容できるよう余裕を持たせている。
/// </summary>
public class VadWindowingComparisonTests
{
    private static readonly string TestsProjectDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(TestsProjectDir, ".."));
    private static readonly string MainProjectDir = Path.Combine(RepoRoot, "LoopbackRecorder");
    private static readonly string TestDataDir = Path.Combine(TestsProjectDir, "TestData");
    private static readonly string SileroModelPath = Path.Combine(MainProjectDir, "silero_vad.onnx");

    private const int SampleRate = 16000;
    private const int ChunkSamples = 480; // 本体(AudioPipeline.ChunkSamples)と同じ30ms周期

    // === regression test用の許容閾値 ===
    // 確率曲線の平均差。実測値0.0115の約4倍を上限とし、TTS以外の音声でのブレも許容する。
    private const double MaxAllowedMeanAbsDiff = 0.05;
    // 検出区間数の差。0(完全一致)を基本としつつ、境界付近の1区間分だけのブレは許容する。
    private const int MaxAllowedSegmentCountDiff = 1;
    // 区間境界時刻の差。実測値30ms(=1チャンク分)に対して、3チャンク分程度まで許容する。
    private const double MaxAllowedBoundaryDiffMs = 100;

    [Fact]
    public void アプリ既定方式と公式ウィンドウ方式の発話確率_区間検出結果を比較する()
    {
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (IOException)
        {
            // リダイレクトされた出力等、変更できない環境では諦めて既定のエンコーディングのまま進める
        }

        var wavFiles = Directory.Exists(TestDataDir) ? Directory.GetFiles(TestDataDir, "*.wav") : Array.Empty<string>();
        if (wavFiles.Length == 0 || !File.Exists(SileroModelPath))
        {
            var skipMessage = $"[SKIP] テストデータ({TestDataDir})またはSileroモデルが見つからないためスキップします。";
            Console.WriteLine(skipMessage);
            // GitHub Actions上でログに埋もれず気づけるよう、workflow commandとしてWarning annotation
            // を出す(::warning::で始まる行はActionsのRun summaryに表示される)。CI(windows-latest)は
            // *.wavが.gitignore対象のため、現状は毎回このパスを通り、VAD窓方式の実測比較は
            // 一度もCI上で実行されていないことに注意。
            Console.WriteLine($"::warning::{nameof(VadWindowingComparisonTests)}: {skipMessage}");
            return;
        }

        foreach (var wavPath in wavFiles.OrderBy(p => p))
        {
            CompareFile(wavPath);
        }
    }

    private static void CompareFile(string wavPath)
    {
        var fileName = Path.GetFileName(wavPath);
        using var reader = new AudioFileReader(wavPath);

        ISampleProvider source = reader;
        if (reader.WaveFormat.Channels == 2)
        {
            source = new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }
        if (source.WaveFormat.SampleRate != SampleRate)
        {
            source = new WdlResamplingSampleProvider(source, SampleRate);
        }

        // 全サンプルを先に読み切っておく(2方式×2種類=4つのSileroVadDetectorインスタンスに
        // まったく同じチャンク列を、同じ順序・同じタイミングで与えるため)
        var allSamples = new List<float>();
        var buffer = new float[ChunkSamples];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++) allSamples.Add(buffer[i]);
        }

        // 確率曲線だけを追うための専用インスタンス(セグメンターには繋がない)と、
        // 区間検出結果を見るためのセグメンター用インスタンスを、方式ごとに別々に用意する。
        // 同じインスタンスを両方の目的で使うと、呼び出しが二重になり内部状態(GRU state/文脈)が
        // ズレてしまうため、あえて独立させている。
        using var appProbeDetector = new SileroVadDetector(SileroModelPath, useOfficialWindowing: false);
        using var officialProbeDetector = new SileroVadDetector(SileroModelPath, useOfficialWindowing: true);
        using var appSegmenterDetector = new SileroVadDetector(SileroModelPath, useOfficialWindowing: false);
        using var officialSegmenterDetector = new SileroVadDetector(SileroModelPath, useOfficialWindowing: true);

        var appSegmenter = CreateSegmenter(appSegmenterDetector);
        var officialSegmenter = CreateSegmenter(officialSegmenterDetector);

        var appProbabilities = new List<float>();
        var officialProbabilities = new List<float>();
        var appSegments = new List<VadSegmentResult>();
        var officialSegments = new List<VadSegmentResult>();

        int chunkCount = allSamples.Count / ChunkSamples + (allSamples.Count % ChunkSamples > 0 ? 1 : 0);
        var chunk = new float[ChunkSamples];
        for (int i = 0; i < chunkCount; i++)
        {
            int offset = i * ChunkSamples;
            int n = Math.Min(ChunkSamples, allSamples.Count - offset);
            Array.Clear(chunk, 0, chunk.Length);
            for (int j = 0; j < n; j++) chunk[j] = allSamples[offset + j];

            appProbabilities.Add(appProbeDetector.GetSpeechProbability(chunk, chunk.Length));
            officialProbabilities.Add(officialProbeDetector.GetSpeechProbability(chunk, chunk.Length));

            var currentAudioTime = TimeSpan.FromSeconds((double)offset / SampleRate);
            var appSeg = appSegmenter.ProcessChunk(chunk, chunk.Length, currentAudioTime);
            if (appSeg != null) appSegments.Add(appSeg);
            var officialSeg = officialSegmenter.ProcessChunk(chunk, chunk.Length, currentAudioTime);
            if (officialSeg != null) officialSegments.Add(officialSeg);
        }
        var finalTime = TimeSpan.FromSeconds((double)allSamples.Count / SampleRate);
        var appFlush = appSegmenter.Flush(finalTime);
        if (appFlush != null) appSegments.Add(appFlush);
        var officialFlush = officialSegmenter.Flush(finalTime);
        if (officialFlush != null) officialSegments.Add(officialFlush);

        // === 確率曲線の比較 ===
        double maxAbsDiff = 0;
        double sumAbsDiff = 0;
        for (int i = 0; i < appProbabilities.Count; i++)
        {
            var diff = Math.Abs(appProbabilities[i] - officialProbabilities[i]);
            maxAbsDiff = Math.Max(maxAbsDiff, diff);
            sumAbsDiff += diff;
        }
        var meanAbsDiff = appProbabilities.Count > 0 ? sumAbsDiff / appProbabilities.Count : 0;

        Console.WriteLine($"=== {fileName}: VAD窓方式の比較 ===");
        Console.WriteLine($"  確率曲線の差: 最大 {maxAbsDiff:F4} / 平均 {meanAbsDiff:F4} (0.0〜1.0スケール)");
        Console.WriteLine($"  検出区間数: アプリ既定方式={appSegments.Count}件 / 公式方式={officialSegments.Count}件");

        // === 区間の境界時刻の比較 ===
        // 件数が一致している場合のみ、同じ順番の区間同士を対応付けて境界の差を見る
        // (件数自体が違う場合は、そもそも「どの区間とどの区間を比べるか」が自明ではないため、
        // 件数の違い自体を主要な結果として報告するにとどめる)
        if (appSegments.Count == officialSegments.Count && appSegments.Count > 0)
        {
            for (int i = 0; i < appSegments.Count; i++)
            {
                var startDiffMs = (appSegments[i].StartTime - officialSegments[i].StartTime).TotalMilliseconds;
                var endDiffMs = (appSegments[i].EndTime - officialSegments[i].EndTime).TotalMilliseconds;
                Console.WriteLine($"  区間{i + 1}: 開始差={startDiffMs:F0}ms 終了差={endDiffMs:F0}ms " +
                    $"(アプリ既定: {appSegments[i].StartTime:mm\\:ss\\.ff}-{appSegments[i].EndTime:mm\\:ss\\.ff}, " +
                    $"公式: {officialSegments[i].StartTime:mm\\:ss\\.ff}-{officialSegments[i].EndTime:mm\\:ss\\.ff})");
            }
        }
        else
        {
            Console.WriteLine("  ⚠ 検出区間数が一致しないため、区間ごとの境界比較はスキップします" +
                "(区間数の違い自体が重要な結果です)。");
        }

        // === regression assertion(レビュー対応) ===
        // これまでは上記の値をConsole.WriteLineで出力するだけで、どれだけ差が開いてもテストは
        // 常に成功していた。今後VAD周りの実装(SileroVadDetector・VoiceActivitySegmenter)を
        // 変更した際に、確率曲線や検出区間が大きく崩れていないことをCIで検知できるよう、
        // 許容閾値を超えた場合はここで失敗させる。
        Assert.True(meanAbsDiff < MaxAllowedMeanAbsDiff,
            $"{fileName}: 確率曲線の平均差が許容値を超えています(平均差={meanAbsDiff:F4}, " +
            $"許容値={MaxAllowedMeanAbsDiff:F4})。VAD実装の変更により、公式ウィンドウ方式との" +
            "乖離が大きくなっていないか確認してください。");

        var segmentCountDiff = Math.Abs(appSegments.Count - officialSegments.Count);
        Assert.True(segmentCountDiff <= MaxAllowedSegmentCountDiff,
            $"{fileName}: 検出区間数の差が許容値を超えています(アプリ既定={appSegments.Count}件, " +
            $"公式={officialSegments.Count}件, 差={segmentCountDiff}, 許容値={MaxAllowedSegmentCountDiff})。");

        if (appSegments.Count == officialSegments.Count)
        {
            for (int i = 0; i < appSegments.Count; i++)
            {
                var startDiffMs = Math.Abs((appSegments[i].StartTime - officialSegments[i].StartTime).TotalMilliseconds);
                var endDiffMs = Math.Abs((appSegments[i].EndTime - officialSegments[i].EndTime).TotalMilliseconds);
                Assert.True(startDiffMs <= MaxAllowedBoundaryDiffMs,
                    $"{fileName}: 区間{i + 1}の開始時刻差が許容値を超えています" +
                    $"(差={startDiffMs:F0}ms, 許容値={MaxAllowedBoundaryDiffMs:F0}ms)。");
                Assert.True(endDiffMs <= MaxAllowedBoundaryDiffMs,
                    $"{fileName}: 区間{i + 1}の終了時刻差が許容値を超えています" +
                    $"(差={endDiffMs:F0}ms, 許容値={MaxAllowedBoundaryDiffMs:F0}ms)。");
            }
        }
    }

    private static VoiceActivitySegmenter CreateSegmenter(SileroVadDetector detector) => new(
        sampleRate: SampleRate,
        chunkSamples: ChunkSamples,
        silenceChunksToEndSpeech: 15,
        minSpeechChunks: 3,
        maxSpeechChunks: 1000,
        prerollChunks: 3,
        forcedSplitOverlapChunks: 5,
        sileroDetector: detector);
}
