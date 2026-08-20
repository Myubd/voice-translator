using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LoopbackRecorder;

/// <summary>原文/訳文リストの1行分。タイムスタンプと本文を別々に色分け表示するために使う。
/// Idは元のSpeechSegment/TranscriptItemと同じ値で、原文側・訳文側の行を対応付けるために使う
/// (以前はリストの「インデックス」だけで対応付けていたため、翻訳が1件失敗すると
/// 以降すべての行がズレる不具合があった)。</summary>
public record TranscriptLine(long Id, string Timestamp, string Text);

public partial class MainWindow : Window
{
    private readonly AudioPipeline _pipeline = new AudioPipeline();

    // HttpClientはアプリ起動中ずっと使い回す(NAudio/Whisperのような長時間稼働アプリとして正しい方針)。
    // ただしSocketsHttpHandler.PooledConnectionLifetimeを設定しないと、DeepL/Ollama側でDNSレコードが
    // 変わった場合(サーバー移転やロードバランサ変更等)に古い接続を握り続けてしまう可能性があるため、
    // 数十分単位のライフタイムを明示しておく。
    private readonly HttpClient _httpClient = new HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15)
    });

    private AppSettings _settings = AppSettings.LoadFromEnv();
    private CancellationTokenSource? _cts;
    private bool _isRunning = false;
    private OverlayWindow? _overlayWindow;
    private HotkeyManager? _hotkeyManager;
    private bool _translationEnabledForRun = false;
    // 実行中のAudioPipeline.RunAsyncタスク。Closing時にawaitして、WASAPIデバイスや
    // Whisperモデルの解放が完了するのを待ってからプロセスを終了させるために保持する
    // (以前はここを保持しておらず、Closingは_cts.Cancel()を呼ぶだけの同期処理だったため、
    // RunAsync側の非同期な後片付けが完了する前にプロセスが終了してしまう可能性があった)。
    private Task? _pipelineTask;

    /// <summary>Id → TranslatedListBox上の行インデックス。原文が届いた時点で「翻訳中…」の
    /// プレースホルダーをこのインデックスに追加しておき、翻訳結果(成功/失敗)が届いたら
    /// 同じ位置を書き換える。これにより翻訳の成否によらず原文/訳文の行が常に揃う。</summary>
    private readonly Dictionary<long, int> _translatedRowIndexById = new();

    /// <summary>Id → 実際の発話区間(開始/終了時刻)。SRTエクスポート時に実時間ベースの
    /// タイムスタンプを出力するために保持する。</summary>
    private readonly Dictionary<long, (TimeSpan Start, TimeSpan End)> _segmentTimesById = new();

    /// <summary>今回のセッション(開始ボタンを押してから)より前の、累積セッション時間。
    /// AudioPipeline側のタイムスタンプ(_audioSamplesRead基準)は開始/停止のたびに0から
    /// リセットされるため、「停止して再度開始」を繰り返した状態で1つのSRTとしてエクスポートすると、
    /// 複数セッション分のタイムスタンプが0近辺で重複してしまう。この値を各セグメントの
    /// タイムスタンプに足し込むことで、複数セッションをまたいでも単調増加するようにする。</summary>
    private TimeSpan _sessionTimeOffset = TimeSpan.Zero;

    /// <summary>今回のセッションで受信した最後のセグメント終了時刻(セッション内相対)。
    /// 次のセッション開始時に_sessionTimeOffsetへ積み増すために保持する。</summary>
    private TimeSpan _lastSegmentEndTimeInSession = TimeSpan.Zero;

    /// <summary>ステータス欄に表示中の翻訳エラーを、一定時間後に元の状態表示へ戻すためのタイマー。
    /// 以前はエラー発生後、次に何かステータスが変わるまでエラーメッセージが表示され続け、
    /// 実際には回復していても「まだ壊れている」ように見えてしまっていた。</summary>
    private readonly DispatcherTimer _statusErrorClearTimer;

    public MainWindow()
    {
        InitializeComponent();

        _statusErrorClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _statusErrorClearTimer.Tick += (_, _) =>
        {
            _statusErrorClearTimer.Stop();
            // エラー表示前の状態に戻す。実行中なら「認識中」相当の表示に、停止中なら「停止中」に。
            StatusText.Text = _isRunning ? "認識中…" : "停止中";
        };

        _pipeline.OriginalTextReceived += args =>
        {
            Dispatcher.Invoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                // セッションをまたいでタイムスタンプが重複/逆行しないよう、累積オフセットを足し込む
                var start = _sessionTimeOffset + args.SegmentStartTime;
                var end = _sessionTimeOffset + args.SegmentEndTime;
                _segmentTimesById[args.Id] = (start, end);
                if (args.SegmentEndTime > _lastSegmentEndTimeInSession) _lastSegmentEndTimeInSession = args.SegmentEndTime;
                OriginalListBox.Items.Add(new TranscriptLine(args.Id, timestamp, args.Text));
                OriginalListBox.ScrollIntoView(OriginalListBox.Items[^1]);

                if (_translationEnabledForRun)
                {
                    // 翻訳結果を待たず、まず「翻訳中…」のプレースホルダーを同じ行位置に追加する。
                    // これで原文/訳文の2ペインが常に行単位で揃った状態を保てる。
                    int index = TranslatedListBox.Items.Add(new TranscriptLine(args.Id, timestamp, "(翻訳中…)"));
                    _translatedRowIndexById[args.Id] = index;
                }
            });
        };

        _pipeline.TranslatedTextReceived += args =>
        {
            Dispatcher.Invoke(() =>
            {
                var displayText = args.Text ?? "(翻訳失敗)";

                if (_translatedRowIndexById.TryGetValue(args.Id, out var index) && index < TranslatedListBox.Items.Count)
                {
                    var existing = (TranscriptLine)TranslatedListBox.Items[index];
                    TranslatedListBox.Items[index] = existing with { Text = displayText };
                }
                else
                {
                    // プレースホルダーが見つからない場合(翻訳無効時からの切り替え等の想定外パス)の保険
                    var timestamp = DateTime.Now.ToString("HH:mm:ss");
                    TranslatedListBox.Items.Add(new TranscriptLine(args.Id, timestamp, displayText));
                }

                if (args.Text != null)
                {
                    TranslatedListBox.ScrollIntoView(TranslatedListBox.Items[^1]);
                    _overlayWindow?.UpsertTranslatedLine(args.Id, args.Text);
                }
            });
        };

        // 翻訳待ちキューが満杯になり、翻訳される前に破棄された行。以前はここで何も起きず、
        // 該当行の「翻訳中…」プレースホルダーが永遠に残ってしまっていた。
        _pipeline.TranscriptItemSkipped += id =>
        {
            Dispatcher.Invoke(() =>
            {
                if (_translatedRowIndexById.TryGetValue(id, out var index) && index < TranslatedListBox.Items.Count)
                {
                    var existing = (TranscriptLine)TranslatedListBox.Items[index];
                    // 「(翻訳失敗)」(=翻訳APIへ送ったが失敗した)とは意図的に文言を分け、
                    // 「処理が追いつかず、そもそも翻訳されなかった」ことが分かるようにする
                    TranslatedListBox.Items[index] = existing with { Text = "(処理遅延によりスキップ)" };
                }
            });
        };

        _pipeline.StatusChanged += status =>
        {
            Dispatcher.Invoke(() =>
            {
                _statusErrorClearTimer.Stop();
                StatusText.Text = status;
            });
        };

        // DeepL/Ollamaの翻訳失敗は、これまでConsole.WriteLineのみでUIに一切出ていなかった。
        // ステータス欄に出すことで、配布後のユーザーでも「翻訳が出ない理由」に気づけるようにする。
        // 単発の一時的なエラー(タイムアウト等)でも、以前は次に何かステータスが変わるまで
        // エラーメッセージが表示され続け、実際には回復していても「まだ壊れている」ように
        // 見えてしまっていたため、一定時間後に通常表示へ自動的に戻すようにする。
        _pipeline.TranslationErrorOccurred += error =>
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = error;
                _statusErrorClearTimer.Stop();
                _statusErrorClearTimer.Start();
            });
        };

        // 処理が追いつかず音声セグメントが破棄された場合、これまでは何も表示されず
        // 「なぜか一部の発話が翻訳されない」状態にしか見えなかった。件数を表示して気づけるようにする
        _pipeline.SegmentsDropped += count =>
        {
            Dispatcher.Invoke(() => { _segmentDropCount = count; UpdateDropCountText(); });
        };
        _pipeline.TranscriptsDropped += count =>
        {
            Dispatcher.Invoke(() => { _transcriptDropCount = count; UpdateDropCountText(); });
        };

        // WASAPI→BufferedWaveProviderの段階での破棄は、Whisperキュー/翻訳キューのdropとは異なり
        // 「音声そのものが一度もWhisperに渡らない」という、最もユーザーに気付かれにくい欠落。
        // 少なくとも累計バイト数を警告として表示する。
        _pipeline.AudioBufferOverflowOccurred += totalDroppedBytes =>
        {
            Dispatcher.Invoke(() => { _audioOverflowBytes = totalDroppedBytes; UpdateDropCountText(); });
        };

        // 「VAD開始→Whisper完了→翻訳完了」の累積遅延を表示する。数値が大きくなり続ける場合、
        // 処理が実際の発話に追いつけていないサイン(CPU負荷や翻訳APIの遅延など)として気づける。
        _pipeline.LatencyMeasured += measurement =>
        {
            Dispatcher.Invoke(() =>
            {
                _lastLatencyMeasurement = measurement;
                UpdateLatencyText();
            });
        };

        // LatencyMeasuredと対になる、キューの滞留件数(診断情報)。
        // 「遅延は大きいが1件だけ重い」のか「キュー自体が詰まっている」のかを見分けられるようにする。
        _pipeline.QueueStatusChanged += status =>
        {
            Dispatcher.Invoke(() =>
            {
                _lastQueueStatus = status;
                UpdateLatencyText();
            });
        };

        Closing += MainWindow_Closing;
        Loaded += (_, _) => RegisterHotkeys();
    }

    private int _segmentDropCount = 0;
    private int _transcriptDropCount = 0;
    private long _audioOverflowBytes = 0;
    private LatencyMeasurement? _lastLatencyMeasurement = null;
    private PipelineQueueStatus? _lastQueueStatus = null;

    /// <summary>遅延(LatencyMeasured)とキュー滞留件数(QueueStatusChanged)は別々のイベントで
    /// 届くため、両方を1つの表示にまとめる。片方だけ届いている(起動直後等)場合でも
    /// 表示が崩れないよう、それぞれnull(未受信)の場合は該当部分を省略する。</summary>
    private void UpdateLatencyText()
    {
        if (_lastLatencyMeasurement == null)
        {
            LatencyText.Text = "";
            return;
        }

        var m = _lastLatencyMeasurement;
        // 「翻訳」を「待ち」(キューで前の項目の処理を待っていた時間)と「呼び出し」(実際のDeepL/Ollama
        // API呼び出しにかかった時間)に分けて表示する。この2つを合算していた頃は、遅延が大きい時に
        // 「翻訳APIが遅い」のか「翻訳ワーカーが1本しかなく詰まっている」のかを見分けられなかった。
        var text = $"遅延: {m.TotalLag.TotalSeconds:0.0}秒" +
            $" (認識 {m.WhisperDuration.TotalSeconds:0.0}s / 翻訳待ち {m.QueueWaitDuration.TotalSeconds:0.0}s / 翻訳 {m.TranslationCallDuration.TotalSeconds:0.0}s)";

        // キューが2件以上溜まっている場合のみ表示する(0〜1件は正常範囲であり、常時表示すると
        // かえって「常に何か詰まっている」ように見えてノイズになるため)
        if (_lastQueueStatus is { } q && (q.SegmentQueueLength >= 2 || q.TranscriptQueueLength >= 2))
        {
            text += $" [待ち行列: 認識待ち{q.SegmentQueueLength}件 / 翻訳待ち{q.TranscriptQueueLength}件]";
        }

        LatencyText.Text = text;
    }

    /// <summary>音声セグメント破棄・翻訳待ちテキスト破棄・音声バッファoverflow、すべての件数を
    /// 1つの警告表示にまとめる。発生段階(WASAPIバッファ/Whisperキュー/翻訳キュー)が異なるため、
    /// どちらでどれだけ破棄されたか分かるようにする。</summary>
    private void UpdateDropCountText()
    {
        var parts = new List<string>();
        if (_audioOverflowBytes > 0) parts.Add($"音声バッファ溢れ{_audioOverflowBytes / 1024}KB");
        if (_segmentDropCount > 0) parts.Add($"音声認識待ち{_segmentDropCount}件");
        if (_transcriptDropCount > 0) parts.Add($"翻訳待ち{_transcriptDropCount}件");
        DropCountText.Text = parts.Count > 0 ? $"⚠ 処理が追いつかず音声をスキップしました: {string.Join(" / ", parts)}" : "";
    }

    private bool _isClosingConfirmed = false;

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // AudioPipeline.RunAsyncが完全に終了する(WASAPIデバイス/Whisperモデルの解放が終わる)前に
        // プロセスが終了してしまうのを防ぐため、いったんClosingをキャンセルしてタスクの完了を待ち、
        // 完了後に改めてShutdownする。以前はCancel()を呼ぶだけで非同期の後片付けをawaitしていなかった。
        if (_isRunning && !_isClosingConfirmed)
        {
            e.Cancel = true;
            _cts?.Cancel();

            if (_pipelineTask != null)
            {
                try
                {
                    await _pipelineTask;
                }
                catch
                {
                    // 終了処理中の例外はここでは無視する(StartStopButton_Click側で既にログ済み)
                }
            }

            _isClosingConfirmed = true;
            Close();
            return;
        }

        // 録音・翻訳タスクが動いたままアプリを終了すると、バックグラウンドタスクが残り続けたり
        // 未保存のオーバーレイ状態が残ったりする恐れがあるため、終了時に明示的に片付ける。
        _hotkeyManager?.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
        _overlayWindow?.Close();
        _httpClient.Dispose();
        // Silero VADのONNXセッション(ネイティブリソース)を解放する。
        // RunAsyncが動いていない状態でのみ呼ぶこと(このメソッドに到達する時点で、
        // 実行中だった場合は上のif節でawait済みのため、ここでは確実に停止している)。
        _pipeline.Dispose();
    }

    /// <summary>ゲームプレイ中はAlt-Tabせずに操作したいという要望に応え、
    /// アプリが非アクティブでも効くグローバルホットキーを登録する。
    /// キーの組み合わせは設定画面の「ショートカット」タブから変更できる(既定はCtrl+Alt+R / Ctrl+Alt+O)。
    /// 設定変更後の再登録にも対応できるよう、呼び出すたびに一度dispose→再作成する。</summary>
    private void RegisterHotkeys()
    {
        _hotkeyManager?.Dispose();
        _hotkeyManager = new HotkeyManager(this);

        // 2つのホットキーは別々にtry/catchする。
        // 以前は1つのtryにまとめていたため、片方(例: 開始/停止)が他アプリと重複していると
        // 例外でそこから先に進めず、重複していないもう片方(オーバーレイ)まで巻き添えで
        // 登録されない不具合があった。
        bool startStopFailed = false;
        bool overlayFailed = false;

        try
        {
            var (modifiers, key) = _settings.GetStartStopHotkey();
            _hotkeyManager.Register(modifiers, key, () => StartStopButton_Click(this, new RoutedEventArgs()));
        }
        catch (Exception ex)
        {
            Logger.Log("MainWindow.Hotkey", "「翻訳開始/停止」のショートカットキー登録に失敗しました。", ex);
            startStopFailed = true;
        }

        try
        {
            var (modifiers, key) = _settings.GetOverlayHotkey();
            _hotkeyManager.Register(modifiers, key, () => OverlayButton_Click(this, new RoutedEventArgs()));
        }
        catch (Exception ex)
        {
            Logger.Log("MainWindow.Hotkey", "「オーバーレイ表示切り替え」のショートカットキー登録に失敗しました。", ex);
            overlayFailed = true;
        }

        // 他アプリと同じ組み合わせが既に登録済み等で失敗しても、通常のボタン操作は引き続き使えるため
        // アプリ自体は継続するが、原因不明のまま「ホットキーが効かない」状態にならないよう通知する
        if (startStopFailed || overlayFailed)
        {
            string which = startStopFailed && overlayFailed
                ? "「翻訳開始/停止」「オーバーレイ表示切り替え」両方のショートカットキー"
                : startStopFailed ? "「翻訳開始/停止」のショートカットキー" : "「オーバーレイ表示切り替え」のショートカットキー";
            StatusText.Text = $"{which}の登録に失敗しました(他アプリと重複している可能性があります)。設定画面で変更できます。";
        }
    }

    // 「停止」がクリックされてから、RunAsync側のバックグラウンド後片付け(Whisper/翻訳ワーカーの
    // 終了・WASAPIデバイスやWhisperモデルの解放)が完全に終わるまでのあいだtrueになる。
    //
    // 以前はここでUIをすぐ「停止中」に戻していたため、後片付けが終わる前に「開始」を
    // 連打すると、同じAudioPipelineインスタンス上でRunAsyncが2重に実行され、
    // _processor/_vad/_translationServiceなど共有フィールドが競合する可能性があった
    // (例: 古いRunAsyncがWhisperProcessorをDisposeした直後に、新しいRunAsyncがそれを使用する等)。
    // このフラグで「開始」ボタンを完全停止まで無効化することでその競合を防ぐ。
    private bool _isStopping = false;

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            if (_isStopping) return; // 停止ボタンの多重クリックは無視する
            _isStopping = true;
            StartStopButton.IsEnabled = false;
            StatusText.Text = "停止処理中...";
            _cts?.Cancel();

            // ここで_pipelineTaskをawaitすることで、後片付けが完全に終わる(=下の「開始」処理側の
            // finallyでSetRunningUiState(false)が呼ばれ_isRunningがfalseになる)まで、
            // このメソッドの呼び出し元(=このクリックハンドラ)は完了しない。
            // これにより、後片付け中はボタンがdisabledのままとなり、途中で再度「開始」を
            // 押すことができなくなる。
            if (_pipelineTask != null)
            {
                try { await _pipelineTask; }
                catch { /* 例外は開始側の処理で既にログ・表示済みのためここでは無視する */ }
            }

            _isStopping = false;
            StartStopButton.IsEnabled = true;
            return;
        }

        // 前回の停止処理がまだ完了していない場合は開始させない(通常はボタンがdisabledのため
        // ここには来ないはずだが、ホットキー経由の呼び出しに備えて念のため二重にガードする)
        if (_isStopping || _pipelineTask != null) return;

        // 設定画面で更新済みの_settingsをそのまま使う(ここで再読み込みすると
        // 設定画面での変更が.envの保存内容次第で上書きされてしまうため)

        // 開始ボタンを押すまでモデル未検出に気づけなかった問題への対応。
        // AudioPipeline.RunAsync内でも同じチェックをしているが、ここで事前に警告することで
        // 「起動中...」表示のまま実質何も起きていない状態にせず、すぐに気づけるようにする
        string resolvedModelPath = Path.IsPathRooted(_settings.WhisperModelPath)
            ? _settings.WhisperModelPath
            : Path.Combine(AppContext.BaseDirectory, _settings.WhisperModelPath);
        if (!File.Exists(resolvedModelPath))
        {
            MessageBox.Show(
                $"Whisperモデルファイルが見つかりません:\n{resolvedModelPath}\n\n設定画面でモデルファイルを配置するか、正しいファイル名を指定してください。",
                "モデルが見つかりません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ゲーム音声優先モードがONの場合、VAD閾値を引き上げて小さい雑音より
        // 大きいゲーム音声を優先的に拾うようにする(倍率は設定画面で調整可能)。
        // VAD閾値はSilero VAD使用時は確率(0〜1)のスケールのため、倍率をそのまま掛けると
        // 1.0を超えてしまい得る(1.0超は「絶対に発話と判定されない」という意味になり、
        // ゲーム音声優先どころかVADが完全に機能しなくなる)。そのため0.97を上限にclampする。
        _pipeline.EnergyThreshold = _settings.GameAudioPriorityMode
            ? Math.Min(0.97f, _settings.VadThreshold * _settings.GameAudioPriorityMultiplier)
            : _settings.VadThreshold;
        _pipeline.HysteresisRatio = _settings.VadHysteresisRatio;
        var translationService = _settings.CreateTranslationService(_httpClient);
        _translationEnabledForRun = translationService.IsEnabled;
        _pipeline.ConfigureTranslation(translationService);

        // 今回の実行(セッション)向けの表示状態をリセットする。
        // 「翻訳中…」プレースホルダーの行インデックス対応もここでリセットしないと、
        // 前回セッションのIdが残ったまま新しいセッションのIdと混ざってしまう
        _translatedRowIndexById.Clear();
        _segmentDropCount = 0;
        _transcriptDropCount = 0;
        _audioOverflowBytes = 0;
        DropCountText.Text = "";
        _lastLatencyMeasurement = null;
        _lastQueueStatus = null;
        LatencyText.Text = "";
        _statusErrorClearTimer.Stop();

        // AudioPipeline側のタイムスタンプは開始のたびに0からリセットされるが、
        // Id(_segmentTimesById)は「Clear」ボタンを押すまでセッションをまたいで保持され続ける。
        // そのため、2回目以降の開始では前回セッションの最終時刻をオフセットとして積み増し、
        // SRTエクスポート時に複数セッション分のタイムスタンプが0近辺で重複しないようにする。
        // (初回起動時は両方0のためこの行は実質no-op)
        _sessionTimeOffset += _lastSegmentEndTimeInSession;
        _lastSegmentEndTimeInSession = TimeSpan.Zero;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetRunningUiState(true);
        StatusText.Text = "起動中...";

        try
        {
            _pipelineTask = _pipeline.RunAsync(_settings.DeviceId, _settings.DeviceKeyword, _settings.WhisperModelPath, _settings.WhisperPrompt, _settings.RecognitionLanguage, _settings.WhisperThreadCount, _settings.TranslationWorkerCount, _settings.MaxLatencySeconds, _cts.Token);
            await _pipelineTask;
        }
        catch (OperationCanceledException)
        {
            // 停止ボタンによるキャンセルは正常系として無視する
        }
        catch (Exception ex)
        {
            Logger.Log("MainWindow", "音声パイプラインの実行中に予期しない例外が発生しました。", ex);
            MessageBox.Show($"エラーが発生しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _pipelineTask = null;
            SetRunningUiState(false);
        }
    }

    private void SetRunningUiState(bool running)
    {
        _isRunning = running;
        StartStopLabel.Text = running ? "翻訳停止" : "翻訳開始";
        StartStopIcon.Text = running ? "\uE71A" : "\uE768"; // 停止アイコン / 再生アイコン
        StatusDot.Fill = running
            ? new SolidColorBrush(Color.FromRgb(0x29, 0xC7, 0xC1))
            : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x77));
        if (!running)
        {
            StatusText.Text = "停止中";
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_settings, _httpClient, _isRunning);
        if (settingsWindow.ShowDialog() == true)
        {
            _settings = settingsWindow.Settings;
            _overlayWindow?.ApplyAppearance(_settings.OverlayFontSize, _settings.OverlayOpacity, _settings.OverlayMaxLines, _settings.OverlayFontColor);
            // ショートカットキーが変更されている可能性があるため、登録し直す
            RegisterHotkeys();

            if (_isRunning)
            {
                StatusText.Text = "設定を保存しました。認識エンジン等の変更を反映するには、一度「翻訳停止」してから「翻訳開始」してください。";
            }
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        OriginalListBox.Items.Clear();
        TranslatedListBox.Items.Clear();
        _translatedRowIndexById.Clear();
        _segmentTimesById.Clear();
        // 表示している発話をすべて消したので、次にタイムスタンプが重複する心配は無くなった。
        // ここでリセットしないと、Clear後に長時間放置してから話した場合、次のSRTの先頭行が
        // 不必要に大きいオフセット付きの時刻から始まってしまう
        _sessionTimeOffset = TimeSpan.Zero;
        _lastSegmentEndTimeInSession = TimeSpan.Zero;
        _overlayWindow?.ClearLines();
    }

    private void OverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow == null || !_overlayWindow.IsVisible)
        {
            _overlayWindow ??= new OverlayWindow();
            _overlayWindow.ApplyAppearance(_settings.OverlayFontSize, _settings.OverlayOpacity, _settings.OverlayMaxLines, _settings.OverlayFontColor);
            _overlayWindow.Show();
        }
        else
        {
            _overlayWindow.Hide();
        }
    }

    /// <summary>原文/訳文の履歴をファイルへ保存する。
    /// これまで履歴を後から見返したり配信のログとして残したりする手段が無かったための対応。
    /// テキスト(原文+訳文)と、訳文のみのSRT字幕の2形式を選べる。</summary>
    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (TranslatedListBox.Items.Count == 0 && OriginalListBox.Items.Count == 0)
        {
            MessageBox.Show("エクスポートする履歴がありません。", "エクスポート", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "テキストファイル (*.txt)|*.txt|SRT字幕ファイル (*.srt)|*.srt",
            FileName = $"transcript-{DateTime.Now:yyyyMMdd-HHmmss}"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            if (dialog.FilterIndex == 2)
            {
                ExportAsSrt(dialog.FileName);
            }
            else
            {
                ExportAsText(dialog.FileName);
            }
            StatusText.Text = $"エクスポートしました: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            Logger.Log("MainWindow.Export", "履歴のエクスポートに失敗しました。", ex);
            MessageBox.Show($"エクスポートに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>訳文の履歴をタイムスタンプ付きでクリップボードにコピーする。
    /// チャットへの貼り付け等、ファイル保存ほど大げさでない共有をワンクリックで行いたい
    /// というニーズに対応するための機能(ExportAsTextと違いファイルには残さない)。</summary>
    private void CopyTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        if (TranslatedListBox.Items.Count == 0)
        {
            StatusText.Text = "コピーする訳文がありません。";
            return;
        }

        var sb = new StringBuilder();
        foreach (TranscriptLine line in TranslatedListBox.Items)
        {
            sb.AppendLine(line.Text);
        }

        try
        {
            // Clipboard.SetTextは他プロセス(クリップボード監視ツール等)との競合で
            // 稀に失敗することがあるため(COMException)、握りつぶさずログに残しつつ
            // ユーザーにも分かる形でステータス表示する
            Clipboard.SetText(sb.ToString().TrimEnd());
            StatusText.Text = "訳文をクリップボードにコピーしました。";
        }
        catch (Exception ex)
        {
            Logger.Log("MainWindow.CopyTranslation", "訳文のクリップボードへのコピーに失敗しました。", ex);
            StatusText.Text = "クリップボードへのコピーに失敗しました。";
        }
    }

    /// <summary>
    /// 原文リストの各行をIdをキーに訳文リストと対応付けてテキスト出力する。
    /// 以前は単純にリストの「インデックス」で原文/訳文をペアにしていたため、
    /// 翻訳が1件でも失敗すると訳文側リストにはその回だけ追加されず、以降すべての行で
    /// インデックスが1つずつズレて無関係な訳文が対応付けられてしまう不具合があった。
    /// 現在は原文側イベントで確定するIdを両リストの行が共通で持っているため、
    /// Idで引き当てることでこのズレが発生しない(翻訳失敗行は「(翻訳失敗)」がそのまま出力される)。
    /// </summary>
    private void ExportAsText(string path)
    {
        var translatedById = new Dictionary<long, TranscriptLine>();
        foreach (TranscriptLine line in TranslatedListBox.Items)
        {
            translatedById[line.Id] = line;
        }

        var sb = new StringBuilder();
        foreach (TranscriptLine original in OriginalListBox.Items)
        {
            sb.AppendLine($"[{original.Timestamp}]");
            sb.AppendLine($"原文: {original.Text}");
            if (translatedById.TryGetValue(original.Id, out var translated))
            {
                sb.AppendLine($"訳文: {translated.Text}");
            }
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// 訳文をSRT字幕として書き出す。以前は各セグメントの正確な発話時間を保持していなかったため、
    /// 1行あたり固定4秒という実際の発話とは無関係な簡易タイミングになっていた。
    /// 現在はAudioPipelineがWhisperの認識結果(result.Start/End)から算出した実際の発話区間
    /// (_segmentTimesById)を保持しているため、それを使って実時間に沿ったタイムスタンプを出力する。
    /// 翻訳が失敗した行(訳文が無い行)はSRTには含めない(字幕として意味を持たないため)。
    /// </summary>
    private void ExportAsSrt(string path)
    {
        var sb = new StringBuilder();
        int index = 1;
        foreach (TranscriptLine line in TranslatedListBox.Items)
        {
            if (!_segmentTimesById.TryGetValue(line.Id, out var times)) continue;
            // 翻訳中/翻訳失敗/処理遅延スキップのプレースホルダーはSRTに含めない
            if (line.Text == "(翻訳中…)" || line.Text == "(翻訳失敗)" || line.Text == "(処理遅延によりスキップ)") continue;

            sb.AppendLine(index.ToString());
            sb.AppendLine($"{times.Start:hh\\:mm\\:ss\\,fff} --> {times.End:hh\\:mm\\:ss\\,fff}");
            sb.AppendLine(line.Text);
            sb.AppendLine();
            index++;
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }
}
