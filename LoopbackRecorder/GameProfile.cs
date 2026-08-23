using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>このプロファイルの値をAppSettingsへ適用する(対象外の項目には一切触れない)。</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.TranslationBackend = TranslationBackend;
        settings.TargetLanguageCode = TargetLanguageCode;
        settings.OllamaContext = OllamaContext;
        settings.ManualGlossary = ManualGlossary;
        settings.OverlayFontSize = OverlayFontSize;
        settings.OverlayOpacity = OverlayOpacity;
        settings.OverlayMaxLines = OverlayMaxLines;
        settings.OverlayFontColor = OverlayFontColor;
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

    /// <summary>保存済みの全プロファイルを名前昇順で読み込む。ファイルが存在しない場合や、
    /// 壊れている場合(手動編集による構文エラー等)は、実行時エラーで落とさず空の一覧を返す。</summary>
    public static List<GameProfile> LoadAll(string? path = null)
    {
        var targetPath = path ?? DefaultPath;
        if (!File.Exists(targetPath))
        {
            return new List<GameProfile>();
        }

        try
        {
            var json = File.ReadAllText(targetPath);
            var profiles = JsonSerializer.Deserialize<List<GameProfile>>(json, JsonOptions);
            return (profiles ?? new List<GameProfile>())
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Log("GameProfile.Load", "game_profiles.jsonの読み込みに失敗しました。プロファイル無しとして続行します。", ex);
            return new List<GameProfile>();
        }
    }

    /// <summary>全プロファイルをJSONファイルへ書き込む(呼び出し側がLoadAll→一覧を編集→SaveAllという
    /// 一括更新の流れで使うことを想定。1件だけの追加/更新/削除もこの流れで行う)。</summary>
    public static void SaveAll(IEnumerable<GameProfile> profiles, string? path = null)
    {
        var targetPath = path ?? DefaultPath;
        var json = JsonSerializer.Serialize(profiles.ToList(), JsonOptions);
        File.WriteAllText(targetPath, json);
    }

    /// <summary>指定した名前のプロファイルを追加(同名が既にあれば上書き)して保存する。
    /// 名前の一致は前後の空白を無視した完全一致(大文字小文字は区別する。日本語のゲームタイトルを
    /// 想定しており、英語名同士の大文字小文字違いを別プロファイルとして許容したいため)。</summary>
    public static void Upsert(GameProfile profile, string? path = null)
    {
        var trimmedName = profile.Name.Trim();
        profile.Name = trimmedName;

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

    /// <summary>指定した名前のプロファイルを削除して保存する。該当が無い場合は何もしない。</summary>
    public static void Delete(string name, string? path = null)
    {
        var profiles = LoadAll(path);
        var removed = profiles.RemoveAll(p => p.Name == name.Trim());
        if (removed > 0)
        {
            SaveAll(profiles, path);
        }
    }
}
