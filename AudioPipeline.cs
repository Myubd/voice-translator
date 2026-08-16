using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    // 音声セグメント用チャンネルの最大件数。溢れた分の検出にも使う
    const int SegmentChannelCapacity = 5;

    // 文字起こし結果(翻訳待ち)用チャンネルの最大件数。
    // 以前はここを無制限(Unbounded)にしていたが、翻訳APIが継続的に遅い/詰まる状況が
    // 長時間続くと際限なく溜まり続け、「ずいぶん前に話した内容がずっと後になってから
    // 訳文として出てくる」状態になりうる。文字列だけの軽量なキューとはいえ、
    // リアルタイム用途である以上は音声セグメント側と同様に上限を設け、
    // 溢れた場合は古いもの(=鮮度が落ちたもの)から捨てて最新の発話を優先する。
    const int TranscriptChannelCapacity = 8;

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

    /// <summary>原文を受信した際に発火する。Idと実際の発話時刻(StartTime/EndTime)を伴うため、
    /// 訳文側イベント(TranslatedTextReceived)と同じIdで対応付けられる。</summary>
    public event Action<OriginalTextEventArgs>? OriginalTextReceived;

    /// <summary>
    /// 翻訳結果を受信した際に発火する。翻訳が失敗した場合もText=nullでこのイベント自体は
    /// 発火する(従来はTranslatedTextReceivedが成功時にしか発火せず、UI側が原文リストと
    /// 訳文リストを単純にインデックスで対応付けていたため、1件でも翻訳が失敗すると
    /// 以降すべての行の原文/訳文がズレてしまう不具合があった)。
    /// </summary>
    public event Action<TranslatedTextEventArgs>? TranslatedTextReceived;

    public event Action<string>? StatusChanged;

    /// <summary>翻訳API呼び出しが失敗した際に通知される(APIキー誤り、レート制限、ネットワーク断など)。
    /// ステータス欄向けの短い文言。個々の訳文の成否はTranslatedTextReceivedのTextでも判別できる。</summary>
    public event Action<string>? TranslationErrorOccurred;

    /// <summary>処理が追いつかず、音声セグメントがキューから古い順に破棄された際に通知される
    /// (破棄された累計件数を渡す)。従来は静かに捨てるだけで、ユーザーからは
    /// 「なぜか一部の発話が翻訳されない」としか見えなかった。</summary>
    public event Action<int>? SegmentsDropped;

    /// <summary>処理が追いつかず、文字起こし済みテキスト(翻訳待ち)が古い順に破棄された際に通知される。
    /// 音声セグメントの破棄(SegmentsDropped)とは別の段階(翻訳キュー)で発生する破棄なので分けて通知する。</summary>
    public event Action<int>? TranscriptsDropped;

    /// <summary>
    /// 1区間ぶんの遅延計測結果。「VAD開始 → Whisper完了 → 翻訳完了」の各段階にかかった時間と、
    /// 発話終了から翻訳完了までの累積遅延(=現在何秒遅れているか)を通知する。
    /// </summary>
    public event Action<LatencyMeasurement>? LatencyMeasured;

    private int _droppedSegmentCount = 0;
    private int _droppedTranscriptCount = 0;
    private long _nextSegmentId = 0;

    // WriteSegment/WriteTranscriptItemはVADループ(単一スレッド)からのみ呼ばれるが、
    // DropOldestの「実際に破棄が起きたか」を正確に検出するため、破棄チェックと書き込みを
    // アトミックに行う目的でロックを使う(詳細は各Writeメソッドのコメントを参照)。
    private readonly object _segmentQueueLock = new object();
    private readonly object _transcriptQueueLock = new object();

    // パイプライン開始からの経過時間を計測する基準時計。DateTime.Nowと違いシステム時刻変更の
    // 影響を受けず、発話の実時間(SRTタイムスタンプ)と遅延計測の両方に使う。
    private readonly Stopwatch _pipelineClock = new Stopwatch();

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

        _processor = processorBuilder.Build();

        // drop数は「今回の実行で何件破棄したか」を表す値なのでRunAsyncのたびにリセットする。
        // 一方 _nextSegmentId はリセットしない: MainWindow側でIdをキーに原文/訳文を対応付けて
        // 表示しているため、同一アプリセッション内で開始/停止を繰り返してもIdが重複しないように
        // (=前回実行分の表示と衝突しないように)、AudioPipelineインスタンスの生存期間を通じて
        // 単調増加させ続ける。
        _droppedSegmentCount = 0;
        _droppedTranscriptCount = 0;
        // _pipelineClockは今回の実行(発話の実時間)を基準にリセットする。そのため、SRTエクスポート等の
        // タイムスタンプは「開始/停止を1回だけ行ったセッション」を前提とした相対時刻になる。
        // 履歴をクリアせずに複数回Start/Stopを繰り返した場合、2回目以降の実行分は
        // タイムスタンプが0から再スタートする点に注意(エクスポート前に履歴をクリアするか、
        // セッションごとにエクスポートする運用を推奨)。
        _pipelineClock.Restart();

        // Build()後、この中で例外が発生した場合にWhisperProcessorが破棄されないまま
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
                // ステレオはNAudio標準のStereoToMonoSampleProvider(左右0.5/0.5)を使う
                sampleProvider = new StereoToMonoSampleProvider(sampleProvider)
                {
                    LeftVolume = 0.5f,
                    RightVolume = 0.5f
                };
            }
            else if (capture.WaveFormat.Channels > 2)
            {
                // 5.1ch等、2chを超える入力(一部の仮想オーディオデバイスで発生。
                // READMEに記載のあるSteelSeries Sonar等との相性問題として報告あり)は
                // StereoToMonoSampleProviderの対象外(2ch専用)でそのまま素通りしてしまい、
                // Whisperに想定外のフォーマットが渡っていた。全チャンネル平均でモノラル化する
                // 汎用の変換を挟むことで、チャンネル数によらず確実にモノラル入力を保証する。
                sampleProvider = new MultiChannelToMonoSampleProvider(sampleProvider);
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
                await DisposeProcessorAsync();
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
            // 遅延がどこまでも蓄積し続けるのを防ぐ。
            // 破棄の検出・カウントは自前で行うため(WriteSegment参照)、FullModeはWait のままにし、
            // producer/consumerが同じreaderを操作しうるのでSingleReaderは指定しない。
            var segmentChannel = Channel.CreateBounded<SpeechSegment>(new BoundedChannelOptions(SegmentChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = false
            });

            // Whisperが書き出した文字起こし結果を翻訳ワーカーへ渡すための第2キュー。
            // これによりWhisper自体は翻訳の完了を待たずに次のセグメントへ進める
            // (翻訳が遅い/詰まっても、音声認識側の処理は止まらない)。
            // 以前は無制限(Unbounded)だったが、翻訳APIが継続的に遅延する状況が続くと
            // 際限なく溜まり続けてしまうため、音声セグメント側と同様に上限+DropOldest
            // (=最新の発話を優先し、古いものから捨てる)を設ける。
            // SingleReader=falseにしているのは、通常の消費(RunTranslationWorkerAsync)に加えて
            // WriteTranscriptItem内の破棄処理(満杯時の読み捨て)も別スレッド(Whisperワーカー側)から
            // 同じreaderに対してTryReadを呼ぶため。ここをtrueにすると「単一リーダー」の前提が
            // 崩れ、Channelの内部実装によっては不正な動作を招く可能性がある。
            var transcriptChannel = Channel.CreateBounded<TranscriptItem>(new BoundedChannelOptions(TranscriptChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = false
            });

            var whisperWorkerTask = RunWhisperWorkerAsync(segmentChannel.Reader, transcriptChannel.Writer, transcriptChannel.Reader);
            var translationWorkerTask = RunTranslationWorkerAsync(transcriptChannel.Reader);

            var readBuffer = new float[ChunkSamples];
            var speechBuffer = new List<float>();
            var prerollBuffer = new Queue<float[]>();
            int silenceChunkCount = 0;
            bool inSpeech = false;
            TimeSpan segmentStartTime = TimeSpan.Zero;

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

                            // 発話開始時刻は「今」ではなく、先頭に付与するプリロール分だけ
                            // 遡った時刻になる(プリロールも実際にはその時刻に鳴っていた音声のため)
                            long prerollSamples = prerollBuffer.Sum(c => (long)c.Length);
                            segmentStartTime = _pipelineClock.Elapsed - TimeSpan.FromSeconds((double)prerollSamples / SampleRate);
                            if (segmentStartTime < TimeSpan.Zero) segmentStartTime = TimeSpan.Zero;

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
                                var segment = BuildSegment(speechBuffer, segmentStartTime, _pipelineClock.Elapsed);
                                WriteSegment(segmentChannel.Writer, segmentChannel.Reader, segment);
                            }
                            speechBuffer.Clear();
                        }
                        else if (tooLong)
                        {
                            // 15秒の強制分割。無音を検出したわけではなく、発話はまだ続いている可能性が高いため
                            // inSpeechはtrueのまま維持し、直前の音声の末尾を少しだけ次のセグメントへ引き継ぐ。
                            // これにより、分割位置をまたぐ文でWhisperが直前の文脈(語尾)を完全に失うのを防ぐ
                            var splitEndTime = _pipelineClock.Elapsed;
                            var segment = BuildSegment(speechBuffer, segmentStartTime, splitEndTime);
                            WriteSegment(segmentChannel.Writer, segmentChannel.Reader, segment);

                            int overlapSamples = Math.Min(ForcedSplitOverlapChunks * ChunkSamples, speechBuffer.Count);
                            var carryOver = speechBuffer.GetRange(speechBuffer.Count - overlapSamples, overlapSamples);
                            speechBuffer.Clear();
                            speechBuffer.AddRange(carryOver);
                            silenceChunkCount = 0;

                            // 次のセグメントの開始時刻は、引き継いだ分だけ現在より少し前になる
                            segmentStartTime = splitEndTime - TimeSpan.FromSeconds((double)overlapSamples / SampleRate);
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
                    var segment = BuildSegment(speechBuffer, segmentStartTime, _pipelineClock.Elapsed);
                    WriteSegment(segmentChannel.Writer, segmentChannel.Reader, segment);
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

                await DisposeProcessorAsync();

                StatusChanged?.Invoke("停止しました");
            }
        }
        catch
        {
            await DisposeProcessorAsync();
            throw;
        }
    }

    /// <summary>
    /// WhisperProcessorの破棄処理。以前は複数箇所(開始失敗時/正常終了時/例外時)に
    /// 同じ「dispose→_processor=nullで二重dispose防止」というパターンが分散していたため、
    /// ここに一元化して読みやすくする。nullチェック→ローカル変数への退避→dispose、という
    /// 手順自体は元々安全(二重disposeは既に防がれていた)だったが、同じコードが3箇所に
    /// 散らばっているのはDRY違反であり保守性を下げていた。
    /// </summary>
    private async Task DisposeProcessorAsync()
    {
        var processor = _processor;
        _processor = null;
        if (processor != null)
        {
            await processor.DisposeAsync();
        }
    }

    private SpeechSegment BuildSegment(List<float> buffer, TimeSpan startTime, TimeSpan endTime)
    {
        return new SpeechSegment
        {
            Id = Interlocked.Increment(ref _nextSegmentId),
            Samples = buffer.ToArray(),
            StartTime = startTime,
            EndTime = endTime
        };
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
    private async Task RunWhisperWorkerAsync(ChannelReader<SpeechSegment> reader, ChannelWriter<TranscriptItem> transcriptWriter, ChannelReader<TranscriptItem> transcriptReader)
    {
        try
        {
            await foreach (var segment in reader.ReadAllAsync(CancellationToken.None))
            {
                try
                {
                    await TranscribeSegmentAsync(segment, transcriptWriter, transcriptReader);
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
    private async Task RunTranslationWorkerAsync(ChannelReader<TranscriptItem> reader)
    {
        await foreach (var item in reader.ReadAllAsync(CancellationToken.None))
        {
            if (_translationService == null) continue;

            var result = await _translationService.TranslateAsync(item.Text);
            var translationCompletedAt = _pipelineClock.Elapsed;

            if (result.Text != null)
            {
                TranslatedTextReceived?.Invoke(new TranslatedTextEventArgs(item.Id, result.Text, item.SegmentStartTime, item.SegmentEndTime));
            }
            else if (result.ErrorMessage != null)
            {
                // DeepL/Ollamaのエラーはこれまでコンソールに出すだけでUIに一切出ていなかった。
                // WPFアプリとして配布した場合、通常ユーザーはコンソールを見ないため、
                // 「なぜか訳文が出ない」状態のまま気づけなかった。ここでStatusへ通知する。
                TranslationErrorOccurred?.Invoke(result.ErrorMessage);

                // 失敗時もIdだけを載せてイベントを発火させる(Text=null)。
                // これによりUI側は「この区間は翻訳に失敗した」とIdで認識でき、
                // 訳文側リストにプレースホルダーを表示することで原文/訳文の対応がズレるのを防げる。
                TranslatedTextReceived?.Invoke(new TranslatedTextEventArgs(item.Id, null, item.SegmentStartTime, item.SegmentEndTime));
            }

            // 遅延計測: 発話終了(SegmentEndTime)を基準に、Whisper完了までの時間・
            // 翻訳完了までの時間・トータルの遅延を算出して通知する
            var whisperDuration = item.WhisperCompletedAt - item.SegmentEndTime;
            var translationDuration = translationCompletedAt - item.WhisperCompletedAt;
            var totalLag = translationCompletedAt - item.SegmentEndTime;
            var measurement = new LatencyMeasurement(item.Id, whisperDuration, translationDuration, totalLag);
            LatencyMeasured?.Invoke(measurement);
            Logger.LogMetric("Latency",
                ("id", item.Id),
                ("whisper_ms", (int)whisperDuration.TotalMilliseconds),
                ("translation_ms", (int)translationDuration.TotalMilliseconds),
                ("total_lag_ms", (int)totalLag.TotalMilliseconds));
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
    /// 音声セグメントをキューへ書き込む。
    /// 以前はBoundedChannelFullMode.DropOldestに任せた上で、書き込み前のreader.Countを見て
    /// 破棄の発生を推測していたが、この「チェック」と「書き込み」の間に別スレッド(Whisperワーカー)が
    /// 1件消費すると、実際には破棄が起きていないのに破棄したとカウントする(逆に、起きたのに
    /// カウントし損ねる)競合が理論上あり得た。ここでは書き込みも含めて全体をロックし、
    /// 満杯なら明示的に自分で1件読み捨ててから書き込む、という一連の操作をアトミックに行うことで
    /// drop数を正確に計測する。
    /// </summary>
    private void WriteSegment(ChannelWriter<SpeechSegment> writer, ChannelReader<SpeechSegment> reader, SpeechSegment segment)
    {
        bool dropped = false;
        lock (_segmentQueueLock)
        {
            while (reader.Count >= SegmentChannelCapacity && reader.TryRead(out _))
            {
                _droppedSegmentCount++;
                dropped = true;
            }
            writer.TryWrite(segment);
        }
        if (dropped)
        {
            SegmentsDropped?.Invoke(_droppedSegmentCount);
            Logger.LogMetric("Queue", ("queue", "segment"), ("capacity", SegmentChannelCapacity), ("dropped_total", _droppedSegmentCount));
        }
    }

    /// <summary>
    /// 文字起こし結果(翻訳待ち)をキューへ書き込む。WriteSegmentと同じ理由で、
    /// 満杯時の読み捨て+書き込みをロックでアトミックに行い、正確なdrop数を計測する。
    /// </summary>
    private void WriteTranscriptItem(ChannelWriter<TranscriptItem> writer, ChannelReader<TranscriptItem> reader, TranscriptItem item)
    {
        bool dropped = false;
        lock (_transcriptQueueLock)
        {
            while (reader.Count >= TranscriptChannelCapacity && reader.TryRead(out _))
            {
                _droppedTranscriptCount++;
                dropped = true;
            }
            writer.TryWrite(item);
        }
        if (dropped)
        {
            TranscriptsDropped?.Invoke(_droppedTranscriptCount);
            Logger.LogMetric("Queue", ("queue", "transcript"), ("capacity", TranscriptChannelCapacity), ("dropped_total", _droppedTranscriptCount));
        }
    }

    private async Task TranscribeSegmentAsync(SpeechSegment segment, ChannelWriter<TranscriptItem> transcriptWriter, ChannelReader<TranscriptItem> transcriptReader)
    {
        if (_processor == null) return;

        using var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(ms), new WaveFormat(SampleRate, 1)))
        {
            foreach (var sample in segment.Samples)
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
                // 完全一致+3秒という重複除去は、Whisperがセグメント境界付近で
                // 同じ文をほぼ即座に2回出力するケース(プリロールの重なり等)を狙ったものだが、
                // 窓が長すぎると「Yes.」のような短い発話が数秒後に本当にもう一度発言された
                // 場合まで誤って握りつぶしてしまう。狙い通りの直近重複だけを除去できるよう
                // 窓を3秒に短縮している(過去のバグ修正の経緯であり、部分一致方式への変更は
                // 無関係な文同士がたまたま一部重なって誤除去されるリスクとのトレードオフになるため、
                // 現状の完全一致方式を維持する)
                isDuplicate = text == _lastGlobalText && (DateTime.Now - _lastGlobalTime) < TimeSpan.FromSeconds(3);
                if (!isDuplicate)
                {
                    _lastGlobalText = text;
                    _lastGlobalTime = DateTime.Now;
                }
            }
            if (isDuplicate) continue;

            // Whisperの結果(result.Start/result.End)は、渡した音声チャンク内での相対時刻。
            // セグメントの実開始時刻(segment.StartTime)を足すことで、パイプライン全体での
            // 実際の発話時刻(絶対時刻)を得られる。これによりSRT出力を「1行固定4秒」という
            // 目安表示ではなく、実際にその発話が行われた時間で生成できるようにする。
            var absoluteStart = segment.StartTime + result.Start;
            var absoluteEnd = segment.StartTime + result.End;

            OriginalTextReceived?.Invoke(new OriginalTextEventArgs(segment.Id, text, absoluteStart, absoluteEnd));

            // 翻訳はここでawaitせず、キューに積んで別ワーカーに任せる。
            // これにより次のセグメントのWhisper処理へすぐ進める。
            var transcriptItem = new TranscriptItem
            {
                Id = segment.Id,
                Text = text,
                SegmentStartTime = absoluteStart,
                SegmentEndTime = absoluteEnd,
                WhisperCompletedAt = _pipelineClock.Elapsed
            };
            WriteTranscriptItem(transcriptWriter, transcriptReader, transcriptItem);
        }
    }
}

/// <summary>デバイス選択用の情報。OS上で一意なIDと表示名の両方を保持する。
/// 名前の部分一致だけに頼ると、似た名前のデバイスが複数ある場合に誤選択しうるため、
/// 可能な限りIDで一意に識別できるようにする。</summary>
public record AudioDeviceInfo(string Id, string Name);

/// <summary>
/// 入力チャンネル数によらず、全チャンネルの単純平均を取ってモノラルへ変換する。
/// NAudioの StereoToMonoSampleProvider は2ch(ステレオ)専用のため、5.1ch等の
/// 仮想オーディオデバイス(README記載のSteelSeries Sonar等との相性問題を含む)からの
/// 出力ではモノラル変換がスキップされ、Whisperに想定外のフォーマットが渡ってしまっていた。
/// </summary>
class MultiChannelToMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[]? _sourceBuffer;

    public MultiChannelToMonoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = Math.Max(1, source.WaveFormat.Channels);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int sourceSamplesNeeded = count * _channels;
        if (_sourceBuffer == null || _sourceBuffer.Length < sourceSamplesNeeded)
        {
            _sourceBuffer = new float[sourceSamplesNeeded];
        }

        int sourceRead = _source.Read(_sourceBuffer, 0, sourceSamplesNeeded);
        int framesRead = sourceRead / _channels;

        for (int frame = 0; frame < framesRead; frame++)
        {
            float sum = 0f;
            int baseIndex = frame * _channels;
            for (int ch = 0; ch < _channels; ch++)
            {
                sum += _sourceBuffer[baseIndex + ch];
            }
            buffer[offset + frame] = sum / _channels;
        }

        return framesRead;
    }
}

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
