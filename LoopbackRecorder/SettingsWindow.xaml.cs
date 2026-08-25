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
using System.Windows.Media;
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

    // オーバーレイの文字色プリセット。(表示名, #RRGGBB)。
    // フルカラーピッカーではなくプリセットに絞っているのは、ゲーム配信中でも視認性が
    // 確保しやすい色(コントラストが十分な明るい色)だけに選択肢を限定するため。
    private static readonly (string Name, string Hex)[] OverlayColorPresets =
    [
        ("白", "#FFFFFF"),
        ("黄", "#FFE066"),
        ("水色", "#66D9FF"),
        ("緑", "#7CFC8C"),
        ("ピンク", "#FF8FCB"),
    ];

    private string _selectedOverlayColor = OverlayColorPresets[0].Hex;
    private readonly List<Border> _overlayColorSwatches = new();

    // ゲームプロファイルページ用。ProfileListComboBoxには名前(string)だけを表示し、
    // 実データはこちらのリストから名前で引く(GameProfileStore.LoadAllを毎回呼ぶと
    // 選択変更のたびにファイルI/Oが走ってしまうため、ページ表示時に一度だけ読み込んでおく)。
    private List<GameProfile> _gameProfiles = new();

    // ==== ショートカットキーの記録用状態 ====
    // "startstop" / "overlay" / null(記録中でない)
    private string? _recordingHotkeyTarget;
    private ModifierKeys _startStopModifiers;
    private Key _startStopKey;
    private ModifierKeys _overlayModifiers;
    private Key _overlayKey;
    private ModifierKeys _ocrModifiers;
    private Key _ocrKey;

    public SettingsWindow(AppSettings currentSettings, HttpClient sharedHttpClient, bool isRunning = false)
    {
        InitializeComponent();
        Settings = currentSettings;
        _httpClient = sharedHttpClient;

        // 翻訳実行中に設定画面を開いた場合、ここで変更した内容(モデル名・APIキー等)は
        // 実行中のAudioPipelineには即座に反映されない(次回の「翻訳開始」時に初めて使われる)。
        // 気づかないまま「設定を変えたのに反映されない」と誤解されるのを防ぐため、
        // バナーで明示する。
        RunningWarningBanner.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;

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

        // XAML側は固定のMaximum=16を仮置きしているだけなので、実機の論理コア数に合わせて上書きする
        // (コア数が16を超えるハイエンド環境でも、逆にコア数が少ない環境でも矛盾しないように)
        WhisperThreadCountSlider.Maximum = Environment.ProcessorCount;
        WhisperThreadCountSlider.Value = Math.Clamp(Settings.WhisperThreadCount, 1, Environment.ProcessorCount);
        TranslationWorkerCountSlider.Value = Math.Clamp(Settings.TranslationWorkerCount, 1, 4);
        MaxLatencySecondsSlider.Value = Math.Clamp(Settings.MaxLatencySeconds, 0, 10);

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
        DeepLToOllamaFallbackCheckBox.IsChecked = Settings.EnableDeepLToOllamaFallback;
        TranslationCacheCheckBox.IsChecked = Settings.EnableTranslationCache;
        VadThresholdSlider.Value = Settings.VadThreshold;
        VadHysteresisSlider.Value = Settings.VadHysteresisRatio;
        GameAudioPriorityCheckBox.IsChecked = Settings.GameAudioPriorityMode;
        GameAudioPriorityMultiplierSlider.Value = Settings.GameAudioPriorityMultiplier;
        OverlayFontSizeSlider.Value = Settings.OverlayFontSize;
        OverlayOpacitySlider.Value = Settings.OverlayOpacity;
        OverlayMaxLinesSlider.Value = Settings.OverlayMaxLines;
        InitializeOverlayColorSwatches(Settings.OverlayFontColor);
        WhisperPromptTextBox.Text = Settings.WhisperPrompt;
        OllamaContextTextBox.Text = Settings.OllamaContext;
        ManualGlossaryTextBox.Text = Settings.ManualGlossary;
        OcrSourceLanguageComboBox.ItemsSource = LanguageCatalog.TargetLanguages;
        OcrSourceLanguageComboBox.SelectedItem = LanguageCatalog.FindByWhisperCode(Settings.OcrSourceLanguageTag);

        // ショートカットキーの現在値を読み込み、表示に反映
        (_startStopModifiers, _startStopKey) = Settings.GetStartStopHotkey();
        (_overlayModifiers, _overlayKey) = Settings.GetOverlayHotkey();
        (_ocrModifiers, _ocrKey) = Settings.GetOcrHotkey();
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
        SetStatusMessage("");

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
            SetStatusMessage("Ollamaへの接続がタイムアウトしました。Ollamaが起動しているか確認してください。");
            return;
        }
        catch (Exception ex)
        {
            Logger.Log("SettingsWindow", "Ollamaモデル一覧の取得に失敗しました。", ex);
            SetStatusMessage($"Ollamaのモデル一覧を取得できませんでした: {ex.Message}");
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
            SetStatusMessage("Ollamaにモデルがインストールされていないようです");
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
        // Ollamaモデル一覧の自動取得は、フォールバック用の表示も含めてUpdateBackendPanelsVisibility側で
        // 一元的に判定する(以前はここにも同じ条件の呼び出しがあり、フォールバック表示との条件が
        // 二重管理でズレる原因になっていたため統合した)
        UpdateBackendPanelsVisibility();
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

    /// <summary>「DeepL失敗時にOllamaへフォールバック」の有効/無効切り替え。
    /// フォールバック先の設定(モデル名・エンドポイント)を入力できるようOllamaPanelの
    /// 表示状態を更新する。</summary>
    private void DeepLToOllamaFallbackCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        UpdateBackendPanelsVisibility();
    }

    /// <summary>オーバーレイの文字色プリセットを、選択可能な色スウォッチ(色付きの小さな正方形)として
    /// OverlayColorSwatchPanelに動的に生成する。XAMLで5個分を毎回手書きする代わりに、
    /// OverlayColorPresets配列を単一の情報源にすることで、プリセットの追加/変更が1箇所で済むようにしている。
    ///
    /// 以前はToggleButtonをそのまま使っていたが、既定のチェック状態の見た目がColor塗りの背景に
    /// 埋もれてほとんど分からず、「何を選んでいるか分からない」というフィードバックを受けた。
    /// Borderで自前描画にし、選択中は太いアクセントカラーの枠+チェックマークを表示することで、
    /// どの背景色でも確実に視認できるようにしている。</summary>
    private void InitializeOverlayColorSwatches(string selectedHex)
    {
        OverlayColorSwatchPanel.Children.Clear();
        _overlayColorSwatches.Clear();

        foreach (var preset in OverlayColorPresets)
        {
            var color = (Color)ColorConverter.ConvertFromString(preset.Hex);

            var checkmark = new TextBlock
            {
                Text = "\uE73E", // Segoe MDL2 Assets: チェックマーク(他のアイコンと同じフォントで統一)
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                // スウォッチは白・黄・水色・緑・ピンクいずれも明るい色なので、黒で固定しても
                // どの色の上でも視認できる
                Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };

            var swatch = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(color),
                Cursor = Cursors.Hand,
                Tag = preset.Hex,
                ToolTip = preset.Name,
                Child = checkmark,
            };
            swatch.MouseLeftButtonDown += (_, _) => SelectOverlayColorSwatch(swatch);

            OverlayColorSwatchPanel.Children.Add(swatch);
            _overlayColorSwatches.Add(swatch);
        }

        var initiallySelected = _overlayColorSwatches.FirstOrDefault(s =>
            string.Equals((string)s.Tag, selectedHex, StringComparison.OrdinalIgnoreCase)) ?? _overlayColorSwatches[0];
        SelectOverlayColorSwatch(initiallySelected);
    }

    // 選択中のスウォッチの枠線色・太さ(未選択時と区別が確実につくよう、アクセントカラー+太めにしている)
    private static readonly SolidColorBrush OverlaySwatchSelectedBorder = new(Color.FromRgb(0x29, 0xC7, 0xC1));
    private static readonly SolidColorBrush OverlaySwatchNormalBorder = new(Color.FromRgb(0x55, 0x55, 0x66));

    private void SelectOverlayColorSwatch(Border selected)
    {
        _selectedOverlayColor = (string)selected.Tag;

        foreach (var swatch in _overlayColorSwatches)
        {
            bool isSelected = ReferenceEquals(swatch, selected);
            swatch.BorderBrush = isSelected ? OverlaySwatchSelectedBorder : OverlaySwatchNormalBorder;
            swatch.BorderThickness = new Thickness(isSelected ? 3 : 1);
            ((TextBlock)swatch.Child).Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ==== ゲームプロファイル ====
    // 対象は「翻訳エンジン」「言語」「オーバーレイ」タブの入力内容のみ(デバイス選択・
    // ホットキー・APIキー等は対象外。GameProfile.csのコメント参照)。

    /// <summary>game_profiles.jsonから一覧を読み込み、ProfileListComboBoxへ反映する。
    /// 「ゲームプロファイル」ページを開くたび(Nav_Checked経由)に呼ばれるため、他の設定画面
    /// インスタンスで保存・削除された内容もページを開き直せば反映される。</summary>
    private void RefreshProfileList()
    {
        _gameProfiles = GameProfileStore.LoadAll();

        var previouslySelected = ProfileListComboBox.SelectedItem as string;
        ProfileListComboBox.ItemsSource = _gameProfiles.Select(p => p.Name).ToList();

        if (previouslySelected != null && _gameProfiles.Any(p => p.Name == previouslySelected))
        {
            ProfileListComboBox.SelectedItem = previouslySelected;
        }
        else if (_gameProfiles.Count > 0)
        {
            ProfileListComboBox.SelectedIndex = 0;
        }

        UpdateProfileButtonsEnabled();
    }

    private void UpdateProfileButtonsEnabled()
    {
        bool hasSelection = ProfileListComboBox.SelectedItem != null;
        ProfileLoadButton.IsEnabled = hasSelection;
        ProfileDeleteButton.IsEnabled = hasSelection;
    }

    private void ProfileListComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAMLロード中、ItemsSource未設定の段階でも発火しうるため、ボタン側のnullは
        // UpdateProfileButtonsEnabled内では起きない(ProfileLoadButton/ProfileDeleteButtonは
        // 同じProfilesPage内の兄弟要素であり、このイベント発火時点で必ず生成済みのため)
        UpdateProfileButtonsEnabled();
    }

    /// <summary>選択中のプロファイルの値を、この設定画面のUIコントロールへ反映する。
    /// AppSettings(Settings)自体は書き換えない(この画面自体の「保存」を押すまでは確定させない、
    /// という他の設定項目と同じ挙動に揃えるため)。</summary>
    private void ApplyProfileToUiFields(GameProfile profile)
    {
        foreach (ComboBoxItem item in BackendComboBox.Items)
        {
            if ((string)item.Tag == profile.TranslationBackend)
            {
                BackendComboBox.SelectedItem = item;
                break;
            }
        }
        UpdateBackendPanelsVisibility();

        TargetLanguageComboBox.SelectedItem = LanguageCatalog.FindByDeepLCode(profile.TargetLanguageCode);
        OllamaContextTextBox.Text = profile.OllamaContext;
        ManualGlossaryTextBox.Text = profile.ManualGlossary;

        OverlayFontSizeSlider.Value = profile.OverlayFontSize;
        OverlayOpacitySlider.Value = profile.OverlayOpacity;
        OverlayMaxLinesSlider.Value = profile.OverlayMaxLines;
        InitializeOverlayColorSwatches(profile.OverlayFontColor);
    }

    private void ProfileLoadButton_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileListComboBox.SelectedItem as string;
        var profile = _gameProfiles.FirstOrDefault(p => p.Name == name);
        if (profile == null) return;

        ApplyProfileToUiFields(profile);
        MessageBox.Show(
            $"プロファイル「{profile.Name}」を読み込みました。実際に反映するには、この設定画面自体を「保存」してください。",
            "プロファイルを読み込みました", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ProfileDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileListComboBox.SelectedItem as string;
        if (name == null) return;

        var confirm = MessageBox.Show(
            $"プロファイル「{name}」を削除します。この操作は取り消せません。よろしいですか?",
            "プロファイルの削除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        GameProfileStore.Delete(name);
        RefreshProfileList();
    }

    /// <summary>この設定画面上で「現在入力中」の翻訳エンジン・言語・オーバーレイの内容を
    /// 新しいプロファイルとして保存する。SaveButton_Click(この設定画面自体の保存)を押していなくても
    /// 保存できる(プロファイル保存とアプリ全体設定の保存は別の操作として独立させている)。</summary>
    private void ProfileSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileNewNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("プロファイル名を入力してください。", "プロファイルの保存",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_gameProfiles.Any(p => p.Name == name))
        {
            var confirm = MessageBox.Show(
                $"同名のプロファイル「{name}」が既にあります。上書きしますか?",
                "プロファイルの保存", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        var profile = new GameProfile
        {
            Name = name,
            TranslationBackend = (BackendComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "deepl",
            TargetLanguageCode = (TargetLanguageComboBox.SelectedItem as LanguageOption)?.DeepLCode ?? "JA",
            OllamaContext = OllamaContextTextBox.Text,
            ManualGlossary = ManualGlossaryTextBox.Text,
            OverlayFontSize = OverlayFontSizeSlider.Value,
            OverlayOpacity = OverlayOpacitySlider.Value,
            OverlayMaxLines = (int)OverlayMaxLinesSlider.Value,
            OverlayFontColor = _selectedOverlayColor,
        };

        GameProfileStore.Upsert(profile);
        ProfileNewNameTextBox.Text = "";
        RefreshProfileList();
        ProfileListComboBox.SelectedItem = name;
    }

    private void UpdateBackendPanelsVisibility()
    {
        // XAMLロード中(まだ子要素が無い)は何もしない
        if (DeepLPanel == null || OllamaPanel == null || DeepLToOllamaFallbackCheckBox == null) return;

        bool isOllama = (BackendComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "ollama";
        // DeepL選択時でも「DeepL失敗時にOllamaへフォールバック」が有効な場合は、
        // フォールバック先として使うOllamaモデル名/エンドポイント/参考コンテキストを設定できるよう
        // OllamaPanelを表示する(参考コンテキストはOllamaPanelにネストされているため、
        // このOllamaPanelの表示/非表示だけで連動して切り替わる)
        bool showOllamaPanel = isOllama || DeepLToOllamaFallbackCheckBox.IsChecked == true;

        DeepLPanel.Visibility = isOllama ? Visibility.Collapsed : Visibility.Visible;
        OllamaPanel.Visibility = showOllamaPanel ? Visibility.Visible : Visibility.Collapsed;

        if (showOllamaPanel && OllamaModelComboBox.Items.Count == 0)
        {
            _ = LoadOllamaModelsAsync();
        }
    }

    /// <summary>設定画面下部のステータスメッセージ(接続エラー・入力エラー等)を表示/非表示する。
    /// メッセージが空文字なら、アラートボックス自体を折りたたんで場所を取らないようにする。
    /// StatusText.Textへの直接代入ではなく必ずこちらを経由すること(表示/非表示の連動漏れを防ぐため)。</summary>
    private void SetStatusMessage(string message)
    {
        StatusText.Text = message;
        StatusMessageBanner.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>左サイドバーの選択に応じて、右側の表示ページを切り替える</summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // XAMLロード中(まだ各ページの要素が無い)は何もしない
        if (AudioPage == null || EnginePage == null || LanguagePage == null
            || OverlayPage == null || ProfilesPage == null || ShortcutPage == null || AboutPage == null) return;

        AudioPage.Visibility = NavAudio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EnginePage.Visibility = NavEngine.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LanguagePage.Visibility = NavLanguage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OverlayPage.Visibility = NavOverlay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = NavProfiles.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ShortcutPage.Visibility = NavShortcuts.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavProfiles.IsChecked == true)
        {
            RefreshProfileList();
        }
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
        OcrHotkeyText.Text = FormatHotkey(_ocrModifiers, _ocrKey);
    }

    private void StartStopHotkeyChangeButton_Click(object sender, RoutedEventArgs e)
    {
        BeginRecordingHotkey("startstop");
    }

    private void OverlayHotkeyChangeButton_Click(object sender, RoutedEventArgs e)
    {
        BeginRecordingHotkey("overlay");
    }

    private void OcrHotkeyChangeButton_Click(object sender, RoutedEventArgs e)
    {
        BeginRecordingHotkey("ocr");
    }

    private void BeginRecordingHotkey(string target)
    {
        _recordingHotkeyTarget = target;
        SetStatusMessage("");
        var placeholder = "キーを入力してください(Escで取消)...";
        if (target == "startstop") StartStopHotkeyText.Text = placeholder;
        else if (target == "overlay") OverlayHotkeyText.Text = placeholder;
        else OcrHotkeyText.Text = placeholder;
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
            SetStatusMessage("修飾キー(Ctrl・Alt・Shift・Winのいずれか)を1つ以上組み合わせてください。");
            _recordingHotkeyTarget = null;
            UpdateHotkeyDisplays();
            return;
        }

        if (_recordingHotkeyTarget == "startstop")
        {
            _startStopModifiers = modifiers;
            _startStopKey = key;
        }
        else if (_recordingHotkeyTarget == "overlay")
        {
            _overlayModifiers = modifiers;
            _overlayKey = key;
        }
        else
        {
            _ocrModifiers = modifiers;
            _ocrKey = key;
        }

        _recordingHotkeyTarget = null;
        UpdateHotkeyDisplays();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // 記録中に保存を押した場合は一旦キャンセル扱いにする
        _recordingHotkeyTarget = null;

        // 「翻訳開始/停止」「オーバーレイ表示切り替え」「OCR単発翻訳」のいずれか2つに同じ組み合わせが
        // 割り当てられていると常に両方が反応してしまい紛らわしいため、保存前に全ペアを検証する
        var assignments = new (string Label, ModifierKeys Modifiers, Key Key)[]
        {
            ("「翻訳開始/停止」", _startStopModifiers, _startStopKey),
            ("「オーバーレイ表示切り替え」", _overlayModifiers, _overlayKey),
            ("「OCR単発翻訳」", _ocrModifiers, _ocrKey),
        };
        for (int i = 0; i < assignments.Length; i++)
        {
            for (int j = i + 1; j < assignments.Length; j++)
            {
                if (assignments[i].Modifiers == assignments[j].Modifiers && assignments[i].Key == assignments[j].Key)
                {
                    SetStatusMessage($"{assignments[i].Label}と{assignments[j].Label}に同じショートカットキーは設定できません。");
                    UpdateHotkeyDisplays();
                    return;
                }
            }
        }

        if (DeviceComboBox.SelectedItem is AudioDeviceInfo selectedDevice)
        {
            Settings.DeviceId = selectedDevice.Id;
            Settings.DeviceKeyword = selectedDevice.Name;
        }
        Settings.WhisperModelPath = WhisperModelComboBox.Text;
        Settings.WhisperThreadCount = (int)WhisperThreadCountSlider.Value;
        Settings.TranslationWorkerCount = (int)TranslationWorkerCountSlider.Value;
        Settings.MaxLatencySeconds = MaxLatencySecondsSlider.Value;
        Settings.TranslationBackend = (BackendComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "deepl";
        Settings.DeepLApiKey = _deepLApiKey;
        Settings.OllamaModel = OllamaModelComboBox.Text;
        Settings.OllamaEndpoint = OllamaEndpointTextBox.Text;
        Settings.EnableDeepLToOllamaFallback = DeepLToOllamaFallbackCheckBox.IsChecked == true;
        Settings.EnableTranslationCache = TranslationCacheCheckBox.IsChecked == true;
        Settings.VadThreshold = (float)VadThresholdSlider.Value;
        Settings.VadHysteresisRatio = (float)VadHysteresisSlider.Value;
        Settings.GameAudioPriorityMode = GameAudioPriorityCheckBox.IsChecked == true;
        Settings.GameAudioPriorityMultiplier = (float)GameAudioPriorityMultiplierSlider.Value;
        Settings.OverlayFontSize = OverlayFontSizeSlider.Value;
        Settings.OverlayOpacity = OverlayOpacitySlider.Value;
        Settings.OverlayMaxLines = (int)OverlayMaxLinesSlider.Value;
        Settings.OverlayFontColor = _selectedOverlayColor;
        Settings.WhisperPrompt = WhisperPromptTextBox.Text;
        Settings.RecognitionLanguage = (RecognitionLanguageComboBox.SelectedItem as LanguageOption)?.WhisperCode ?? "auto";
        Settings.TargetLanguageCode = (TargetLanguageComboBox.SelectedItem as LanguageOption)?.DeepLCode ?? "JA";
        Settings.OllamaContext = OllamaContextTextBox.Text;
        Settings.ManualGlossary = ManualGlossaryTextBox.Text;
        Settings.OcrSourceLanguageTag = (OcrSourceLanguageComboBox.SelectedItem as LanguageOption)?.WhisperCode ?? "en";
        Settings.StartStopHotkeyModifiers = _startStopModifiers.ToString();
        Settings.StartStopHotkeyKey = _startStopKey.ToString();
        Settings.OverlayHotkeyModifiers = _overlayModifiers.ToString();
        Settings.OverlayHotkeyKey = _overlayKey.ToString();
        Settings.OcrHotkeyModifiers = _ocrModifiers.ToString();
        Settings.OcrHotkeyKey = _ocrKey.ToString();

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
            SetStatusMessage("リンクを開けませんでした。ブラウザで手動で開いてください。");
        }
        e.Handled = true;
    }
}
