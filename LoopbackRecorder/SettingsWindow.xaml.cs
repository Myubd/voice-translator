using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

namespace LoopbackRecorder;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; private set; }

    // 以前はこのウィンドウ専用にHttpClientを新規作成していたが、SettingsWindow自体が
    // 設定画面を開くたびに(MainWindow側から)new SettingsWindow(...)で再生成されるため、
    // 結果的に開閉のたびにHttpClientも新規作成されていた(ソケット枯渇・
    // PooledConnectionLifetime未設定によるDNS変更未追従の原因になりうる)。
    // MainWindow側で1つだけ保持している共有HttpClient(SocketsHttpHandler設定済み)を
    // コンストラクタで受け取って使い回すことで、アプリ全体でHttpClientを1つに統一する。
    private readonly HttpClient _httpClient;

    // Ollamaのモデル一覧取得は、Ollama自体が応答しない場合に長時間ぶら下がる可能性がある。
    // 設定画面を閉じたのにリクエストだけ裏で残り続けないよう、Closingでキャンセルできるようにする
    private CancellationTokenSource? _ollamaLoadCts;

    // DeepL APIキーはPasswordBox/TextBoxを表示切り替えで共有するため、値そのものはここで一元管理する
    private string _deepLApiKey = "";

    // ==== ショートカットキーの記録用状態 ====
    // "startstop" / "overlay" / null(記録中でない)
    private string? _recordingHotkeyTarget;
    private ModifierKeys _startStopModifiers;
    private Key _startStopKey;
    private ModifierKeys _overlayModifiers;
    private Key _overlayKey;

    public SettingsWindow(AppSettings currentSettings, HttpClient sharedHttpClient)
    {
        InitializeComponent();
        Settings = currentSettings;
        _httpClient = sharedHttpClient;

        // デバイス一覧を読み込む。保存済みのDeviceId(一意なOS識別子)があればそれを優先して選択し、
        // 無ければ従来どおり名前の部分一致にフォールバックする(同名デバイスが複数ある場合の誤選択を避けるため)
        var devices = AudioPipeline.GetAvailableDevices();
        DeviceComboBox.ItemsSource = devices;
        DeviceComboBox.DisplayMemberPath = nameof(AudioDeviceInfo.Name);

        var matchedDevice = !string.IsNullOrWhiteSpace(Settings.DeviceId)
            ? devices.FirstOrDefault(d => d.Id == Settings.DeviceId)
            : null;
        matchedDevice ??= devices.FirstOrDefault(
            d => d.Name.Contains(Settings.DeviceKeyword, System.StringComparison.OrdinalIgnoreCase));
        DeviceComboBox.SelectedItem = matchedDevice ?? devices.FirstOrDefault();

        // Whisperモデルファイル(ggml-*.bin)を自動検出してドロップダウンに反映。
        // 実行ファイルの場所を基準に探索する(AudioPipelineのモデルパス解決と揃える)。
        // 以前はカレントディレクトリ(".")基準だったため、exeをどこから起動したかによって
        // AudioPipeline側の実際の探索結果とここでの一覧表示がずれることがあった
        var modelDirectory = AppContext.BaseDirectory;
        var modelFiles = System.IO.Directory.Exists(modelDirectory)
            ? System.IO.Directory.GetFiles(modelDirectory, "ggml-*.bin").Select(System.IO.Path.GetFileName).ToList()
            : new List<string?>();
        foreach (var file in modelFiles)
        {
            WhisperModelComboBox.Items.Add(file);
        }
        WhisperModelComboBox.Text = Settings.WhisperModelPath;

        // 認識言語・翻訳先言語のドロップダウンを初期化
        RecognitionLanguageComboBox.ItemsSource = LanguageCatalog.SourceLanguages;
        RecognitionLanguageComboBox.SelectedItem = LanguageCatalog.SourceLanguages
            .FirstOrDefault(l => l.WhisperCode == Settings.RecognitionLanguage) ?? LanguageCatalog.SourceLanguages[0];

        TargetLanguageComboBox.ItemsSource = LanguageCatalog.TargetLanguages;
        TargetLanguageComboBox.SelectedItem = LanguageCatalog.FindByDeepLCode(Settings.TargetLanguageCode);

        // テキストボックス類を先にセットしておく(バックエンド選択の復元がイベントを発火させ、
        // その中でエンドポイント値を参照するモデル一覧取得が走るため、先に値を確定させる必要がある)
        _deepLApiKey = Settings.DeepLApiKey;
        DeepLApiKeyPasswordBox.Password = _deepLApiKey;
        OllamaModelComboBox.Text = Settings.OllamaModel;
        OllamaEndpointTextBox.Text = Settings.OllamaEndpoint;
        VadThresholdSlider.Value = Settings.VadThreshold;
        VadHysteresisSlider.Value = Settings.VadHysteresisRatio;
        GameAudioPriorityCheckBox.IsChecked = Settings.GameAudioPriorityMode;
        GameAudioPriorityMultiplierSlider.Value = Settings.GameAudioPriorityMultiplier;
        OverlayFontSizeSlider.Value = Settings.OverlayFontSize;
        OverlayOpacitySlider.Value = Settings.OverlayOpacity;
        OverlayMaxLinesSlider.Value = Settings.OverlayMaxLines;
        WhisperPromptTextBox.Text = Settings.WhisperPrompt;
        OllamaContextTextBox.Text = Settings.OllamaContext;

        // ショートカットキーの現在値を読み込み、表示に反映
        (_startStopModifiers, _startStopKey) = Settings.GetStartStopHotkey();
        (_overlayModifiers, _overlayKey) = Settings.GetOverlayHotkey();
        UpdateHotkeyDisplays();
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;

        // バックエンド選択を復元
        foreach (ComboBoxItem item in BackendComboBox.Items)
        {
            if ((string)item.Tag == Settings.TranslationBackend)
            {
                BackendComboBox.SelectedItem = item;
                break;
            }
        }
        BackendComboBox.SelectedIndex = BackendComboBox.SelectedIndex < 0 ? 0 : BackendComboBox.SelectedIndex;

        UpdateBackendPanelsVisibility();
        _ = LoadOllamaModelsAsync();

        Closed += SettingsWindow_Closed;
    }

    private void SettingsWindow_Closed(object? sender, EventArgs e)
    {
        // 設定画面を閉じた後もOllamaへのリクエストが裏で残り続けないようキャンセルする。
        // HttpClient自体はMainWindow側でアプリ全体を通じて共有されているため、ここではDisposeしない
        // (Disposeすると、次に設定画面を開いたときや録音中のMainWindow側の通信まで壊れてしまう)。
        _ollamaLoadCts?.Cancel();
        _ollamaLoadCts?.Dispose();
    }

    /// <summary>Ollamaにインストール済みのモデル一覧を取得し、ドロップダウンに反映する</summary>
    private async Task LoadOllamaModelsAsync()
    {
        StatusText.Text = "";

        // Ollamaが応答しない場合に無期限に待たされないよう、タイムアウトを設ける。
        // また設定画面を閉じた場合はこのリクエストごとキャンセルする
        _ollamaLoadCts?.Cancel();
        _ollamaLoadCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        List<string> models;
        try
        {
            models = await OllamaTranslationService.GetInstalledModelsAsync(
                _httpClient, OllamaEndpointTextBox.Text, _ollamaLoadCts.Token);
        }
        catch (OperationCanceledException)
        {
            // ウィンドウを閉じた、またはタイムアウトした場合。閉じた後ならUI更新は不要
            if (!IsLoaded) return;
            StatusText.Text = "Ollamaへの接続がタイムアウトしました。Ollamaが起動しているか確認してください。";
            return;
        }
        catch (Exception ex)
        {
            Logger.Log("SettingsWindow", "Ollamaモデル一覧の取得に失敗しました。", ex);
            StatusText.Text = $"Ollamaのモデル一覧を取得できませんでした: {ex.Message}";
            return;
        }

        var previouslySelected = OllamaModelComboBox.Text;
        OllamaModelComboBox.Items.Clear();
        foreach (var model in models)
        {
            OllamaModelComboBox.Items.Add(model);
        }

        // 取得前に入力/設定されていたモデル名を維持する。一致するものがあればそれを選択する
        if (models.Contains(previouslySelected))
        {
            OllamaModelComboBox.SelectedItem = previouslySelected;
        }
        else
        {
            OllamaModelComboBox.Text = previouslySelected;
        }

        if (models.Count == 0)
        {
            StatusText.Text = "Ollamaにモデルがインストールされていないようです";
        }
    }

    private void DeepLApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _deepLApiKey = DeepLApiKeyPasswordBox.Password;
    }

    private void DeepLApiKeyTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _deepLApiKey = DeepLApiKeyTextBox.Text;
    }

    /// <summary>「表示」トグルON: 画面共有中などに気付けるよう、既定では隠しているキーを平文表示に切り替える</summary>
    private void DeepLApiKeyVisibilityToggle_Checked(object sender, RoutedEventArgs e)
    {
        DeepLApiKeyTextBox.Text = _deepLApiKey;
        DeepLApiKeyTextBox.Visibility = Visibility.Visible;
        DeepLApiKeyPasswordBox.Visibility = Visibility.Collapsed;
    }

    private void DeepLApiKeyVisibilityToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        DeepLApiKeyPasswordBox.Password = _deepLApiKey;
        DeepLApiKeyPasswordBox.Visibility = Visibility.Visible;
        DeepLApiKeyTextBox.Visibility = Visibility.Collapsed;
    }

    private void BackendComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateBackendPanelsVisibility();

        bool isOllama = (BackendComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "ollama";
        if (isOllama && OllamaModelComboBox.Items.Count == 0)
        {
            _ = LoadOllamaModelsAsync();
        }
    }

    /// <summary>
    /// 「モデル一覧を更新」ボタン。
    /// 以前はOllamaEndpointTextBox自体の変更を監視しておらず、「Ollamaに切り替えた瞬間、
    /// モデル一覧が空の場合のみ自動取得する」条件だったため、エンドポイントを書き換えても
    /// 一覧は古いまま(または空のまま)で、設定画面を一度閉じて開き直さないと反映されなかった。
    /// TextChangedでの自動更新は「入力中に何度もOllamaへリクエストが飛ぶ」ことになり
    /// かえって扱いにくいため、明示的なボタンとして提供する。
    /// </summary>
    private void OllamaEndpointRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadOllamaModelsAsync();
    }

    private void UpdateBackendPanelsVisibility()
    {
        // XAMLロード中(まだ子要素が無い)は何もしない
        if (DeepLPanel == null || OllamaPanel == null || OllamaContextPanel == null) return;

        bool isOllama = (BackendComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "ollama";
        DeepLPanel.Visibility = isOllama ? Visibility.Collapsed : Visibility.Visible;
        OllamaPanel.Visibility = isOllama ? Visibility.Visible : Visibility.Collapsed;
        // 参考コンテキストは「言語」タブ側にあるが、Ollama使用時のみ意味を持つため
        // バックエンド選択(エンジンタブ)と連動して表示/非表示を切り替える
        OllamaContextPanel.Visibility = isOllama ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>左サイドバーの選択に応じて、右側の表示ページを切り替える</summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // XAMLロード中(まだ各ページの要素が無い)は何もしない
        if (AudioPage == null || EnginePage == null || LanguagePage == null
            || OverlayPage == null || ShortcutPage == null || AboutPage == null) return;

        AudioPage.Visibility = NavAudio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EnginePage.Visibility = NavEngine.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LanguagePage.Visibility = NavLanguage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OverlayPage.Visibility = NavOverlay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ShortcutPage.Visibility = NavShortcuts.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==== ショートカットキーの記録 ====

    private static string FormatHotkey(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join(" + ", parts);
    }

    private void UpdateHotkeyDisplays()
    {
        StartStopHotkeyText.Text = FormatHotkey(_startStopModifiers, _startStopKey);
        OverlayHotkeyText.Text = FormatHotkey(_overlayModifiers, _overlayKey);
    }

    private void StartStopHotkeyChangeButton_Click(object sender, RoutedEventArgs e)
    {
        BeginRecordingHotkey("startstop");
    }

    private void OverlayHotkeyChangeButton_Click(object sender, RoutedEventArgs e)
    {
        BeginRecordingHotkey("overlay");
    }

    private void BeginRecordingHotkey(string target)
    {
        _recordingHotkeyTarget = target;
        StatusText.Text = "";
        var placeholder = "キーを入力してください(Escで取消)...";
        if (target == "startstop") StartStopHotkeyText.Text = placeholder;
        else OverlayHotkeyText.Text = placeholder;
    }

    /// <summary>ショートカットキー記録中、ウィンドウ全体でキー入力を捕捉する。
    /// フォーカスがどのコントロールにあってもPreview(トンネリング)イベントなので確実に拾える。</summary>
    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingHotkeyTarget == null) return;

        e.Handled = true;

        // Alt絡みの組み合わせはKey.Systemとして通知され、実際のキーはSystemKeyに入る
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 修飾キー単体の押下はまだ入力途中なので、次のキー入力を待つ
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
        {
            return;
        }

        if (key == Key.Escape)
        {
            _recordingHotkeyTarget = null;
            UpdateHotkeyDisplays();
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            StatusText.Text = "修飾キー(Ctrl・Alt・Shift・Winのいずれか)を1つ以上組み合わせてください。";
            _recordingHotkeyTarget = null;
            UpdateHotkeyDisplays();
            return;
        }

        if (_recordingHotkeyTarget == "startstop")
        {
            _startStopModifiers = modifiers;
            _startStopKey = key;
        }
        else
        {
            _overlayModifiers = modifiers;
            _overlayKey = key;
        }

        _recordingHotkeyTarget = null;
        UpdateHotkeyDisplays();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // 記録中に保存を押した場合は一旦キャンセル扱いにする
        _recordingHotkeyTarget = null;

        // 「翻訳開始/停止」と「オーバーレイ表示切り替え」に同じ組み合わせが割り当てられていると
        // 常に両方が反応してしまい紛らわしいため、保存前に検証する
        if (_startStopModifiers == _overlayModifiers && _startStopKey == _overlayKey)
        {
            StatusText.Text = "「翻訳開始/停止」と「オーバーレイ表示切り替え」に同じショートカットキーは設定できません。";
            UpdateHotkeyDisplays();
            return;
        }

        if (DeviceComboBox.SelectedItem is AudioDeviceInfo selectedDevice)
        {
            Settings.DeviceId = selectedDevice.Id;
            Settings.DeviceKeyword = selectedDevice.Name;
        }
        Settings.WhisperModelPath = WhisperModelComboBox.Text;
        Settings.TranslationBackend = (BackendComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "deepl";
        Settings.DeepLApiKey = _deepLApiKey;
        Settings.OllamaModel = OllamaModelComboBox.Text;
        Settings.OllamaEndpoint = OllamaEndpointTextBox.Text;
        Settings.VadThreshold = (float)VadThresholdSlider.Value;
        Settings.VadHysteresisRatio = (float)VadHysteresisSlider.Value;
        Settings.GameAudioPriorityMode = GameAudioPriorityCheckBox.IsChecked == true;
        Settings.GameAudioPriorityMultiplier = (float)GameAudioPriorityMultiplierSlider.Value;
        Settings.OverlayFontSize = OverlayFontSizeSlider.Value;
        Settings.OverlayOpacity = OverlayOpacitySlider.Value;
        Settings.OverlayMaxLines = (int)OverlayMaxLinesSlider.Value;
        Settings.WhisperPrompt = WhisperPromptTextBox.Text;
        Settings.RecognitionLanguage = (RecognitionLanguageComboBox.SelectedItem as LanguageOption)?.WhisperCode ?? "auto";
        Settings.TargetLanguageCode = (TargetLanguageComboBox.SelectedItem as LanguageOption)?.DeepLCode ?? "JA";
        Settings.OllamaContext = OllamaContextTextBox.Text;
        Settings.StartStopHotkeyModifiers = _startStopModifiers.ToString();
        Settings.StartStopHotkeyKey = _startStopKey.ToString();
        Settings.OverlayHotkeyModifiers = _overlayModifiers.ToString();
        Settings.OverlayHotkeyKey = _overlayKey.ToString();

        Settings.SaveToEnv();

        if (Settings.LastSaveDeepLKeySaveFailed)
        {
            // APIキーを安全に(DPAPI暗号化して)保存できなかったため、平文フォールバックはせず
            // 保存自体を見送っている。他の設定は保存済みなのでダイアログは閉じるが、
            // ユーザーには気づけるよう明示的に警告する。
            MessageBox.Show(
                "DeepL APIキーを安全に保存できませんでした(暗号化に失敗しました)。\n" +
                "他の設定は保存されましたが、APIキーは変更前の値のままです。",
                "APIキーの保存に失敗しました", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>「このアプリについて」内のリポジトリリンクを、既定のブラウザで開く。
    /// UseShellExecute=trueが必要(.NET Core以降はProcess.Startの既定がfalseになり、
    /// 指定しないとURLを直接起動できずWin32Exceptionになる)。</summary>
    private void RepositoryLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Log("SettingsWindow", "リポジトリリンクを開けませんでした。", ex);
            StatusText.Text = "リンクを開けませんでした。ブラウザで手動で開いてください。";
        }
        e.Handled = true;
    }
}
