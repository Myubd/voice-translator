using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

/// <summary>
/// Silero VAD(https://github.com/snakers4/silero-vad)のONNXモデルをラップし、
/// 音声チャンクを渡すと「発話らしさ」を0.0〜1.0の確率として返すクラス。
///
/// 【モデルの入出力仕様(公式C++サンプルで確認済み)】
///   入力: input(float32[1, N]) … N=512(32ms@16kHz)+64(前チャンク末尾=文脈)=576サンプルが
///         公式に検証されている想定サイズ。sr(int64スカラー、サンプルレート)。
///         state(float32[2,1,128]) … GRU内部状態(前回呼び出しの出力をそのまま次回の入力に使う)。
///   出力: output(float32[1,1]) … 発話確率。stateN(float32[2,1,128]) … 次回に渡す内部状態。
///
/// 【このアプリでの窓合わせについて(公式実装との違い)】
/// 公式実装は「512サンプルごとに区切って、区切りの先頭に前回チャンク末尾64サンプルを
/// 文脈として付与する」という非重複ウィンドウを前提にしている。
/// 一方このアプリの音声取得ループは30ms(480サンプル)周期で動いており、512サンプル単位とは
/// 端数が合わない。そのため、ここでは「直近576サンプルの連続スライディングウィンドウ」を
/// 毎回の呼び出しで評価する方式を採っている。GRUの内部状態(state)は呼び出しのたびに
/// 継続して更新するため、公式の32ms周期より短い間隔(30ms)で状態が進むことになるが、
/// Silero VAD自体は連続音声にロバストな設計であり、実用上の精度に大きな影響は無い
/// (完全に公式のI/Oパターンと一致させたい場合は、512サンプル単位のバッファリングに
/// 変更する余地がある)。
/// </summary>
public sealed class SileroVadDetector : IDisposable
{
    // 公式実装で検証されている、16kHz時の1ウィンドウあたりのサンプル数(32ms)
    private const int WindowSamples = 512;
    // 前チャンクから引き継ぐ「文脈」サンプル数。モデルの精度確保のため公式仕様に合わせている
    private const int ContextSamples = 64;
    private const int EffectiveWindowSamples = WindowSamples + ContextSamples;
    private const int SampleRate = 16000;
    private const int StateSize = 2 * 1 * 128;

    private readonly InferenceSession _session;

    // 直近576サンプルを保持するスライディングバッファ(先頭が古い)
    private readonly float[] _slidingWindow = new float[EffectiveWindowSamples];
    private int _slidingWindowFilled = 0;

    private float[] _state = new float[StateSize];
    private readonly long[] _srInput = { SampleRate };

    public SileroVadDetector(string modelPath)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"Silero VADモデルファイルが見つかりません: {modelPath}", modelPath);
        }

        var options = new SessionOptions
        {
            // VAD推論はごく軽量(1回1ms未満)なため、Whisper推論用のCPUコアを奪い合わないよう
            // スレッド数を絞る(既定のまま複数スレッドを使わせると、Whisper側のスループットに
            // 悪影響が出ることがある)
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        _session = new InferenceSession(modelPath, options);
        Reset();
    }

    /// <summary>新しいセッション(録音開始)のたびに呼び出し、内部状態をリセットする。
    /// 前回セッションの発話状態を引き継いでしまうと、セッション開始直後の判定が
    /// 不自然になる可能性があるため。</summary>
    public void Reset()
    {
        Array.Clear(_slidingWindow, 0, _slidingWindow.Length);
        _slidingWindowFilled = 0;
        _state = new float[StateSize];
    }

    /// <summary>
    /// 音声チャンクを1つ処理し、発話確率(0.0〜1.0)を返す。
    /// countがWindowSamplesと異なっていても動作するが、精度は公式想定のチャンクサイズ(30ms程度)に
    /// 近いほど良い。
    /// </summary>
    public float GetSpeechProbability(float[] chunk, int count)
    {
        // スライディングウィンドウを1チャンク分だけ左にシフトし、末尾に新しいサンプルを追加する。
        // (countがEffectiveWindowSamplesより大きいことは通常無い想定だが、防御的に切り詰める)
        int n = Math.Min(count, EffectiveWindowSamples);
        if (n < count)
        {
            // 通常起こらないが、万一チャンクサイズがウィンドウより大きい場合は末尾n件のみ採用する
            int skip = count - n;
            ShiftAndAppend(chunk, skip, n);
        }
        else
        {
            ShiftAndAppend(chunk, 0, n);
        }
        _slidingWindowFilled = Math.Min(_slidingWindowFilled + n, EffectiveWindowSamples);

        // ウィンドウがまだ埋まりきっていない(セッション開始直後の数チャンク)場合は、
        // 無音として扱う(モデルに全ゼロに近い入力を渡しても実害は無く、誤発火よりは安全側)
        if (_slidingWindowFilled < EffectiveWindowSamples)
        {
            return 0f;
        }

        var inputTensor = new DenseTensor<float>(_slidingWindow, new[] { 1, EffectiveWindowSamples });
        var stateTensor = new DenseTensor<float>(_state, new[] { 2, 1, 128 });
        var srTensor = new DenseTensor<long>(_srInput, new[] { 1 });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("sr", srTensor),
            NamedOnnxValue.CreateFromTensor("state", stateTensor),
        };

        using var results = _session.Run(inputs, new[] { "output", "stateN" });
        // 出力の並び順に依存しないよう、名前で明示的に取得する
        // (session.Runは通常渡した名前の順で返すが、ここでは念のため厳密に照合する)
        float probability = results.First(v => v.Name == "output").AsTensor<float>().GetValue(0);
        var newState = results.First(v => v.Name == "stateN").AsTensor<float>();
        newState.ToArray().CopyTo(_state, 0);

        return probability;
    }

    private void ShiftAndAppend(float[] source, int sourceOffset, int count)
    {
        if (count >= EffectiveWindowSamples)
        {
            // 1チャンクだけでウィンドウ全体を上書きできる場合(通常は起こらない)
            Array.Copy(source, sourceOffset + count - EffectiveWindowSamples, _slidingWindow, 0, EffectiveWindowSamples);
            return;
        }

        int keep = EffectiveWindowSamples - count;
        Array.Copy(_slidingWindow, count, _slidingWindow, 0, keep);
        Array.Copy(source, sourceOffset, _slidingWindow, keep, count);
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
