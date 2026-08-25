using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// 直近に翻訳済みの原文をメモリ上にキャッシュし、同じ原文が再度来た場合は
/// DeepL/Ollamaへの呼び出しを行わずキャッシュ済みの訳文を即座に返すデコレーター。
///
/// ゲーム実況・ボイスチャットでは「gg」「nice」「ok」のような短い相槌や定型句が
/// 頻繁に繰り返される。これらを毎回API呼び出ししていたのは、翻訳API消費量・
/// 実応答速度の両面で無駄が大きい(DeepLは有料枠を消費し、Ollamaは推論コストがかかる)。
///
/// 完全一致(Whisperの文字起こし結果の文字列そのもの)のみをキャッシュ対象とする。
/// 表記ゆれ(句読点の有無・大文字小文字等)は別の原文として扱い、あえて正規化しない。
/// 正規化すると、本来ニュアンスが異なる原文同士を同一視して誤った訳文を返すリスクが
/// あるため、「完全に同じ文字列だった場合のみ」という保守的な条件に留めている。
///
/// 翻訳が失敗した場合(ErrorMessageが返る場合)はキャッシュしない。一時的なAPI障害を
/// キャッシュしてしまうと、障害復旧後もその原文だけ延々と失敗結果を返し続けることになるため。
/// </summary>
public sealed class CachingTranslationService : ITranslationService
{
    private readonly ITranslationService _inner;
    private readonly int _maxEntries;
    private readonly ConcurrentDictionary<string, string> _cache = new();

    // 同じ原文に対する翻訳リクエストが複数ワーカーから同時に来た場合、cache miss後の
    // _inner.TranslateAsync()呼び出しそのものを1回に集約するためのin-flightテーブル。
    // 「キャッシュに無ければ問い合わせる」だけだと、Worker A/B/Cが同時に同じ原文を
    // missした場合、全員がDeepL/Ollamaへ個別にリクエストを投げてしまう
    // (cache stampede)。Lazy<Task<>>で「翻訳中のTaskそのもの」を共有することで、
    // 2番目以降のワーカーは新規リクエストを発行せず、進行中のTaskをawaitするだけになる。
    private readonly ConcurrentDictionary<string, Lazy<Task<TranslationResult>>> _inFlight = new();

    // ConcurrentDictionaryは挿入順を保持しないため、上限超過時にどれを間引くかを
    // 判断するための挿入順キューを別途持つ。単純なFIFOで十分(LRUほど厳密な
    // 「よく使うものを残す」制御はここでは求めていない。ゲーム実況中の短時間の
    // セッションで無限に増え続けないようにする、という程度の目的のため)。
    private readonly ConcurrentQueue<string> _insertionOrder = new();

    /// <param name="inner">実際に翻訳を行う内側のサービス(DeepL/Ollama/Fallback等、何でもよい)</param>
    /// <param name="maxEntries">キャッシュに保持する原文の最大件数。長時間セッションでメモリが
    /// 際限なく増え続けないよう上限を設け、超過分は古い順(FIFO)に間引く。</param>
    public CachingTranslationService(ITranslationService inner, int maxEntries = 500)
    {
        _inner = inner;
        _maxEntries = maxEntries;
    }

    public bool IsEnabled => _inner.IsEnabled;

    public Task PrepareAsync(CancellationToken cancellationToken) => _inner.PrepareAsync(cancellationToken);

    // テスト用: AppSettings.CreateTranslationServiceが正しいサービスをキャッシュでラップして
    // いるかを検証するため、内側のインスタンスを参照できるようにする(本体のロジックからは使わない)。
    internal ITranslationService Inner => _inner;

    public async Task<TranslationResult> TranslateAsync(string text, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(text, out var cached))
        {
            Logger.LogMetric("TranslationCache", ("event", "hit"));
            return TranslationResult.Success(cached);
        }

        // 既に他のワーカーが同じ原文を翻訳中であれば、そのTaskに相乗りする。
        // Lazy<>のおかげで、GetOrAddが複数スレッドから同時に呼ばれても
        // ファクトリ(_inner.TranslateAsync呼び出し)自体は1回しか実行されない。
        var lazyTask = _inFlight.GetOrAdd(
            text,
            _ => new Lazy<Task<TranslationResult>>(
                () => _inner.TranslateAsync(text, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var result = await lazyTask.Value;

            if (result.Text != null)
            {
                Store(text, result.Text);
            }
            // 複数ワーカーが同じin-flight Taskに相乗りした場合、ここは全員通過するため
            // "miss"の記録回数は実際のAPI呼び出し回数より多くなり得る(=キャッシュされて
            // いなかったリクエストの件数、という意味のmetricとして扱う。実際の重複排除が
            // 効いているかどうかは呼び出し先(DeepL/Ollamaサービス)側の呼び出し回数で確認する)。
            Logger.LogMetric("TranslationCache", ("event", "miss"));

            return result;
        }
        finally
        {
            // 完了したエントリはin-flightテーブルから外す。他ワーカーが待っている間に
            // 除去してしまうとawait中のlazyTaskの参照自体は生きているため問題ない
            // (TryRemoveは「これから新規に来るリクエスト」向けの掃除)。
            _inFlight.TryRemove(text, out _);
        }
    }

    private void Store(string text, string translated)
    {
        // 既に他のワーカーが同じ原文を先にキャッシュ済みの場合はTryAddが失敗するだけで、
        // 挿入順キューに二重登録されることもない(そのまま無視してよい)。
        if (!_cache.TryAdd(text, translated))
        {
            return;
        }
        _insertionOrder.Enqueue(text);

        while (_cache.Count > _maxEntries && _insertionOrder.TryDequeue(out var oldest))
        {
            _cache.TryRemove(oldest, out _);
        }
    }
}
