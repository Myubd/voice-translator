using System;
using Xunit;

namespace LoopbackRecorder.Tests;

/// <summary>
/// VoiceActivitySegmenter(VADステートマシン)の単体テスト。
///
/// SileroVadDetectorは常にnullで渡し、RMSベースのフォールバック経路のみを検証する
/// (Sileroの経路はONNXモデル・推論ランタイムに依存し、単体テストとしては別の性質の
/// 検証(モデルの出力自体が妥当か)になるため、ここでは対象外とする)。
///
/// 各テストは目的ごとに、その場でVoiceActivitySegmenterを個別のパラメータで生成する。
/// クラス自体がミュータブルな状態(_speechBuffer等)を持つため、テスト間で使い回さず
/// 独立させることで、テストの実行順序に依存しない・並列実行しても安全にしている。
/// </summary>
public class VoiceActivitySegmenterTests
{
    // 実アプリのAudioPipelineと同じ値(16kHz, 30ms=480サンプル/チャンク)。
    private const int SampleRate = 16000;
    private const int ChunkSamples = 480;

    /// <summary>無音チャンク(全サンプル0)。ComputeRmsは平均を引いてからRMSを取るため、
    /// 定数値のチャンクは値の大小に関わらず常にRMS=0になる。</summary>
    private static float[] SilenceChunk() => new float[ChunkSamples];

    /// <summary>
    /// 指定した振幅の矩形波チャンクを作る。±amplitudeを交互に並べた矩形波は、
    /// 平均が0・各サンプルの2乗が常にamplitude^2になるため、RMS=amplitudeちょうどになる
    /// (三角関数を使うより、閾値との比較がテスト内で追いやすいため矩形波を採用)。
    /// </summary>
    private static float[] LoudChunk(float amplitude)
    {
        var chunk = new float[ChunkSamples];
        for (int i = 0; i < chunk.Length; i++)
        {
            chunk[i] = (i % 2 == 0) ? amplitude : -amplitude;
        }
        return chunk;
    }

    /// <summary>chunkSamples分の時間だけ経過させた時刻を作るヘルパー。
    /// 呼び出し側(AudioPipeline)が実サンプル数から算出する仕様に合わせ、
    /// テストでも「これまでに読んだチャンク数」から経過時間を計算する。</summary>
    private static TimeSpan TimeAfterChunks(int chunkCount) =>
        TimeSpan.FromSeconds((double)(chunkCount * ChunkSamples) / SampleRate);

    /// <summary>
    /// デフォルトパラメータのVoiceActivitySegmenterを作る。
    /// silenceChunksToEndSpeech/minSpeechChunks/maxSpeechChunks/prerollChunksは
    /// 実アプリの値だとテストが長くなりすぎるため、テストが読みやすい小さめの値にしている
    /// (アルゴリズム自体はチャンク数のパラメータ化のみで、値の大小はロジックに影響しない)。
    /// </summary>
    private static VoiceActivitySegmenter CreateSegmenter(
        int silenceChunksToEndSpeech = 3,
        int minSpeechChunks = 2,
        int maxSpeechChunks = 100,
        int prerollChunks = 2,
        int forcedSplitOverlapChunks = 1,
        float energyThreshold = 0.3f,
        float hysteresisRatio = 0.5f)
    {
        var segmenter = new VoiceActivitySegmenter(
            SampleRate, ChunkSamples, silenceChunksToEndSpeech, minSpeechChunks,
            maxSpeechChunks, prerollChunks, forcedSplitOverlapChunks, sileroDetector: null)
        {
            EnergyThreshold = energyThreshold,
            HysteresisRatio = hysteresisRatio,
        };
        return segmenter;
    }

    [Fact]
    public void 無音のみを与え続けても発話区間は検出されない()
    {
        var segmenter = CreateSegmenter();

        for (int i = 1; i <= 20; i++)
        {
            var result = segmenter.ProcessChunk(SilenceChunk(), ChunkSamples, TimeAfterChunks(i));
            Assert.Null(result);
        }

        Assert.Null(segmenter.Flush(TimeAfterChunks(20)));
    }

    [Fact]
    public void 発話後に十分な長さの無音が続くと区間が確定する()
    {
        // preroll=2, minSpeechChunks=2, silenceChunksToEndSpeech=3
        var segmenter = CreateSegmenter(silenceChunksToEndSpeech: 3, minSpeechChunks: 2, prerollChunks: 2);
        int chunkIndex = 0;
        VadSegmentResult? result = null;

        // プリロール用に無音を2チャンク分先に流しておく
        for (int i = 0; i < 2; i++)
        {
            chunkIndex++;
            result = segmenter.ProcessChunk(SilenceChunk(), ChunkSamples, TimeAfterChunks(chunkIndex));
            Assert.Null(result);
        }

        // 発話(閾値0.3を超える振幅0.6)を4チャンク
        for (int i = 0; i < 4; i++)
        {
            chunkIndex++;
            result = segmenter.ProcessChunk(LoudChunk(0.6f), ChunkSamples, TimeAfterChunks(chunkIndex));
            Assert.Null(result); // まだ発話終了していない
        }

        // 無音による発話終了(silenceChunksToEndSpeech=3チャンク必要)
        for (int i = 0; i < 3; i++)
        {
            chunkIndex++;
            result = segmenter.ProcessChunk(SilenceChunk(), ChunkSamples, TimeAfterChunks(chunkIndex));
        }

        // 3チャンク目の無音でようやく区間が確定するはず
        Assert.NotNull(result);
        // プリロール(2) + 発話(4) + 終了までの無音(3) = 9チャンク分のサンプルが含まれる
        Assert.Equal(9 * ChunkSamples, result!.Samples.Count);
        // StartTimeは「最初の発話チャンクを読み終えた時刻(=3チャンク目の終わり=TimeAfterChunks(3))」
        // から、保持されているプリロール2チャンク分の時間だけ遡った時刻になる
        Assert.Equal(TimeAfterChunks(1), result.StartTime);
        Assert.Equal(TimeAfterChunks(chunkIndex), result.EndTime);
    }

    [Fact]
    public void 最小発話チャンク数未満の短い発話はセグメントとして確定しない()
    {
        // minSpeechChunksを高めに設定し、「発話+終了までの無音」の合計チャンク数が
        // それに満たない短いケースを作る(プリロールは0にして計算をシンプルにする)。
        var segmenter = CreateSegmenter(silenceChunksToEndSpeech: 2, minSpeechChunks: 10, prerollChunks: 0);
        int chunkIndex = 0;
        VadSegmentResult? result = null;

        // 発話1チャンクのみ
        chunkIndex++;
        result = segmenter.ProcessChunk(LoudChunk(0.6f), ChunkSamples, TimeAfterChunks(chunkIndex));
        Assert.Null(result);

        // 終了に必要な無音2チャンク(合計でも1+2=3チャンク < minSpeechChunks=10)
        for (int i = 0; i < 2; i++)
        {
            chunkIndex++;
            result = segmenter.ProcessChunk(SilenceChunk(), ChunkSamples, TimeAfterChunks(chunkIndex));
        }

        // 発話は終了したと判定されるが、長さが足りないためセグメントとしては返らない
        Assert.Null(result);
    }

    [Fact]
    public void 短い無音は発話継続中とみなされセグメントを分断しない()
    {
        // silenceChunksToEndSpeechより短い無音(いわゆる息継ぎ)を挟んでも、
        // 発話が終了したとみなされず1つの区間として繋がることを確認する。
        var segmenter = CreateSegmenter(silenceChunksToEndSpeech: 5, minSpeechChunks: 2, prerollChunks: 0);
        int chunkIndex = 0;
        VadSegmentResult? result = null;

        // 発話2チャンク
        for (int i = 0; i < 2; i++)
        {
            chunkIndex++;
            result = segmenter.ProcessChunk(LoudChunk(0.6f), ChunkSamples, TimeAfterChunks(chunkIndex));
            Assert.Null(result);
        }

        // 息継ぎ(2チャンクの無音。終了に必要な5チャンクより短い)
        for (int i = 0; i < 2; i++)
        {
            chunkIndex++;
            result = segmenter.ProcessChunk(SilenceChunk(), ChunkSamples, TimeAfterChunks(chunkIndex));
            Assert.Null(result);
        }

        // 発話再開(無音カウントが0にリセットされているはず)
        for (int i = 0; i < 2; i++)
        {
            chunkIndex++;
            result = segmenter.ProcessChunk(LoudChunk(0.6f), ChunkSamples, TimeAfterChunks(chunkIndex));
            Assert.Null(result);
        }

        // ここで無音カウントがリセットされていなければ、息継ぎの2チャンク分が引き継がれて
        // 残り3チャンクで終了してしまうところを、正しくリセットされていれば5チャンク必要になる
        for (int i = 0; i < 4; i++)
        {
            chunkIndex++;
            result = segmenter.ProcessChunk(SilenceChunk(), ChunkSamples, TimeAfterChunks(chunkIndex));
            Assert.Null(result); // まだ5チャンクに満たない
        }
        chunkIndex++;
        result = segmenter.ProcessChunk(SilenceChunk(), ChunkSamples, TimeAfterChunks(chunkIndex));

        Assert.NotNull(result);
        // 発話(2) + 息継ぎ(2) + 発話(2) + 終了までの無音(5) = 11チャンク、全て1つの区間に含まれる
        Assert.Equal(11 * ChunkSamples, result!.Samples.Count);
    }

    [Fact]
    public void ヒステリシス閾値を上回る中程度の音量は発話開始直後なら継続とみなされる()
    {
        // EnergyThreshold=0.3, HysteresisRatio=0.5 → 発話継続の閾値は0.15
        var segmenter = CreateSegmenter(
            silenceChunksToEndSpeech: 3, minSpeechChunks: 2, prerollChunks: 0,
            energyThreshold: 0.3f, hysteresisRatio: 0.5f);
        int chunkIndex = 0;

        // 発話開始(0.3を超える0.5)
        chunkIndex++;
        segmenter.ProcessChunk(LoudChunk(0.5f), ChunkSamples, TimeAfterChunks(chunkIndex));

        // 開始閾値0.3は下回るが、継続閾値0.15は上回る中音量(0.2) → 発話中とみなされ継続する
        chunkIndex++;
        var result = segmenter.ProcessChunk(LoudChunk(0.2f), ChunkSamples, TimeAfterChunks(chunkIndex));
        Assert.Null(result); // 終了もしていない

        // 続けて無音を3チャンク流すと、直前の中音量チャンクで無音カウントが0にリセットされていた
        // はずなので、ここから改めて3チャンク必要になる
        for (int i = 0; i < 2; i++)
        {
            chunkIndex++;
            result = segmenter.ProcessChunk(SilenceChunk(), ChunkSamples, TimeAfterChunks(chunkIndex));
            Assert.Null(result);
        }
        chunkIndex++;
        result = segmenter.ProcessChunk(SilenceChunk(), ChunkSamples, TimeAfterChunks(chunkIndex));

        Assert.NotNull(result);
        // 発話(1) + 中音量(1) + 終了までの無音(3) = 5チャンク
        Assert.Equal(5 * ChunkSamples, result!.Samples.Count);
    }

    [Fact]
    public void 同じ中程度の音量は発話開始の判定には使えない()
    {
        // 上のテストと同じ振幅(0.2)でも、それが「最初の」チャンクの場合は
        // 開始閾値0.3を下回るため、発話は始まらない(ヒステリシスは発話中にのみ適用される)。
        var segmenter = CreateSegmenter(energyThreshold: 0.3f, hysteresisRatio: 0.5f, prerollChunks: 0);

        var result = segmenter.ProcessChunk(LoudChunk(0.2f), ChunkSamples, TimeAfterChunks(1));

        Assert.Null(result);
        Assert.Null(segmenter.Flush(TimeAfterChunks(1))); // 発話が始まっていないので何も残らない
    }

    [Fact]
    public void 最大発話チャンク数に達すると強制分割されオーバーラップ分が次の区間へ引き継がれる()
    {
        // 無音による自然終了(silenceLongEnough)と強制分割(tooLong)は、発話が終わる
        // 直前のチャンクが「閾値を下回っている(=無音側の)チャンク」であっても正しく
        // 強制分割される(=無音長さが終了条件に満たない場合は強制分割が優先される)ことを確認する。
        var segmenter = CreateSegmenter(
            silenceChunksToEndSpeech: 3, minSpeechChunks: 1, maxSpeechChunks: 5,
            forcedSplitOverlapChunks: 2, prerollChunks: 0);
        int chunkIndex = 0;
        VadSegmentResult? result = null;

        // 発話4チャンク(まだ最大チャンク数5には達していない)
        for (int i = 0; i < 4; i++)
        {
            chunkIndex++;
            result = segmenter.ProcessChunk(LoudChunk(0.6f), ChunkSamples, TimeAfterChunks(chunkIndex));
            Assert.Null(result);
        }

        // 5チャンク目に、発話継続の閾値は下回るが無音終了(3チャンク)にはまだ満たない
        // 静かな1チャンクを挟む。これによりバッファが5チャンク分に達し、強制分割が発生する。
        chunkIndex++;
        result = segmenter.ProcessChunk(SilenceChunk(), ChunkSamples, TimeAfterChunks(chunkIndex));

        Assert.NotNull(result);
        Assert.Equal(5 * ChunkSamples, result!.Samples.Count);
        // プリロール無しなので、StartTimeは最初の発話チャンクを読み終えた時刻(1チャンク後)になる
        Assert.Equal(TimeAfterChunks(1), result.StartTime);
        Assert.Equal(TimeAfterChunks(5), result.EndTime);

        // 強制分割後も発話中とみなされ続けているはず(無音による自然終了ではないため)。
        // まだ無音カウントが終了条件(3チャンク)に満たない間は終了しないことを確認する
        for (int i = 0; i < 2; i++)
        {
            chunkIndex++;
            var midResult = segmenter.ProcessChunk(SilenceChunk(), ChunkSamples, TimeAfterChunks(chunkIndex));
            Assert.Null(midResult);
        }

        // 3チャンク目の無音(分割後で数えて3回目)でようやく発話が終了する
        chunkIndex++;
        result = segmenter.ProcessChunk(SilenceChunk(), ChunkSamples, TimeAfterChunks(chunkIndex));

        Assert.NotNull(result);
        // 引き継いだオーバーラップ分(2チャンク) + 終了までの無音3チャンク = 5チャンク
        Assert.Equal(5 * ChunkSamples, result!.Samples.Count);
        // 次区間の開始時刻は、分割時刻(TimeAfterChunks(5))からオーバーラップ2チャンク分だけ遡った時刻
        Assert.Equal(TimeAfterChunks(3), result.StartTime);
        Assert.Equal(TimeAfterChunks(8), result.EndTime);
    }

    [Fact]
    public void 一度も閾値を下回らない連続した発話でも最大チャンク数で強制分割される()
    {
        // 修正前は、tooLongの判定が「閾値を下回ったチャンクを処理した時」にしか行われず、
        // ゲーム音声のように常にRMSが高いまま続く(=一度も閾値を下回らない)ケースでは
        // 強制分割が永久に発動しない問題があった。この回帰を防ぐためのテスト。
        var segmenter = CreateSegmenter(
            silenceChunksToEndSpeech: 3, minSpeechChunks: 1, maxSpeechChunks: 5,
            forcedSplitOverlapChunks: 2, prerollChunks: 0);
        int chunkIndex = 0;
        VadSegmentResult? result = null;

        // 閾値を一度も下回らない、5チャンク連続の発話
        for (int i = 0; i < 5; i++)
        {
            chunkIndex++;
            result = segmenter.ProcessChunk(LoudChunk(0.6f), ChunkSamples, TimeAfterChunks(chunkIndex));
            if (i < 4) Assert.Null(result);
        }

        // 5チャンク目(閾値を上回ったまま)で強制分割が発動するはず
        Assert.NotNull(result);
        Assert.Equal(5 * ChunkSamples, result!.Samples.Count);
        Assert.Equal(TimeAfterChunks(1), result.StartTime);
        Assert.Equal(TimeAfterChunks(5), result.EndTime);
    }

    [Fact]
    public void Flushは発話継続中でも十分な長さがあれば最後の区間として返す()
    {
        // 録音停止時、無音による自然終了を待たずに呼ばれるのがFlush。
        var segmenter = CreateSegmenter(minSpeechChunks: 2, prerollChunks: 0);
        int chunkIndex = 0;

        for (int i = 0; i < 3; i++)
        {
            chunkIndex++;
            var r = segmenter.ProcessChunk(LoudChunk(0.6f), ChunkSamples, TimeAfterChunks(chunkIndex));
            Assert.Null(r); // 無音による終了はまだ起きていない
        }

        var flushed = segmenter.Flush(TimeAfterChunks(chunkIndex));

        Assert.NotNull(flushed);
        Assert.Equal(3 * ChunkSamples, flushed!.Samples.Count);
        // プリロール無しなので、StartTimeは最初のチャンクを読み終えた時刻(=1チャンク分後)になる
        // (currentAudioTimeは「そのチャンクを読み終えた時点」の時刻であり、チャンク開始時刻ではないため)
        Assert.Equal(TimeAfterChunks(1), flushed.StartTime);
        Assert.Equal(TimeAfterChunks(3), flushed.EndTime);
    }

    [Fact]
    public void Flushは最小発話チャンク数未満ならnullを返す()
    {
        var segmenter = CreateSegmenter(minSpeechChunks: 5, prerollChunks: 0);

        segmenter.ProcessChunk(LoudChunk(0.6f), ChunkSamples, TimeAfterChunks(1));
        var flushed = segmenter.Flush(TimeAfterChunks(1));

        Assert.Null(flushed);
    }

    [Fact]
    public void プリロール分のサンプルは発話区間の先頭に含まれる()
    {
        var segmenter = CreateSegmenter(prerollChunks: 3, minSpeechChunks: 1, silenceChunksToEndSpeech: 2);
        int chunkIndex = 0;
        VadSegmentResult? result = null;

        // プリロール上限(3)を超える4チャンクの無音を先に流す(先頭の1チャンクは
        // 押し出されてプリロールバッファに残らないはず)
        for (int i = 0; i < 4; i++)
        {
            chunkIndex++;
            segmenter.ProcessChunk(SilenceChunk(), ChunkSamples, TimeAfterChunks(chunkIndex));
        }

        chunkIndex++;
        segmenter.ProcessChunk(LoudChunk(0.6f), ChunkSamples, TimeAfterChunks(chunkIndex)); // 発話開始(5チャンク目)

        for (int i = 0; i < 2; i++)
        {
            chunkIndex++;
            result = segmenter.ProcessChunk(SilenceChunk(), ChunkSamples, TimeAfterChunks(chunkIndex));
        }

        Assert.NotNull(result);
        // プリロール(直近3チャンクぶんのみ。4チャンク流したうち最初の1つは押し出されている) +
        // 発話(1) + 終了までの無音(2) = 6チャンク
        Assert.Equal(6 * ChunkSamples, result!.Samples.Count);
        // 発話が実際に始まったのは5チャンク目の直後(TimeAfterChunks(5))だが、
        // プリロール3チャンク分遡るのでStartTimeはTimeAfterChunks(2)になる
        Assert.Equal(TimeAfterChunks(2), result.StartTime);
    }
}
