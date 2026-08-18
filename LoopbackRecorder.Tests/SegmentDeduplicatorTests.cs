using System;
using Xunit;

namespace LoopbackRecorder.Tests;

/// <summary>
/// SegmentDeduplicator(Whisper文字起こし結果の重複除去)の単体テスト。
/// 「文字列完全一致」かつ「音声区間の時間的重なり」の両方を満たす場合のみ重複とみなす
/// 仕様(AudioPipeline.csから引き継いだ元のロジック)を境界値中心に検証する。
/// </summary>
public class SegmentDeduplicatorTests
{
    [Fact]
    public void 初回の発話は重複と判定されない()
    {
        var dedup = new SegmentDeduplicator();

        var result = dedup.IsDuplicate("こんにちは", TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(1));

        Assert.False(result);
    }

    [Fact]
    public void 同じ文字列かつ音声区間が重なっている場合は重複とみなされる()
    {
        var dedup = new SegmentDeduplicator();
        dedup.IsDuplicate("こんにちは", TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(2.0));

        // 1.5〜2.5秒は前回の1.0〜2.0秒と重なっている(1.5 < 2.0 かつ 2.5 > 1.0)
        var result = dedup.IsDuplicate("こんにちは", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.5));

        Assert.True(result);
    }

    [Fact]
    public void 同じ文字列でも音声区間が重なっていなければ重複とみなされない()
    {
        // 「同じ短い発話が数秒後に本当にもう一度行われた」正当なケースを想定
        var dedup = new SegmentDeduplicator();
        dedup.IsDuplicate("はい", TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(1.5));

        var result = dedup.IsDuplicate("はい", TimeSpan.FromSeconds(5.0), TimeSpan.FromSeconds(5.5));

        Assert.False(result);
    }

    [Fact]
    public void 音声区間が重なっていても文字列が異なれば重複とみなされない()
    {
        var dedup = new SegmentDeduplicator();
        dedup.IsDuplicate("こんにちは", TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(2.0));

        var result = dedup.IsDuplicate("さようなら", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.5));

        Assert.False(result);
    }

    [Fact]
    public void 区間の端点がちょうど接するだけでは重なりとみなされない()
    {
        // absoluteStart < End かつ absoluteEnd > Start が条件のため、
        // 端点が一致するだけ(2.0 == 2.0)は「重なっていない」扱いになる
        var dedup = new SegmentDeduplicator();
        dedup.IsDuplicate("こんにちは", TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(2.0));

        var result = dedup.IsDuplicate("こんにちは", TimeSpan.FromSeconds(2.0), TimeSpan.FromSeconds(3.0));

        Assert.False(result);
    }

    [Fact]
    public void 重複でないと判定された発話は次回判定の基準として更新される()
    {
        var dedup = new SegmentDeduplicator();
        dedup.IsDuplicate("最初", TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(1));
        // 2件目(重複ではない)で基準が更新されるはず
        dedup.IsDuplicate("2番目", TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(2.0));

        // "最初"とは時間が重なっていないが、"2番目"とは重なっている
        var result = dedup.IsDuplicate("2番目", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.5));

        Assert.True(result);
    }

    [Fact]
    public void Resetすると重複除去の状態がクリアされる()
    {
        var dedup = new SegmentDeduplicator();
        dedup.IsDuplicate("こんにちは", TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(2.0));

        dedup.Reset();
        // Reset後は、直前と全く同じ文字列・区間でも「初回」として扱われ重複にならない
        var result = dedup.IsDuplicate("こんにちは", TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(2.0));

        Assert.False(result);
    }
}
