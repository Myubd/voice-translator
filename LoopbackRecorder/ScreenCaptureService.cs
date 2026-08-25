using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace LoopbackRecorder;

/// <summary>
/// 画面上の指定領域(物理ピクセル・スクリーン座標)をキャプチャし、PNGバイト列として返す。
/// OCR(Windows.Media.Ocr)側はWindows.Graphics.Imaging.SoftwareBitmapを要求するが、
/// System.DrawingのBitmapからSoftwareBitmapへ直接変換するAPIは無いため、一度PNGへ
/// エンコードしてからOcrService側でBitmapDecoder経由でデコードし直す
/// (多少の変換コストはあるが、責務を分離できて実装がシンプルになる)。
/// </summary>
public static class ScreenCaptureService
{
    /// <summary>指定した矩形(物理ピクセル、プライマリモニタ左上を原点とするスクリーン座標。
    /// マルチモニタでプライマリより左/上にモニタがある場合は負の値になり得る)をキャプチャして
    /// PNGバイト列を返す。widthまたはheightが0以下の場合は例外を投げる。</summary>
    public static byte[] CaptureRegion(Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new ArgumentException($"キャプチャ範囲が不正です(Width={region.Width}, Height={region.Height})。", nameof(region));
        }

        using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            // CopyFromScreenはマルチモニタ環境でも、仮想スクリーン座標(負の値を含み得る)を
            // そのまま渡せる(プライマリモニタの左上が(0,0)で、左/上に追加モニタがあれば負になる)。
            graphics.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size, CopyPixelOperation.SourceCopy);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
