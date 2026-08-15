using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// 翻訳結果。成功時はTextに訳文が入り、失敗時はErrorMessageにユーザー向けの理由が入る。
/// 従来はfailure時にnullを返すだけでエラー内容がConsole.WriteLineにしか出ておらず、
/// WPFアプリとして配布した場合ユーザーからは見えなかった。
/// </summary>
public record TranslationResult(string? Text, string? ErrorMessage)
{
    public static TranslationResult Success(string text) => new(text, null);
    public static TranslationResult Failure(string errorMessage) => new(null, errorMessage);
}

/// <summary>
/// 翻訳サービスの共通インターフェース。
/// DeepL(クラウドAPI)とOllama(ローカルAI)を同じ形で扱えるようにし、
/// 設定(TRANSLATION_BACKEND)だけで切り替えられるようにする。
/// </summary>
public interface ITranslationService
{
    Task<TranslationResult> TranslateAsync(string text);

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

    // DeepLがハングした場合に翻訳ワーカー全体が無期限に止まらないよう、要求ごとにタイムアウトを設ける。
    // HttpClient自体はMainWindow側でアプリ全体で共有されているため、Timeoutプロパティを直接変えず、
    // 呼び出しごとにCancellationTokenSourceで打ち切る形にする。
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public async Task<TranslationResult> TranslateAsync(string text)
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

            using var timeoutCts = new CancellationTokenSource(RequestTimeout);
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                Logger.Log("DeepL", $"HTTP {(int)response.StatusCode}: {errorBody}");
                return TranslationResult.Failure(BuildUserFacingError((int)response.StatusCode));
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var translations = doc.RootElement.GetProperty("translations");
            var translated = translations.GetArrayLength() > 0
                ? translations[0].GetProperty("text").GetString()
                : null;

            return translated != null
                ? TranslationResult.Success(translated)
                : TranslationResult.Failure("DeepLから翻訳結果を取得できませんでした。");
        }
        catch (OperationCanceledException ex)
        {
            Logger.Log("DeepL", "リクエストがタイムアウトしました。", ex);
            return TranslationResult.Failure("DeepLへの接続がタイムアウトしました。ネットワークを確認してください。");
        }
        catch (Exception ex)
        {
            Logger.Log("DeepL", "翻訳リクエストで例外が発生しました。", ex);
            return TranslationResult.Failure($"DeepLへの接続に失敗しました: {ex.Message}");
        }
    }

    private static string BuildUserFacingError(int statusCode) => statusCode switch
    {
        401 or 403 => "DeepL APIキーが正しくないか、権限がありません。設定画面で確認してください。",
        429 or 456 => "DeepLの利用上限に達しました(無料/有料プランの制限)。",
        _ => $"DeepLへのリクエストが失敗しました(HTTP {statusCode})。"
    };
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
        catch (Exception ex)
        {
            // 抽出に失敗しても翻訳自体は続行できるようにする(コンテキスト無しにフォールバック)。
            // ただし「なぜ用語集が反映されないのか」が分かるよう原因は記録しておく
            Logger.Log("Ollama.Glossary", "参考コンテキストからの用語集抽出に失敗しました。用語集無しで続行します。", ex);
            _glossary = null;
        }
    }

    /// <summary>
    /// Ollamaにインストール済みのモデル名一覧を取得する(設定画面のドロップダウン用)。
    /// Ollamaが起動していない場合は空リストを返す。
    /// </summary>
    public static async Task<List<string>> GetInstalledModelsAsync(
        HttpClient httpClient, string endpoint, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"{endpoint}/api/tags", cancellationToken);
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

    // ローカルLLMは応答が遅いことがあるためDeepLより長めに取るが、ハングしたまま
    // 無期限に翻訳ワーカーを止めないよう上限は設ける。
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public async Task<TranslationResult> TranslateAsync(string text)
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

            using var timeoutCts = new CancellationTokenSource(RequestTimeout);
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                Logger.Log("Ollama", $"HTTP {(int)response.StatusCode}: {errorBody}");
                return TranslationResult.Failure($"Ollamaへのリクエストが失敗しました(HTTP {(int)response.StatusCode})。モデル名を確認してください。");
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var translated = doc.RootElement.GetProperty("response").GetString()?.Trim();

            return !string.IsNullOrWhiteSpace(translated)
                ? TranslationResult.Success(translated!)
                : TranslationResult.Failure("Ollamaから翻訳結果を取得できませんでした。");
        }
        catch (OperationCanceledException ex)
        {
            Logger.Log("Ollama", "リクエストがタイムアウトしました。", ex);
            return TranslationResult.Failure("Ollamaへの接続がタイムアウトしました。Ollamaが起動しているか確認してください。");
        }
        catch (HttpRequestException ex)
        {
            Logger.Log("Ollama", "Ollamaへの接続に失敗しました。", ex);
            return TranslationResult.Failure("Ollamaに接続できません。Ollamaが起動しているか確認してください。");
        }
        catch (Exception ex)
        {
            Logger.Log("Ollama", "翻訳リクエストで例外が発生しました。", ex);
            return TranslationResult.Failure($"Ollamaでの翻訳に失敗しました: {ex.Message}");
        }
    }
}
