using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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

    private const string EnvPath = ".env";

    public static AppSettings LoadFromEnv()
    {
        EnvLoader.Load(EnvPath);

        var settings = new AppSettings
        {
            DeviceKeyword = Environment.GetEnvironmentVariable("DEVICE_KEYWORD") ?? "Chat",
            DeviceId = Environment.GetEnvironmentVariable("DEVICE_ID") ?? "",
            TranslationBackend = Environment.GetEnvironmentVariable("TRANSLATION_BACKEND") ?? "deepl",
            DeepLApiKey = Environment.GetEnvironmentVariable("DEEPL_API_KEY") ?? "",
            OllamaModel = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "llama3.1",
            OllamaEndpoint = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? "http://localhost:11434",
            WhisperModelPath = Environment.GetEnvironmentVariable("WHISPER_MODEL_PATH") ?? "ggml-base.bin",
        };

        var vadThresholdStr = Environment.GetEnvironmentVariable("VAD_THRESHOLD");
        if (float.TryParse(vadThresholdStr, out var vadThreshold))
        {
            settings.VadThreshold = vadThreshold;
        }

        var vadHysteresisStr = Environment.GetEnvironmentVariable("VAD_HYSTERESIS_RATIO");
        if (float.TryParse(vadHysteresisStr, out var vadHysteresisRatio))
        {
            settings.VadHysteresisRatio = vadHysteresisRatio;
        }

        if (bool.TryParse(Environment.GetEnvironmentVariable("GAME_AUDIO_PRIORITY_MODE"), out var priorityMode))
        {
            settings.GameAudioPriorityMode = priorityMode;
        }
        if (double.TryParse(Environment.GetEnvironmentVariable("OVERLAY_FONT_SIZE"), out var fontSize))
        {
            settings.OverlayFontSize = fontSize;
        }
        if (double.TryParse(Environment.GetEnvironmentVariable("OVERLAY_OPACITY"), out var opacity))
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
        settings.OllamaContext = (Environment.GetEnvironmentVariable("OLLAMA_CONTEXT") ?? "").Replace("\\n", "\n");

        return settings;
    }

    /// <summary>現在の設定を.envファイルに書き戻す(キー以外の項目も含めて保存する)</summary>
    public void SaveToEnv()
    {
        // .envは1行=1設定の形式なので、複数行になりうる参考コンテキストは改行をエスケープする
        var escapedContext = OllamaContext.Replace("\r\n", "\n").Replace("\n", "\\n");

        var lines = new List<string>
        {
            "# 音声デバイス名に含まれるキーワード(例: CABLE, Chat)。DEVICE_IDが見つからない場合のフォールバック用",
            $"DEVICE_KEYWORD={DeviceKeyword}",
            "",
            "# 音声デバイスの一意なID。設定画面でデバイスを選択すると自動的に保存される(手動編集非推奨)",
            $"DEVICE_ID={DeviceId}",
            "",
            "# VAD(発話検出)の閾値",
            $"VAD_THRESHOLD={VadThreshold}",
            "",
            "# VADヒステリシス比率(0〜1)。発話継続中の判定閾値をVAD_THRESHOLDからどれだけ下げるか。",
            "# 小さいほど、息継ぎ等の短い音量低下でセグメントが分断されにくくなる",
            $"VAD_HYSTERESIS_RATIO={VadHysteresisRatio}",
            "",
            "# ゲーム音声優先モード(小さい雑音より大きい音声を優先的に拾う)",
            $"GAME_AUDIO_PRIORITY_MODE={GameAudioPriorityMode}",
            "",
            "# オーバーレイの見た目",
            $"OVERLAY_FONT_SIZE={OverlayFontSize}",
            $"OVERLAY_OPACITY={OverlayOpacity}",
            $"OVERLAY_MAX_LINES={OverlayMaxLines}",
            "",
            "# 使用するWhisperモデルファイル名",
            $"WHISPER_MODEL_PATH={WhisperModelPath}",
            "",
            "# Whisperに渡す認識ヒント(固有名詞など。カンマ区切りで自由に記述)",
            $"WHISPER_PROMPT={WhisperPrompt}",
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
            "# DeepLを使う場合のAPIキー(無料プランは末尾に :fx が付く)",
            $"DEEPL_API_KEY={DeepLApiKey}",
            "",
            "# Ollama(ローカルAI)を使う場合に使用するモデル名",
            $"OLLAMA_MODEL={OllamaModel}",
            "",
            "# Ollamaのエンドポイント(通常は変更不要)",
            $"OLLAMA_ENDPOINT={OllamaEndpoint}",
            "",
            "# Ollama使用時、翻訳の背景知識として渡す参考コンテキスト(改行は\\nでエスケープ済み)",
            $"OLLAMA_CONTEXT={escapedContext}",
        };

        File.WriteAllLines(EnvPath, lines);

        // 実行中のプロセスにもすぐ反映されるよう環境変数も更新する
        Environment.SetEnvironmentVariable("DEVICE_KEYWORD", DeviceKeyword);
        Environment.SetEnvironmentVariable("DEVICE_ID", DeviceId);
        Environment.SetEnvironmentVariable("VAD_THRESHOLD", VadThreshold.ToString());
        Environment.SetEnvironmentVariable("VAD_HYSTERESIS_RATIO", VadHysteresisRatio.ToString());
        Environment.SetEnvironmentVariable("GAME_AUDIO_PRIORITY_MODE", GameAudioPriorityMode.ToString());
        Environment.SetEnvironmentVariable("OVERLAY_FONT_SIZE", OverlayFontSize.ToString());
        Environment.SetEnvironmentVariable("OVERLAY_OPACITY", OverlayOpacity.ToString());
        Environment.SetEnvironmentVariable("OVERLAY_MAX_LINES", OverlayMaxLines.ToString());
        Environment.SetEnvironmentVariable("WHISPER_MODEL_PATH", WhisperModelPath);
        Environment.SetEnvironmentVariable("WHISPER_PROMPT", WhisperPrompt);
        Environment.SetEnvironmentVariable("RECOGNITION_LANGUAGE", RecognitionLanguage);
        Environment.SetEnvironmentVariable("TARGET_LANGUAGE_CODE", TargetLanguageCode);
        Environment.SetEnvironmentVariable("TRANSLATION_BACKEND", TranslationBackend);
        Environment.SetEnvironmentVariable("DEEPL_API_KEY", DeepLApiKey);
        Environment.SetEnvironmentVariable("OLLAMA_MODEL", OllamaModel);
        Environment.SetEnvironmentVariable("OLLAMA_ENDPOINT", OllamaEndpoint);
        Environment.SetEnvironmentVariable("OLLAMA_CONTEXT", escapedContext);
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
