using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Input;

/// <summary>
/// アプリの設定値をまとめて保持するクラス。
/// .envから読み込み、SettingsWindowでの変更を.envに書き戻せるようにする。
/// </summary>
public class AppSettings
{
    public string DeviceKeyword { get; set; } = "Chat";

    /// <summary>OS上で一意なデバイスID(MMDevice.ID)。設定画面でデバイスを選択すると保存される。
    /// 名前の部分一致(DeviceKeyword)より優先して使われ、同名デバイスの誤選択を防ぐ。
    /// 未設定、またはデバイス構成変更でIDが見つからない場合はDeviceKeywordにフォールバックする。</summary>
    public string DeviceId { get; set; } = "";
    public string TranslationBackend { get; set; } = "deepl"; // "deepl" または "ollama"
    public string DeepLApiKey { get; set; } = "";
    public string OllamaModel { get; set; } = "llama3.1";
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";
    public string WhisperModelPath { get; set; } = "ggml-base.bin";
    public float VadThreshold { get; set; } = 0.015f;

    /// <summary>VADヒステリシス比率(0〜1)。発話継続中の「まだ話している」判定閾値を
    /// 開始閾値(VadThreshold)からどれだけ下げるか。小さいほど息継ぎ等での分断が起きにくくなる。</summary>
    public float VadHysteresisRatio { get; set; } = 0.6f;

    /// <summary>ONの場合、VAD閾値を引き上げ、小さい雑音より大きいゲーム音声を優先的に拾うようにする</summary>
    public bool GameAudioPriorityMode { get; set; } = false;

    /// <summary>ゲーム音声優先モードON時にVAD閾値へ掛ける倍率。
    /// 以前は1.5固定でUIから調整できなかったため、設定画面から変更できるようにした。</summary>
    public float GameAudioPriorityMultiplier { get; set; } = 1.5f;

    public double OverlayFontSize { get; set; } = 22;
    public double OverlayOpacity { get; set; } = 0.7;
    public int OverlayMaxLines { get; set; } = 4;

    /// <summary>固有名詞などの認識精度を上げるため、Whisperに事前情報として渡すヒント文</summary>
    public string WhisperPrompt { get; set; } = "";

    /// <summary>Whisperの認識言語コード(例: "auto", "en", "ja")</summary>
    public string RecognitionLanguage { get; set; } = "auto";

    /// <summary>翻訳先言語のDeepLコード(例: "JA", "EN-US", "KO")</summary>
    public string TargetLanguageCode { get; set; } = "JA";

    /// <summary>Ollama使用時、翻訳の背景知識として渡す参考コンテキスト(記事の抜粋など)</summary>
    public string OllamaContext { get; set; } = "";

    // ==== グローバルショートカットキー ====
    // ModifierKeys/Keyの各Enum名(例: "Control, Alt" / "R")をそのまま文字列として保存する。
    // Enum.TryParseがFlags列挙体のカンマ区切り表記をそのまま解釈できるため、独自のパース処理が不要になる。
    public string StartStopHotkeyModifiers { get; set; } = "Control, Alt";
    public string StartStopHotkeyKey { get; set; } = "R";
    public string OverlayHotkeyModifiers { get; set; } = "Control, Alt";
    public string OverlayHotkeyKey { get; set; } = "O";

    /// <summary>「翻訳開始/停止」に割り当てられたショートカットキーを取得する。値が不正な場合は既定値(Ctrl+Alt+R)を返す</summary>
    public (ModifierKeys Modifiers, Key Key) GetStartStopHotkey() =>
        ParseHotkey(StartStopHotkeyModifiers, StartStopHotkeyKey, ModifierKeys.Control | ModifierKeys.Alt, Key.R);

    /// <summary>「オーバーレイ表示切り替え」に割り当てられたショートカットキーを取得する。値が不正な場合は既定値(Ctrl+Alt+O)を返す</summary>
    public (ModifierKeys Modifiers, Key Key) GetOverlayHotkey() =>
        ParseHotkey(OverlayHotkeyModifiers, OverlayHotkeyKey, ModifierKeys.Control | ModifierKeys.Alt, Key.O);

    private static (ModifierKeys, Key) ParseHotkey(string modifiersStr, string keyStr, ModifierKeys defaultModifiers, Key defaultKey)
    {
        var modifiers = Enum.TryParse<ModifierKeys>(modifiersStr, ignoreCase: true, out var parsedModifiers) ? parsedModifiers : defaultModifiers;
        var key = Enum.TryParse<Key>(keyStr, ignoreCase: true, out var parsedKey) ? parsedKey : defaultKey;
        return (modifiers, key);
    }

    // exeがどのディレクトリから起動されても同じ.envを見つけられるよう、Whisperモデルパスや
    // ログと同じくAppContext.BaseDirectory基準で解決する。以前はカレントディレクトリ基準("./.env")
    // だったため、ショートカット経由の起動や別ディレクトリからの起動で.envが見つからない、
    // または意図しない場所に新規作成されてしまう不具合があった。
    private static readonly string EnvPath = Path.Combine(AppContext.BaseDirectory, ".env");

    // DeepL APIキーを.envに保存する際の暗号化に使うエントロピー(ソルトのようなもの)。
    // Windows DPAPIはユーザーアカウントに紐づけて暗号化するため、これと組み合わせることで
    // 「.envファイルをそのままコピー/誤共有/誤アップロードされても、同じWindowsユーザーの
    // 同じPC以外では復号できない」形にする。
    private static readonly byte[] DeepLKeyEntropy = Encoding.UTF8.GetBytes("LoopbackRecorder.DeepLApiKey.v1");

    public static AppSettings LoadFromEnv()
    {
        EnvLoader.Load(EnvPath);

        var settings = new AppSettings
        {
            DeviceKeyword = Environment.GetEnvironmentVariable("DEVICE_KEYWORD") ?? "Chat",
            DeviceId = Environment.GetEnvironmentVariable("DEVICE_ID") ?? "",
            TranslationBackend = Environment.GetEnvironmentVariable("TRANSLATION_BACKEND") ?? "deepl",
            OllamaModel = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "llama3.1",
            OllamaEndpoint = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? "http://localhost:11434",
            WhisperModelPath = Environment.GetEnvironmentVariable("WHISPER_MODEL_PATH") ?? "ggml-base.bin",
        };

        // DeepL APIキー: DPAPIで暗号化された新形式(DEEPL_API_KEY_ENC)を優先して読み込む。
        // 旧バージョンの平文保存(DEEPL_API_KEY)しか無い場合はそちらを読み込み、
        // 次回SaveToEnv()時に自動的に暗号化形式へ移行される。
        var encryptedKey = Environment.GetEnvironmentVariable("DEEPL_API_KEY_ENC");
        settings.DeepLApiKey = !string.IsNullOrEmpty(encryptedKey)
            ? DecryptDeepLApiKey(encryptedKey)
            : Environment.GetEnvironmentVariable("DEEPL_API_KEY") ?? "";

        // .envのような設定ファイルはOSのカルチャ(小数点がカンマになる地域設定等)に
        // 影響されると壊れるため、数値の読み書きは必ずInvariantCultureで行う
        var vadThresholdStr = Environment.GetEnvironmentVariable("VAD_THRESHOLD");
        if (float.TryParse(vadThresholdStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var vadThreshold))
        {
            settings.VadThreshold = vadThreshold;
        }

        var vadHysteresisStr = Environment.GetEnvironmentVariable("VAD_HYSTERESIS_RATIO");
        if (float.TryParse(vadHysteresisStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var vadHysteresisRatio))
        {
            settings.VadHysteresisRatio = vadHysteresisRatio;
        }

        if (bool.TryParse(Environment.GetEnvironmentVariable("GAME_AUDIO_PRIORITY_MODE"), out var priorityMode))
        {
            settings.GameAudioPriorityMode = priorityMode;
        }
        if (float.TryParse(Environment.GetEnvironmentVariable("GAME_AUDIO_PRIORITY_MULTIPLIER"),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var priorityMultiplier))
        {
            settings.GameAudioPriorityMultiplier = priorityMultiplier;
        }
        if (double.TryParse(Environment.GetEnvironmentVariable("OVERLAY_FONT_SIZE"),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var fontSize))
        {
            settings.OverlayFontSize = fontSize;
        }
        if (double.TryParse(Environment.GetEnvironmentVariable("OVERLAY_OPACITY"),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
        {
            settings.OverlayOpacity = opacity;
        }
        if (int.TryParse(Environment.GetEnvironmentVariable("OVERLAY_MAX_LINES"), out var maxLines))
        {
            settings.OverlayMaxLines = maxLines;
        }
        settings.WhisperPrompt = Environment.GetEnvironmentVariable("WHISPER_PROMPT") ?? "";
        settings.RecognitionLanguage = Environment.GetEnvironmentVariable("RECOGNITION_LANGUAGE") ?? "auto";
        settings.TargetLanguageCode = Environment.GetEnvironmentVariable("TARGET_LANGUAGE_CODE") ?? "JA";
        // 改行のエスケープ(\n → 改行)はEnvLoader側で共通処理済み
        settings.OllamaContext = Environment.GetEnvironmentVariable("OLLAMA_CONTEXT") ?? "";
        settings.OllamaEndpoint = NormalizeEndpoint(settings.OllamaEndpoint);

        settings.StartStopHotkeyModifiers = Environment.GetEnvironmentVariable("START_STOP_HOTKEY_MODIFIERS") ?? "Control, Alt";
        settings.StartStopHotkeyKey = Environment.GetEnvironmentVariable("START_STOP_HOTKEY_KEY") ?? "R";
        settings.OverlayHotkeyModifiers = Environment.GetEnvironmentVariable("OVERLAY_HOTKEY_MODIFIERS") ?? "Control, Alt";
        settings.OverlayHotkeyKey = Environment.GetEnvironmentVariable("OVERLAY_HOTKEY_KEY") ?? "O";

        return settings;
    }

    /// <summary>
    /// Ollamaエンドポイントの入力ゆれを吸収する。
    /// 末尾に"/"が付いていると、呼び出し側で"{endpoint}/api/generate"のように連結した際に
    /// "//"の二重スラッシュになってしまうことがあるため、末尾のスラッシュを正規化する。
    /// URIとして解釈できない値はそのまま返す(呼び出し時のHTTPエラーで気づける)。
    /// </summary>
    private static string NormalizeEndpoint(string endpoint)
    {
        var trimmed = (endpoint ?? "").Trim();
        if (trimmed.Length == 0) return trimmed;

        trimmed = trimmed.TrimEnd('/');

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return trimmed;
        }

        // http/httpsとして妥当でない場合は変更せずそのまま返す
        return trimmed;
    }

    /// <summary>
    /// DeepL APIキーをWindows DPAPI(現在のWindowsユーザーアカウント紐付け)で暗号化し、
    /// .envファイルに書き込める形(Base64文字列)にする。
    /// 非Windows環境(開発機など)ではDPAPIが使えないため、平文のまま返す。
    /// </summary>
    private static string EncryptDeepLApiKey(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        if (!OperatingSystem.IsWindows()) return plainText;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(bytes, DeepLKeyEntropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        catch (Exception ex)
        {
            // 暗号化に失敗した場合でも設定の保存自体は止めたくないため、平文でフォールバックする。
            // (この場合でも次回起動時にDEEPL_API_KEY_ENCとして読み込まれ、再暗号化が試みられる)
            Logger.Log("AppSettings", "DeepL APIキーの暗号化に失敗しました。平文で保存します。", ex);
            return plainText;
        }
    }

    /// <summary>EncryptDeepLApiKeyで暗号化された文字列を復号する。
    /// 別PC/別Windowsユーザーで保存された.envを読み込んだ場合など、復号できない値は
    /// 空文字を返す(ユーザーには再入力を促す形になる)。</summary>
    private static string DecryptDeepLApiKey(string storedValue)
    {
        if (string.IsNullOrEmpty(storedValue)) return "";
        if (!OperatingSystem.IsWindows()) return storedValue;

        try
        {
            var protectedBytes = Convert.FromBase64String(storedValue);
            var bytes = ProtectedData.Unprotect(protectedBytes, DeepLKeyEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Logger.Log("AppSettings", "DeepL APIキーの復号に失敗しました(別PC/別ユーザーの.env、または破損の可能性)。再入力が必要です。", ex);
            return "";
        }
    }

    /// <summary>現在の設定を.envファイルに書き戻す(キー以外の項目も含めて保存する)</summary>
    public void SaveToEnv()
    {
        // .envは1行=1設定の形式なので、複数行になりうる値は改行をエスケープする。
        // WHISPER_PROMPTはUIのTextBox自体はAcceptsReturn=Falseだが、貼り付けで
        // 改行が入るケースもあるため、参考コンテキストと同様にエスケープしておく
        var escapedContext = OllamaContext.Replace("\r\n", "\n").Replace("\n", "\\n");
        var escapedPrompt = WhisperPrompt.Replace("\r\n", "\n").Replace("\n", "\\n");
        OllamaEndpoint = NormalizeEndpoint(OllamaEndpoint);
        var encryptedApiKey = EncryptDeepLApiKey(DeepLApiKey);

        var lines = new List<string>
        {
            "# 音声デバイス名に含まれるキーワード(例: CABLE, Chat)。DEVICE_IDが見つからない場合のフォールバック用",
            $"DEVICE_KEYWORD={DeviceKeyword}",
            "",
            "# 音声デバイスの一意なID。設定画面でデバイスを選択すると自動的に保存される(手動編集非推奨)",
            $"DEVICE_ID={DeviceId}",
            "",
            "# VAD(発話検出)の閾値",
            $"VAD_THRESHOLD={VadThreshold.ToString(CultureInfo.InvariantCulture)}",
            "",
            "# VADヒステリシス比率(0〜1)。発話継続中の判定閾値をVAD_THRESHOLDからどれだけ下げるか。",
            "# 小さいほど、息継ぎ等の短い音量低下でセグメントが分断されにくくなる",
            $"VAD_HYSTERESIS_RATIO={VadHysteresisRatio.ToString(CultureInfo.InvariantCulture)}",
            "",
            "# ゲーム音声優先モード(小さい雑音より大きい音声を優先的に拾う)",
            $"GAME_AUDIO_PRIORITY_MODE={GameAudioPriorityMode}",
            "",
            "# ゲーム音声優先モードON時にVAD閾値へ掛ける倍率(大きいほど小さい音を拾わなくなる)",
            $"GAME_AUDIO_PRIORITY_MULTIPLIER={GameAudioPriorityMultiplier.ToString(CultureInfo.InvariantCulture)}",
            "",
            "# オーバーレイの見た目",
            $"OVERLAY_FONT_SIZE={OverlayFontSize.ToString(CultureInfo.InvariantCulture)}",
            $"OVERLAY_OPACITY={OverlayOpacity.ToString(CultureInfo.InvariantCulture)}",
            $"OVERLAY_MAX_LINES={OverlayMaxLines}",
            "",
            "# 使用するWhisperモデルファイル名",
            $"WHISPER_MODEL_PATH={WhisperModelPath}",
            "",
            "# Whisperに渡す認識ヒント(固有名詞など。カンマ区切りで自由に記述。改行は\\nでエスケープ済み)",
            $"WHISPER_PROMPT={escapedPrompt}",
            "",
            "# 認識言語(Whisperコード。例: auto, en, ja, ko, zh)",
            $"RECOGNITION_LANGUAGE={RecognitionLanguage}",
            "",
            "# 翻訳先言語(DeepLコード。例: JA, EN-US, KO, ZH)",
            $"TARGET_LANGUAGE_CODE={TargetLanguageCode}",
            "",
            "# 翻訳バックエンド: \"deepl\" または \"ollama\"",
            $"TRANSLATION_BACKEND={TranslationBackend}",
            "",
            "# DeepLを使う場合のAPIキー(無料プランは末尾に :fx が付く)。",
            "# Windows DPAPIで暗号化して保存しているため、このファイルを他PC/他ユーザーへ",
            "# コピーしても復号できない(このPC・このWindowsユーザーでのみ有効)。",
            $"DEEPL_API_KEY_ENC={encryptedApiKey}",
            "",
            "# Ollama(ローカルAI)を使う場合に使用するモデル名",
            $"OLLAMA_MODEL={OllamaModel}",
            "",
            "# Ollamaのエンドポイント(通常は変更不要)",
            $"OLLAMA_ENDPOINT={OllamaEndpoint}",
            "",
            "# Ollama使用時、翻訳の背景知識として渡す参考コンテキスト(改行は\\nでエスケープ済み)",
            $"OLLAMA_CONTEXT={escapedContext}",
            "",
            "# グローバルショートカットキー(設定画面の「ショートカット」タブから変更可能)",
            $"START_STOP_HOTKEY_MODIFIERS={StartStopHotkeyModifiers}",
            $"START_STOP_HOTKEY_KEY={StartStopHotkeyKey}",
            $"OVERLAY_HOTKEY_MODIFIERS={OverlayHotkeyModifiers}",
            $"OVERLAY_HOTKEY_KEY={OverlayHotkeyKey}",
        };

        // APIキーを含む設定ファイルなので、書き込み途中のクラッシュ/電源断で内容が
        // 中途半端になるのを避けるため、一時ファイルに書いてから置き換える(アトミック寄りの保存)
        var tempPath = EnvPath + ".tmp";
        File.WriteAllLines(tempPath, lines, Encoding.UTF8);
        File.Move(tempPath, EnvPath, overwrite: true);

        // 実行中のプロセスにもすぐ反映されるよう環境変数も更新する
        Environment.SetEnvironmentVariable("DEVICE_KEYWORD", DeviceKeyword);
        Environment.SetEnvironmentVariable("DEVICE_ID", DeviceId);
        Environment.SetEnvironmentVariable("VAD_THRESHOLD", VadThreshold.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("VAD_HYSTERESIS_RATIO", VadHysteresisRatio.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("GAME_AUDIO_PRIORITY_MODE", GameAudioPriorityMode.ToString());
        Environment.SetEnvironmentVariable("GAME_AUDIO_PRIORITY_MULTIPLIER", GameAudioPriorityMultiplier.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("OVERLAY_FONT_SIZE", OverlayFontSize.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("OVERLAY_OPACITY", OverlayOpacity.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("OVERLAY_MAX_LINES", OverlayMaxLines.ToString());
        Environment.SetEnvironmentVariable("WHISPER_MODEL_PATH", WhisperModelPath);
        Environment.SetEnvironmentVariable("WHISPER_PROMPT", WhisperPrompt);
        Environment.SetEnvironmentVariable("RECOGNITION_LANGUAGE", RecognitionLanguage);
        Environment.SetEnvironmentVariable("TARGET_LANGUAGE_CODE", TargetLanguageCode);
        Environment.SetEnvironmentVariable("TRANSLATION_BACKEND", TranslationBackend);
        Environment.SetEnvironmentVariable("DEEPL_API_KEY_ENC", encryptedApiKey);
        Environment.SetEnvironmentVariable("OLLAMA_MODEL", OllamaModel);
        Environment.SetEnvironmentVariable("OLLAMA_ENDPOINT", OllamaEndpoint);
        Environment.SetEnvironmentVariable("OLLAMA_CONTEXT", escapedContext);
        Environment.SetEnvironmentVariable("START_STOP_HOTKEY_MODIFIERS", StartStopHotkeyModifiers);
        Environment.SetEnvironmentVariable("START_STOP_HOTKEY_KEY", StartStopHotkeyKey);
        Environment.SetEnvironmentVariable("OVERLAY_HOTKEY_MODIFIERS", OverlayHotkeyModifiers);
        Environment.SetEnvironmentVariable("OVERLAY_HOTKEY_KEY", OverlayHotkeyKey);
    }

    public ITranslationService? CreateTranslationService(System.Net.Http.HttpClient httpClient)
    {
        var targetOption = LanguageCatalog.FindByDeepLCode(TargetLanguageCode);

        if (TranslationBackend.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            return new OllamaTranslationService(httpClient, OllamaModel, OllamaEndpoint, targetOption.EnglishName, OllamaContext);
        }

        if (!string.IsNullOrWhiteSpace(DeepLApiKey))
        {
            return new DeepLTranslationService(httpClient, DeepLApiKey, targetOption.DeepLCode);
        }

        return null;
    }
}
