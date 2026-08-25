using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// ゲームごとに切り替えたい設定値をまとめたプロファイル。
///
/// AppSettings全体(デバイス選択・ホットキー・DeepL APIキー等)をプロファイル化すると、
/// 切り替えるたびに意図せず接続先デバイスやAPIキーまで変わってしまい、事故の元になる。
/// そのため、ゲームによって実際に変えたくなる「翻訳関連」と「オーバーレイ表示」の項目
/// だけに、あえてスコープを絞っている。
/// </summary>
public sealed class GameProfile
{
    public string Name { get; set; } = "";

    // ---- 翻訳関連 ----
    public string TranslationBackend { get; set; } = "deepl";
    public string TargetLanguageCode { get; set; } = "JA";
    public string OllamaContext { get; set; } = "";
    public string ManualGlossary { get; set; } = "";

    // ---- オーバーレイ表示 ----
    public double OverlayFontSize { get; set; } = 22;
    public double OverlayOpacity { get; set; } = 0.7;
    public int OverlayMaxLines { get; set; } = 4;
    public string OverlayFontColor { get; set; } = "#FFFFFF";

    /// <summary>現在のAppSettingsから、プロファイル化対象の項目だけを抜き出して新規作成する。</summary>
    public static GameProfile CaptureFrom(AppSettings settings, string name) => new()
    {
        Name = name,
        TranslationBackend = settings.TranslationBackend,
        TargetLanguageCode = settings.TargetLanguageCode,
        OllamaContext = settings.OllamaContext,
        ManualGlossary = settings.ManualGlossary,
        OverlayFontSize = settings.OverlayFontSize,
        OverlayOpacity = settings.OverlayOpacity,
        OverlayMaxLines = settings.OverlayMaxLines,
        OverlayFontColor = settings.OverlayFontColor,
    };

    /// <summary>このプロファイルの値をAppSettingsへ適用する(対象外の項目には一切触れない)。
    ///
    /// game_profiles.jsonは手動編集や壊れたバックアップからの復旧で異常値
    /// (OverlayOpacity=999等)が混入し得るため、AppSettings.LoadFromEnv()が
    /// .envの値に対して行っているのと同じ範囲のclampをここでも行ってから適用する。</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.TranslationBackend = TranslationBackend;
        settings.TargetLanguageCode = TargetLanguageCode;
        settings.OllamaContext = OllamaContext;
        settings.ManualGlossary = ManualGlossary;
        settings.OverlayFontSize = Math.Clamp(OverlayFontSize, 14, 48);
        settings.OverlayOpacity = Math.Clamp(OverlayOpacity, 0, 1);
        settings.OverlayMaxLines = Math.Clamp(OverlayMaxLines, 1, 10);

        // "#RRGGBB"形式(7文字、#始まり)以外は無視して既定値を保持する。
        // AppSettings.LoadFromEnv()のOVERLAY_FONT_COLOR検証と同じ条件。
        if (!string.IsNullOrEmpty(OverlayFontColor) && OverlayFontColor.Length == 7 && OverlayFontColor[0] == '#')
        {
            settings.OverlayFontColor = OverlayFontColor;
        }
    }
}

/// <summary>
/// 複数のGameProfileを1つのJSONファイルに保存・読込する。
/// .envと同じくAppContext.BaseDirectory基準の固定ファイル(既定: game_profiles.json)に
/// 保存する(.env自体はデバイス・APIキー等を含む「現在アクティブな設定」の置き場所として
/// 引き続き使い、プロファイルはそれとは別の「名前付きのプリセット集」として独立させている)。
/// </summary>
public static class GameProfileStore
{
    private static readonly string DefaultPath = Path.Combine(AppContext.BaseDirectory, "game_profiles.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>保存済みの全プロファイルを名前昇順で読み込む。本体ファイルが存在しない/壊れている
    /// 場合は".bak"(直前の世代のバックアップ)からの復旧を試みる。それも無ければ実行時エラーで
    /// 落とさず空の一覧を返す(この場合のみ「本当にプロファイルが無い」と区別が付かないため、
    /// 呼び出し元でユーザーに気づかせたい場合はrecoveredFromBackupを見て通知すること)。</summary>
    public static List<GameProfile> LoadAll(string? path = null) => LoadAll(path, out _);

    public static List<GameProfile> LoadAll(string? path, out bool recoveredFromBackup)
    {
        var targetPath = path ?? DefaultPath;
        recoveredFromBackup = false;

        if (TryLoadFrom(targetPath, out var profiles))
        {
            return profiles;
        }

        var backupPath = targetPath + ".bak";
        if (TryLoadFrom(backupPath, out var backupProfiles))
        {
            Logger.Log("GameProfile.Load",
                $"{Path.GetFileName(targetPath)}の読み込みに失敗したため、バックアップ({Path.GetFileName(backupPath)})から復旧しました。");
            recoveredFromBackup = true;
            return backupProfiles;
        }

        return new List<GameProfile>();
    }

    /// <summary>指定パスからの読み込みを試みる。ファイルが存在しない/壊れている場合はfalseを返す
    /// (例外は呼び出し元に投げず、ここで握りつぶしてログのみ残す)。</summary>
    private static bool TryLoadFrom(string targetPath, out List<GameProfile> profiles)
    {
        profiles = new List<GameProfile>();
        if (!File.Exists(targetPath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(targetPath);
            var loaded = JsonSerializer.Deserialize<List<GameProfile>>(json, JsonOptions);
            profiles = (loaded ?? new List<GameProfile>())
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log("GameProfile.Load", $"{targetPath}の読み込みに失敗しました。", ex);
            return false;
        }
    }

    /// <summary>全プロファイルをJSONファイルへ書き込む(呼び出し側がLoadAll→一覧を編集→SaveAllという
    /// 一括更新の流れで使うことを想定。1件だけの追加/更新/削除もこの流れで行う)。
    ///
    /// .envの保存(AppSettings.SaveToEnv)と同じく、一時ファイルに書いてからFile.Moveで置き換える
    /// アトミック寄りの保存にする。以前はFile.WriteAllTextの一発書きだったため、書き込み途中の
    /// クラッシュ/電源断でファイルが壊れる可能性があり、しかもAPIキー等を含む.envより保護が
    /// 弱いというアンバランスな状態だった。あわせて、置き換え直前の内容を".bak"として1世代
    /// 残すことで、万一新しい内容自体に問題があっても直前の状態に戻せるようにする。</summary>
    public static void SaveAll(IEnumerable<GameProfile> profiles, string? path = null)
    {
        var targetPath = path ?? DefaultPath;
        var tempPath = targetPath + ".tmp";
        var backupPath = targetPath + ".bak";
        var json = JsonSerializer.Serialize(profiles.ToList(), JsonOptions);

        File.WriteAllText(tempPath, json, new UTF8Encoding(false));

        if (File.Exists(targetPath))
        {
            // 新しい内容に置き換える前に、現在の内容をバックアップとして残す。
            // File.Copyの失敗(権限等)で保存全体を止めたくないため、ここは失敗してもログのみ。
            try
            {
                File.Copy(targetPath, backupPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.Log("GameProfile.Save", "バックアップファイルの作成に失敗しました。保存自体は続行します。", ex);
            }
        }

        File.Move(tempPath, targetPath, overwrite: true);
    }

    // Upsert/Deleteは「LoadAll→一覧を編集→SaveAll」という2段階操作で、間に排他制御が無いと、
    // 複数箇所(設定画面の別操作、将来の自動プロファイル切替機能等)から同時に呼ばれた場合に
    // 後勝ちで一方の変更が消える(lost update)可能性がある。単一プロセス内からの呼び出しのみを
    // 想定しているため、ファイルロックまでは不要と判断し、プロセス内lockで直列化するに留める。
    private static readonly object FileLock = new();

    /// <summary>指定した名前のプロファイルを追加(同名が既にあれば上書き)して保存する。
    /// 名前の一致は前後の空白を無視した完全一致(大文字小文字は区別する。日本語のゲームタイトルを
    /// 想定しており、英語名同士の大文字小文字違いを別プロファイルとして許容したいため)。</summary>
    public static void Upsert(GameProfile profile, string? path = null)
    {
        var trimmedName = profile.Name.Trim();
        profile.Name = trimmedName;

        lock (FileLock)
        {
            var profiles = LoadAll(path);
            var existingIndex = profiles.FindIndex(p => p.Name == trimmedName);
            if (existingIndex >= 0)
            {
                profiles[existingIndex] = profile;
            }
            else
            {
                profiles.Add(profile);
            }
            SaveAll(profiles, path);
        }
    }

    /// <summary>指定した名前のプロファイルを削除して保存する。該当が無い場合は何もしない。</summary>
    public static void Delete(string name, string? path = null)
    {
        lock (FileLock)
        {
            var profiles = LoadAll(path);
            var removed = profiles.RemoveAll(p => p.Name == name.Trim());
            if (removed > 0)
            {
                SaveAll(profiles, path);
            }
        }
    }
}
