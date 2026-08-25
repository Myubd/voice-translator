using System.Collections.Generic;
using System.Linq;

/// <summary>1つの言語についての各種表記をまとめたもの</summary>
public record LanguageOption(string Display, string WhisperCode, string DeepLCode, string EnglishName);

/// <summary>アプリで選択できる言語の一覧</summary>
public static class LanguageCatalog
{
    /// <summary>認識(Whisper)側で選べる言語。「自動」を含む</summary>
    public static readonly List<LanguageOption> SourceLanguages = new()
    {
        new("自動検出", "auto", "", "auto"),
        new("英語", "en", "EN-US", "English"),
        new("日本語", "ja", "JA", "Japanese"),
        new("韓国語", "ko", "KO", "Korean"),
        new("中国語", "zh", "ZH", "Chinese"),
        new("スペイン語", "es", "ES", "Spanish"),
        new("フランス語", "fr", "FR", "French"),
        new("ドイツ語", "de", "DE", "German"),
        new("ロシア語", "ru", "RU", "Russian"),
        new("ポルトガル語", "pt", "PT-PT", "Portuguese"),
    };

    /// <summary>翻訳先で選べる言語。「自動」は含まない</summary>
    public static readonly List<LanguageOption> TargetLanguages =
        SourceLanguages.Where(l => l.WhisperCode != "auto").ToList();

    /// <summary>既定(フォールバック)言語。日本語をコードで明示的に指定する。
    /// 以前はTargetLanguages[1]という配列インデックス依存だったため、リストの並び順を変えると
    /// 意図せず既定言語が変わってしまう問題があった。</summary>
    private static readonly LanguageOption DefaultTargetLanguage =
        TargetLanguages.First(l => l.DeepLCode == "JA");

    public static LanguageOption FindByDeepLCode(string deepLCode) =>
        TargetLanguages.FirstOrDefault(l => l.DeepLCode == deepLCode) ?? DefaultTargetLanguage;

    /// <summary>WhisperCode(BCP-47に近い簡易コード。en/ja/ko等)から言語を探す。
    /// OCR認識言語の選択(Windows.Media.Ocrへ渡すBCP-47タグ)に使う。
    /// 認識対象に「自動」は無いため、SourceLanguages(autoを含む)ではなくTargetLanguagesから探す。</summary>
    public static LanguageOption FindByWhisperCode(string whisperCode) =>
        TargetLanguages.FirstOrDefault(l => l.WhisperCode == whisperCode) ?? DefaultTargetLanguage;
}
