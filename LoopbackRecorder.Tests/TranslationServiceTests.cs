using System;
using System.Threading;
using System.Threading.Tasks;
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

/// <summary>
/// NullTranslationService(「翻訳せず文字起こしのみ」を表すNullオブジェクト)の単体テスト。
/// TranslationWorker/AudioPipeline側は、このクラスのIsEnabled==falseを見て翻訳をスキップする
/// ため、ここでは「IsEnabledがfalseであること」「万一呼ばれてもクラッシュせず失敗結果を返すこと」
/// の2点のみを検証する(呼び出し側の分岐ロジック自体はTranslationWorkerTestsで検証済み)。
/// </summary>
public class NullTranslationServiceTests
{
    [Fact]
    public void IsEnabledはfalseを返す()
    {
        Assert.False(NullTranslationService.Instance.IsEnabled);
    }

    [Fact]
    public async System.Threading.Tasks.Task TranslateAsyncを呼んでも例外にならず失敗結果を返す()
    {
        var result = await NullTranslationService.Instance.TranslateAsync("test", System.Threading.CancellationToken.None);

        Assert.Null(result.Text);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void Instanceは常に同一のシングルトンを返す()
    {
        Assert.Same(NullTranslationService.Instance, NullTranslationService.Instance);
    }
}

/// <summary>
/// FallbackTranslationService(DeepL失敗時にOllamaへ自動フォールバックするラッパー)の単体テスト。
/// 実際のHTTP通信は行わず、呼び出し回数・結果を記録するフェイクサービスで主(DeepL相当)・
/// 副(Ollama相当)の両方を差し替えて検証する。
/// </summary>
public class FallbackTranslationServiceTests
{
    private sealed class FakeService : ITranslationService
    {
        private readonly Func<string, CancellationToken, Task<TranslationResult>> _handler;
        // 副(Ollama相当)側のPrepareAsyncはFallbackTranslationService内でバックグラウンド実行
        // (fire-and-forget)されるため、テスト側で「呼ばれたこと」を確定的に待てるよう、
        // 呼ばれた時点で完了するTaskCompletionSourceを公開しておく。
        private readonly TaskCompletionSource<bool> _prepareCompletion = new();
        public int CallCount { get; private set; }
        public bool PrepareCalled { get; private set; }
        public Task PrepareCompletion => _prepareCompletion.Task;
        public bool IsEnabled => true;

        public FakeService(Func<string, TranslationResult> handler)
        {
            _handler = (text, _) => Task.FromResult(handler(text));
        }

        public FakeService(Func<string, CancellationToken, Task<TranslationResult>> handler)
        {
            _handler = handler;
        }

        public Task<TranslationResult> TranslateAsync(string text, CancellationToken cancellationToken)
        {
            CallCount++;
            return _handler(text, cancellationToken);
        }

        public Task PrepareAsync(CancellationToken cancellationToken)
        {
            PrepareCalled = true;
            _prepareCompletion.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    /// <summary>PrepareAsyncの完了を任意のタイミングまで遅延させられるフェイク。
    /// 「副(Ollama相当)の準備が重くても、主(DeepL相当)完了時点でPrepareAsync全体は
    /// 返ってくる(=セッション開始がブロックされない)」ことを検証するために使う。</summary>
    private sealed class SlowPrepareService : ITranslationService
    {
        private readonly TaskCompletionSource<bool> _canFinishPrepare = new();
        private readonly TaskCompletionSource<bool> _prepareStarted = new();
        public bool PrepareCalled { get; private set; }
        public bool IsEnabled => true;

        /// <summary>PrepareAsyncが(バックグラウンドスレッドで)実際に呼ばれるまで待つためのTask。
        /// Task.Runでのスケジューリングには僅かな遅延があるため、単純にPrepareCalledを
        /// ポーリングするのではなく、この通知を待つことでテストの決定性を保つ。</summary>
        public Task Started => _prepareStarted.Task;

        public void AllowPrepareToFinish() => _canFinishPrepare.TrySetResult(true);

        public Task<TranslationResult> TranslateAsync(string text, CancellationToken cancellationToken)
            => Task.FromResult(TranslationResult.Success(text));

        public async Task PrepareAsync(CancellationToken cancellationToken)
        {
            PrepareCalled = true;
            _prepareStarted.TrySetResult(true);
            await _canFinishPrepare.Task; // AllowPrepareToFinish()が呼ばれるまで完了しない
        }
    }

    [Fact]
    public async Task 主が成功した場合は副を一切呼ばない()
    {
        var primary = new FakeService(_ => TranslationResult.Success("primary-ok"));
        var fallback = new FakeService(_ => TranslationResult.Success("fallback-ok"));
        var sut = new FallbackTranslationService(primary, fallback);

        var result = await sut.TranslateAsync("hello", CancellationToken.None);

        Assert.Equal("primary-ok", result.Text);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(0, fallback.CallCount); // 成功時に副へ余計なリクエストが飛んでいないこと
    }

    [Fact]
    public async Task 主が失敗した場合は副の結果にフォールバックする()
    {
        var primary = new FakeService(_ => TranslationResult.Failure("deepl timeout"));
        var fallback = new FakeService(_ => TranslationResult.Success("fallback-ok"));
        var sut = new FallbackTranslationService(primary, fallback);

        var result = await sut.TranslateAsync("hello", CancellationToken.None);

        Assert.Equal("fallback-ok", result.Text);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(1, fallback.CallCount);
    }

    [Fact]
    public async Task 主副とも失敗した場合は両方のエラーメッセージを含む()
    {
        var primary = new FakeService(_ => TranslationResult.Failure("deepl timeout"));
        var fallback = new FakeService(_ => TranslationResult.Failure("ollama not running"));
        var sut = new FallbackTranslationService(primary, fallback);

        var result = await sut.TranslateAsync("hello", CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Contains("deepl timeout", result.ErrorMessage);
        Assert.Contains("ollama not running", result.ErrorMessage);
    }

    [Fact]
    public async Task 停止操作によるキャンセルの場合は副を呼ばない()
    {
        using var cts = new CancellationTokenSource();
        var primary = new FakeService((_, ct) =>
        {
            cts.Cancel(); // 「翻訳中に停止ボタンが押された」状況を再現
            return Task.FromResult(TranslationResult.Failure("cancelled"));
        });
        var fallback = new FakeService(_ => TranslationResult.Success("fallback-ok"));
        var sut = new FallbackTranslationService(primary, fallback);

        var result = await sut.TranslateAsync("hello", cts.Token);

        Assert.Equal(0, fallback.CallCount); // 停止操作時は追加のリクエストを送らない
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task PrepareAsyncは主が完了した後副がバックグラウンドで呼ばれる()
    {
        var primary = new FakeService(_ => TranslationResult.Success("x"));
        var fallback = new FakeService(_ => TranslationResult.Success("y"));
        var sut = new FallbackTranslationService(primary, fallback);

        await sut.PrepareAsync(CancellationToken.None);

        // 副はバックグラウンド実行(fire-and-forget)のため、PrepareAsync自体の完了を
        // 待つだけでは呼ばれたかどうかを確定的に判定できない。専用の完了通知を待つ
        await fallback.PrepareCompletion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(primary.PrepareCalled);
        Assert.True(fallback.PrepareCalled);
    }

    [Fact]
    public async Task 副の準備が重くても主完了時点でPrepareAsyncはブロックされない()
    {
        // Ollamaモデルの事前ロードのような重い処理が副にあっても、DeepLだけで完結する
        // セッションの開始が待たされてはならない、という今回の変更の核心を検証する
        var primary = new FakeService(_ => TranslationResult.Success("x"));
        var slowFallback = new SlowPrepareService();
        var sut = new FallbackTranslationService(primary, slowFallback);

        var prepareTask = sut.PrepareAsync(CancellationToken.None);
        var completed = await Task.WhenAny(prepareTask, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(prepareTask, completed); // 副が完了していなくてもPrepareAsync自体は返っている

        await slowFallback.Started.WaitAsync(TimeSpan.FromSeconds(2)); // 副の準備が(バックグラウンドで)開始されるのを確定的に待つ
        Assert.True(slowFallback.PrepareCalled);

        slowFallback.AllowPrepareToFinish(); // 後片付け: バックグラウンドタスクを完了させておく
    }

    [Fact]
    public void IsEnabledは主の値をそのまま返す()
    {
        var alwaysOffPrimary = NullTranslationService.Instance; // IsEnabled=falseの実例として流用
        var fallback = new FakeService(_ => TranslationResult.Success("y"));
        var sut = new FallbackTranslationService(alwaysOffPrimary, fallback);

        Assert.False(sut.IsEnabled);
    }

    [Fact]
    public async Task 主が認証エラーの場合は副にフォールバックせずそのまま返す()
    {
        var primary = new FakeService(_ => TranslationResult.AuthFailure("APIキーが正しくありません"));
        var fallback = new FakeService(_ => TranslationResult.Success("fallback-ok"));
        var sut = new FallbackTranslationService(primary, fallback);

        var result = await sut.TranslateAsync("hello", CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Equal("APIキーが正しくありません", result.ErrorMessage);
        Assert.Equal(0, fallback.CallCount); // 設定ミスがフォールバックで隠れないよう、副は呼ばない
    }

    [Fact]
    public async Task 主が利用上限エラーの場合は副にフォールバックしつつ成功結果にも警告を残す()
    {
        var primary = new FakeService(_ => TranslationResult.QuotaFailure("DeepLの利用上限に達しました"));
        var fallback = new FakeService(_ => TranslationResult.Success("fallback-ok"));
        var sut = new FallbackTranslationService(primary, fallback);

        var result = await sut.TranslateAsync("hello", CancellationToken.None);

        Assert.Equal("fallback-ok", result.Text); // 訳文自体は問題なく返る
        Assert.Equal(1, fallback.CallCount); // 429/456は401/403と違いフォールバックを続ける
        Assert.NotNull(result.Warning);
        Assert.Contains("DeepLの利用上限に達しました", result.Warning);
    }
}
