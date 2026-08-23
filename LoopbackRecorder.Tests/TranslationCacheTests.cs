using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LoopbackRecorder.Tests;

/// <summary>
/// CachingTranslationService(同じ原文の翻訳結果をメモリキャッシュするデコレーター)の単体テスト。
/// 実際のHTTP通信は行わず、呼び出し回数を記録するフェイクサービスで内側の翻訳サービスを差し替える。
/// </summary>
public class CachingTranslationServiceTests
{
    private sealed class FakeService : ITranslationService
    {
        private readonly System.Func<string, TranslationResult> _handler;
        public int CallCount { get; private set; }
        public bool IsEnabled { get; set; } = true;

        public FakeService(System.Func<string, TranslationResult> handler)
        {
            _handler = handler;
        }

        public Task<TranslationResult> TranslateAsync(string text, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_handler(text));
        }
    }

    [Fact]
    public async Task 同じ原文を2回訳すと内側のサービスは1回しか呼ばれない()
    {
        var inner = new FakeService(text => TranslationResult.Success($"訳:{text}"));
        var sut = new CachingTranslationService(inner);

        var first = await sut.TranslateAsync("gg", CancellationToken.None);
        var second = await sut.TranslateAsync("gg", CancellationToken.None);

        Assert.Equal("訳:gg", first.Text);
        Assert.Equal("訳:gg", second.Text);
        Assert.Equal(1, inner.CallCount); // 2回目はキャッシュから返り、内側は呼ばれない
    }

    [Fact]
    public async Task 異なる原文はそれぞれ内側のサービスが呼ばれる()
    {
        var inner = new FakeService(text => TranslationResult.Success($"訳:{text}"));
        var sut = new CachingTranslationService(inner);

        await sut.TranslateAsync("gg", CancellationToken.None);
        await sut.TranslateAsync("nice", CancellationToken.None);

        Assert.Equal(2, inner.CallCount);
    }

    [Fact]
    public async Task 翻訳が失敗した場合はキャッシュされず次回も内側が呼ばれる()
    {
        var inner = new FakeService(_ => TranslationResult.Failure("timeout"));
        var sut = new CachingTranslationService(inner);

        var first = await sut.TranslateAsync("gg", CancellationToken.None);
        var second = await sut.TranslateAsync("gg", CancellationToken.None);

        Assert.Null(first.Text);
        Assert.Null(second.Text);
        Assert.Equal(2, inner.CallCount); // 失敗結果はキャッシュしないため毎回呼ばれる
    }

    [Fact]
    public async Task 上限件数を超えると古いものから間引かれる()
    {
        var inner = new FakeService(text => TranslationResult.Success($"訳:{text}"));
        var sut = new CachingTranslationService(inner, maxEntries: 2);

        await sut.TranslateAsync("a", CancellationToken.None); // 挿入順: a
        await sut.TranslateAsync("b", CancellationToken.None); // 挿入順: a, b
        await sut.TranslateAsync("c", CancellationToken.None); // 上限超過でaが間引かれる: b, c

        var before = inner.CallCount;

        await sut.TranslateAsync("a", CancellationToken.None); // 間引かれているため再度呼ばれる

        Assert.Equal(before + 1, inner.CallCount);
    }

    [Fact]
    public void IsEnabledは内側の値をそのまま返す()
    {
        var inner = new FakeService(_ => TranslationResult.Success("x")) { IsEnabled = false };
        var sut = new CachingTranslationService(inner);

        Assert.False(sut.IsEnabled);
    }
}
