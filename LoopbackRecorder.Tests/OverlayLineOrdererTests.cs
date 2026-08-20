using System.Linq;
using Xunit;

namespace LoopbackRecorder.Tests;

public class OverlayLineOrdererTests
{
    [Fact]
    public void 発話順どおりに届いた場合は末尾に追加され続ける()
    {
        var orderer = new OverlayLineOrderer(maxLines: 10);

        var r1 = orderer.Upsert(1, "one");
        var r2 = orderer.Upsert(2, "two");
        var r3 = orderer.Upsert(3, "three");

        Assert.Equal(0, r1.Index);
        Assert.True(r1.IsLatest);
        Assert.Equal(1, r2.Index);
        Assert.True(r2.IsLatest);
        Assert.Equal(2, r3.Index);
        Assert.True(r3.IsLatest);
        Assert.Equal(new long[] { 1, 2, 3 }, orderer.Lines.Select(l => l.Id));
    }

    [Fact]
    public void 完了順が入れ替わってもId順の正しい位置に挿入される()
    {
        // 並列翻訳ワーカーにより、Id=1(先に話した内容)がDeepL失敗→Ollamaフォールバックで遅れ、
        // 先にId=2とId=3の翻訳が完了する、というシナリオを想定
        var orderer = new OverlayLineOrderer(maxLines: 10);

        orderer.Upsert(2, "two");
        orderer.Upsert(3, "three");
        var r1 = orderer.Upsert(1, "one"); // 遅れて到着

        // Id=1は既存の2件より前(先頭)に挿入されるべきで、「最新」ではない
        Assert.Equal(0, r1.Index);
        Assert.False(r1.IsLatest);
        Assert.Equal(new long[] { 1, 2, 3 }, orderer.Lines.Select(l => l.Id));
        Assert.Equal(new[] { "one", "two", "three" }, orderer.Lines.Select(l => l.Text));
    }

    [Fact]
    public void 中間のIdが遅れて到着した場合も正しい位置に挿入される()
    {
        var orderer = new OverlayLineOrderer(maxLines: 10);

        orderer.Upsert(1, "one");
        orderer.Upsert(3, "three");
        var r2 = orderer.Upsert(2, "two"); // 1と3の間に挿入されるべき

        Assert.Equal(1, r2.Index);
        Assert.False(r2.IsLatest);
        Assert.Equal(new long[] { 1, 2, 3 }, orderer.Lines.Select(l => l.Id));
    }

    [Fact]
    public void 同じIdを2回渡すと追加ではなく上書き更新される()
    {
        var orderer = new OverlayLineOrderer(maxLines: 10);

        orderer.Upsert(1, "one (仮)");
        var result = orderer.Upsert(1, "one (確定)");

        Assert.True(result.IsUpdate);
        Assert.Equal(0, result.Index);
        Assert.Single(orderer.Lines);
        Assert.Equal("one (確定)", orderer.Lines[0].Text);
    }

    [Fact]
    public void 上限行数を超えると先頭最古の行から間引かれる()
    {
        var orderer = new OverlayLineOrderer(maxLines: 3);

        orderer.Upsert(1, "one");
        orderer.Upsert(2, "two");
        orderer.Upsert(3, "three");
        var result = orderer.Upsert(4, "four"); // 4件目、1が間引かれるはず

        Assert.Equal(1, result.RemovedFromFrontCount);
        Assert.False(result.WasTrimmedAway);
        Assert.True(result.IsLatest);
        Assert.Equal(2, result.Index); // 間引き後は[2,3,4]なので4のインデックスは2
        Assert.Equal(new long[] { 2, 3, 4 }, orderer.Lines.Select(l => l.Id));
    }

    [Fact]
    public void 上限に達している状態で最古より古いIdが到着すると即座に間引かれ画面には出ない()
    {
        // 例: MaxLines=3で既に[2,3,4]が表示されている状態で、
        // フォールバックにより大幅に遅れてId=1の翻訳が完了したケース。
        // 表示上は既にId=1より新しい発話が3件(上限いっぱい)埋まっているため、
        // Id=1は「挿入されたが即座に一番古いものとして間引かれる」動作が正しい。
        var orderer = new OverlayLineOrderer(maxLines: 3);
        orderer.Upsert(2, "two");
        orderer.Upsert(3, "three");
        orderer.Upsert(4, "four");

        var result = orderer.Upsert(1, "one (大幅に遅延)");

        Assert.True(result.WasTrimmedAway);
        Assert.Equal(-1, result.Index);
        Assert.False(result.IsLatest);
        Assert.Equal(new long[] { 2, 3, 4 }, orderer.Lines.Select(l => l.Id));
    }

    [Fact]
    public void MaxLinesを引き下げるとTrimToMaxで超過分が先頭から間引かれる()
    {
        var orderer = new OverlayLineOrderer(maxLines: 5);
        orderer.Upsert(1, "one");
        orderer.Upsert(2, "two");
        orderer.Upsert(3, "three");
        orderer.Upsert(4, "four");

        orderer.MaxLines = 2;
        var removed = orderer.TrimToMax();

        Assert.Equal(2, removed);
        Assert.Equal(new long[] { 3, 4 }, orderer.Lines.Select(l => l.Id));
    }

    [Fact]
    public void Clearで全行が削除される()
    {
        var orderer = new OverlayLineOrderer(maxLines: 5);
        orderer.Upsert(1, "one");
        orderer.Upsert(2, "two");

        orderer.Clear();

        Assert.Empty(orderer.Lines);
    }

    [Fact]
    public void コンストラクタに0以下を渡しても最低1行は保持できる()
    {
        var orderer = new OverlayLineOrderer(maxLines: 0);

        Assert.Equal(1, orderer.MaxLines);
    }
}
