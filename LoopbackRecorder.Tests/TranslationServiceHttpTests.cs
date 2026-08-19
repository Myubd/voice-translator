using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LoopbackRecorder.Tests;

/// <summary>
/// DeepLTranslationService / OllamaTranslationServiceのHTTP通信部分の単体テスト。
/// 実際のAPIには接続せず、HttpMessageHandlerを差し替えて応答を固定する。
/// </summary>
public class TranslationServiceHttpTests
{
    /// <summary>
    /// 送られてきたHTTPリクエストを記録しつつ、あらかじめ用意した応答を順番に返すスタブ。
    /// 応答は関数(Func)で渡すことで、例外送出(タイムアウト/接続エラーの再現)にも対応する。
    /// </summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> RequestBodies { get; } = new();

        public StubHttpMessageHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responders.Enqueue(responder);
            return this;
        }

        public StubHttpMessageHandler EnqueueJson(HttpStatusCode statusCode, string json) =>
            Enqueue(_ => new HttpResponseMessage(statusCode) { Content = new StringContent(json) });

        public StubHttpMessageHandler EnqueueThrow(Exception ex) =>
            Enqueue(_ => throw ex);

        /// <summary>
        /// リクエストボディ(JSON)の"prompt"フィールドをデコード済みの文字列として取り出す。
        /// System.Text.Json は既定で非ASCII文字(日本語等)を\uXXXXにエスケープしてシリアライズするため、
        /// 生のリクエストボディ文字列に対して日本語をそのままContainsで検索しても見つからない。
        /// JsonDocumentでパースすることでエスケープが解除された実際の文字列を取得できる。
        /// </summary>
        public string GetPromptFrom(int requestIndex)
        {
            using var doc = JsonDocument.Parse(RequestBodies[requestIndex]);
            return doc.RootElement.GetProperty("prompt").GetString() ?? "";
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : "");

            if (_responders.Count == 0)
            {
                throw new InvalidOperationException("StubHttpMessageHandler: 想定より多くのリクエストが送信されました。");
            }
            return _responders.Dequeue()(request);
        }
    }

    // ==================== DeepL ====================

    [Fact]
    public async Task DeepL_成功時は訳文を返す()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, """{"translations":[{"text":"Hello"}]}""");
        var client = new HttpClient(handler);
        var service = new DeepLTranslationService(client, "dummy-key");

        var result = await service.TranslateAsync("こんにちは", CancellationToken.None);

        Assert.Equal("Hello", result.Text);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task DeepL_無料プランのキーはapi_freeエンドポイントを使う()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, """{"translations":[{"text":"Hello"}]}""");
        var client = new HttpClient(handler);
        var service = new DeepLTranslationService(client, "dummy-key:fx");

        await service.TranslateAsync("こんにちは", CancellationToken.None);

        Assert.Contains("api-free.deepl.com", handler.Requests[0].RequestUri!.Host);
    }

    [Fact]
    public async Task DeepL_有料プランのキーは通常エンドポイントを使う()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, """{"translations":[{"text":"Hello"}]}""");
        var client = new HttpClient(handler);
        var service = new DeepLTranslationService(client, "dummy-key"); // ":fx"無し

        await service.TranslateAsync("こんにちは", CancellationToken.None);

        var host = handler.Requests[0].RequestUri!.Host;
        Assert.Equal("api.deepl.com", host);
    }

    [Fact]
    public async Task DeepL_401はAPIキーエラーの案内文になる()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.Unauthorized, "{}");
        var service = new DeepLTranslationService(new HttpClient(handler), "wrong-key");

        var result = await service.TranslateAsync("x", CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Contains("APIキー", result.ErrorMessage);
    }

    [Fact]
    public async Task DeepL_429は利用上限エラーの案内文になる()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson((HttpStatusCode)429, "{}");
        var service = new DeepLTranslationService(new HttpClient(handler), "key");

        var result = await service.TranslateAsync("x", CancellationToken.None);

        Assert.Contains("利用上限", result.ErrorMessage);
    }

    [Fact]
    public async Task DeepL_ネットワーク例外は接続失敗メッセージになる()
    {
        var handler = new StubHttpMessageHandler().EnqueueThrow(new HttpRequestException("network down"));
        var service = new DeepLTranslationService(new HttpClient(handler), "key");

        var result = await service.TranslateAsync("x", CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Contains("接続に失敗", result.ErrorMessage);
    }

    [Fact]
    public async Task DeepL_呼び出し前に外部キャンセル済みなら停止操作扱いのメッセージになる()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, """{"translations":[{"text":"Hello"}]}""");
        var service = new DeepLTranslationService(new HttpClient(handler), "key");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await service.TranslateAsync("x", cts.Token);

        Assert.Contains("停止操作", result.ErrorMessage);
    }

    [Fact]
    public async Task DeepL_翻訳配列が空の場合は取得失敗メッセージになる()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, """{"translations":[]}""");
        var service = new DeepLTranslationService(new HttpClient(handler), "key");

        var result = await service.TranslateAsync("x", CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Contains("取得できませんでした", result.ErrorMessage);
    }

    // ==================== Ollama ====================

    [Fact]
    public async Task Ollama_成功時は訳文を返す()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, """{"response":"こんにちは"}""");
        var service = new OllamaTranslationService(new HttpClient(handler), "llama3.1", "http://localhost:11434");

        var result = await service.TranslateAsync("hello", CancellationToken.None);

        Assert.Equal("こんにちは", result.Text);
    }

    [Fact]
    public async Task Ollama_エラーステータスはHTTPコード付きメッセージになる()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.NotFound, "{}");
        var service = new OllamaTranslationService(new HttpClient(handler), "no-such-model", "http://localhost:11434");

        var result = await service.TranslateAsync("hello", CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Contains("404", result.ErrorMessage);
    }

    [Fact]
    public async Task Ollama_空応答は取得失敗メッセージになる()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, """{"response":"   "}""");
        var service = new OllamaTranslationService(new HttpClient(handler), "llama3.1", "http://localhost:11434");

        var result = await service.TranslateAsync("hello", CancellationToken.None);

        Assert.Contains("取得できませんでした", result.ErrorMessage);
    }

    [Fact]
    public async Task Ollama_接続例外は起動確認を促すメッセージになる()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueThrow(new HttpRequestException("connection refused"));
        var service = new OllamaTranslationService(new HttpClient(handler), "llama3.1", "http://localhost:11434");

        var result = await service.TranslateAsync("hello", CancellationToken.None);

        Assert.Contains("起動しているか確認", result.ErrorMessage);
    }

    [Fact]
    public async Task Ollama_PrepareAsyncで抽出した用語集が翻訳プロンプトに反映される()
    {
        // 1回目のリクエスト(PrepareAsync): モデルの事前ロード。2回目(PrepareAsync): 用語集抽出。
        // 3回目(TranslateAsync): 実際の翻訳。
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, """{"done":true}""")
            .EnqueueJson(HttpStatusCode.OK, """{"response":"Aetherium => エーテリウム"}""")
            .EnqueueJson(HttpStatusCode.OK, """{"response":"エーテリウムを見つけた"}""");
        var service = new OllamaTranslationService(
            new HttpClient(handler), "llama3.1", "http://localhost:11434",
            targetLanguageName: "Japanese", context: "Aetherium is a rare metal.");

        await service.PrepareAsync(CancellationToken.None);
        var result = await service.TranslateAsync("I found Aetherium.", CancellationToken.None);

        Assert.Equal("エーテリウムを見つけた", result.Text);
        // 3回目(翻訳)のリクエストボディに、抽出した用語集の内容が含まれているはず
        // (raw JSON文字列は日本語が\uXXXXエスケープされるため、prompt値をデコードしてから比較する)
        Assert.Contains("Aetherium => エーテリウム", handler.GetPromptFrom(2));
    }

    [Fact]
    public async Task Ollama_参考コンテキストが空でもモデルの事前ロードリクエストは送られる()
    {
        // TODO「Ollamaモデルの事前ロード」対応: 用語集(参考コンテキスト)が無い場合でも、
        // 最初の実翻訳が遅れないよう、PrepareAsync時点でモデルだけは事前ロードしておく
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, """{"done":true}""");
        var service = new OllamaTranslationService(
            new HttpClient(handler), "llama3.1", "http://localhost:11434", context: "");

        await service.PrepareAsync(CancellationToken.None);

        Assert.Single(handler.Requests); // 事前ロードの1件のみ(用語集抽出は行われない)
        // 事前ロードリクエストは"prompt"を含まない(生成を伴わせないため)
        using var doc = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.False(doc.RootElement.TryGetProperty("prompt", out _));
        Assert.Equal("llama3.1", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Ollama_モデル事前ロードが失敗しても例外を投げず翻訳は続行できる()
    {
        // 事前ロードの失敗(モデル未インストール等)は致命的ではなく、
        // その分だけ最初の翻訳が多少遅くなるだけであるべき
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.NotFound, """{"error":"model not found"}""")
            .EnqueueJson(HttpStatusCode.OK, """{"response":"翻訳結果"}""");
        var service = new OllamaTranslationService(
            new HttpClient(handler), "no-such-model", "http://localhost:11434", context: "");

        await service.PrepareAsync(CancellationToken.None); // 例外にならないこと
        var result = await service.TranslateAsync("hello", CancellationToken.None);

        Assert.Equal("翻訳結果", result.Text);
    }

    [Fact]
    public async Task Ollama_用語集抽出に失敗しても翻訳自体は続行できる()
    {
        // PrepareAsyncの抽出リクエストがエラーになっても、例外を投げずglossary無しで続行する
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, """{"done":true}""") // 事前ロード
            .EnqueueJson(HttpStatusCode.InternalServerError, "{}") // 用語集抽出(失敗)
            .EnqueueJson(HttpStatusCode.OK, """{"response":"翻訳結果"}"""); // 翻訳
        var service = new OllamaTranslationService(
            new HttpClient(handler), "llama3.1", "http://localhost:11434", context: "some context");

        await service.PrepareAsync(CancellationToken.None);
        var result = await service.TranslateAsync("hello", CancellationToken.None);

        Assert.Equal("翻訳結果", result.Text);
        Assert.DoesNotContain("Glossary", handler.RequestBodies[2]);
    }
}
