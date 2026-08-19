using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace LoopbackRecorder.Tests;

/// <summary>
/// AppSettings.LoadFromEnv/SaveToEnvの単体テスト。
///
/// 注意点: AppSettingsは内部でEnvLoader経由のプロセス環境変数(Environment.GetEnvironmentVariable)
/// を読み書きするグローバル状態に依存している。そのため各テストは、対象キーを実行前に必ずクリアし
/// (EnvLoader.Loadは「既に環境変数が設定済みなら上書きしない」動作のため、クリアしないと前の
/// テストや実行環境の値が残って結果に影響する)、実行後に元の値へ復元する。
/// このクラス内のテストは環境変数という共有状態を使うため、同時実行させないよう
/// [Collection]でシーケンシャル実行を強制している(他クラスとの並列実行には影響しない)。
/// </summary>
[Collection("AppSettings env-var tests (sequential)")]
public class AppSettingsTests : IDisposable
{
    private static readonly string[] EnvKeys =
    {
        "DEVICE_KEYWORD", "DEVICE_ID", "TRANSLATION_BACKEND", "ENABLE_DEEPL_TO_OLLAMA_FALLBACK", "OLLAMA_MODEL", "OLLAMA_ENDPOINT",
        "WHISPER_MODEL_PATH", "WHISPER_THREAD_COUNT", "DEEPL_API_KEY_ENC", "DEEPL_API_KEY",
        "VAD_THRESHOLD", "VAD_HYSTERESIS_RATIO", "GAME_AUDIO_PRIORITY_MODE", "GAME_AUDIO_PRIORITY_MULTIPLIER",
        "OVERLAY_FONT_SIZE", "OVERLAY_OPACITY", "OVERLAY_MAX_LINES", "OVERLAY_FONT_COLOR",
        "WHISPER_PROMPT", "RECOGNITION_LANGUAGE", "TARGET_LANGUAGE_CODE", "OLLAMA_CONTEXT",
        "START_STOP_HOTKEY_MODIFIERS", "START_STOP_HOTKEY_KEY", "OVERLAY_HOTKEY_MODIFIERS", "OVERLAY_HOTKEY_KEY",
    };

    private readonly Dictionary<string, string?> _originalValues = new();
    private readonly List<string> _tempFiles = new();

    public AppSettingsTests()
    {
        // このプロセス(テストホスト)に既に載っている値を退避してからクリアする。
        // 通常のテスト実行環境ではどのキーも未設定のはずだが、念のため元の状態に戻せるようにしておく。
        foreach (var key in EnvKeys)
        {
            _originalValues[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    public void Dispose()
    {
        foreach (var key in EnvKeys)
        {
            Environment.SetEnvironmentVariable(key, _originalValues[key]);
        }
        foreach (var path in _tempFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* テスト後片付けの失敗は無視してよい */ }
        }
    }

    private string CreateTempEnvPath()
    {
        // AppSettings.SaveToEnvは"{path}.tmp"を経由してFile.Moveするため、実ファイルはまだ
        // 存在しない状態のパスを渡す(存在しないパスへのLoadFromEnvは「既定値のまま」で成功する)。
        var path = Path.Combine(Path.GetTempPath(), $"loopback-test-{Guid.NewGuid():N}.env");
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void 存在しないパスを指定した場合は既定値のまま返る()
    {
        var settings = AppSettings.LoadFromEnv(CreateTempEnvPath());

        Assert.Equal(0.5f, settings.VadThreshold);
        Assert.Equal(0.6f, settings.VadHysteresisRatio);
        Assert.Equal("deepl", settings.TranslationBackend);
        Assert.Equal(4, settings.OverlayMaxLines);
    }

    [Fact]
    public void VAD閾値が下限を下回る場合は下限にクランプされる()
    {
        var path = CreateTempEnvPath();
        File.WriteAllText(path, "VAD_THRESHOLD=0.2\n"); // 0.06より大きいので旧スケール扱いにはならない

        var settings = AppSettings.LoadFromEnv(path);

        Assert.Equal(0.2f, settings.VadThreshold);
    }

    [Fact]
    public void VAD閾値が旧スケールとみなされる範囲の場合は0_5へリセットされる()
    {
        // 0.06以下は「Silero VAD導入前のRMSスケールの残骸」とみなし、クランプではなく
        // 新スケールの既定値(0.5)へリセットする仕様(AppSettings.LoadFromEnv内のコメント参照)
        var path = CreateTempEnvPath();
        File.WriteAllText(path, "VAD_THRESHOLD=0.015\n");

        var settings = AppSettings.LoadFromEnv(path);

        Assert.Equal(0.5f, settings.VadThreshold);
    }

    [Fact]
    public void VAD閾値が上限を超える場合は上限にクランプされる()
    {
        var path = CreateTempEnvPath();
        File.WriteAllText(path, "VAD_THRESHOLD=999\n");

        var settings = AppSettings.LoadFromEnv(path);

        Assert.Equal(0.95f, settings.VadThreshold);
    }

    [Fact]
    public void ヒステリシス比率が範囲外の場合はクランプされる()
    {
        var path = CreateTempEnvPath();
        File.WriteAllText(path, "VAD_HYSTERESIS_RATIO=5\n");

        var settings = AppSettings.LoadFromEnv(path);

        Assert.Equal(1.0f, settings.VadHysteresisRatio);
    }

    [Fact]
    public void オーバーレイ最大行数が範囲外の場合はクランプされる()
    {
        var path = CreateTempEnvPath();
        File.WriteAllText(path, "OVERLAY_MAX_LINES=999\n");

        var settings = AppSettings.LoadFromEnv(path);

        Assert.Equal(10, settings.OverlayMaxLines);
    }

    [Fact]
    public void オーバーレイ文字色が正しい形式なら読み込まれる()
    {
        var path = CreateTempEnvPath();
        File.WriteAllText(path, "OVERLAY_FONT_COLOR=#FFE066\n");

        var settings = AppSettings.LoadFromEnv(path);

        Assert.Equal("#FFE066", settings.OverlayFontColor);
    }

    [Fact]
    public void オーバーレイ文字色が不正な形式の場合は既定値の白のまま()
    {
        // 手動で.envを編集して壊れた値(#始まりでない、桁数が違う等)が入っていても、
        // 例外にならず安全に既定値へフォールバックすることを確認する
        var path = CreateTempEnvPath();
        File.WriteAllText(path, "OVERLAY_FONT_COLOR=not-a-color\n");

        var settings = AppSettings.LoadFromEnv(path);

        Assert.Equal("#FFFFFF", settings.OverlayFontColor);
    }

    [Fact]
    public void Whisperスレッド数が範囲外の場合は論理コア数にクランプされる()
    {
        var path = CreateTempEnvPath();
        // 論理コア数を超える極端な値は、実行環境に依存せず必ず上限超過になるよう
        // Environment.ProcessorCountの2倍を使う
        File.WriteAllText(path, $"WHISPER_THREAD_COUNT={Environment.ProcessorCount * 2}\n");

        var settings = AppSettings.LoadFromEnv(path);

        Assert.Equal(Environment.ProcessorCount, settings.WhisperThreadCount);
    }

    [Fact]
    public void Whisperスレッド数が0以下の場合は下限の1にクランプされる()
    {
        var path = CreateTempEnvPath();
        File.WriteAllText(path, "WHISPER_THREAD_COUNT=0\n");

        var settings = AppSettings.LoadFromEnv(path);

        Assert.Equal(1, settings.WhisperThreadCount);
    }

    [Fact]
    public void SaveしてLoadすると基本項目がラウンドトリップする()
    {
        var path = CreateTempEnvPath();
        var original = new AppSettings
        {
            DeviceKeyword = "TestDevice",
            TranslationBackend = "ollama",
            EnableDeepLToOllamaFallback = true,
            OllamaModel = "test-model",
            VadThreshold = 0.42f,
            VadHysteresisRatio = 0.33f,
            OverlayMaxLines = 7,
            OverlayFontColor = "#66D9FF",
            WhisperThreadCount = 3,
            RecognitionLanguage = "en",
            TargetLanguageCode = "EN-US",
        };

        original.SaveToEnv(path);
        // SaveToEnvはプロセスの環境変数も同時に更新してしまうため(仕様通り)、
        // ファイルからの再読み込みを正しく検証するにはここでもう一度クリアする必要がある。
        foreach (var key in EnvKeys) Environment.SetEnvironmentVariable(key, null);

        var reloaded = AppSettings.LoadFromEnv(path);

        Assert.Equal("TestDevice", reloaded.DeviceKeyword);
        Assert.Equal("ollama", reloaded.TranslationBackend);
        Assert.True(reloaded.EnableDeepLToOllamaFallback);
        Assert.Equal("test-model", reloaded.OllamaModel);
        Assert.Equal(0.42f, reloaded.VadThreshold);
        Assert.Equal(0.33f, reloaded.VadHysteresisRatio);
        Assert.Equal(7, reloaded.OverlayMaxLines);
        Assert.Equal("#66D9FF", reloaded.OverlayFontColor);
        Assert.Equal(3, reloaded.WhisperThreadCount);
        Assert.Equal("en", reloaded.RecognitionLanguage);
        Assert.Equal("EN-US", reloaded.TargetLanguageCode);
    }

    [Fact]
    public void DeepL選択でAPIキー未設定の場合はNullTranslationServiceが返る()
    {
        var settings = new AppSettings { TranslationBackend = "deepl", DeepLApiKey = "" };

        var service = settings.CreateTranslationService(new System.Net.Http.HttpClient());

        Assert.Same(NullTranslationService.Instance, service);
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public void DeepL選択でAPIキー設定済みの場合はDeepLTranslationServiceが返る()
    {
        var settings = new AppSettings { TranslationBackend = "deepl", DeepLApiKey = "dummy-key:fx" };

        var service = settings.CreateTranslationService(new System.Net.Http.HttpClient());

        Assert.IsType<DeepLTranslationService>(service);
        Assert.True(service.IsEnabled);
    }

    [Fact]
    public void Ollama選択の場合はAPIキー無しでもOllamaTranslationServiceが返る()
    {
        var settings = new AppSettings { TranslationBackend = "ollama", DeepLApiKey = "" };

        var service = settings.CreateTranslationService(new System.Net.Http.HttpClient());

        Assert.IsType<OllamaTranslationService>(service);
        Assert.True(service.IsEnabled);
    }

    [Fact]
    public void DeepLフォールバック無効の場合はDeepLTranslationServiceがそのまま返る()
    {
        var settings = new AppSettings
        {
            TranslationBackend = "deepl",
            DeepLApiKey = "dummy-key:fx",
            EnableDeepLToOllamaFallback = false,
        };

        var service = settings.CreateTranslationService(new System.Net.Http.HttpClient());

        Assert.IsType<DeepLTranslationService>(service);
    }

    [Fact]
    public void DeepLフォールバック有効の場合はFallbackTranslationServiceでラップされる()
    {
        var settings = new AppSettings
        {
            TranslationBackend = "deepl",
            DeepLApiKey = "dummy-key:fx",
            EnableDeepLToOllamaFallback = true,
        };

        var service = settings.CreateTranslationService(new System.Net.Http.HttpClient());

        Assert.IsType<FallbackTranslationService>(service);
    }

    [Fact]
    public void フォールバック有効でもAPIキー未設定の場合はNullTranslationServiceが返る()
    {
        // フォールバックはDeepL自体が使える(=APIキーがある)ことが前提の機能なので、
        // キー未設定の場合は従来通りNullTranslationServiceになるべきで、
        // FallbackTranslationServiceでラップされてはいけない
        var settings = new AppSettings
        {
            TranslationBackend = "deepl",
            DeepLApiKey = "",
            EnableDeepLToOllamaFallback = true,
        };

        var service = settings.CreateTranslationService(new System.Net.Http.HttpClient());

        Assert.Same(NullTranslationService.Instance, service);
    }

    [Fact]
    public void 改行を含む参考コンテキストはエスケープされてラウンドトリップする()
    {
        var path = CreateTempEnvPath();
        var original = new AppSettings { OllamaContext = "1行目\n2行目" };

        original.SaveToEnv(path);
        foreach (var key in EnvKeys) Environment.SetEnvironmentVariable(key, null);

        var reloaded = AppSettings.LoadFromEnv(path);

        Assert.Equal("1行目\n2行目", reloaded.OllamaContext);
    }
}
