using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// 翻訳サービスの共通インターフェース。
/// DeepL(クラウドAPI)とOllama(ローカルAI)を同じ形で扱えるようにし、
/// 設定(TRANSLATION_BACKEND)だけで切り替えられるようにする。
/// </summary>
public interface ITranslationService
{
    Task<string?> TranslateAsync(string text);
}

/// <summary>
/// DeepL APIを使った翻訳。APIキーが必要。
/// </summary>
public class DeepLTranslationService : ITranslationService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _endpoint;

    public DeepLTranslationService(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
        // 無料プランのキーは末尾に ":fx" が付く。有料プランはエンドポイントが異なる
        _endpoint = apiKey.EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";
    }

    public async Task<string?> TranslateAsync(string text)
    {
        try
        {
            var requestBody = new
            {
                text = new[] { text },
                target_lang = "JA"
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Add("Authorization", $"DeepL-Auth-Key {_apiKey}");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[DeepLエラー] HTTP {(int)response.StatusCode}: {errorBody}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var translations = doc.RootElement.GetProperty("translations");
            return translations.GetArrayLength() > 0
                ? translations[0].GetProperty("text").GetString()
                : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DeepLエラー] {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Ollama(ローカルで動くLLM)を使った翻訳。APIキー不要、完全にローカル完結。
/// 事前に Ollama をインストールし、`ollama pull llama3.1` 等でモデルを取得しておく必要がある。
/// </summary>
public class OllamaTranslationService : ITranslationService
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly string _endpoint;

    public OllamaTranslationService(HttpClient httpClient, string model, string endpoint)
    {
        _httpClient = httpClient;
        _model = model;
        _endpoint = endpoint;
    }

    /// <summary>
    /// Ollamaにインストール済みのモデル名一覧を取得する(設定画面のドロップダウン用)。
    /// Ollamaが起動していない場合は空リストを返す。
    /// </summary>
    public static async Task<List<string>> GetInstalledModelsAsync(HttpClient httpClient, string endpoint)
    {
        using var response = await httpClient.GetAsync($"{endpoint}/api/tags");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var models = new List<string>();
        foreach (var model in doc.RootElement.GetProperty("models").EnumerateArray())
        {
            var name = model.GetProperty("name").GetString();
            if (!string.IsNullOrWhiteSpace(name)) models.Add(name);
        }
        return models;
    }

    public async Task<string?> TranslateAsync(string text)
    {
        try
        {
            var prompt = $"You are a translation engine. Translate the following text into natural Japanese only.\n" +
                         $"Rules:\n" +
                         $"- Output ONLY the Japanese translation, nothing else.\n" +
                         $"- Do not mix in any other language (no Chinese, no Portuguese, no English words).\n" +
                         $"- Do not add explanations, notes, or quotation marks.\n" +
                         $"- If the text is already Japanese, output it unchanged.\n\n" +
                         $"Text: {text}\n" +
                         $"Japanese translation:";

            var requestBody = new
            {
                model = _model,
                prompt,
                stream = false,
                options = new
                {
                    temperature = 0.1 // 低くすることで訳のブレ・言語混入を抑える
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/api/generate");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[Ollamaエラー] HTTP {(int)response.StatusCode}: {errorBody}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("response").GetString()?.Trim();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Ollamaエラー] {ex.Message}");
            return null;
        }
    }
}
