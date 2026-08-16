using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// VAD(発話区間検出)のステートマシン。
///
/// 以前はAudioPipeline.RunAsync内に直接書かれていたロジックで、キャプチャ/リサンプリング/
/// Channel/Whisper呼び出しなど他の責務と混在していたため、動作を変えずにここへ切り出した。
/// このクラスはNAudio/Channel/Whisperのいずれにも依存せず、「音声チャンクを1つ渡すと、
/// 完成した発話区間があれば返す」という単純な入出力になっているため、単体テストしやすく、
/// VADアルゴリズムの実装だけを差し替えられる設計になっている。
///
/// 【発話らしさの判定方法(2種類)】
/// - SileroVadDetectorが渡されている場合: ONNXニューラルモデル(Silero VAD)による
///   発話確率(0.0〜1.0)を使う。BGM/ゲーム効果音のような「音量は大きいが声道特有の
///   周波数特性を持たない音」を人声と誤検出しにくいのが利点。
/// - 渡されていない場合(モデルファイルが見つからない等でロードに失敗した場合の
///   フォールバック): 従来のRMS(音量)ベースの判定を使う。
/// どちらの場合も、EnergyThreshold/HysteresisRatioという同じ2つのパラメータで
/// 「発話開始/継続の判定しきい値」を制御する(スケールが異なる点に注意。
/// Sileroは0〜1の確率、RMSは概ね0.001〜0.05程度の実効値)。
/// </summary>
public sealed class VoiceActivitySegmenter
{
    private readonly int _sampleRate;
    private readonly int _chunkSamples;
    private readonly int _silenceChunksToEndSpeech;
    private readonly int _minSpeechChunks;
    private readonly int _maxSpeechChunks;
    private readonly int _prerollChunks;
    private readonly int _forcedSplitOverlapChunks;
    private readonly SileroVadDetector? _sileroDetector;

    /// <summary>発話「開始」の判定に使う閾値。SileroVadDetector使用時は発話確率(0〜1)、
    /// 未使用時(RMSフォールバック)はRMS実効値(概ね0.001〜0.05)として解釈される。</summary>
    public float EnergyThreshold { get; set; } = 0.5f;

    /// <summary>
    /// ヒステリシス比率(0〜1)。発話「開始」の判定にはEnergyThresholdをそのまま使うが、
    /// 一度発話が始まった後は EnergyThreshold × HysteresisRatio という、より低い閾値で
    /// 「まだ発話が続いている」とみなす。息継ぎ・語尾の減衰などで発話らしさスコアが一時的に
    /// 下がっても、そこで発話が終わったと誤判定してセグメントが分断されるのを防ぐ。
    /// </summary>
    public float HysteresisRatio { get; set; } = 0.6f;

    private readonly List<float> _speechBuffer = new();
    private readonly Queue<float[]> _prerollBuffer = new();
    private int _silenceChunkCount;
    private bool _inSpeech;
    private TimeSpan _segmentStartTime;

    /// <param name="sileroDetector">Silero VADによる判定を使う場合に渡す。nullの場合は
    /// 従来のRMSベース判定にフォールバックする(モデルファイルが見つからない場合等)。
    /// 呼び出し側がセッションをまたいで使い回す想定のため、Resetは呼び出し側の責任で行う
    /// (VoiceActivitySegmenterのコンストラクタでは呼ばない)。</param>
    public VoiceActivitySegmenter(
        int sampleRate,
        int chunkSamples,
        int silenceChunksToEndSpeech,
        int minSpeechChunks,
        int maxSpeechChunks,
        int prerollChunks,
        int forcedSplitOverlapChunks,
        SileroVadDetector? sileroDetector = null)
    {
        _sampleRate = sampleRate;
        _chunkSamples = chunkSamples;
        _silenceChunksToEndSpeech = silenceChunksToEndSpeech;
        _minSpeechChunks = minSpeechChunks;
        _maxSpeechChunks = maxSpeechChunks;
        _prerollChunks = prerollChunks;
        _forcedSplitOverlapChunks = forcedSplitOverlapChunks;
        _sileroDetector = sileroDetector;
    }

    /// <summary>
    /// 音声チャンクを1つ処理する。このチャンクの結果として発話区間が完成した場合
    /// (無音による自然な終了、または15秒による強制分割)はその内容を返す。まだ発話が
    /// 継続中/未開始の場合はnullを返す。
    /// </summary>
    /// <param name="chunk">読み出したサンプル(先頭countぶんが有効)</param>
    /// <param name="count">有効なサンプル数</param>
    /// <param name="currentAudioTime">このチャンクを読み終えた時点での音声内時刻
    /// (呼び出し側が実サンプル数から算出したもの。壁時計ではない)</param>
    public VadSegmentResult? ProcessChunk(float[] chunk, int count, TimeSpan currentAudioTime)
    {
        // Sileroが使える場合はニューラルモデルによる発話確率、そうでなければ従来のRMSを使う。
        // どちらも「値が大きいほど発話らしい」という同じ向きのスコアなので、
        // 以降のヒステリシス・状態遷移ロジックは完全に共通化できる。
        float speechScore = _sileroDetector != null
            ? _sileroDetector.GetSpeechProbability(chunk, count)
            : ComputeRms(chunk, count);

        // ヒステリシス: 発話中でない時は開始閾値、発話中は継続閾値(より低い)で判定する。
        // 同じ閾値を使い回すと、閾値ギリギリのスコアが続く区間でisSpeechChunkがtrue/falseを
        // 細かく往復し、無音カウントが0にリセットされたり、逆に短時間で無音判定が
        // 成立してセグメントが分断されたりしやすい。
        float activeThreshold = _inSpeech ? EnergyThreshold * HysteresisRatio : EnergyThreshold;
        bool isSpeechChunk = speechScore > activeThreshold;

        VadSegmentResult? result = null;

        if (isSpeechChunk)
        {
            if (!_inSpeech)
            {
                _inSpeech = true;
                _speechBuffer.Clear();

                // 発話開始時刻は「今」ではなく、先頭に付与するプリロール分だけ
                // 遡った時刻になる(プリロールも実際にはその時刻に鳴っていた音声のため)
                long prerollSamples = _prerollBuffer.Sum(c => (long)c.Length);
                _segmentStartTime = currentAudioTime - TimeSpan.FromSeconds((double)prerollSamples / _sampleRate);
                if (_segmentStartTime < TimeSpan.Zero) _segmentStartTime = TimeSpan.Zero;

                // 発話開始の瞬間、直前まで無音だと思って捨てていた分(プリロール)を
                // 先頭に付与することで、語頭の欠落を防ぐ
                foreach (var c in _prerollBuffer)
                {
                    _speechBuffer.AddRange(c);
                }
            }
            _speechBuffer.AddRange(chunk.Take(count));
            _silenceChunkCount = 0;
        }
        else if (_inSpeech)
        {
            _speechBuffer.AddRange(chunk.Take(count));
            _silenceChunkCount++;

            bool silenceLongEnough = _silenceChunkCount >= _silenceChunksToEndSpeech;
            bool tooLong = _speechBuffer.Count / _chunkSamples >= _maxSpeechChunks;

            if (silenceLongEnough)
            {
                // 無音による自然な発話終了。プリロールと同様、この後は非発話状態に戻る
                _inSpeech = false;
                if (_speechBuffer.Count / _chunkSamples >= _minSpeechChunks)
                {
                    result = new VadSegmentResult(new List<float>(_speechBuffer), _segmentStartTime, currentAudioTime);
                }
                _speechBuffer.Clear();
            }
            else if (tooLong)
            {
                // 15秒の強制分割。無音を検出したわけではなく、発話はまだ続いている可能性が高いため
                // _inSpeechはtrueのまま維持し、直前の音声の末尾を少しだけ次のセグメントへ引き継ぐ。
                // これにより、分割位置をまたぐ文でWhisperが直前の文脈(語尾)を完全に失うのを防ぐ
                var splitEndTime = currentAudioTime;
                result = new VadSegmentResult(new List<float>(_speechBuffer), _segmentStartTime, splitEndTime);

                int overlapSamples = Math.Min(_forcedSplitOverlapChunks * _chunkSamples, _speechBuffer.Count);
                var carryOver = _speechBuffer.GetRange(_speechBuffer.Count - overlapSamples, overlapSamples);
                _speechBuffer.Clear();
                _speechBuffer.AddRange(carryOver);
                _silenceChunkCount = 0;

                // 次のセグメントの開始時刻は、引き継いだ分だけ現在より少し前になる
                _segmentStartTime = splitEndTime - TimeSpan.FromSeconds((double)overlapSamples / _sampleRate);
            }
        }

        // 発話中でない間も、直近のチャンクを常にプリロール用バッファに保持しておく
        if (!_inSpeech)
        {
            _prerollBuffer.Enqueue(chunk.Take(count).ToArray());
            while (_prerollBuffer.Count > _prerollChunks)
            {
                _prerollBuffer.Dequeue();
            }
        }

        return result;
    }

    /// <summary>
    /// 録音停止時に呼ぶ。話している最中に停止した場合、まだ無音判定が確定する前の発話区間が
    /// バッファに残っている可能性がある。短すぎなければ、これも最後の1区間として返す
    /// (ここで返さないと、話している最中に停止した最後の発話が丸ごと消える)。
    /// </summary>
    public VadSegmentResult? Flush(TimeSpan currentAudioTime)
    {
        if (_speechBuffer.Count / _chunkSamples >= _minSpeechChunks)
        {
            return new VadSegmentResult(new List<float>(_speechBuffer), _segmentStartTime, currentAudioTime);
        }
        return null;
    }

    /// <summary>
    /// マイク/オーディオデバイス固有の直流成分(DCオフセット)を取り除いてからRMSを計算する。
    /// DCオフセットが乗っていると、無音のはずの区間でもRMSが下がりきらず、VADが
    /// 「ずっと発話中」と誤判定し続けることがあるため、平均値を差し引いてから実効値を求める。
    /// (VAD判定にのみ使用し、Whisperに渡す音声データ自体は元のサンプルのまま加工しない)
    /// </summary>
    private static float ComputeRms(float[] buffer, int count)
    {
        double sum = 0;
        for (int i = 0; i < count; i++) sum += buffer[i];
        double mean = sum / count;

        double sumSquares = 0;
        for (int i = 0; i < count; i++)
        {
            double centered = buffer[i] - mean;
            sumSquares += centered * centered;
        }
        return (float)Math.Sqrt(sumSquares / count);
    }
}

/// <summary>VoiceActivitySegmenterが検出した1つの発話区間。ID割当やキュー投入といった
/// パイプライン側の責務とは無関係な、生のサンプルデータと時刻のみを保持する。</summary>
public sealed record VadSegmentResult(List<float> Samples, TimeSpan StartTime, TimeSpan EndTime);
