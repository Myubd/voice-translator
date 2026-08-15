using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace LoopbackRecorder;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; private set; }
    private readonly HttpClient _httpClient = new HttpClient();

    // DeepL APIキーはPasswordBox/TextBoxを表示切り替えで共有するため、値そのものはここで一元管理する
    private string _deepLApiKey = "";

    public SettingsWindow(AppSettings currentSettings)
    {
        InitializeComponent();
        Settings = currentSettings;

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

        // Whisperモデルファイル(ggml-*.bin)を自動検出してドロップダウンに反映
        var modelFiles = System.IO.Directory.Exists(".")
            ? System.IO.Directory.GetFiles(".", "ggml-*.bin").Select(System.IO.Path.GetFileName).ToList()
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
        OverlayFontSizeSlider.Value = Settings.OverlayFontSize;
        OverlayOpacitySlider.Value = Settings.OverlayOpacity;
        OverlayMaxLinesSlider.Value = Settings.OverlayMaxLines;
        WhisperPromptTextBox.Text = Settings.WhisperPrompt;
        OllamaContextTextBox.Text = Settings.OllamaContext;

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
    }

    /// <summary>Ollamaにインストール済みのモデル一覧を取得し、ドロップダウンに反映する</summary>
    private async Task LoadOllamaModelsAsync()
    {
        StatusText.Text = "";
        List<string> models;
        try
        {
            models = await OllamaTranslationService.GetInstalledModelsAsync(_httpClient, OllamaEndpointTextBox.Text);
        }
        catch (System.Exception ex)
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

    private void UpdateBackendPanelsVisibility()
    {
        // XAMLロード中(まだ子要素が無い)は何もしない
        if (DeepLPanel == null || OllamaPanel == null) return;

        bool isOllama = (BackendComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "ollama";
        DeepLPanel.Visibility = isOllama ? Visibility.Collapsed : Visibility.Visible;
        OllamaPanel.Visibility = isOllama ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>左サイドバーの選択に応じて、右側の表示ページを切り替える</summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // XAMLロード中(まだ各ページの要素が無い)は何もしない
        if (AudioPage == null || RecognitionPage == null || TranslationPage == null
            || OverlayPage == null || AboutPage == null) return;

        AudioPage.Visibility = NavAudio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RecognitionPage.Visibility = NavRecognition.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TranslationPage.Visibility = NavTranslation.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OverlayPage.Visibility = NavOverlay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
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
        Settings.OverlayFontSize = OverlayFontSizeSlider.Value;
        Settings.OverlayOpacity = OverlayOpacitySlider.Value;
        Settings.OverlayMaxLines = (int)OverlayMaxLinesSlider.Value;
        Settings.WhisperPrompt = WhisperPromptTextBox.Text;
        Settings.RecognitionLanguage = (RecognitionLanguageComboBox.SelectedItem as LanguageOption)?.WhisperCode ?? "auto";
        Settings.TargetLanguageCode = (TargetLanguageComboBox.SelectedItem as LanguageOption)?.DeepLCode ?? "JA";
        Settings.OllamaContext = OllamaContextTextBox.Text;

        Settings.SaveToEnv();

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
