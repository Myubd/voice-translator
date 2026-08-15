using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

/// <summary>
/// 音声キャプチャ→VAD→文字起こし→翻訳、という一連の処理を担当するクラス。
/// UI(WPF)には依存せず、結果はイベントで通知する。
/// </summary>
public class AudioPipeline
{
    // ==== VAD(発話区間検出)まわりのパラメータ ====
    const int SampleRate = 16000;
    const int ChunkSamples = 480;            // 16kHzで30ms分
    const int SilenceChunksToEndSpeech = 14; // 約420ms無音が続いたら発話終了とみなす(遅延短縮のため500ms→420ms)
    const int MinSpeechChunks = 15;          // 約450ms未満の短い音は雑音として破棄(Yes/No等の短い発話も拾えるよう750ms→450msに短縮)
    const int MaxSpeechChunks = 500;         // 約15秒で強制的に区切る
    const int PrerollChunks = 13;            // 発話開始前の約390msを先頭に付与し、頭切れを防ぐ

    // 15秒の強制分割が発生した際、次のセグメントの先頭に引き継ぐ音声の長さ(約300ms)。
    // 何も引き継がないと、分割位置で文がぶつ切りになり、Whisperが文脈(直前の語尾)を
    // 失ったまま次のセグメントの認識を始めることになるため、少しだけ重複させて渡す
    const int ForcedSplitOverlapChunks = 10;

    // 音声セグメント用チャンネルの最大件数。DropOldestで溢れた分の検出にも使う
    const int SegmentChannelCapacity = 5;

    public float EnergyThreshold { get; set; } = 0.015f;

    /// <summary>
    /// ヒステリシス比率(0〜1)。発話「開始」の判定にはEnergyThresholdをそのまま使うが、
    /// 一度発話が始まった後は EnergyThreshold × HysteresisRatio という、より低い閾値で
    /// 「まだ発話が続いている」とみなす。
    /// これにより、閾値ギリギリの音量(息継ぎ・語尾の減衰など)で発話中に短時間だけRMSが
    /// 下がった場合でも、そこで発話が終わったと誤判定してセグメントが分断されるのを防げる。
    /// 値を下げるほど、一度始まった発話は多少音量が下がっても継続扱いされやすくなる
    /// (=無音判定はより大きな音量低下があった時だけ働く)。
    /// 1.0にすると従来と同じ単一閾値の挙動になる。
    /// </summary>
    public float HysteresisRatio { get; set; } = 0.6f;

    public event Action<string>? OriginalTextReceived;
    public event Action<string>? TranslatedTextReceived;
    public event Action<string>? StatusChanged;

    /// <summary>翻訳API呼び出しが失敗した際に通知される(APIキー誤り、レート制限、ネットワーク断など)。
    /// 従来はConsole.WriteLineのみで、配布したWPFアプリではユーザーから見えなかった。</summary>
    public event Action<string>? TranslationErrorOccurred;

    /// <summary>処理が追いつかず、音声セグメントがキューから古い順に破棄された際に通知される
    /// (破棄された累計件数を渡す)。従来はDropOldestで静かに捨てるだけで、ユーザーからは
    /// 「なぜか一部の発話が翻訳されない」としか見えなかった。</summary>
    public event Action<int>? SegmentsDropped;

    private int _droppedSegmentCount = 0;

    private WhisperProcessor? _processor;
    private ITranslationService? _translationService;

    private readonly object _dedupLock = new object();
    private string? _lastGlobalText;
    private DateTime _lastGlobalTime = DateTime.MinValue;

    /// <summary>利用可能な出力(ループバック対象)デバイスの一覧を、OS上で一意なIDと表示名のペアで取得する</summary>
    public static List<AudioDeviceInfo> GetAvailableDevices()
    {
        var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
            .ToList();
    }

    public void ConfigureTranslation(ITranslationService? service)
    {
        _translationService = service;
    }

    public async Task RunAsync(string deviceId, string deviceKeyword, string modelPath, string whisperPrompt, string recognitionLanguage, CancellationToken cancellationToken)
    {
        var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

        // OS上で一意なDevice IDが保存されていればそれを最優先で使う(同名デバイスの誤選択を防ぐ)。
        // ID未設定、またはデバイス構成が変わってIDが見つからない場合のみ、従来どおり名前の部分一致にフォールバックする。
        MMDevice? target = null;
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            target = devices.FirstOrDefault(d => d.ID == deviceId);
        }
        if (target == null && !string.IsNullOrWhiteSpace(deviceKeyword))
        {
            target = devices.FirstOrDefault(
                d => d.FriendlyName.Contains(deviceKeyword, StringComparison.OrdinalIgnoreCase));
        }

        if (target == null)
        {
            StatusChanged?.Invoke($"'{deviceKeyword}' を含むデバイスが見つかりませんでした。設定画面でデバイスを選び直してください。");
            return;
        }

        // exeがどのディレクトリから起動されても見つかるよう、相対パスは実行ファイルの場所を基準に解決する
        string resolvedModelPath = Path.IsPathRooted(modelPath)
            ? modelPath
            : Path.Combine(AppContext.BaseDirectory, modelPath);

        if (!File.Exists(resolvedModelPath))
        {
            StatusChanged?.Invoke($"モデルファイルが見つかりません: {resolvedModelPath}");
            return;
        }

        using var whisperFactory = WhisperFactory.FromPath(resolvedModelPath);
        var processorBuilder = whisperFactory.CreateBuilder()
            .WithLanguage(string.IsNullOrWhiteSpace(recognitionLanguage) ? "auto" : recognitionLanguage)
            .WithThreads(Math.Max(2, Environment.ProcessorCount / 2)); // CPUコア数に応じてスレッド数を明示指定

        if (!string.IsNullOrWhiteSpace(whisperPrompt))
        {
            // 固有名詞などのヒントをWhisperに渡し、認識精度の向上を狙う
            processorBuilder = processorBuilder.WithPrompt(whisperPrompt);
        }

        var processor = processorBuilder.Build();
        _processor = processor;

        // processorBuild()後、この中で例外が発生した場合にWhisperProcessorが破棄されないまま
        // 残ってしまうのを防ぐため、以降の処理全体を try/catch で囲む
        try
        {
            using var capture = new WasapiLoopbackCapture(target);
            var bufferedProvider = new BufferedWaveProvider(capture.WaveFormat)
            {
                BufferLength = capture.WaveFormat.AverageBytesPerSecond * 5,
                DiscardOnBufferOverflow = true
            };

            ISampleProvider sampleProvider = bufferedProvider.ToSampleProvider();
            if (capture.WaveFormat.Channels == 2)
            {
                sampleProvider = new StereoToMonoSampleProvider(sampleProvider)
                {
                    LeftVolume = 0.5f,
                    RightVolume = 0.5f
                };
            }
            var resampler = new WdlResamplingSampleProvider(sampleProvider, SampleRate);

            capture.DataAvailable += (s, e) =>
            {
                bufferedProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };

            // デバイスが他プロセス(Sonar等)と一時的に競合してエラーになることがあるため、
            // 数回リトライしてから諦める(Sonarを手動再起動しなくても自然に復帰することが多い)
            const int maxRetries = 5;
            bool captureStarted = false;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    capture.StartRecording();
                    captureStarted = true;
                    break;
                }
                catch (System.Runtime.InteropServices.COMException) when (attempt < maxRetries)
                {
                    StatusChanged?.Invoke($"デバイスが使用中のためリトライ中... ({attempt}/{maxRetries})");
                    await Task.Delay(1000, cancellationToken);
                }
            }

            // 最終試行まで失敗した場合、ここで明示的に打ち切る。
            // これを入れないと、録音が始まっていないまま後続のVADループへ進んでしまう
            // (無音がずっと続くだけに見え、原因が分かりにくくなる)
            if (!captureStarted)
            {
                StatusChanged?.Invoke("音声キャプチャを開始できませんでした。デバイスが他アプリで使用中の可能性があります。");
                await processor.DisposeAsync();
                _processor = null;
                return;
            }

            // 翻訳サービスの準備処理(Ollama使用時、参考コンテキストからの用語集抽出など)を先に済ませておく
            if (_translationService != null)
            {
                StatusChanged?.Invoke("翻訳の準備中(用語集を抽出しています)...");
                await _translationService.PrepareAsync();
            }

            StatusChanged?.Invoke($"認識中: {target.FriendlyName}");

            // Whisper処理を1本のキューで順番に処理するためのチャンネル。
            // これにより、発話が連続しても複数のWhisper推論が同時実行されてリソースを
            // 奪い合うことがなくなる(翻訳は引き続き並行実行して問題ない)。
            // 発話が連続すると処理待ちが溜まっていくため、キューに上限を設ける。
            // 上限を超えたら「一番古い(=もう鮮度が落ちている)」セグメントを捨てて、
            // 遅延がどこまでも蓄積し続けるのを防ぐ
            var segmentChannel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(SegmentChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });
            _droppedSegmentCount = 0;

            // Whisperの文字起こし結果を翻訳ワーカーへ渡すための第2キュー。
            // これによりWhisper自体は翻訳の完了を待たずに次のセグメントへ進める
            // (翻訳が遅い/詰まっても、音声認識側の処理は止まらない)。
            // 文字列のみを保持する軽量なキューなので上限は設けず、Whisper側のDropOldestで
            // 全体の遅延蓄積を防ぐ設計に任せる。
            var transcriptChannel = Channel.CreateUnbounded<string>();

            var whisperWorkerTask = RunWhisperWorkerAsync(segmentChannel.Reader, transcriptChannel.Writer);
            var translationWorkerTask = RunTranslationWorkerAsync(transcriptChannel.Reader);

            var readBuffer = new float[ChunkSamples];
            var speechBuffer = new List<float>();
            var prerollBuffer = new Queue<float[]>();
            int silenceChunkCount = 0;
            bool inSpeech = false;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    while (bufferedProvider.BufferedDuration.TotalMilliseconds < 40)
                    {
                        await Task.Delay(5, cancellationToken);
                    }

                    int read = resampler.Read(readBuffer, 0, ChunkSamples);
                    if (read == 0)
                    {
                        await Task.Delay(10, cancellationToken);
                        continue;
                    }

                    float rms = ComputeRms(readBuffer, read);

                    // ヒステリシス: 発話中でない時は開始閾値(EnergyThreshold)、
                    // 発話中は継続閾値(EnergyThreshold×HysteresisRatio、より低い)で判定する。
                    // 同じ閾値を使い回すと、閾値ギリギリの音量が続く区間(息継ぎ等)で
                    // isSpeechChunkがtrue/falseを細かく往復し、無音カウントが0にリセットされたり
                    // 逆に短時間で無音判定が成立してセグメントが分断されたりしやすい。
                    float activeThreshold = inSpeech ? EnergyThreshold * HysteresisRatio : EnergyThreshold;
                    bool isSpeechChunk = rms > activeThreshold;

                    if (isSpeechChunk)
                    {
                        if (!inSpeech)
                        {
                            inSpeech = true;
                            speechBuffer.Clear();
                            // 発話開始の瞬間、直前まで無音だと思って捨てていた分(プリロール)を
                            // 先頭に付与することで、語頭の欠落を防ぐ
                            foreach (var chunk in prerollBuffer)
                            {
                                speechBuffer.AddRange(chunk);
                            }
                        }
                        speechBuffer.AddRange(readBuffer.Take(read));
                        silenceChunkCount = 0;
                    }
                    else if (inSpeech)
                    {
                        speechBuffer.AddRange(readBuffer.Take(read));
                        silenceChunkCount++;

                        bool silenceLongEnough = silenceChunkCount >= SilenceChunksToEndSpeech;
                        bool tooLong = speechBuffer.Count / ChunkSamples >= MaxSpeechChunks;

                        if (silenceLongEnough)
                        {
                            // 無音による自然な発話終了。プリロールと同様、この後は非発話状態に戻る
                            inSpeech = false;
                            if (speechBuffer.Count / ChunkSamples >= MinSpeechChunks)
                            {
                                WriteSegment(segmentChannel.Writer, segmentChannel.Reader, speechBuffer.ToArray());
                            }
                            speechBuffer.Clear();
                        }
                        else if (tooLong)
                        {
                            // 15秒の強制分割。無音を検出したわけではなく、発話はまだ続いている可能性が高いため
                            // inSpeechはtrueのまま維持し、直前の音声の末尾を少しだけ次のセグメントへ引き継ぐ。
                            // これにより、分割位置をまたぐ文でWhisperが直前の文脈(語尾)を完全に失うのを防ぐ
                            WriteSegment(segmentChannel.Writer, segmentChannel.Reader, speechBuffer.ToArray());

                            int overlapSamples = Math.Min(ForcedSplitOverlapChunks * ChunkSamples, speechBuffer.Count);
                            var carryOver = speechBuffer.GetRange(speechBuffer.Count - overlapSamples, overlapSamples);
                            speechBuffer.Clear();
                            speechBuffer.AddRange(carryOver);
                            silenceChunkCount = 0;
                        }
                    }

                    // 発話中でない間も、直近のチャンクを常にプリロール用バッファに保持しておく
                    if (!inSpeech)
                    {
                        prerollBuffer.Enqueue(readBuffer.Take(read).ToArray());
                        while (prerollBuffer.Count > PrerollChunks)
                        {
                            prerollBuffer.Dequeue();
                        }
                    }
                }
            }
            finally
            {
                capture.StopRecording();

                // 停止した瞬間、まだ発話の途中(無音判定が確定する前)だった分がspeechBufferに
                // 残っている可能性がある。短すぎなければ、これも最後の1区間として送っておく。
                // ここで送らないと、話している最中に停止した最後の発話が丸ごと消える。
                if (speechBuffer.Count / ChunkSamples >= MinSpeechChunks)
                {
                    WriteSegment(segmentChannel.Writer, segmentChannel.Reader, speechBuffer.ToArray());
                }

                // キューへの書き込みを締め切る。
                // RunWhisperWorkerAsyncはCancellationToken.Noneで読んでいるため、ここでCompleteすれば
                // 既にキューに積まれている(まだ処理していない)分もキャンセルされずに最後まで処理される。
                segmentChannel.Writer.Complete();
                try
                {
                    await whisperWorkerTask;
                }
                catch (Exception ex)
                {
                    // ワーカー内の個別例外は既にRunWhisperWorkerAsync側でログ済みだが、
                    // ここに来る場合はワーカー自体の異常終了なので念のため記録する
                    Logger.Log("AudioPipeline.Whisper", "Whisperワーカーの終了待機中に例外が発生しました。", ex);
                }

                // Whisper側が終わったら文字起こし結果のキューも締め切り、翻訳ワーカーの完了を待つ
                transcriptChannel.Writer.Complete();
                try
                {
                    await translationWorkerTask;
                }
                catch (Exception ex)
                {
                    Logger.Log("AudioPipeline.Translation", "翻訳ワーカーの終了待機中に例外が発生しました。", ex);
                }

                await processor.DisposeAsync();
                _processor = null;

                StatusChanged?.Invoke("停止しました");
            }
        }
        catch
        {
            if (_processor != null)
            {
                await _processor.DisposeAsync();
                _processor = null;
            }
            throw;
        }
    }

    /// <summary>
    /// キューに積まれた音声区間を1本ずつ順番に文字起こしする。
    /// Whisperの推論を同時に複数走らせないことで、CPU/GPUリソースの奪い合いを防ぐ。
    /// 認識結果は翻訳を待たずtranscriptWriterへ渡すだけなので、翻訳が遅くてもここは詰まらない。
    /// </summary>
    /// <remarks>
    /// ReadAllAsyncには意図的にCancellationToken.Noneを渡している。
    /// 呼び出し元(RunAsync)はキャンセル時、segmentChannel.Writer.Complete()を呼んだ後にこのタスクをawaitするが、
    /// もしここで外側のcancellationTokenをそのまま渡すと、Complete()以前にキューへ積まれていた
    /// 未処理のセグメントごと即座にOperationCanceledExceptionで捨てられてしまう。
    /// Completeされたチャンネルはreader側で自然に列挙が終わるため、キャンセルトークンは不要。
    /// </remarks>
    private async Task RunWhisperWorkerAsync(ChannelReader<float[]> reader, ChannelWriter<string> transcriptWriter)
    {
        try
        {
            await foreach (var segment in reader.ReadAllAsync(CancellationToken.None))
            {
                try
                {
                    await TranscribeSegmentAsync(segment, transcriptWriter);
                }
                catch (Exception ex)
                {
                    // 1区間の失敗で全体を止めないよう処理は継続するが、原因調査ができるよう記録は残す
                    Logger.Log("AudioPipeline.Whisper", "1区間の文字起こし処理で例外が発生しました。この区間はスキップします。", ex);
                }
            }
        }
        finally
        {
            transcriptWriter.TryComplete();
        }
    }

    /// <summary>
    /// Whisperが書き出した文字起こし結果を1件ずつ順番に翻訳する。
    /// Whisperワーカーとは別タスクなので、翻訳API(DeepL/Ollama)が遅くても
    /// 音声認識自体はブロックされない。
    /// </summary>
    private async Task RunTranslationWorkerAsync(ChannelReader<string> reader)
    {
        await foreach (var text in reader.ReadAllAsync(CancellationToken.None))
        {
            if (_translationService == null) continue;

            var result = await _translationService.TranslateAsync(text);
            if (result.Text != null)
            {
                TranslatedTextReceived?.Invoke(result.Text);
            }
            else if (result.ErrorMessage != null)
            {
                // DeepL/Ollamaのエラーはこれまでコンソールに出すだけでUIに一切出ていなかった。
                // WPFアプリとして配布した場合、通常ユーザーはコンソールを見ないため、
                // 「なぜか訳文が出ない」状態のまま気づけなかった。ここでStatusへ通知する。
                TranslationErrorOccurred?.Invoke(result.ErrorMessage);
            }
        }
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

    /// <summary>
    /// 音声セグメントをキューへ書き込む。DropOldestモードのチャンネルはキューが満杯の場合
    /// 常に書き込みに成功する(内部で一番古い要素を破棄する)ため、TryWriteの戻り値だけでは
    /// 破棄が起きたかどうか分からない。ここでは書き込み前のキュー件数を見て破棄の発生を検知し、
    /// 累計件数をSegmentsDroppedイベントで通知する。
    /// </summary>
    private void WriteSegment(ChannelWriter<float[]> writer, ChannelReader<float[]> reader, float[] segment)
    {
        if (reader.Count >= SegmentChannelCapacity)
        {
            _droppedSegmentCount++;
            SegmentsDropped?.Invoke(_droppedSegmentCount);
        }
        writer.TryWrite(segment);
    }

    private async Task TranscribeSegmentAsync(float[] samples, ChannelWriter<string> transcriptWriter)
    {
        if (_processor == null) return;

        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(ms), new WaveFormat(SampleRate, 1)))
        {
            foreach (var sample in samples)
            {
                writer.WriteSample(sample);
            }
        }
        ms.Position = 0;

        string? lastText = null;
        await foreach (var result in _processor.ProcessAsync(ms))
        {
            var text = result.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Whisperが無音・非音声区間で出す典型的なハルシネーション
            // (例: [BLANK_AUDIO], [MUSIC], [Silence] など、文全体が角括弧/丸括弧で囲まれたタグ)を無視する
            if (Regex.IsMatch(text, @"^[\[\(].*[\]\)]$"))
            {
                continue;
            }

            if (text == lastText) continue;
            lastText = text;

            bool isDuplicate;
            lock (_dedupLock)
            {
                // 完全一致+10秒という従来の重複除去は、Whisperがセグメント境界付近で
                // 同じ文をほぼ即座に2回出力するケース(プリロールの重なり等)を狙ったものだが、
                // 窓が長すぎると「Yes.」のような短い発話が数秒後に本当にもう一度発言された
                // 場合まで誤って握りつぶしてしまう。ここでは狙い通りの直近重複だけを
                // 除去できるよう、窓を3秒に短縮する
                isDuplicate = text == _lastGlobalText && (DateTime.Now - _lastGlobalTime) < TimeSpan.FromSeconds(3);
                if (!isDuplicate)
                {
                    _lastGlobalText = text;
                    _lastGlobalTime = DateTime.Now;
                }
            }
            if (isDuplicate) continue;

            OriginalTextReceived?.Invoke(text);

            // 翻訳はここでawaitせず、キューに積んで別ワーカーに任せる。
            // これにより次のセグメントのWhisper処理へすぐ進める。
            transcriptWriter.TryWrite(text);
        }
    }
}

/// <summary>デバイス選択用の情報。OS上で一意なIDと表示名の両方を保持する。
/// 名前の部分一致だけに頼ると、似た名前のデバイスが複数ある場合に誤選択しうるため、
/// 可能な限りIDで一意に識別できるようにする。</summary>
public record AudioDeviceInfo(string Id, string Name);

/// <summary>
/// WaveFileWriterがDispose時に内部のMemoryStreamまで閉じてしまわないようにするためのラッパー。
/// </summary>
class IgnoreDisposeStream : Stream
{
    private readonly Stream _inner;
    public IgnoreDisposeStream(Stream inner) => _inner = inner;

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        // 内部のMemoryStreamは意図的に閉じない
    }
}
