using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

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
        // ゲーム音声優先モードがONの場合、VAD閾値を引き上げて小さい雑音より
        // 大きいゲーム音声を優先的に拾うようにする
        _pipeline.EnergyThreshold = _settings.GameAudioPriorityMode
            ? _settings.VadThreshold * 1.5f
            : _settings.VadThreshold;
        _pipeline.ConfigureTranslation(_settings.CreateTranslationService(_httpClient));

        _cts = new CancellationTokenSource();
        SetRunningUiState(true);
        StatusText.Text = "起動中...";

        try
        {
            await _pipeline.RunAsync(_settings.DeviceKeyword, _settings.WhisperModelPath, _settings.WhisperPrompt, _settings.RecognitionLanguage, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 停止ボタンによるキャンセルは正常系として無視する
        }
        catch (Exception ex)
        {
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
}
