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
public class AudioPipeline : IDisposable
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

    // 翻訳ワーカーの並行実行数は、以前はここに固定値(定数)を持たせていたが、
    // AppSettings.TranslationWorkerCount(設定画面「翻訳ワーカー数」スライダー)から
    // 実行時に渡せるようにした。RunAsync呼び出し元(MainWindow)は既にAppSettings側で
    // 1〜4にclamp済みだが、ライブラリ単体で誤った値を渡された場合の防御として
    // ここでも念のためclampする(0だと翻訳が一切実行されなくなるため)。
    //
    // 【完了順序について】並列化すると、翻訳の「完了」順は発話順と一致しなくなりうる
    // (例: 先に話した内容がDeepL失敗でOllamaにフォールバックして遅れている間に、後から話した
    // 内容が先に翻訳完了する)。メイン画面の原文/訳文リストとエクスポート機能はId基準で
    // 行を更新・対応付けする設計になっているため、この完了順の入れ替わりによる影響を受けない。
    // ゲームオーバーレイ(OverlayWindow)も、Id基準で正しい表示位置に挿入する方式に変更済みのため
    // (OverlayWindow.UpsertTranslatedLine参照)、この完了順の入れ替わりの影響を受けない。

    public float EnergyThreshold
    {
        get => _energyThreshold;
        set
        {
            _energyThreshold = value;
            if (_vad != null) _vad.EnergyThreshold = value;
        }
    }
    private float _energyThreshold = 0.015f;

    /// <summary>
    /// ヒステリシス比率(0〜1)。発話「開始」の判定にはEnergyThresholdをそのまま使うが、
    /// 一度発話が始まった後は EnergyThreshold × HysteresisRatio という、より低い閾値で
    /// 「まだ発話が続いている」とみなす。
    /// これにより、閾値ギリギリの音量(息継ぎ・語尾の減衰など)で発話中に短時間だけRMSが
    /// 下がった場合でも、そこで発話が終わったと誤判定してセグメントが分断されるのを防げる。
    /// 値を下げるほど、一度始まった発話は多少音量が下がっても継続扱いされやすくなる
    /// (=無音判定はより大きな音量低下があった時だけ働く)。
    /// 1.0にすると従来と同じ単一閾値の挙動になる。
    ///
    /// 実際の判定ロジック自体はVoiceActivitySegmenterに切り出されており、ここは
    /// (RunAsync開始前を含め、いつ設定されても良いように)そのままVoiceActivitySegmenterへ
    /// 委譲するプロパティになっている。
    /// </summary>
    public float HysteresisRatio
    {
        get => _hysteresisRatio;
        set
        {
            _hysteresisRatio = value;
            if (_vad != null) _vad.HysteresisRatio = value;
        }
    }
    private float _hysteresisRatio = 0.6f;

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

    /// <summary>WASAPI→BufferedWaveProviderの段階で、VAD側の読み出しが追いつかず生の音声データが
    /// 破棄された際に通知される(破棄された累計バイト数を渡す)。SegmentsDropped/TranscriptsDroppedは
    /// いずれも「一度セグメント化/文字起こしされた後」の破棄だが、ここで破棄されると
    /// そもそも該当区間の音声がWhisperに一度も渡らないため、ユーザーからは
    /// 「話したのに何も表示されない」としか見えない、最も気付きにくい種類の欠落となる。</summary>
    public event Action<long>? AudioBufferOverflowOccurred;

    /// <summary>翻訳待ちキューが満杯になり、文字起こし済みテキスト(TranscriptItem)が翻訳される前に
    /// 破棄された際、その項目のIdを通知する。以前はTranscriptsDropped(件数)しか発火しないため、
    /// UI側は原文受信時に先に出した「翻訳中…」のプレースホルダーをどのIdについて消せばよいか
    /// 分からず、実際には翻訳されないまま「翻訳中…」が永遠に残ってしまっていた。</summary>
    public event Action<long>? TranscriptItemSkipped;

    /// <summary>
    /// 1区間ぶんの遅延計測結果。「VAD開始 → Whisper完了 → 翻訳完了」の各段階にかかった時間と、
    /// 発話終了から翻訳完了までの累積遅延(=現在何秒遅れているか)を通知する。
    /// </summary>
    public event Action<LatencyMeasurement>? LatencyMeasured;

    /// <summary>
    /// LatencyMeasuredと同じタイミングで発火する、2つのキュー(音声セグメント待ち/翻訳待ち)の
    /// 現在の滞留件数。遅延の原因がキューの詰まりによるものかどうかを切り分けるための診断情報。
    /// </summary>
    public event Action<PipelineQueueStatus>? QueueStatusChanged;
    // 遅延計算(Whisper所要時間・翻訳所要時間・累積遅延)は責務分割の第一歩としてLatencyTrackerへ切り出した。
    private readonly LatencyTracker _latencyTracker = new();

    private int _droppedSegmentCount = 0;
    private int _droppedTranscriptCount = 0;
    private long _nextSegmentId = 0;

    // WriteSegment/WriteTranscriptItemはVADループ(単一スレッド)からのみ呼ばれるが、
    // DropOldestの「実際に破棄が起きたか」を正確に検出するため、破棄チェックと書き込みを
    // アトミックに行う目的でロックを使う(詳細は各Writeメソッドのコメントを参照)。
    private readonly object _segmentQueueLock = new object();
    private readonly object _transcriptQueueLock = new object();

    // パイプライン開始からの経過時間を計測する基準時計。DateTime.Nowと違いシステム時刻変更の
    // 影響を受けない。ただし、これはあくまで「処理を行っているスレッドの壁時計」であり、
    // 発話の実際の音声時刻とは別物。Whisper処理が重くなってVADの読み出しが遅れた場合、
    // _pipelineClock.Elapsedは「実際に音声が鳴った時刻」より遅れた値を返してしまう
    // (=CPU負荷が低いときは正確だが、処理が遅延するほど不正確になる)。
    // そのため、音声セグメントのタイムスタンプ(SRT等)には使わず、あくまで
    // 「Whisper/翻訳の各段階にどれだけ壁時計時間がかかったか」を測る遅延計測にのみ使う。
    private readonly Stopwatch _pipelineClock = new Stopwatch();

    // 実際に読み出した音声サンプル数(16kHzリサンプル後)の累計。これを基準に音声時刻を
    // 算出することで、VAD処理側が遅延しても「音声内の何秒目か」というタイムスタンプ自体は
    // ずれない(WASAPI/BufferedWaveProviderでの取りこぼし=overflowが発生しない限り、
    // 音声時刻の算出根拠として_pipelineClock.Elapsedより正確)。
    private long _audioSamplesRead = 0;

    // 発話終了(SegmentEndTime)からこの時間を超えて未処理のまま残っている発話は、
    // Whisper/翻訳どちらの手前でも処理を打ち切り、最新の発話を優先する(P0-4: freshness-based drop)。
    // RunAsync呼び出し時に設定値(AppSettings.MaxLatencySeconds)から設定される。
    // TimeSpan.Zero以下の場合は機能を無効化(=以前までの「常に全部処理する」挙動)する。
    private TimeSpan _maxLatency = TimeSpan.FromSeconds(3);

    private WhisperProcessor? _processor;
    private ITranslationService _translationService = NullTranslationService.Instance;

    // VAD(発話区間検出)のステートマシン本体。RunAsync開始時に生成し、終了時に破棄する。
    // ロジック自体はVoiceActivitySegmenterクラスに切り出してあり、ここでは生成と
    // EnergyThreshold/HysteresisRatioプロパティの委譲、および結果の受け取りのみを行う。
    private VoiceActivitySegmenter? _vad;

    // Silero VAD(ONNXニューラルモデル)による発話検出器。ONNXモデルのロード自体には
    // ある程度コストがかかるため、RunAsyncのたびに作り直すのではなく、AudioPipelineの
    // 生存期間を通じて1つだけ保持し、セッション開始のたびにReset()で内部状態だけ初期化する。
    // ロードに失敗した場合(モデルファイルが見つからない等)はnullのままとし、
    // VoiceActivitySegmenter側で自動的に従来のRMSベース判定にフォールバックする。
    private SileroVadDetector? _sileroDetector;
    private bool _sileroDetectorLoadAttempted = false;

    // Whisper結果の重複除去は責務分割の2つ目としてSegmentDeduplicatorへ切り出した。
    private readonly SegmentDeduplicator _deduplicator = new();

    /// <summary>利用可能な出力(ループバック対象)デバイスの一覧を、OS上で一意なIDと表示名のペアで取得する</summary>
    public static List<AudioDeviceInfo> GetAvailableDevices()
    {
        // MMDeviceEnumerator/MMDeviceはCOMラッパー(IDisposable)。設定画面を開くたびに
        // このメソッドが呼ばれる想定のため、明示的にdisposeしないとCOMオブジェクトが
        // 積み上がる可能性がある。列挙して得た各MMDeviceも使い終わったら解放する
        // (MMDeviceCollection自体はIDisposableを実装していないためusing対象外)。
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        try
        {
            return devices
                .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
                .ToList();
        }
        finally
        {
            foreach (var d in devices) d.Dispose();
        }
    }

    public void ConfigureTranslation(ITranslationService? service)
    {
        // nullを渡された場合(未設定/APIキー未入力等)はNullTranslationServiceに正規化し、
        // 以降のパイプライン内部(TranslationWorker等)がnullチェックを持たずに済むようにする
        _translationService = service ?? NullTranslationService.Instance;
    }

    public async Task RunAsync(string deviceId, string deviceKeyword, string modelPath, string whisperPrompt, string recognitionLanguage, int whisperThreadCount, int translationWorkerCount, double maxLatencySeconds, CancellationToken cancellationToken)
    {
        using var enumerator = new MMDeviceEnumerator();
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
            // 選択候補が見つからなかった場合、列挙した全デバイスがここで不要になるため解放する
            foreach (var d in devices) d.Dispose();
            StatusChanged?.Invoke($"'{deviceKeyword}' を含むデバイスが見つかりませんでした。設定画面でデバイスを選び直してください。");
            return;
        }

        // 選ばれなかった方のMMDeviceはこの後使わないため、ここで解放しておく
        // (targetは録音中ずっと使うため、WasapiLoopbackCaptureの生存期間と合わせてここでは解放しない)
        foreach (var d in devices)
        {
            if (!ReferenceEquals(d, target)) d.Dispose();
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

        // Silero VAD(ONNXモデル)のロードは初回のみ試みる。csproj側でsilero_vad.onnxを
        // 実行ファイルと同じフォルダにコピーする設定にしてあるため、通常はそのまま見つかるはずだが、
        // 万一(配布物からファイルが欠落している等)見つからない/ロードに失敗した場合でも、
        // アプリ自体は起動を諦めず、従来のRMSベースVADにフォールバックして動作を継続する。
        if (!_sileroDetectorLoadAttempted)
        {
            _sileroDetectorLoadAttempted = true;
            string sileroModelPath = Path.Combine(AppContext.BaseDirectory, "silero_vad.onnx");
            try
            {
                _sileroDetector = new SileroVadDetector(sileroModelPath);
                // 成功時もログに残しておく。「エラーが出ていない=Sileroが使われている」と
                // 決め打ちせず、あとから確認できるようにするため
                // (実際にどちらが使われているかはStatusText/ログの両方で確認可能にしている)
                Logger.Log("AudioPipeline.SileroVad", $"Silero VADモデルをロードしました: {sileroModelPath}");
            }
            catch (Exception ex)
            {
                Logger.Log("AudioPipeline.SileroVad",
                    "Silero VADモデルのロードに失敗しました。従来のRMSベースVADで続行します。", ex);
                _sileroDetector = null;
            }
        }

        using var whisperFactory = WhisperFactory.FromPath(resolvedModelPath);
        // 呼び出し元(AppSettings.WhisperThreadCount)で1〜論理コア数にclamp済みだが、
        // 想定外の値(0以下)が渡された場合に備えて念のためここでも下限を保証しておく
        var processorBuilder = whisperFactory.CreateBuilder()
            .WithLanguage(string.IsNullOrWhiteSpace(recognitionLanguage) ? "auto" : recognitionLanguage)
            .WithThreads(Math.Max(1, whisperThreadCount));

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
        _audioSamplesRead = 0;
        // 前回セッションの重複除去状態を持ち越さない(詳細はSegmentDeduplicator.Resetのコメント参照)
        _deduplicator.Reset();
        // _pipelineClockは今回の実行(発話の実時間)を基準にリセットする。そのため、SRTエクスポート等の
        // タイムスタンプは「開始/停止を1回だけ行ったセッション」を前提とした相対時刻になる。
        // 履歴をクリアせずに複数回Start/Stopを繰り返した場合、2回目以降の実行分は
        // タイムスタンプが0から再スタートする点に注意(エクスポート前に履歴をクリアするか、
        // セッションごとにエクスポートする運用を推奨)。
        _pipelineClock.Restart();

        // 0以下は「機能を無効化」という意図的な設定として扱う(TimeSpan.Zeroにすると
        // 「常に0秒でタイムアウト=何も処理しない」になってしまうため、代わりにTimeSpan.MaxValueに
        // することで実質チェックが常にfalseになるようにする)
        _maxLatency = maxLatencySeconds > 0 ? TimeSpan.FromSeconds(maxLatencySeconds) : TimeSpan.MaxValue;

        // Build()後、この中で例外が発生した場合にWhisperProcessorが破棄されないまま
        // 残ってしまうのを防ぐため、以降の処理全体を try/catch で囲む
        try
        {
            using var capture = new WasapiLoopbackCapture(target);
            // 以前は5秒分だったが、Whisper推論中(特に大きいモデル/低スペック環境)にVAD側の
            // 読み出しがそれ以上遅れるケースがあり、その間の発話が丸ごと欠落しうるため
            // 15秒分に拡大した。それでも溢れる場合は下のoverflow検出でユーザーに通知する。
            const int bufferSeconds = 15;
            var bufferedProvider = new BufferedWaveProvider(capture.WaveFormat)
            {
                BufferLength = capture.WaveFormat.AverageBytesPerSecond * bufferSeconds,
                DiscardOnBufferOverflow = true
            };
            long droppedAudioBytes = 0;

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
                // BufferedWaveProvider.AddSamples自体はDiscardOnBufferOverflow=trueのとき
                // 溢れた分を例外を投げずに黙って捨てるだけで、呼び出し側には何も知らせない。
                // ここで事前にバッファの空き容量をチェックし、溢れる場合は破棄バイト数を
                // 累計してイベント通知することで、「音声が消えたのに何も表示されない」状態を防ぐ。
                int availableBytes = bufferedProvider.BufferLength - bufferedProvider.BufferedBytes;
                if (e.BytesRecorded > availableBytes)
                {
                    long overflowBytes = e.BytesRecorded - availableBytes;
                    droppedAudioBytes += overflowBytes;
                    Logger.Log("AudioPipeline.Capture",
                        $"Audio capture buffer overflow: {overflowBytes} bytes (累計 {droppedAudioBytes} bytes)");
                    AudioBufferOverflowOccurred?.Invoke(droppedAudioBytes);
                }
                bufferedProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };

            // デバイスが他プロセス(Sonar等)と一時的に競合してエラーになることがあるため、
            // 数回リトライしてから諦める(Sonarを手動再起動しなくても自然に復帰することが多い)。
            //
            // 以前はCOMException全般をリトライ対象にしていたが、これは「デバイスが他アプリで
            // 使用中」以外の致命的なエラー(フォーマット非対応、デバイス自体が存在しない等)まで
            // 無条件に5回×1秒待ってから失敗させてしまい、原因の分からないまま無駄に待たされる
            // ユーザー体験になっていた。HResultで実際に一時的な競合を示すエラーコードのみ
            // リトライし、それ以外は即座に失敗させて原因を伝える。
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
                catch (System.Runtime.InteropServices.COMException ex)
                    when (attempt < maxRetries && IsTransientDeviceError(ex.HResult))
                {
                    StatusChanged?.Invoke($"デバイスが使用中のためリトライ中... ({attempt}/{maxRetries})");
                    await Task.Delay(1000, cancellationToken);
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    // リトライ対象外のエラー、またはリトライ上限に達した場合はここに来る。
                    // captureStarted=falseのまま抜け、下のガードでユーザーに明示的なメッセージを出す。
                    Logger.Log("AudioPipeline.Capture",
                        $"音声キャプチャの開始に失敗しました(HResult: 0x{ex.HResult:X8})。", ex);
                    break;
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

            // 翻訳サービスの準備処理(Ollama使用時、モデルの事前ロードや参考コンテキストからの
            // 用語集抽出など)を先に済ませておく。
            // cancellationTokenを渡すことで、この処理中にユーザーが「停止」した場合も
            // (以前のようにPrepareAsyncの完了/タイムアウトを待たされることなく)即座に打ち切れる。
            if (_translationService.IsEnabled)
            {
                StatusChanged?.Invoke("翻訳エンジンを準備中...");
                try
                {
                    await _translationService.PrepareAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // 準備中にユーザーが停止した場合はそのまま抜け、外側のtry/finallyで後片付けする
                    throw;
                }
            }

            // どちらのVADエンジンで動作しているかを毎回ステータス表示に含める。
            // 成功時は無言、失敗時だけ警告、という以前の実装だと「エラーが出ていない=Sileroが
            // 使われている」と決め打ちすることになり分かりにくいため、成功/失敗どちらの場合も
            // ここで明示する。
            string vadEngineLabel = _sileroDetector != null ? "Silero VAD" : "簡易VAD(RMS)";
            StatusChanged?.Invoke($"認識中: {target.FriendlyName} / VAD: {vadEngineLabel}");

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
            // SingleReader=falseにしているのは、通常の消費(TranslationWorker.RunAsync)に加えて
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

            // 翻訳ワーカーをTranslationWorkerCount本並列で起動する。各インスタンスは同じ
            // transcriptChannel.Reader/_transcriptQueueLockを共有するが、キューの読み出しは
            // ロックで排他されているため競合しない(詳細はTranslationWorkerCountのコメント参照)。
            // イベントはすべて自身のpublicイベントへ中継する(どのワーカーが発火させたかは
            // 呼び出し元からは区別する必要がない)。
            var translationWorkerTasks = new List<Task>();
            var effectiveTranslationWorkerCount = Math.Clamp(translationWorkerCount, 1, 4);
            for (int i = 0; i < effectiveTranslationWorkerCount; i++)
            {
                var translationWorker = new TranslationWorker(transcriptChannel.Reader, _transcriptQueueLock, _translationService, _pipelineClock, _latencyTracker, _maxLatency);
                translationWorker.TranscriptItemSkipped += id => TranscriptItemSkipped?.Invoke(id);
                translationWorker.TranslatedTextReceived += args => TranslatedTextReceived?.Invoke(args);
                translationWorker.TranslationErrorOccurred += msg => TranslationErrorOccurred?.Invoke(msg);
                translationWorker.LatencyMeasured += m =>
                {
                    LatencyMeasured?.Invoke(m);
                    // 同じタイミングで2つのキューの滞留件数も通知する。
                    // Channel.Reader.Countは他スレッドから読んでもスレッドセーフ(内部でロック済み)なので、
                    // ここで直接参照して問題ない。
                    QueueStatusChanged?.Invoke(new PipelineQueueStatus(segmentChannel.Reader.Count, transcriptChannel.Reader.Count));
                };
                translationWorkerTasks.Add(translationWorker.RunAsync(cancellationToken));
            }

            var readBuffer = new float[ChunkSamples];
            // VADの状態(発話中か、直近の無音チャンク数、プリロールバッファ等)は
            // すべてVoiceActivitySegmenter側で保持する。ここではRunAsync開始時点の
            // EnergyThreshold/HysteresisRatioと、(利用可能なら)Silero VAD検出器を渡して生成する。
            _sileroDetector?.Reset();

            // EnergyThreshold/HysteresisRatioはSilero VAD(確率0〜1)のスケールで保存・設定されている。
            // 万一Silero VADのロードに失敗し、従来のRMSベース判定にフォールバックする場合、
            // ユーザーが設定した値(例: 0.5)をそのままRMS閾値として使うと、RMSが0.5を超えることは
            // 通常無いためVADが実質的に一切反応しなくなる「静かな機能不全」につながる。
            // これを避けるため、フォールバック時は固定の安全なRMS閾値を使い、その旨をユーザーに通知する。
            const float FallbackRmsThreshold = 0.015f;
            float effectiveEnergyThreshold = EnergyThreshold;
            if (_sileroDetector == null)
            {
                effectiveEnergyThreshold = FallbackRmsThreshold;
                StatusChanged?.Invoke("Silero VADモデルが利用できないため、簡易(RMS)方式の発話検出で動作しています");
            }

            _vad = new VoiceActivitySegmenter(
                SampleRate, ChunkSamples, SilenceChunksToEndSpeech, MinSpeechChunks,
                MaxSpeechChunks, PrerollChunks, ForcedSplitOverlapChunks, _sileroDetector)
            {
                EnergyThreshold = effectiveEnergyThreshold,
                HysteresisRatio = HysteresisRatio
            };
            // 直近に読み出した音声サンプルまでの「音声内時刻」。_pipelineClock.Elapsed(壁時計)とは
            // 異なり、実際に何サンプル分の音声を読み終えたかだけに基づくため、VAD/Whisperの処理が
            // 遅延してもこの値自体はずれない。
            TimeSpan currentAudioTime = TimeSpan.Zero;

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

                    // このチャンクぶんを読み終えた時点での音声内時刻を更新する。
                    // (audioSamplePosition / SampleRate、というGPTレビューの改善案に相当)
                    _audioSamplesRead += read;
                    currentAudioTime = TimeSpan.FromSeconds((double)_audioSamplesRead / SampleRate);

                    var vadResult = _vad.ProcessChunk(readBuffer, read, currentAudioTime);
                    if (vadResult != null)
                    {
                        var segment = BuildSegment(vadResult.Samples, vadResult.StartTime, vadResult.EndTime);
                        WriteSegment(segmentChannel.Writer, segmentChannel.Reader, segment);
                    }
                }
            }
            finally
            {
                capture.StopRecording();

                // 停止した瞬間、まだ発話の途中(無音判定が確定する前)だった分が
                // VoiceActivitySegmenter内に残っている可能性がある。短すぎなければ、
                // これも最後の1区間として送っておく。
                // ここで送らないと、話している最中に停止した最後の発話が丸ごと消える。
                var finalSegment = _vad.Flush(currentAudioTime);
                if (finalSegment != null)
                {
                    var segment = BuildSegment(finalSegment.Samples, finalSegment.StartTime, finalSegment.EndTime);
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

                // Whisper側が終わったら文字起こし結果のキューも締め切り、翻訳ワーカー(全インスタンス)の
                // 完了を待つ。TryComplete()を使う(Complete()は使わない)理由: RunWhisperWorkerAsync側の
                // finally節が既にtranscriptWriter.TryComplete()を呼んでいるため、ここでも呼ばれた時点で
                // このキューは既に完了済みになっている。Complete()は「既に完了済みの場合は
                // ChannelClosedExceptionを投げる」仕様のため、録音を停止するたびに必ずこの例外が
                // 発生し、ユーザーに「The channel has been closed.」というエラーダイアログが
                // 出てしまっていた。TryComplete()は既に完了済みでも例外を投げず単にfalseを返すだけなので、
                // ここでは意図(=念のためもう一度締め切りを試みる)を安全に表現できる。
                transcriptChannel.Writer.TryComplete();
                try
                {
                    await Task.WhenAll(translationWorkerTasks);
                }
                catch (Exception ex)
                {
                    Logger.Log("AudioPipeline.Translation", "翻訳ワーカーの終了待機中に例外が発生しました。", ex);
                }

                await DisposeProcessorAsync();
                _vad = null;

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
            while (true)
            {
                // 以前は reader.ReadAllAsync() でロックの外から直接消費していたため、
                // WriteSegment側の「Count確認→TryRead(捨てる)→TryWrite」という一連の操作と
                // このスレッドのTryReadが競合し、「実際には満杯でなかったのに1件dropする」
                // (逆に、dropすべきなのにカウントし損ねる)可能性があった。
                // TryRead自体を_segmentQueueLockの下で行うことで、WriteSegment側のdrop判定と
                // 完全に排他させ、drop数を正確に計測できるようにする。
                // (ロックを保持するのはTryReadの一瞬だけで、データが無い間の待機は
                // WaitToReadAsyncでロック外で行うため、producer側の書き込みを妨げない)
                SpeechSegment? segment;
                bool got;
                lock (_segmentQueueLock)
                {
                    got = reader.TryRead(out segment);
                }

                if (!got)
                {
                    bool more;
                    try
                    {
                        more = await reader.WaitToReadAsync(CancellationToken.None);
                    }
                    catch
                    {
                        break;
                    }
                    if (!more) break; // Writer.Complete()済みでキューも空 → 終了
                    continue;
                }

                try
                {
                    await TranscribeSegmentAsync(segment!, transcriptWriter, transcriptReader);
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
    ///
    /// cancellationTokenはRunAsync全体のキャンセルトークンと同一のもの。停止操作で
    /// 既にキャンセルされている場合、まだ処理していないキュー内の項目(最大TranscriptChannelCapacity件)は
    /// 翻訳を試みず即座にスキップする。以前はここでキャンセルを考慮しておらず、停止後も
    /// キュー内の全項目をDeepL(15秒)/Ollama(30秒)のタイムアウトいっぱいまで律儀に処理し続けており、
    /// 停止ボタンを押してから実際に終了するまで数十秒〜数分かかることがあった。
    /// (呼び出し中に停止された場合は、TranslateAsync側にもcancellationTokenを渡しているため、
    /// タイムアウトを待たずに即座に打ち切られる。)
    /// </summary>
    // 翻訳ワーカーの本体はTranslationWorker.RunAsyncへ移動した(呼び出し箇所はRunAsync内を参照)

    /// <summary>
    /// 音声セグメントをキューへ書き込む。
    /// 満杯なら古い方から明示的に読み捨ててから書き込む、という一連の操作をロックでアトミックに行う。
    /// 以前はこのロックを消費側(Whisperワーカー)のTryReadが取得していなかったため、
    /// 「チェック」と「書き込み」の間に消費側が1件読んでしまい、実際には満杯でなかったのに
    /// 1件dropしたと誤カウントする競合が理論上あり得た。RunWhisperWorkerAsync側のTryReadも
    /// 同じ_segmentQueueLockの下で行うようにしたことで、この競合は解消されている。
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
    /// 読み捨てた項目のIdはTranscriptItemSkippedで個別に通知し、UI側が該当行の
    /// 「翻訳中…」プレースホルダーを解消できるようにする。
    /// </summary>
    private void WriteTranscriptItem(ChannelWriter<TranscriptItem> writer, ChannelReader<TranscriptItem> reader, TranscriptItem item)
    {
        var skippedIds = new List<long>();
        lock (_transcriptQueueLock)
        {
            while (reader.Count >= TranscriptChannelCapacity && reader.TryRead(out var evicted))
            {
                _droppedTranscriptCount++;
                skippedIds.Add(evicted.Id);
            }
            writer.TryWrite(item);
        }
        if (skippedIds.Count > 0)
        {
            TranscriptsDropped?.Invoke(_droppedTranscriptCount);
            foreach (var skippedId in skippedIds)
            {
                TranscriptItemSkipped?.Invoke(skippedId);
            }
            Logger.LogMetric("Queue", ("queue", "transcript"), ("capacity", TranscriptChannelCapacity), ("dropped_total", _droppedTranscriptCount));
        }
    }

    private async Task TranscribeSegmentAsync(SpeechSegment segment, ChannelWriter<TranscriptItem> transcriptWriter, ChannelReader<TranscriptItem> transcriptReader)
    {
        if (_processor == null) return;

        // freshness-based drop(P0-4): Whisper推論は全処理の中で最も重い(数百ms〜数秒)ため、
        // ここで古い発話を弾いておくことが最も効果的。「キューの件数」だけを見ていると、
        // 例えばキューが2件程度でも1件のWhisper処理に長く時間がかかっている状況(高負荷時等)では
        // 実際の遅延はどんどん広がっていく、という問題を検出できなかった(GitHubレビューのP0-4指摘)。
        // 経過時間を直接見ることで、「詰まっているかどうか」ではなく「この発話はもう聞き逃した
        // 過去の話題として扱ってよいか」を判断する。
        var currentLatency = _pipelineClock.Elapsed - segment.EndTime;
        if (currentLatency > _maxLatency)
        {
            _droppedSegmentCount++;
            SegmentsDropped?.Invoke(_droppedSegmentCount);
            Logger.LogMetric("Queue", ("queue", "segment"), ("reason", "stale"),
                ("latency_ms", (int)currentLatency.TotalMilliseconds), ("dropped_total", _droppedSegmentCount));
            return;
        }

        // 以前はWhisperへ渡すためだけにMemoryStream上へWAVヘッダ付きで書き出していたが、
        // Whisper.netのProcessAsyncは float[] samples を直接受け付けるオーバーロードを持っており、
        // 単一チャンネル・16kHzで既に保持しているsegment.Samplesをそのまま渡せる。
        // WAVラッピング(MemoryStream確保+WaveFileWriterでのサンプルごとの書き込み)は
        // 発話のたびに発生する不要なコピー/メモリ割り当てだったため省略する。
        string? lastText = null;
        await foreach (var result in _processor.ProcessAsync(segment.Samples))
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

            // Whisperの結果(result.Start/result.End)は、渡した音声チャンク内での相対時刻。
            // セグメントの実開始時刻(segment.StartTime)を足すことで、パイプライン全体での
            // 実際の発話時刻(絶対時刻)を得られる。重複除去の時間的重なり判定にも使うため、
            // ここ(重複判定より前)で計算しておく。
            var absoluteStart = segment.StartTime + result.Start;
            var absoluteEnd = segment.StartTime + result.End;

            // 重複判定はSegmentDeduplicatorに委譲(判定ロジック・ロックの詳細はそちら参照)
            if (_deduplicator.IsDuplicate(text, absoluteStart, absoluteEnd)) continue;

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

    /// <summary>
    /// Silero VADのONNXセッション(ネイティブリソース)を解放する。RunAsync自体は
    /// 開始/停止を何度でも繰り返せる設計だが、このDisposeはアプリ終了時に1回だけ呼ぶ想定。
    /// (RunAsyncの実行中に呼んではいけない。呼んだ場合、以降のVAD推論が失敗する)
    /// </summary>
    public void Dispose()
    {
        _sileroDetector?.Dispose();
        _sileroDetector = null;
    }

    // WASAPI(Windows Audio Session API)がCOMExceptionのHResultとして返す、
    // 「デバイスが一時的に使えない」系のエラーコード。これらのみリトライ対象とする。
    // 参照: https://learn.microsoft.com/windows/win32/coreaudio/audclnt-e-device-in-use
    private const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x88890019);
    private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    private const int AUDCLNT_E_ENDPOINT_CREATE_FAILED = unchecked((int)0x88890014);

    /// <summary>他プロセスによる一時的な占有や、デバイス列挙のタイミング競合など、
    /// 「少し待てば自然に回復し得る」種類のエラーかどうかを判定する。
    /// フォーマット非対応(AUDCLNT_E_UNSUPPORTED_FORMAT)やデバイス自体が存在しない場合の
    /// エラーはここに含めない(何度待っても状況が変わらないため、即座に失敗させて
    /// ユーザーに気づかせた方が良い)。</summary>
    private static bool IsTransientDeviceError(int hResult) => hResult is
        AUDCLNT_E_DEVICE_IN_USE or
        AUDCLNT_E_DEVICE_INVALIDATED or
        AUDCLNT_E_ENDPOINT_CREATE_FAILED;
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
