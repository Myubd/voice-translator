using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// ITranslationServiceをラップし、同時に実行できるTranslateAsync呼び出し数を
/// SemaphoreSlimで制限するデコレータ。
///
/// 背景(コードレビュー対応): AudioPipelineは TranslationWorkerCount(最大4)本の
/// TranslationWorkerを並列起動するが、これまではワーカー数がそのままDeepL/Ollamaへの
/// 同時リクエスト数になっていた。DeepLが遅い環境では、複数ワーカーが同時にDeepLを
/// 叩くことで429(レート制限)やタイムアウトが起きやすくなり、さらに
/// DeepL失敗→Ollamaフォールバックが重なると負荷が増幅する懸念があった。
///
/// このクラスで「ワーカー数」と「実際にバックエンドへ同時に飛ぶリクエスト数」を分離する。
/// 複数のTranslationWorkerインスタンスは同じITranslationServiceインスタンス
/// (AudioPipeline._translationService)を共有して呼び出しているため、ここで1つの
/// SemaphoreSlimインスタンスをラップするだけで、ワーカー数を増やしても
/// バックエンドへの同時打ちはそのSemaphoreの上限までに抑えられる。
///
/// キュー待ち時間への影響について: LatencyTrackerのQueueWaitDuration
/// (TranslationWorkerがチャンネルから項目を取り出してからTranslateAsyncを呼ぶまでの時間)
/// には、このSemaphore待ち時間は含まれない(Semaphore待ちはTranslateAsync呼び出しの
/// 内側で発生するため)。Semaphoreの上限に達している状態が続く場合、体感の遅延は
/// TranslationCallDurationの増加として観測される。
/// </summary>
public sealed class ConcurrencyLimitedTranslationService : ITranslationService
{
    private readonly ITranslationService _inner;
    private readonly SemaphoreSlim _semaphore;

    public ConcurrencyLimitedTranslationService(ITranslationService inner, SemaphoreSlim semaphore)
    {
        _inner = inner;
        _semaphore = semaphore;
    }

    // 「翻訳サービスとして有効か」はSemaphoreの制限とは無関係なので、そのまま内側に委譲する
    public bool IsEnabled => _inner.IsEnabled;

    // テスト用: AppSettings.CreateTranslationServiceが正しいサービスをラップしているかを
    // 検証するため、内側のインスタンスを参照できるようにする(本体のロジックからは使わない)。
    internal ITranslationService Inner => _inner;

    // PrepareAsync(セッション開始時に1回だけ呼ばれる準備処理)は同時実行数の懸念が無いため
    // Semaphoreを介さず、そのまま内側に委譲する
    public Task PrepareAsync(CancellationToken cancellationToken) => _inner.PrepareAsync(cancellationToken);

    public async Task<TranslationResult> TranslateAsync(string text, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            return await _inner.TranslateAsync(text, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
