using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LoopbackRecorder;

/// <summary>
/// 画面全体(マルチモニタ環境なら全モニタを合わせた仮想スクリーン全体)を覆う半透明ウィンドウを出し、
/// ユーザーがドラッグで選択した矩形を物理ピクセル座標(スクリーン座標)で通知する。
///
/// 座標変換について: WPFの通常の座標系(Left/Top/Width/Height、MouseEventArgs.GetPosition)は
/// DIP(1/96インチ、DPI非依存)単位だが、ScreenCaptureService.CaptureRegionが要求するのは
/// GDIのCopyFromScreen基準の物理ピクセル座標。Visual.PointToScreenは(WPFの他のAPIと違い)
/// 物理ピクセル単位のスクリーン座標を返すため、これを使えばモニタごとのDPIスケールを
/// 自前で計算せずに正しく変換できる(マルチモニタでモニタ間のDPIが異なる環境でも対応できる)。
/// </summary>
public partial class RegionSelectorWindow : Window
{
    /// <summary>選択確定時に、選択された矩形(物理ピクセル、仮想スクリーン座標)を通知する。
    /// Escキーでキャンセルされた場合や、選択範囲が小さすぎて誤操作とみなした場合は呼ばれない。</summary>
    public event Action<System.Drawing.Rectangle>? RegionSelected;

    // 選択とみなす最小サイズ(DIP)。これ未満はクリックミス等とみなしキャンセル扱いにする。
    private const double MinSelectionSizeDip = 10;

    private Point? _dragStartDip;

    public RegionSelectorWindow()
    {
        InitializeComponent();

        // 仮想スクリーン全体(マルチモニタ環境で全モニタを合わせた範囲)を覆う。
        // SystemParameters.VirtualScreen*はDIP単位で、WPF WindowのLeft/Top/Width/Heightに
        // そのまま指定できる。
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HintText.Visibility = Visibility.Collapsed;

        _dragStartDip = e.GetPosition(RootCanvas);
        SelectionRectangle.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRectangle, _dragStartDip.Value.X);
        Canvas.SetTop(SelectionRectangle, _dragStartDip.Value.Y);
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;
        CaptureMouse();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStartDip is not { } start)
        {
            return;
        }

        var current = e.GetPosition(RootCanvas);
        var x = Math.Min(start.X, current.X);
        var y = Math.Min(start.Y, current.Y);
        var width = Math.Abs(current.X - start.X);
        var height = Math.Abs(current.Y - start.Y);

        Canvas.SetLeft(SelectionRectangle, x);
        Canvas.SetTop(SelectionRectangle, y);
        SelectionRectangle.Width = width;
        SelectionRectangle.Height = height;
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStartDip is not { } start)
        {
            return;
        }

        ReleaseMouseCapture();
        var end = e.GetPosition(RootCanvas);
        _dragStartDip = null;

        var dipRect = new Rect(
            new Point(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y)),
            new Point(Math.Max(start.X, end.X), Math.Max(start.Y, end.Y)));

        if (dipRect.Width < MinSelectionSizeDip || dipRect.Height < MinSelectionSizeDip)
        {
            Close();
            return;
        }

        var physicalRect = ConvertToPhysicalPixels(dipRect);
        Close();
        RegionSelected?.Invoke(physicalRect);
    }

    /// <summary>Canvas相対のDIP矩形を、スクリーン全体基準の物理ピクセル矩形へ変換する。</summary>
    private System.Drawing.Rectangle ConvertToPhysicalPixels(Rect dipRect)
    {
        // Visual.PointToScreenは物理ピクセル単位のスクリーン座標を返す
        // (WPFの他の大半のAPIがDIPを使うのに対し、PointToScreen/PointFromScreenはこの点で例外)。
        // そのためScreenCaptureService.CaptureRegion(GDIのCopyFromScreen、物理ピクセル基準)へ
        // そのまま渡せる。
        var topLeft = RootCanvas.PointToScreen(dipRect.TopLeft);
        var bottomRight = RootCanvas.PointToScreen(dipRect.BottomRight);

        return new System.Drawing.Rectangle(
            (int)Math.Round(topLeft.X),
            (int)Math.Round(topLeft.Y),
            (int)Math.Round(bottomRight.X - topLeft.X),
            (int)Math.Round(bottomRight.Y - topLeft.Y));
    }
}
