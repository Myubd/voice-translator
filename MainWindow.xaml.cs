using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace LoopbackRecorder;

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
                OriginalListBox.Items.Add(text);
                OriginalListBox.ScrollIntoView(OriginalListBox.Items[^1]);
            });
        };

        _pipeline.TranslatedTextReceived += text =>
        {
            Dispatcher.Invoke(() =>
            {
                TranslatedListBox.Items.Add(text);
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
            StartStopButton.Content = "開始";
            _isRunning = false;
            return;
        }

        // 設定画面で更新済みの_settingsをそのまま使う(ここで再読み込みすると
        // 設定画面での変更が.envの保存内容次第で上書きされてしまうため)
        _pipeline.EnergyThreshold = _settings.VadThreshold;
        _pipeline.ConfigureTranslation(_settings.CreateTranslationService(_httpClient));

        _cts = new CancellationTokenSource();
        _isRunning = true;
        StartStopButton.Content = "停止";
        StatusText.Text = "起動中...";

        try
        {
            await _pipeline.RunAsync(_settings.DeviceKeyword, _settings.WhisperModelPath, _cts.Token);
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
            _isRunning = false;
            StartStopButton.Content = "開始";
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_settings);
        if (settingsWindow.ShowDialog() == true)
        {
            _settings = settingsWindow.Settings;
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
            _overlayWindow.Show();
        }
        else
        {
            _overlayWindow.Hide();
        }
    }
}
