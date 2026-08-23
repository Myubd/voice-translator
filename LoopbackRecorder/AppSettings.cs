using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

/// <summary>
/// アプリの設定値をまとめて保持するクラス。
/// .envから読み込み、SettingsWindowでの変更を.envに書き戻せるようにする。
///
/// partialにしている理由: ホットキー関連の3メソッド(GetStartStopHotkey/GetOverlayHotkey/
/// ParseHotkey、AppSettings.Hotkeys.csへ分離)だけがSystem.Windows.Input(WPF)に依存しており、
/// このファイル自体はWPF非依存にしておくことで、LoopbackRecorder.Tests(WPF無しのnet8.0)から
/// VoiceActivitySegmenter.csと同じ方式(Compile Includeでの直接コンパイル)でテストできるようにする。
/// </summary>
public partial class AppSettings
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

    /// <summary>trueの場合、DeepL翻訳が失敗した時に自動的にOllamaへフォールバックする。
    /// TranslationBackend=="deepl"の場合のみ意味を持つ(Ollama選択時はそもそも副が無い)。
    /// 既定はfalse: Ollama未セットアップの環境でDeepLのみ使っているユーザーに対して、
    /// 意図せずOllamaへの接続を試みてエラーログが増える等の副作用が出ないようにするため。</summary>
    public bool EnableDeepLToOllamaFallback { get; set; } = false;

    /// <summary>trueの場合、翻訳済みの原文(完全一致)をメモリ上にキャッシュし、
    /// 同じ原文が再度来た場合はAPI呼び出しを行わずキャッシュ済みの訳文を返す
    /// (CachingTranslationService)。「gg」「nice」等の短い定型句の繰り返しでの
    /// API消費量・応答時間を削減する目的で、既定はtrue(副作用が無いため)。</summary>
    public bool EnableTranslationCache { get; set; } = true;
    public string WhisperModelPath { get; set; } = "ggml-base.bin";

    /// <summary>Whisper推論に使うスレッド数。以前はEnvironment.ProcessorCount / 2固定だったが、
    /// ゲームと同時実行するアプリとしてはCPUコアの半分を占有するのは重すぎる場合があるため、
    /// 設定画面から調整できるようにした。デフォルトはより控えめなProcessorCount / 4
    /// (最低2)とし、必要な場合のみユーザーが引き上げられるようにしている。</summary>
    public int WhisperThreadCount { get; set; } = Math.Max(2, Environment.ProcessorCount / 4);

    /// <summary>翻訳ワーカーの並行実行数。以前は1本の直列ループだったため、DeepL失敗時に
    /// Ollamaへフォールバックする処理が発生すると、1件の翻訳にDeepL+Ollama両方のタイムアウト
    /// (最大45秒程度)がかかり、その間キューの他の項目が一切処理されない、という問題があった。
    /// 既定値2は、DeepL無料プランのレート制限を考慮した控えめな値。上げすぎるとDeepL側から
    /// 429(レート制限)エラーを受けやすくなるため、上限は4にclampしている。</summary>
    public int TranslationWorkerCount { get; set; } = 2;

    /// <summary>これ以上遅れた発話は、処理を諦めて最新の発話を優先するための閾値(秒)。
    /// キューの「件数」だけで溢れを判定していると、処理自体が遅い(Whisper推論が重い、
    /// 翻訳APIが遅い等)場合に、キューは詰まっていなくても実際の遅延はどんどん広がっていく、
    /// という状況を検出できない。例えばキューがわずか2件でも、1件の処理に10秒かかれば
    /// 20秒の遅延になる。ゲーム実況のようなリアルタイム用途では「全部訳す」より
    /// 「常に最新の会話を訳す」方が価値があるため、経過時間(SegmentEndTimeからの経過)を
    /// 直接見て、これを超えた発話はWhisper/翻訳どちらの手前でも処理自体を打ち切る。
    /// 0以下を設定すると機能自体を無効化できる(常にすべて処理する、以前までの挙動)。</summary>
    public double MaxLatencySeconds { get; set; } = 3.0;

    /// <summary>VAD(発話区間検出)の開始判定閾値。Silero VAD(ONNXニューラルモデル)を
    /// 使う場合は発話確率(0〜1、大きいほど「発話らしい」と判定されにくくなる)のスケール。
    /// (Silero VAD導入前はRMS実効値のスケール(概ね0.001〜0.05)だった。
    /// 0.5という値は新スケールでの標準的な閾値であり、Silero VAD公式サンプルの既定値でもある)</summary>
    public float VadThreshold { get; set; } = 0.5f;

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

    /// <summary>オーバーレイの訳文の文字色(#RRGGBB形式)。既定は白。
    /// ゲーム画面の色味によっては白が見えにくいことがあるため、設定画面からプリセットの中から選べる。</summary>
    public string OverlayFontColor { get; set; } = "#FFFFFF";

    /// <summary>固有名詞などの認識精度を上げるため、Whisperに事前情報として渡すヒント文</summary>
    public string WhisperPrompt { get; set; } = "";

    /// <summary>Whisperの認識言語コード(例: "auto", "en", "ja")</summary>
    public string RecognitionLanguage { get; set; } = "auto";

    /// <summary>翻訳先言語のDeepLコード(例: "JA", "EN-US", "KO")</summary>
    public string TargetLanguageCode { get; set; } = "JA";

    /// <summary>Ollama使用時、翻訳の背景知識として渡す参考コンテキスト(記事の抜粋など)</summary>
    public string OllamaContext { get; set; } = "";

    /// <summary>Ollama使用時、ユーザーが直接入力する手動用語集("original => translation"形式、
    /// 1行1エントリ)。参考コンテキストからのLLM自動抽出と異なりネットワーク呼び出しを伴わず、
    /// 確実にその表記で翻訳に反映される。同じ原文が自動抽出側にもあった場合はこちらが優先される。</summary>
    public string ManualGlossary { get; set; } = "";

    // ==== グローバルショートカットキー ====
    // ModifierKeys/Keyの各Enum名(例: "Control, Alt" / "R")をそのまま文字列として保存する。
    // Enum.TryParseがFlags列挙体のカンマ区切り表記をそのまま解釈できるため、独自のパース処理が不要になる。
    public string StartStopHotkeyModifiers { get; set; } = "Control, Alt";
    public string StartStopHotkeyKey { get; set; } = "R";
    public string OverlayHotkeyModifiers { get; set; } = "Control, Alt";
    public string OverlayHotkeyKey { get; set; } = "O";


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

    /// <summary>
    /// .envから設定を読み込む。
    /// </summary>
    /// <param name="envPath">読み込む.envファイルのパス。省略時は既定の<see cref="EnvPath"/>
    /// (実行ファイルと同じフォルダ)を使う。単体テストから一時ファイルを指定できるように
    /// するための引数で、通常の呼び出し(引数省略)では従来と動作は変わらない。</param>
    public static AppSettings LoadFromEnv(string? envPath = null)
    {
        EnvLoader.Load(envPath ?? EnvPath);

        var settings = new AppSettings
        {
            DeviceKeyword = Environment.GetEnvironmentVariable("DEVICE_KEYWORD") ?? "Chat",
            DeviceId = Environment.GetEnvironmentVariable("DEVICE_ID") ?? "",
            TranslationBackend = Environment.GetEnvironmentVariable("TRANSLATION_BACKEND") ?? "deepl",
            OllamaModel = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "llama3.1",
            OllamaEndpoint = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? "http://localhost:11434",
            WhisperModelPath = Environment.GetEnvironmentVariable("WHISPER_MODEL_PATH") ?? "ggml-base.bin",
        };

        // 未設定(初回起動・旧バージョンの.env)の場合はコンストラクタ既定値
        // (Math.Max(2, ProcessorCount / 4))を使う。範囲外の値が.envに直接書かれていた場合は
        // 1〜論理コア数の範囲にclampする(0以下や極端に大きい値を渡すとWhisper.net側で
        // 例外になったり、CPUを使い切ってゲーム側のフレームレートを落としたりするため)。
        if (int.TryParse(Environment.GetEnvironmentVariable("WHISPER_THREAD_COUNT"), out var whisperThreadCount))
        {
            settings.WhisperThreadCount = Math.Clamp(whisperThreadCount, 1, Environment.ProcessorCount);
        }

        // 同じ理屈でTranslationWorkerCountも1〜4にclampする。0以下だと翻訳が一切実行されなくなり、
        // 大きすぎるとDeepLのレート制限に抵触しやすくなるため。
        if (int.TryParse(Environment.GetEnvironmentVariable("TRANSLATION_WORKER_COUNT"), out var translationWorkerCount))
        {
            settings.TranslationWorkerCount = Math.Clamp(translationWorkerCount, 1, 4);
        }

        // 0以下は「機能を無効化(常に全部処理する)」という意図的な設定として許容するため、
        // 下限のclampはしない。上限だけ、誤って極端に大きい値(数千秒等)を入力してしまった場合に
        // 備えて60秒に制限しておく(それ以上待つくらいなら実質的に無効化したのと同じであるため)。
        if (double.TryParse(Environment.GetEnvironmentVariable("MAX_LATENCY_SECONDS"),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var maxLatencySeconds))
        {
            settings.MaxLatencySeconds = Math.Min(maxLatencySeconds, 60.0);
        }

        // DeepL APIキー: DPAPIで暗号化された新形式(DEEPL_API_KEY_ENC)を優先して読み込む。
        // 旧バージョンの平文保存(DEEPL_API_KEY)しか無い場合はそちらを読み込み、
        // 次回SaveToEnv()時に自動的に暗号化形式へ移行される。
        var encryptedKey = Environment.GetEnvironmentVariable("DEEPL_API_KEY_ENC");
        settings.DeepLApiKey = !string.IsNullOrEmpty(encryptedKey)
            ? DecryptDeepLApiKey(encryptedKey)
            : Environment.GetEnvironmentVariable("DEEPL_API_KEY") ?? "";

        // .envのような設定ファイルはOSのカルチャ(小数点がカンマになる地域設定等)に
        // 影響されると壊れるため、数値の読み書きは必ずInvariantCultureで行う。
        // また、設定画面はUI(Slider)の範囲内でしか保存できないが、.envファイルは
        // ユーザーが直接編集できてしまうため、範囲外の値が入っていた場合は
        // UI(Slider)の範囲にclampする。clampせず異常値を使い続けると、
        // 例えばVAD_HYSTERESIS_RATIOに999のような値が入っている場合、常に無音判定
        // されなくなって発話が延々と1つのセグメントとして扱われ続ける、といった
        // 気付きにくい不具合につながる。
        var vadThresholdStr = Environment.GetEnvironmentVariable("VAD_THRESHOLD");
        if (float.TryParse(vadThresholdStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var vadThreshold))
        {
            // Silero VAD導入前の.envには、旧スケール(RMS実効値、概ね0.001〜0.05)の値が
            // 残っている可能性がある。新スケール(Silero発話確率、0〜1)の最小値である0.05以下は
            // ほぼ確実に旧スケールの値だと判断できるため、そのままclampするのではなく、
            // 新スケールの既定値にリセットする(0.05にclampしてしまうと「ほぼ何でも発話と
            // 判定してしまう極端に低い確率閾値」になり、実用にならないため)。
            settings.VadThreshold = vadThreshold <= 0.06f
                ? 0.5f
                : Math.Clamp(vadThreshold, 0.05f, 0.95f);
        }

        var vadHysteresisStr = Environment.GetEnvironmentVariable("VAD_HYSTERESIS_RATIO");
        if (float.TryParse(vadHysteresisStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var vadHysteresisRatio))
        {
            settings.VadHysteresisRatio = Math.Clamp(vadHysteresisRatio, 0.2f, 1.0f);
        }

        if (bool.TryParse(Environment.GetEnvironmentVariable("GAME_AUDIO_PRIORITY_MODE"), out var priorityMode))
        {
            settings.GameAudioPriorityMode = priorityMode;
        }

        if (bool.TryParse(Environment.GetEnvironmentVariable("ENABLE_DEEPL_TO_OLLAMA_FALLBACK"), out var fallbackEnabled))
        {
            settings.EnableDeepLToOllamaFallback = fallbackEnabled;
        }
        if (bool.TryParse(Environment.GetEnvironmentVariable("ENABLE_TRANSLATION_CACHE"), out var translationCacheEnabled))
        {
            settings.EnableTranslationCache = translationCacheEnabled;
        }
        if (float.TryParse(Environment.GetEnvironmentVariable("GAME_AUDIO_PRIORITY_MULTIPLIER"),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var priorityMultiplier))
        {
            settings.GameAudioPriorityMultiplier = Math.Clamp(priorityMultiplier, 1.0f, 3.0f);
        }
        if (double.TryParse(Environment.GetEnvironmentVariable("OVERLAY_FONT_SIZE"),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var fontSize))
        {
            settings.OverlayFontSize = Math.Clamp(fontSize, 14, 48);
        }
        if (double.TryParse(Environment.GetEnvironmentVariable("OVERLAY_OPACITY"),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
        {
            settings.OverlayOpacity = Math.Clamp(opacity, 0, 1);
        }
        if (int.TryParse(Environment.GetEnvironmentVariable("OVERLAY_MAX_LINES"), out var maxLines))
        {
            settings.OverlayMaxLines = Math.Clamp(maxLines, 1, 10);
        }

        var overlayFontColor = Environment.GetEnvironmentVariable("OVERLAY_FONT_COLOR");
        // "#RRGGBB"形式(7文字、#始まり)の場合のみ採用する。壊れた値(手動編集ミス等)が
        // そのままWPFのColor変換に渡って例外にならないよう、ここで簡易バリデーションしておく
        if (!string.IsNullOrEmpty(overlayFontColor) && overlayFontColor.Length == 7 && overlayFontColor[0] == '#')
        {
            settings.OverlayFontColor = overlayFontColor;
        }
        settings.WhisperPrompt = Environment.GetEnvironmentVariable("WHISPER_PROMPT") ?? "";
        settings.RecognitionLanguage = Environment.GetEnvironmentVariable("RECOGNITION_LANGUAGE") ?? "auto";
        settings.TargetLanguageCode = Environment.GetEnvironmentVariable("TARGET_LANGUAGE_CODE") ?? "JA";
        // 改行のエスケープ(\n → 改行)はEnvLoader側で共通処理済み
        settings.OllamaContext = Environment.GetEnvironmentVariable("OLLAMA_CONTEXT") ?? "";
        settings.ManualGlossary = Environment.GetEnvironmentVariable("MANUAL_GLOSSARY") ?? "";
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
    /// <summary>
    /// 暗号化結果。
    /// - Encrypted: DPAPIでの暗号化に成功した(通常の状態)
    /// - PlaintextByDesign: 非Windows環境(開発機など)でDPAPI自体が使えないため、
    ///   設計として最初から平文運用になっている
    /// - Failed: Windows環境でDPAPIの暗号化自体に失敗した(異常系)。この場合、
    ///   Valueは意味を持たない(空文字)。呼び出し側(SaveToEnv)は平文でのフォールバック保存を
    ///   せず、APIキーの保存自体を見送る。
    ///
    /// 以前は暗号化に失敗した場合、常に平文で.envへ書き込んでいた。「設定を失わない」という
    /// 意味では親切だが、DPAPIを使っている意義(このPC・このWindowsユーザー以外では
    /// 復号できないようにする)を暗号化失敗時にだけ自ら崩してしまうことになり、セキュリティ上
    /// 望ましくない。配布アプリとしては「暗号化できない環境では平文保存しない」方が安全なため、
    /// Failedの場合はAPIキーを保存せず、ユーザーに通知する形に変更した。
    /// </summary>
    private enum EncryptStatus { Encrypted, PlaintextByDesign, Failed }

    private readonly record struct EncryptResult(string Value, EncryptStatus Status);

    /// <summary>直前のSaveToEnv()呼び出しで、DPAPI暗号化の失敗によりDeepL APIキーの
    /// 保存を見送った(=以前の値のまま変更されなかった)場合にtrueになる。
    /// 呼び出し元のUIはこれを見て、ユーザーに再試行や再入力を促すことができる。</summary>
    public bool LastSaveDeepLKeySaveFailed { get; private set; }

    private static EncryptResult EncryptDeepLApiKey(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return new EncryptResult("", EncryptStatus.Encrypted);
        if (!OperatingSystem.IsWindows()) return new EncryptResult(plainText, EncryptStatus.PlaintextByDesign);

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(bytes, DeepLKeyEntropy, DataProtectionScope.CurrentUser);
            return new EncryptResult(Convert.ToBase64String(protectedBytes), EncryptStatus.Encrypted);
        }
        catch (Exception ex)
        {
            Logger.Log("AppSettings", "DeepL APIキーの暗号化に失敗しました。安全に保存できないため、APIキーの保存を見送ります。", ex);
            return new EncryptResult("", EncryptStatus.Failed);
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
    /// <summary>
    /// 現在の設定を.envへ保存する(プロセスの環境変数も合わせて更新する)。
    /// </summary>
    /// <param name="envPath">書き込む.envファイルのパス。省略時は既定の<see cref="EnvPath"/>
    /// (実行ファイルと同じフォルダ)を使う。単体テストから一時ファイルを指定できるように
    /// するための引数で、通常の呼び出し(引数省略)では従来と動作は変わらない。</param>
    public void SaveToEnv(string? envPath = null)
    {
        // .envは1行=1設定の形式なので、複数行になりうる値は改行をエスケープする。
        // WHISPER_PROMPTはUIのTextBox自体はAcceptsReturn=Falseだが、貼り付けで
        // 改行が入るケースもあるため、参考コンテキストと同様にエスケープしておく
        var escapedContext = OllamaContext.Replace("\r\n", "\n").Replace("\n", "\\n");
        var escapedManualGlossary = ManualGlossary.Replace("\r\n", "\n").Replace("\n", "\\n");
        var escapedPrompt = WhisperPrompt.Replace("\r\n", "\n").Replace("\n", "\\n");
        OllamaEndpoint = NormalizeEndpoint(OllamaEndpoint);
        var encryptResult = EncryptDeepLApiKey(DeepLApiKey);

        string deepLApiKeyEncLine;
        string deepLApiKeyPlainLine;
        LastSaveDeepLKeySaveFailed = false;

        switch (encryptResult.Status)
        {
            case EncryptStatus.Encrypted:
                // 暗号化に成功した場合のみDEEPL_API_KEY_ENC(暗号化専用キー)に書く
                deepLApiKeyEncLine = $"DEEPL_API_KEY_ENC={encryptResult.Value}";
                deepLApiKeyPlainLine = "DEEPL_API_KEY=";
                break;
            case EncryptStatus.PlaintextByDesign:
                // 非Windows環境向け。旧形式のDEEPL_API_KEY(平文キー)として保存する
                deepLApiKeyEncLine = "DEEPL_API_KEY_ENC=";
                deepLApiKeyPlainLine = $"DEEPL_API_KEY={encryptResult.Value}";
                break;
            default: // EncryptStatus.Failed
                // 暗号化できない環境で平文保存するとDPAPIを使う意義が損なわれるため、
                // APIキーに関する行は書き換えず、.envに現在残っている値をそのまま維持する
                // (=ユーザーが今回入力/変更した値は保存されない)。
                deepLApiKeyEncLine = $"DEEPL_API_KEY_ENC={Environment.GetEnvironmentVariable("DEEPL_API_KEY_ENC") ?? ""}";
                deepLApiKeyPlainLine = $"DEEPL_API_KEY={Environment.GetEnvironmentVariable("DEEPL_API_KEY") ?? ""}";
                LastSaveDeepLKeySaveFailed = true;
                break;
        }

        var lines = new List<string>
        {
            "# 音声デバイス名に含まれるキーワード(例: CABLE, Chat)。DEVICE_IDが見つからない場合のフォールバック用",
            $"DEVICE_KEYWORD={DeviceKeyword}",
            "",
            "# 音声デバイスの一意なID。設定画面でデバイスを選択すると自動的に保存される(手動編集非推奨)",
            $"DEVICE_ID={DeviceId}",
            "",
            "# VAD(発話検出)の閾値。Silero VAD使用時は発話確率(0.05〜0.95)のスケール",
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
            $"OVERLAY_FONT_COLOR={OverlayFontColor}",
            "",
            "# 使用するWhisperモデルファイル名",
            $"WHISPER_MODEL_PATH={WhisperModelPath}",
            "",
            "# Whisper推論に使うスレッド数(1〜論理コア数)。大きいほど文字起こしは速くなるが、",
            "# ゲームなど他アプリのCPU負荷に影響しやすくなる",
            $"WHISPER_THREAD_COUNT={WhisperThreadCount}",
            "",
            "# 翻訳ワーカーの並行実行数(1〜4)。大きいほどキューが詰まりにくくなるが、",
            "# DeepL無料プランのレート制限に抵触しやすくなる",
            $"TRANSLATION_WORKER_COUNT={TranslationWorkerCount}",
            "",
            "# 発話終了からこの秒数を超えて未処理のまま残っている発話は、処理を諦めて",
            "# 最新の会話を優先する(0以下で機能を無効化=常に全部処理する)",
            $"MAX_LATENCY_SECONDS={MaxLatencySeconds.ToString(CultureInfo.InvariantCulture)}",
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
            "# trueの場合、DeepL翻訳が失敗した時に自動的にOllamaへフォールバックする",
            "# (TRANSLATION_BACKEND=deeplの場合のみ有効。Ollamaのセットアップが別途必要)",
            $"ENABLE_DEEPL_TO_OLLAMA_FALLBACK={EnableDeepLToOllamaFallback}",
            "",
            "# trueの場合、翻訳済みの原文(完全一致)をメモリ上にキャッシュし、",
            "# 同じ短い定型句等が繰り返された際にAPI呼び出しを省略する",
            $"ENABLE_TRANSLATION_CACHE={EnableTranslationCache}",
            "",
            "# DeepLを使う場合のAPIキー(無料プランは末尾に :fx が付く)。",
            "# Windows DPAPIで暗号化して保存しているため、このファイルを他PC/他ユーザーへ",
            "# コピーしても復号できない(このPC・このWindowsユーザーでのみ有効)。",
            "# (万一DPAPIでの暗号化自体に失敗した場合、安全のためAPIキーは保存されない)",
            deepLApiKeyEncLine,
            deepLApiKeyPlainLine,
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
            "# Ollama使用時、ユーザーが直接入力する手動用語集(\"original => translation\"形式、",
            "# 1行1エントリ。改行は\\nでエスケープ済み。同じ原文が参考コンテキストの自動抽出側にもあった場合はこちらが優先される)",
            $"MANUAL_GLOSSARY={escapedManualGlossary}",
            "",
            "# グローバルショートカットキー(設定画面の「ショートカット」タブから変更可能)",
            $"START_STOP_HOTKEY_MODIFIERS={StartStopHotkeyModifiers}",
            $"START_STOP_HOTKEY_KEY={StartStopHotkeyKey}",
            $"OVERLAY_HOTKEY_MODIFIERS={OverlayHotkeyModifiers}",
            $"OVERLAY_HOTKEY_KEY={OverlayHotkeyKey}",
        };

        // APIキーを含む設定ファイルなので、書き込み途中のクラッシュ/電源断で内容が
        // 中途半端になるのを避けるため、一時ファイルに書いてから置き換える(アトミック寄りの保存)。
        // Encoding.UTF8は既定でBOM付きになるため、明示的にBOM無しUTF-8を指定する
        // (このアプリはWindows専用でありBOM有無で読み込みに支障は無いが、他ツールで
        // .envを直接編集/diffする際にBOMが混入していると扱いにくいため)
        var targetPath = envPath ?? EnvPath;
        var tempPath = targetPath + ".tmp";
        File.WriteAllLines(tempPath, lines, new UTF8Encoding(false));
        File.Move(tempPath, targetPath, overwrite: true);

        // 実行中のプロセスにもすぐ反映されるよう環境変数も更新する
        Environment.SetEnvironmentVariable("DEVICE_KEYWORD", DeviceKeyword);
        Environment.SetEnvironmentVariable("DEVICE_ID", DeviceId);
        Environment.SetEnvironmentVariable("VAD_THRESHOLD", VadThreshold.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("VAD_HYSTERESIS_RATIO", VadHysteresisRatio.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("GAME_AUDIO_PRIORITY_MODE", GameAudioPriorityMode.ToString());
        Environment.SetEnvironmentVariable("ENABLE_DEEPL_TO_OLLAMA_FALLBACK", EnableDeepLToOllamaFallback.ToString());
        Environment.SetEnvironmentVariable("GAME_AUDIO_PRIORITY_MULTIPLIER", GameAudioPriorityMultiplier.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("OVERLAY_FONT_SIZE", OverlayFontSize.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("OVERLAY_OPACITY", OverlayOpacity.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("OVERLAY_MAX_LINES", OverlayMaxLines.ToString());
        Environment.SetEnvironmentVariable("OVERLAY_FONT_COLOR", OverlayFontColor);
        Environment.SetEnvironmentVariable("WHISPER_MODEL_PATH", WhisperModelPath);
        Environment.SetEnvironmentVariable("WHISPER_THREAD_COUNT", WhisperThreadCount.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("TRANSLATION_WORKER_COUNT", TranslationWorkerCount.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("MAX_LATENCY_SECONDS", MaxLatencySeconds.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("WHISPER_PROMPT", WhisperPrompt);
        Environment.SetEnvironmentVariable("RECOGNITION_LANGUAGE", RecognitionLanguage);
        Environment.SetEnvironmentVariable("TARGET_LANGUAGE_CODE", TargetLanguageCode);
        Environment.SetEnvironmentVariable("TRANSLATION_BACKEND", TranslationBackend);
        // ファイルへの書き込みと同じ理屈で、暗号化成否に応じて正しい方のキーだけに値を設定する
        // (もう片方は空文字にして、LoadFromEnv側のフォールバック判定と矛盾しないようにする)。
        // Failed時は.envの行を書き換えていないため、実行中プロセスの環境変数も変更しない
        // (元の値のまま=DeepLApiKeyプロパティに保持されている値と食い違うが、次回LoadFromEnv時に
        // .envの実際の値から再読込されるため矛盾は解消される)
        if (encryptResult.Status != EncryptStatus.Failed)
        {
            Environment.SetEnvironmentVariable("DEEPL_API_KEY_ENC", encryptResult.Status == EncryptStatus.Encrypted ? encryptResult.Value : "");
            Environment.SetEnvironmentVariable("DEEPL_API_KEY", encryptResult.Status == EncryptStatus.Encrypted ? "" : encryptResult.Value);
        }
        Environment.SetEnvironmentVariable("ENABLE_TRANSLATION_CACHE", EnableTranslationCache.ToString());
        Environment.SetEnvironmentVariable("OLLAMA_MODEL", OllamaModel);
        Environment.SetEnvironmentVariable("OLLAMA_ENDPOINT", OllamaEndpoint);
        Environment.SetEnvironmentVariable("OLLAMA_CONTEXT", escapedContext);
        Environment.SetEnvironmentVariable("MANUAL_GLOSSARY", escapedManualGlossary);
        Environment.SetEnvironmentVariable("START_STOP_HOTKEY_MODIFIERS", StartStopHotkeyModifiers);
        Environment.SetEnvironmentVariable("START_STOP_HOTKEY_KEY", StartStopHotkeyKey);
        Environment.SetEnvironmentVariable("OVERLAY_HOTKEY_MODIFIERS", OverlayHotkeyModifiers);
        Environment.SetEnvironmentVariable("OVERLAY_HOTKEY_KEY", OverlayHotkeyKey);
    }

    // TranslationWorkerCountとは独立に、DeepL/Ollamaそれぞれへの同時リクエスト数を制限する
    // 上限値(コードレビュー対応)。DeepLはクラウドAPIのレート制限を考慮して2、Ollamaは
    // ローカルの単一モデルインスタンスへの負荷集中を避けるため1に固定している
    // (将来的にユーザー設定にする余地はあるが、まずは固定値で様子を見る)。
    private const int DeepLMaxConcurrentRequests = 2;
    private const int OllamaMaxConcurrentRequests = 1;

    /// <summary>設定に応じた翻訳サービスを生成する。DeepLでAPIキー未設定の場合は
    /// (以前はnullを返していたが)NullTranslationService(IsEnabled=false)を返し、
    /// 呼び出し元がnullチェックをしなくても「翻訳せず文字起こしのみ」を扱えるようにする。
    ///
    /// EnableDeepLToOllamaFallback=trueの場合、DeepL選択時はFallbackTranslationServiceで
    /// 包み、DeepL失敗時に自動的にOllamaへ切り替えられるようにする(OllamaModel/Endpoint等は
    /// 通常のOllama設定をそのまま流用する。Ollama側が未セットアップでも、実際に使われる
    /// (=DeepLが失敗する)まではエラーにならない)。
    ///
    /// さらに、DeepL/Ollamaそれぞれを ConcurrencyLimitedTranslationService で包み、
    /// TranslationWorkerCount(翻訳ワーカー数、最大4)とは独立に、バックエンドへの同時
    /// リクエスト数を制限する(コードレビュー対応: ワーカー数=API同時実行数になっていたことで
    /// DeepLの429/タイムアウトが連鎖しうる懸念への対応)。</summary>
    public ITranslationService CreateTranslationService(System.Net.Http.HttpClient httpClient)
    {
        var targetOption = LanguageCatalog.FindByDeepLCode(TargetLanguageCode);

        if (TranslationBackend.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            var ollama = new OllamaTranslationService(httpClient, OllamaModel, OllamaEndpoint, targetOption.EnglishName, OllamaContext, ManualGlossary);
            var limitedOllama = new ConcurrencyLimitedTranslationService(ollama, new SemaphoreSlim(OllamaMaxConcurrentRequests));
            return WithCache(limitedOllama);
        }

        if (!string.IsNullOrWhiteSpace(DeepLApiKey))
        {
            var deepL = new DeepLTranslationService(httpClient, DeepLApiKey, targetOption.DeepLCode);
            var limitedDeepL = new ConcurrencyLimitedTranslationService(deepL, new SemaphoreSlim(DeepLMaxConcurrentRequests));
            if (!EnableDeepLToOllamaFallback) return WithCache(limitedDeepL);

            var ollamaFallback = new OllamaTranslationService(httpClient, OllamaModel, OllamaEndpoint, targetOption.EnglishName, OllamaContext, ManualGlossary);
            var limitedOllamaFallback = new ConcurrencyLimitedTranslationService(ollamaFallback, new SemaphoreSlim(OllamaMaxConcurrentRequests));
            return WithCache(new FallbackTranslationService(limitedDeepL, limitedOllamaFallback));
        }

        return NullTranslationService.Instance;
    }

    /// <summary>EnableTranslationCache設定に応じて、翻訳サービスをCachingTranslationServiceで
    /// 包むかどうかを切り替える。NullTranslationService(翻訳無効時)はそもそもTranslateAsyncが
    /// 呼ばれないため、ここを通す対象に含めていない。</summary>
    private ITranslationService WithCache(ITranslationService service)
        => EnableTranslationCache ? new CachingTranslationService(service) : service;
}
