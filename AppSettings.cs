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
    public string TranslationBackend { get; set; } = "deepl"; // "deepl" または "ollama"
    public string DeepLApiKey { get; set; } = "";
    public string OllamaModel { get; set; } = "llama3.1";
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";
    public string WhisperModelPath { get; set; } = "ggml-base.bin";
    public float VadThreshold { get; set; } = 0.015f;

    private const string EnvPath = ".env";

    public static AppSettings LoadFromEnv()
    {
        EnvLoader.Load(EnvPath);

        var settings = new AppSettings
        {
            DeviceKeyword = Environment.GetEnvironmentVariable("DEVICE_KEYWORD") ?? "Chat",
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

        return settings;
    }

    /// <summary>現在の設定を.envファイルに書き戻す(キー以外の項目も含めて保存する)</summary>
    public void SaveToEnv()
    {
        var lines = new List<string>
        {
            "# 音声デバイス名に含まれるキーワード(例: CABLE, Chat)",
            $"DEVICE_KEYWORD={DeviceKeyword}",
            "",
            "# VAD(発話検出)の閾値",
            $"VAD_THRESHOLD={VadThreshold}",
            "",
            "# 使用するWhisperモデルファイル名",
            $"WHISPER_MODEL_PATH={WhisperModelPath}",
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
        };

        File.WriteAllLines(EnvPath, lines);

        // 実行中のプロセスにもすぐ反映されるよう環境変数も更新する
        Environment.SetEnvironmentVariable("DEVICE_KEYWORD", DeviceKeyword);
        Environment.SetEnvironmentVariable("VAD_THRESHOLD", VadThreshold.ToString());
        Environment.SetEnvironmentVariable("WHISPER_MODEL_PATH", WhisperModelPath);
        Environment.SetEnvironmentVariable("TRANSLATION_BACKEND", TranslationBackend);
        Environment.SetEnvironmentVariable("DEEPL_API_KEY", DeepLApiKey);
        Environment.SetEnvironmentVariable("OLLAMA_MODEL", OllamaModel);
        Environment.SetEnvironmentVariable("OLLAMA_ENDPOINT", OllamaEndpoint);
    }

    public ITranslationService? CreateTranslationService(System.Net.Http.HttpClient httpClient)
    {
        if (TranslationBackend.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            return new OllamaTranslationService(httpClient, OllamaModel, OllamaEndpoint);
        }

        if (!string.IsNullOrWhiteSpace(DeepLApiKey))
        {
            return new DeepLTranslationService(httpClient, DeepLApiKey);
        }

        return null;
    }
}
