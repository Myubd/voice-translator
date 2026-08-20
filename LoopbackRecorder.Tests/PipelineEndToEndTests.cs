using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;
using Xunit;

namespace LoopbackRecorder.Tests;

/// <summary>
/// 実音声ファイルを使い、VAD(発話区間検出)→Whisper文字起こしという、パイプラインの
/// 中核となる結線が実際に動作することを検証するE2Eテスト。
///
/// 【他のテストとの違い】このプロジェクトの他のテストは、実際のWhisper推論やSilero VADモデルの
/// ロードを伴わない(フェイクの翻訳サービスや、あらかじめ用意した合成音声チャンクを使う)ため、
/// 「本物の音声を本物のモデルに通した時に、想定通りに発話区間が切り出され、Whisperが空でない
/// 文字起こし結果を返す」ところまでは検証できていなかった。P0の①③(レイテンシ計測・VAD窓方式)は
/// いずれも「実際に動かして初めて分かる」性質の懸念だったため、このテストはそうした問題を
/// 継続的に検出するための安全網として追加した。
///
/// 【手動オプトイン・CIでは実質スキップされる】
/// このテストは実音声ファイルと実際のWhisperモデル(数百MB)を必要とするため、リポジトリには
/// 含めておらず、CI(GitHub Actions)上には前提条件が存在しない。前提条件が揃っていない場合、
/// テスト本体を実行せずに即座に成功扱いで終了する(=CIを壊さない)。実際に検証したい場合は、
/// 開発機で以下を用意した上で `dotnet test` を実行する:
///
///   1. LoopbackRecorder.Tests/TestData/ フォルダを作成し、WAVファイルを1つ以上置く
///      (用意の仕方: PowerShellのSystem.Speech.Synthesis.SpeechSynthesizerで合成音声を生成するか、
///      Audacity等で自分の声を録音してWAV書き出しする。サンプルレート・チャンネル数は自由でよい
///      ——本体アプリと同様にこのテスト側で自動的に16kHzモノラルへ変換する)。
///   2. LoopbackRecorder/ (本体プロジェクトフォルダ) に Whisperモデル(ggml-base.bin等、
///      通常のアプリ利用のために既に配置しているはず)がある状態にする。
///      複数のggml-*.binがある場合は最初に見つかったものを使う。特定のモデルを使いたい場合は
///      環境変数 E2E_WHISPER_MODEL_PATH に絶対パスを設定する。
///
/// Silero VADモデル(silero_vad.onnx)は小さいため本体プロジェクトフォルダに同梱済みで、
/// 追加の準備は不要。
/// </summary>
public class PipelineEndToEndTests
{
    // テスト実行時のAppContext.BaseDirectoryは "LoopbackRecorder.Tests/bin/Debug/net8.0/" 等になる。
    // そこから3階層上がると "LoopbackRecorder.Tests/"(このcsprojのあるフォルダ)、
    // さらに1階層上がるとリポジトリのルート(LoopbackRecorderフォルダとLoopbackRecorder.Testsフォルダが
    // 並んでいる階層)になる想定(README/csprojコメントに記載の標準的なフォルダ構成を前提とする)。
    private static readonly string TestsProjectDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(TestsProjectDir, ".."));
    private static readonly string MainProjectDir = Path.Combine(RepoRoot, "LoopbackRecorder");
    private static readonly string TestDataDir = Path.Combine(TestsProjectDir, "TestData");
    private static readonly string SileroModelPath = Path.Combine(MainProjectDir, "silero_vad.onnx");

    private const int SampleRate = 16000;
    private const int ChunkSamples = 480; // 本体(AudioPipeline.ChunkSamples)と同じ30ms周期に揃える

    [Fact]
    public async Task 実音声ファイルからVADと文字起こしの結線が動作する()
    {
        // Windowsのコンソール(cmd.exe)は既定でShift-JIS系のコードページになっており、
        // .NETのConsole.WriteLineがUTF-8で書き出す文字起こし結果(日本語)が文字化けして
        // 表示されてしまう。テストの合否には影響しないが、目視で文字起こし結果を確認できることが
        // このテストの重要な価値(④「実際に動かして初めて分かる問題の検出」)なので、
        // ここでコンソールの出力エンコーディングをUTF-8に明示的に切り替える。
        // 出力がリダイレクト/パイプされている環境(一部のCIやIDEのテストランナー等)では
        // Console.OutputEncodingの変更自体が例外を投げることがあるため、失敗しても
        // テスト自体は継続できるようtry/catchで囲む(文字化けは許容し、テストの実行は優先する)。
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (IOException)
        {
            // リダイレクトされた出力等、変更できない環境では諦めて既定のエンコーディングのまま進める
        }

        var wavFiles = Directory.Exists(TestDataDir)
            ? Directory.GetFiles(TestDataDir, "*.wav")
            : Array.Empty<string>();
        var whisperModelPath = ResolveWhisperModelPath();

        if (wavFiles.Length == 0 || whisperModelPath == null)
        {
            // 前提条件(テスト用音声・Whisperモデル)が揃っていない場合はここで打ち切る。
            // xUnitには標準の「実行時スキップ」機能が無い(追加パッケージが必要)ため、
            // 早期returnで代用している。CI上は常にこの分岐を通り、テストは常に「成功」表示になるが、
            // 実際には何も検証していないことに注意(ログにその旨を明示する)。
            Console.WriteLine(
                $"[SKIP] テストデータ({TestDataDir})またはWhisperモデルが見つからないため、" +
                "このテストは何も検証せずに終了します。準備方法はこのクラスのXMLコメントを参照してください。");
            return;
        }

        using var sileroDetector = File.Exists(SileroModelPath) ? new SileroVadDetector(SileroModelPath) : null;
        using var whisperFactory = WhisperFactory.FromPath(whisperModelPath);
        using var processor = whisperFactory.CreateBuilder()
            .WithLanguage("auto")
            .Build();

        foreach (var wavPath in wavFiles.OrderBy(p => p))
        {
            await VerifyFileAsync(wavPath, sileroDetector, processor);
        }
    }

    private static async Task VerifyFileAsync(string wavPath, SileroVadDetector? sileroDetector, WhisperProcessor processor)
    {
        var fileName = Path.GetFileName(wavPath);
        using var reader = new AudioFileReader(wavPath);

        // 本体(AudioPipeline)は、実際のキャプチャデバイスがどんなサンプルレート/チャンネル数で
        // あっても16kHzモノラルへ自動変換してからVAD/Whisperに渡している。テスト用音声を用意する側に
        // 「必ず16kHzモノラルで書き出す」という手間を強いる必要は本来無く、本体と同じ変換を
        // このテストでも行うことで、市販の録音ソフトや音声合成ツールがそのまま出力した
        // WAV(44.1kHz/48kHz、ステレオ等)をそのまま置くだけで検証できるようにする。
        ISampleProvider source = reader;
        if (reader.WaveFormat.Channels == 2)
        {
            // NAudioのStereoToMonoSampleProviderは2ch専用。それ以外(3ch以上)は現状未対応のため、
            // 下の分岐で分かりやすいメッセージを出して失敗させる(本体側のMultiChannelToMonoSampleProvider
            // 相当の一般的なNチャンネル対応は、テスト用途では過剰なため導入していない)。
            source = new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }
        else if (reader.WaveFormat.Channels != 1)
        {
            Assert.Fail($"{fileName} は1ch(モノラル)または2ch(ステレオ)のWAVである必要があります" +
                $"(実際: {reader.WaveFormat.Channels}ch)。");
        }

        if (source.WaveFormat.SampleRate != SampleRate)
        {
            source = new WdlResamplingSampleProvider(source, SampleRate);
        }

        var segmenter = new VoiceActivitySegmenter(
            sampleRate: SampleRate,
            chunkSamples: ChunkSamples,
            silenceChunksToEndSpeech: 15, // 本体既定値相当(約450ms)
            minSpeechChunks: 3,
            maxSpeechChunks: 1000,
            prerollChunks: 3,
            forcedSplitOverlapChunks: 5,
            sileroDetector: sileroDetector);

        var segments = new List<VadSegmentResult>();
        var buffer = new float[ChunkSamples];
        long chunkIndex = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            // 末尾で1チャンク分に満たない場合は0埋めする(本体のキャプチャループでも、
            // デバイス停止直前などに半端な件数のReadが起こりうるため、同じ状況を想定する)
            if (read < buffer.Length)
            {
                Array.Clear(buffer, read, buffer.Length - read);
            }

            var currentAudioTime = TimeSpan.FromSeconds((double)(chunkIndex * ChunkSamples) / SampleRate);
            var segment = segmenter.ProcessChunk(buffer, buffer.Length, currentAudioTime);
            if (segment != null)
            {
                segments.Add(segment);
            }
            chunkIndex++;
        }

        // ファイル末尾で発話中のまま終わった場合(無音で終わっていない録音)の取りこぼしを防ぐ
        var finalAudioTime = TimeSpan.FromSeconds((double)(chunkIndex * ChunkSamples) / SampleRate);
        var flushed = segmenter.Flush(finalAudioTime);
        if (flushed != null)
        {
            segments.Add(flushed);
        }

        // 発話区間の正確な件数・境界はファイルの内容(何秒話しているか等)に依存するため、
        // このテストでは厳密な件数を固定でアサートしない。「最低1件は検出される」ことだけを
        // 検証することで、VAD側の閾値設定やモデルロード自体が壊れていないかを確認する
        // (ファイルの用意方法の説明で「発話+無音+発話」を推奨しているのはこのため)。
        Assert.True(segments.Count > 0, $"{fileName} から発話区間が1件も検出されませんでした。");

        Console.WriteLine($"=== {fileName}: {segments.Count}件の発話区間を検出 ===");

        foreach (var segment in segments)
        {
            var texts = new List<string>();
            await foreach (var result in processor.ProcessAsync(segment.Samples.ToArray()))
            {
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    texts.Add(result.Text.Trim());
                }
            }
            var transcribed = string.Join(" ", texts);
            Console.WriteLine($"  [{segment.StartTime:mm\\:ss\\.ff} - {segment.EndTime:mm\\:ss\\.ff}] {transcribed}");

            // 発話区間としてVADが切り出した以上、Whisperが完全な空文字列を返すのは異常な兆候
            // (無音・雑音の誤検出であれば、通常は[BLANK_AUDIO]等のタグ付きテキストが返る。
            // 本体側(AudioPipeline.TranscribeSegmentAsync)はこのタグを無視する処理をしているが、
            // このテストでは「Whisperが何かしら反応を返したか」を見たいため、タグ自体は許容する)
            Assert.False(string.IsNullOrWhiteSpace(transcribed),
                $"{fileName}の発話区間[{segment.StartTime}-{segment.EndTime}]でWhisperが完全に空の" +
                "文字起こし結果を返しました。VADの閾値が低すぎて無音区間まで拾っている可能性があります。");
        }
    }

    /// <summary>環境変数E2E_WHISPER_MODEL_PATHが設定されていればそれを使う。
    /// 無ければ本体プロジェクトフォルダ内の最初のggml-*.binを探す(通常のアプリ利用のために
    /// 既に配置されているはずのモデルをそのまま使い回す)。どちらも見つからなければnull。</summary>
    private static string? ResolveWhisperModelPath()
    {
        var envPath = Environment.GetEnvironmentVariable("E2E_WHISPER_MODEL_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return envPath;
        }

        if (!Directory.Exists(MainProjectDir))
        {
            return null;
        }

        return Directory.GetFiles(MainProjectDir, "ggml-*.bin").OrderBy(p => p).FirstOrDefault();
    }
}
