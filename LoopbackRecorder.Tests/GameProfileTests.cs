using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace LoopbackRecorder.Tests;

/// <summary>
/// GameProfile(プロファイルのモデル)とGameProfileStore(JSONへの保存/読込)の単体テスト。
/// 実ファイルへの読み書きが絡むため、各テストで一時ファイルパスを使い、テスト終了後に削除する。
/// </summary>
public class GameProfileTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string CreateTempProfilesPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"loopback-test-profiles-{Guid.NewGuid():N}.json");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void CaptureFromとApplyToで翻訳エンジンとオーバーレイの値が往復する()
    {
        var original = new AppSettings
        {
            TranslationBackend = "ollama",
            TargetLanguageCode = "EN-US",
            OllamaContext = "背景知識テキスト",
            ManualGlossary = "Aetherium => エーテリウム",
            OverlayFontSize = 30,
            OverlayOpacity = 0.5,
            OverlayMaxLines = 6,
            OverlayFontColor = "#66D9FF",
        };

        var profile = GameProfile.CaptureFrom(original, "TestGame");

        var target = new AppSettings();
        profile.ApplyTo(target);

        Assert.Equal("ollama", target.TranslationBackend);
        Assert.Equal("EN-US", target.TargetLanguageCode);
        Assert.Equal("背景知識テキスト", target.OllamaContext);
        Assert.Equal("Aetherium => エーテリウム", target.ManualGlossary);
        Assert.Equal(30, target.OverlayFontSize);
        Assert.Equal(0.5, target.OverlayOpacity);
        Assert.Equal(6, target.OverlayMaxLines);
        Assert.Equal("#66D9FF", target.OverlayFontColor);
    }

    [Fact]
    public void ApplyToはプロファイル対象外の項目デバイス選択等には触れない()
    {
        var target = new AppSettings { DeviceKeyword = "既存のデバイス設定", DeepLApiKey = "既存のAPIキー" };
        var profile = new GameProfile { Name = "TestGame", TranslationBackend = "ollama" };

        profile.ApplyTo(target);

        Assert.Equal("既存のデバイス設定", target.DeviceKeyword);
        Assert.Equal("既存のAPIキー", target.DeepLApiKey);
    }

    [Fact]
    public void 存在しないパスのLoadAllは空リストを返す()
    {
        var path = CreateTempProfilesPath(); // まだファイルを作っていないパス

        var profiles = GameProfileStore.LoadAll(path);

        Assert.Empty(profiles);
    }

    [Fact]
    public void SaveAllしたプロファイルをLoadAllで読み戻せる()
    {
        var path = CreateTempProfilesPath();
        var profiles = new List<GameProfile>
        {
            new() { Name = "Elden Ring", TranslationBackend = "ollama", OllamaContext = "エルデンリングの用語" },
            new() { Name = "Apex Legends", TranslationBackend = "deepl" },
        };

        GameProfileStore.SaveAll(profiles, path);
        var reloaded = GameProfileStore.LoadAll(path);

        // 名前昇順で返る仕様のため、"Apex Legends"が先
        Assert.Equal(2, reloaded.Count);
        Assert.Equal("Apex Legends", reloaded[0].Name);
        Assert.Equal("Elden Ring", reloaded[1].Name);
        Assert.Equal("エルデンリングの用語", reloaded[1].OllamaContext);
    }

    [Fact]
    public void Upsertは新規名なら追加し既存名なら上書きする()
    {
        var path = CreateTempProfilesPath();
        GameProfileStore.Upsert(new GameProfile { Name = "Elden Ring", OllamaContext = "旧内容" }, path);
        GameProfileStore.Upsert(new GameProfile { Name = "Apex Legends" }, path);

        // 既存の"Elden Ring"を上書き
        GameProfileStore.Upsert(new GameProfile { Name = "Elden Ring", OllamaContext = "新内容" }, path);

        var reloaded = GameProfileStore.LoadAll(path);

        Assert.Equal(2, reloaded.Count); // 追加されず上書きされたので2件のまま
        var eldenRing = reloaded.Single(p => p.Name == "Elden Ring");
        Assert.Equal("新内容", eldenRing.OllamaContext);
    }

    [Fact]
    public void Upsertはプロファイル名の前後の空白を除去する()
    {
        var path = CreateTempProfilesPath();

        GameProfileStore.Upsert(new GameProfile { Name = "  Elden Ring  " }, path);

        var reloaded = GameProfileStore.LoadAll(path);
        Assert.Equal("Elden Ring", Assert.Single(reloaded).Name);
    }

    [Fact]
    public void Deleteは該当プロファイルのみ削除する()
    {
        var path = CreateTempProfilesPath();
        GameProfileStore.Upsert(new GameProfile { Name = "Elden Ring" }, path);
        GameProfileStore.Upsert(new GameProfile { Name = "Apex Legends" }, path);

        GameProfileStore.Delete("Elden Ring", path);

        var reloaded = GameProfileStore.LoadAll(path);
        Assert.Equal("Apex Legends", Assert.Single(reloaded).Name);
    }

    [Fact]
    public void Deleteは該当が無い場合は何もせず例外も投げない()
    {
        var path = CreateTempProfilesPath();
        GameProfileStore.Upsert(new GameProfile { Name = "Apex Legends" }, path);

        GameProfileStore.Delete("存在しない名前", path);

        var reloaded = GameProfileStore.LoadAll(path);
        Assert.Single(reloaded);
    }

    [Fact]
    public void 壊れたJSONファイルの場合は例外を投げず空リストを返す()
    {
        var path = CreateTempProfilesPath();
        File.WriteAllText(path, "{ これは壊れたJSON");

        var profiles = GameProfileStore.LoadAll(path);

        Assert.Empty(profiles);
    }
}
