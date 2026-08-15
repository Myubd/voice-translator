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

    /// <summary>セッション開始時に一度だけ呼ばれる準備処理。既定では何もしない</summary>
    Task PrepareAsync() => Task.CompletedTask;
}

/// <summary>
/// DeepL APIを使った翻訳。APIキーが必要。
/// </summary>
public class DeepLTranslationService : ITranslationService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly string _targetLangCode;

    public DeepLTranslationService(HttpClient httpClient, string apiKey, string targetLangCode = "JA")
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
        _targetLangCode = targetLangCode;
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
                target_lang = _targetLangCode
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
    private readonly string _targetLanguageName;
    private readonly string _context;

    // 参考コンテキストから抽出した「固有名詞→訳語」の短い用語集。
    // 生の参考コンテキストを毎回プロンプトに含めるより、事前に1回だけ抽出したこちらを
    // 使い回す方が、レイテンシと(文脈からの)ハルシネーションの両方を抑えられる。
    private string? _glossary;

    public OllamaTranslationService(HttpClient httpClient, string model, string endpoint,
        string targetLanguageName = "Japanese", string context = "")
    {
        _httpClient = httpClient;
        _model = model;
        _endpoint = endpoint;
        _targetLanguageName = targetLanguageName;
        _context = context;
    }

    /// <summary>
    /// 参考コンテキストが設定されている場合、セッション開始時に一度だけ用語集を抽出しておく。
    /// 以降の翻訳では、生の参考コンテキストではなくこの短い用語集を使う。
    /// </summary>
    public async Task PrepareAsync()
    {
        if (string.IsNullOrWhiteSpace(_context))
        {
            _glossary = null;
            return;
        }

        try
        {
            var extractionPrompt =
                $"Extract a compact glossary of proper nouns, names, places, and specialized terms " +
                $"from the background text below, together with their natural {_targetLanguageName} translation.\n" +
                $"Output ONLY lines in the format \"original => translation\", one per line. No other text, no headers.\n\n" +
                $"Background text:\n{_context}";

            var requestBody = new
            {
                model = _model,
                prompt = extractionPrompt,
                stream = false,
                options = new { temperature = 0.1 }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_endpoint}/api/generate");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _glossary = null;
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var extracted = doc.RootElement.GetProperty("response").GetString()?.Trim();

            // 暴走防止のため、万一異常に長い応答が返ってきた場合は安全のため使わない
            _glossary = (!string.IsNullOrWhiteSpace(extracted) && extracted.Length < 3000) ? extracted : null;
        }
        catch
        {
            // 抽出に失敗しても翻訳自体は続行できるようにする(コンテキスト無しにフォールバック)
            _glossary = null;
        }
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
            var contextSection = string.IsNullOrWhiteSpace(_glossary)
                ? ""
                : $"Glossary (use these translations ONLY for terms that already appear in the Text below; " +
                  $"NEVER use it to add, infer, or substitute any name, fact, or detail that is not explicitly present in the Text):\n{_glossary}\n\n";

            var prompt = $"You are a translation engine. Translate the following text into natural {_targetLanguageName} only.\n" +
                         $"Rules:\n" +
                         $"- Output ONLY the {_targetLanguageName} translation, nothing else.\n" +
                         $"- Do not mix in any other language.\n" +
                         $"- Do not add explanations, notes, or quotation marks.\n" +
                         $"- Keep proper nouns, product names, and usernames in their original form when a translation would be unnatural.\n" +
                         $"- Translate ONLY what is written in the Text. Do not add, guess, or substitute any name, fact, or detail that is not explicitly present in the Text, even if the background context mentions related information.\n" +
                         $"- If the text is already in {_targetLanguageName}, output it unchanged.\n\n" +
                         $"{contextSection}" +
                         $"Text: {text}\n" +
                         $"{_targetLanguageName} translation:";

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
