using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace LoopbackRecorder;

/// <summary>原文/訳文リストの1行分。タイムスタンプと本文を別々に色分け表示するために使う</summary>
public record TranscriptLine(string Timestamp, string Text);

public partial class MainWindow : Window
{
    private readonly AudioPipeline _pipeline = new AudioPipeline();
    private readonly HttpClient _httpClient = new HttpClient();
    private AppSettings _settings = AppSettings.LoadFromEnv();
    private CancellationTokenSource? _cts;
    private bool _isRunning = false;
    private OverlayWindow? _overlayWindow;
    private HotkeyManager? _hotkeyManager;

    public MainWindow()
    {
        InitializeComponent();

        _pipeline.OriginalTextReceived += text =>
        {
            Dispatcher.Invoke(() =>
            {
                OriginalListBox.Items.Add(new TranscriptLine(DateTime.Now.ToString("HH:mm:ss"), text));
                OriginalListBox.ScrollIntoView(OriginalListBox.Items[^1]);
            });
        };

        _pipeline.TranslatedTextReceived += text =>
        {
            Dispatcher.Invoke(() =>
            {
                TranslatedListBox.Items.Add(new TranscriptLine(DateTime.Now.ToString("HH:mm:ss"), text));
                TranslatedListBox.ScrollIntoView(TranslatedListBox.Items[^1]);
                _overlayWindow?.AddTranslatedLine(text);
            });
        };

        _pipeline.StatusChanged += status =>
        {
            Dispatcher.Invoke(() => StatusText.Text = status);
        };

        // DeepL/Ollamaの翻訳失敗は、これまでConsole.WriteLineのみでUIに一切出ていなかった。
        // ステータス欄に出すことで、配布後のユーザーでも「翻訳が出ない理由」に気づけるようにする。
        _pipeline.TranslationErrorOccurred += error =>
        {
            Dispatcher.Invoke(() => StatusText.Text = error);
        };

        // 処理が追いつかず音声セグメントが破棄された場合、これまでは何も表示されず
        // 「なぜか一部の発話が翻訳されない」状態にしか見えなかった。件数を表示して気づけるようにする
        _pipeline.SegmentsDropped += count =>
        {
            Dispatcher.Invoke(() => DropCountText.Text = $"⚠ 処理遅延のため音声セグメントを{count}件スキップしました");
        };

        Closing += MainWindow_Closing;
        Loaded += (_, _) => RegisterHotkeys();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 録音・翻訳タスクが動いたままアプリを終了すると、バックグラウンドタスクが残り続けたり
        // 未保存のオーバーレイ状態が残ったりする恐れがあるため、終了時に明示的に片付ける。
        _hotkeyManager?.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
        _overlayWindow?.Close();
        _httpClient.Dispose();
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

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            _cts?.Cancel();
            SetRunningUiState(false);
            return;
        }

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
        // 大きいゲーム音声を優先的に拾うようにする(倍率は設定画面で調整可能)
        _pipeline.EnergyThreshold = _settings.GameAudioPriorityMode
            ? _settings.VadThreshold * _settings.GameAudioPriorityMultiplier
            : _settings.VadThreshold;
        _pipeline.HysteresisRatio = _settings.VadHysteresisRatio;
        _pipeline.ConfigureTranslation(_settings.CreateTranslationService(_httpClient));

        DropCountText.Text = "";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetRunningUiState(true);
        StatusText.Text = "起動中...";

        try
        {
            await _pipeline.RunAsync(_settings.DeviceId, _settings.DeviceKeyword, _settings.WhisperModelPath, _settings.WhisperPrompt, _settings.RecognitionLanguage, _cts.Token);
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
        var settingsWindow = new SettingsWindow(_settings);
        if (settingsWindow.ShowDialog() == true)
        {
            _settings = settingsWindow.Settings;
            _overlayWindow?.ApplyAppearance(_settings.OverlayFontSize, _settings.OverlayOpacity, _settings.OverlayMaxLines);
            // ショートカットキーが変更されている可能性があるため、登録し直す
            RegisterHotkeys();
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        OriginalListBox.Items.Clear();
        TranslatedListBox.Items.Clear();
        _overlayWindow?.ClearLines();
    }

    private void OverlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlayWindow == null || !_overlayWindow.IsVisible)
        {
            _overlayWindow ??= new OverlayWindow();
            _overlayWindow.ApplyAppearance(_settings.OverlayFontSize, _settings.OverlayOpacity, _settings.OverlayMaxLines);
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

    private void ExportAsText(string path)
    {
        var sb = new StringBuilder();
        int count = Math.Max(OriginalListBox.Items.Count, TranslatedListBox.Items.Count);
        for (int i = 0; i < count; i++)
        {
            var original = i < OriginalListBox.Items.Count ? (TranscriptLine)OriginalListBox.Items[i] : null;
            var translated = i < TranslatedListBox.Items.Count ? (TranscriptLine)TranslatedListBox.Items[i] : null;

            var timestamp = original?.Timestamp ?? translated?.Timestamp ?? "";
            sb.AppendLine($"[{timestamp}]");
            if (original != null) sb.AppendLine($"原文: {original.Text}");
            if (translated != null) sb.AppendLine($"訳文: {translated.Text}");
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>訳文のみをSRT字幕として書き出す。
    /// 各セグメントの正確な発話時間は保持していないため、1行あたり固定で数秒間表示する
    /// 簡易的なタイミングになる(動画に正確に同期させたい場合は目安として使う想定)</summary>
    private void ExportAsSrt(string path)
    {
        const int secondsPerLine = 4;
        var sb = new StringBuilder();
        for (int i = 0; i < TranslatedListBox.Items.Count; i++)
        {
            var line = (TranscriptLine)TranslatedListBox.Items[i];
            var start = TimeSpan.FromSeconds(i * secondsPerLine);
            var end = TimeSpan.FromSeconds((i + 1) * secondsPerLine);

            sb.AppendLine((i + 1).ToString());
            sb.AppendLine($"{start:hh\\:mm\\:ss\\,fff} --> {end:hh\\:mm\\:ss\\,fff}");
            sb.AppendLine(line.Text);
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }
}
