using Xunit;

namespace LoopbackRecorder.Tests;

/// <summary>
/// OllamaTranslationService.BuildValidatedGlossary(用語集抽出結果の検証・整形ロジック)の単体テスト。
///
/// このメソッドはOllamaのLLM出力(自由記述に近いテキスト)を、翻訳プロンプトに混入させる前に
/// "original => translation" 形式へ厳格にバリデーションする役割を持つ。プロンプトインジェクション的な
/// 事故や、フォーマット崩れによるハルシネーションを防ぐための最終防衛ラインなので、
/// 境界値(件数上限・文字数上限・重複・不正フォーマット)を重点的に検証する。
/// </summary>
public class TranslationServiceTests
{
    [Fact]
    public void 正しい形式の行はそのまま整形されて返る()
    {
        var input = "Aetherium => エーテリウム\nRoundtable Hold => 円卓";

        var result = OllamaTranslationService.BuildValidatedGlossary(input);

        Assert.Equal("Aetherium => エーテリウム\nRoundtable Hold => 円卓", result);
    }

    [Fact]
    public void 空文字列だけの入力はnullを返す()
    {
        var result = OllamaTranslationService.BuildValidatedGlossary("");
        Assert.Null(result);
    }

    [Fact]
    public void 矢印が無い行は除外される()
    {
        var input = "これは矢印を含まない説明文です\nMalenia => マレニア";

        var result = OllamaTranslationService.BuildValidatedGlossary(input);

        Assert.Equal("Malenia => マレニア", result);
    }

    [Fact]
    public void 矢印が複数回登場する行は最初の矢印だけで分割される()
    {
        // Split("=>", 2, ...)により、2つ目以降の"=>"は訳語側の文字列の一部として扱われる
        // (訳文自体に"=>"のような記号が含まれるケースを誤って除外しないための挙動)
        var input = "A => B => C\nRadahn => ラダーン";

        var result = OllamaTranslationService.BuildValidatedGlossary(input);

        Assert.Equal("A => B => C\nRadahn => ラダーン", result);
    }

    [Theory]
    [InlineData(" => 訳語")]      // 原文が空
    [InlineData("Term => ")]      // 訳語が空
    [InlineData("   =>    ")]     // 両方空
    public void 原文または訳語が空の行は除外される(string invalidLine)
    {
        var input = invalidLine + "\nTarnished => 褪せ人";

        var result = OllamaTranslationService.BuildValidatedGlossary(input);

        Assert.Equal("Tarnished => 褪せ人", result);
    }

    [Fact]
    public void 上限文字数を超える原文または訳語の行は除外される()
    {
        var tooLong = new string('a', OllamaTranslationService.MaxGlossaryTermLength + 1);
        var input = $"{tooLong} => 訳語\nOK => 妥当";

        var result = OllamaTranslationService.BuildValidatedGlossary(input);

        Assert.Equal("OK => 妥当", result);
    }

    [Fact]
    public void 上限文字数ちょうどの行は許容される()
    {
        var justFits = new string('a', OllamaTranslationService.MaxGlossaryTermLength);
        var input = $"{justFits} => 訳語";

        var result = OllamaTranslationService.BuildValidatedGlossary(input);

        Assert.Equal($"{justFits} => 訳語", result);
    }

    [Fact]
    public void 同じ原文が複数回出現した場合は最初の1件のみ採用される()
    {
        var input = "Aetherium => エーテリウム(1回目)\nAetherium => エーテリウム(2回目、無視されるはず)";

        var result = OllamaTranslationService.BuildValidatedGlossary(input);

        Assert.Equal("Aetherium => エーテリウム(1回目)", result);
    }

    [Fact]
    public void 原文の大文字小文字違いも重複とみなされる()
    {
        // seenOriginalsはOrdinalIgnoreCaseで比較しているため、大文字小文字だけが違う原文も
        // 同じ用語とみなして2件目以降を除外する
        var input = "aetherium => 小文字\nAETHERIUM => 大文字(無視されるはず)";

        var result = OllamaTranslationService.BuildValidatedGlossary(input);

        Assert.Equal("aetherium => 小文字", result);
    }

    [Fact]
    public void 件数上限を超えた分は切り捨てられる()
    {
        var lines = new System.Collections.Generic.List<string>();
        for (int i = 0; i < OllamaTranslationService.MaxGlossaryEntries + 10; i++)
        {
            lines.Add($"Term{i} => 訳語{i}");
        }
        var input = string.Join("\n", lines);

        var result = OllamaTranslationService.BuildValidatedGlossary(input);

        var resultLines = result!.Split('\n');
        Assert.Equal(OllamaTranslationService.MaxGlossaryEntries, resultLines.Length);
        // 上限に達した時点で処理を打ち切るため、先頭からMaxGlossaryEntries件が採用される
        Assert.Equal("Term0 => 訳語0", resultLines[0]);
        Assert.Equal($"Term{OllamaTranslationService.MaxGlossaryEntries - 1} => 訳語{OllamaTranslationService.MaxGlossaryEntries - 1}", resultLines[^1]);
    }

    [Fact]
    public void 前後の空白と改行コードはトリムされる()
    {
        var input = "  Term  =>  Value  \r\n";

        var result = OllamaTranslationService.BuildValidatedGlossary(input);

        Assert.Equal("Term => Value", result);
    }
}
