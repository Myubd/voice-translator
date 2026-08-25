using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace LoopbackRecorder;

/// <summary>
/// Windows標準搭載のOCRエンジン(Windows.Media.Ocr)を使い、PNG画像からテキストを抽出する。
/// Whisperのような追加モデルの配布・ダウンロードが不要な一方、対象言語の「言語パック」が
/// Windows側にインストールされていないと、その言語ではエンジン自体を作成できない
/// (TryCreateFromLanguageがnullを返す)。マイナー言語でOCRしたい場合、Windowsの設定→
/// 時刻と言語→言語 から対象言語パックの追加が必要になる旨をエラーメッセージに含める。
/// </summary>
public sealed class WindowsOcrService
{
    /// <summary>指定言語でPNG画像からテキストを抽出する。languageTagはBCP-47形式(例: "en", "ja", "ko")。
    /// 該当言語のOCRエンジンを作成できない場合(言語パック未導入・不正なタグ等)は
    /// OcrUnavailableExceptionを投げる。</summary>
    public async Task<string> RecognizeTextAsync(byte[] pngBytes, string languageTag, CancellationToken cancellationToken)
    {
        OcrEngine? engine;
        try
        {
            engine = OcrEngine.TryCreateFromLanguage(new Language(languageTag));
        }
        catch (Exception ex)
        {
            // Languageコンストラクタは、BCP-47として解釈できない文字列を渡すと例外を投げる
            throw new OcrUnavailableException($"言語タグ'{languageTag}'を認識できませんでした。", ex);
        }

        if (engine == null)
        {
            throw new OcrUnavailableException(
                $"言語「{languageTag}」のOCRエンジンを作成できませんでした。" +
                "Windowsの設定(時刻と言語→言語)でこの言語の言語パックが追加されているか確認してください。");
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        // OcrEngine.RecognizeAsyncはBgra8(Premultiplied)のSoftwareBitmapのみを受け付けるため、
        // デコーダのネイティブ形式のままではなく明示的にこの形式を要求する
        // (これを省略すると、画像によってはRecognizeAsyncが例外を投げることがある)。
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        cancellationToken.ThrowIfCancellationRequested();

        var result = await engine.RecognizeAsync(softwareBitmap);
        return result.Text;
    }
}

/// <summary>指定言語のOCRエンジンを作成できなかった場合の例外(言語パック未導入・不正な言語タグ等)。</summary>
public sealed class OcrUnavailableException : Exception
{
    public OcrUnavailableException(string message) : base(message)
    {
    }

    public OcrUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
