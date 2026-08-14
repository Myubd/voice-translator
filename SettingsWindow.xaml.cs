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

    public SettingsWindow(AppSettings currentSettings)
    {
        InitializeComponent();
        Settings = currentSettings;

        // デバイス一覧を読み込み、現在のキーワードに一致するものを選択状態にする
        var deviceNames = AudioPipeline.GetAvailableDeviceNames();
        foreach (var name in deviceNames)
        {
            DeviceComboBox.Items.Add(name);
        }
        var matchedDevice = deviceNames.FirstOrDefault(
            n => n.Contains(Settings.DeviceKeyword, System.StringComparison.OrdinalIgnoreCase));
        DeviceComboBox.SelectedItem = matchedDevice ?? deviceNames.FirstOrDefault();

        // Whisperモデルファイル(ggml-*.bin)を自動検出してドロップダウンに反映
        var modelFiles = System.IO.Directory.Exists(".")
            ? System.IO.Directory.GetFiles(".", "ggml-*.bin").Select(System.IO.Path.GetFileName).ToList()
            : new System.Collections.Generic.List<string?>();
        foreach (var file in modelFiles)
        {
            WhisperModelComboBox.Items.Add(file);
        }
        WhisperModelComboBox.Text = Settings.WhisperModelPath;

        // テキストボックス類を先にセットしておく(バックエンド選択の復元がイベントを発火させ、
        // その中でエンドポイント値を参照するモデル一覧取得が走るため、先に値を確定させる必要がある)
        DeepLApiKeyTextBox.Text = Settings.DeepLApiKey;
        OllamaModelComboBox.Text = Settings.OllamaModel;
        OllamaEndpointTextBox.Text = Settings.OllamaEndpoint;
        VadThresholdSlider.Value = Settings.VadThreshold;

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

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Settings.DeviceKeyword = DeviceComboBox.SelectedItem as string ?? Settings.DeviceKeyword;
        Settings.WhisperModelPath = WhisperModelComboBox.Text;
        Settings.TranslationBackend = (BackendComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "deepl";
        Settings.DeepLApiKey = DeepLApiKeyTextBox.Text;
        Settings.OllamaModel = OllamaModelComboBox.Text;
        Settings.OllamaEndpoint = OllamaEndpointTextBox.Text;
        Settings.VadThreshold = (float)VadThresholdSlider.Value;

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
