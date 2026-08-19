using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LoopbackRecorder;

public partial class OverlayWindow : Window
{
    private int _maxLines = 4;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    /// <summary>文字サイズ・背景の不透明度・最大表示行数・文字色を設定に合わせて適用する</summary>
    public void ApplyAppearance(double fontSize, double opacity, int maxLines, string fontColorHex = "#FFFFFF")
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ApplyAppearance(fontSize, opacity, maxLines, fontColorHex));
            return;
        }

        TranslatedListBox.Tag = fontSize;
        _maxLines = System.Math.Max(1, maxLines);

        byte alpha = (byte)(System.Math.Clamp(opacity, 0.0, 1.0) * 255);
        BackgroundBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(fontColorHex);
            TranslatedListBox.Foreground = new SolidColorBrush(color);
        }
        catch (FormatException)
        {
            // 万一不正な値(手動で.envを編集した等)が渡された場合は、既存の色をそのまま維持する
            // (真っ白なテキストが読めなくなるより、変更前の状態を保つ方が安全なため)
        }

        TrimAndRefreshEmphasis();
    }

    /// <summary>訳文を1行追加する。
    /// UI要素(TranslatedListBox)を直接操作するため、必ずUIスレッドから呼ぶ必要がある。
    /// 以前はこの制約がコメントのみで、呼び出し元の規律に依存していたため見えにくかった。
    /// Dispatcher.CheckAccess()で明示的にチェックし、UIスレッド以外から呼ばれた場合は
    /// 自動的にUIスレッドへ委譲することで、呼び出し側のミスによるクラッシュを防ぐ。</summary>
    public void AddTranslatedLine(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AddTranslatedLine(text));
            return;
        }

        TranslatedListBox.Items.Add(text);
        TrimAndRefreshEmphasis();

        if (TranslatedListBox.Items.Count > 0)
        {
            TranslatedListBox.ScrollIntoView(TranslatedListBox.Items[^1]);
        }
    }

    /// <summary>設定された最大行数までに切り詰め、最新行だけを目立たせる</summary>
    private void TrimAndRefreshEmphasis()
    {
        while (TranslatedListBox.Items.Count > _maxLines)
        {
            TranslatedListBox.Items.RemoveAt(0);
        }

        TranslatedListBox.UpdateLayout();
        int count = TranslatedListBox.Items.Count;
        for (int i = 0; i < count; i++)
        {
            if (TranslatedListBox.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem container)
            {
                bool isLatest = i == count - 1;
                container.Opacity = isLatest ? 1.0 : 0.5;
                container.FontWeight = isLatest ? FontWeights.Bold : FontWeights.Normal;
            }
        }
    }

    /// <summary>表示している訳文をすべてクリアする</summary>
    public void ClearLines()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ClearLines);
            return;
        }

        TranslatedListBox.Items.Clear();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // タイトルバーが無いウィンドウなので、クリック&ドラッグで移動できるようにする
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove(); // ブロッキング呼び出し。マウスボタンが離されると返る
            SnapToScreenEdgeIfClose();
        }
    }

    // 画面端(作業領域の端)からこの距離(DIP)以内でドロップした場合に吸着させる
    private const double SnapThreshold = 24;

    /// <summary>
    /// ドラッグ終了時、画面端に近ければそこへ吸着させる。
    ///
    /// ドラッグ中ずっと位置を監視して吸着させる方式(LocationChangedイベントを使う方式)も
    /// 検討したが、DragMove()はOSのネイティブなドラッグループを使っているため、ドラッグの
    /// 最中に横から位置を書き換えるとOS側の次のマウス位置更新と競合し、カーソルの下で
    /// ウィンドウが微妙にガタつく(jitter)問題が起きやすい。
    /// ドロップした瞬間にだけ吸着させる方式ならこの競合が起きず、多くのアプリでも
    /// 採用されている素直な挙動になる。
    /// Left/Top/Width/HeightとSystemParameters.WorkAreaはいずれもDIP(デバイス非依存ピクセル)
    /// で統一されているため、DPIスケーリング環境でも計算がずれない。
    /// </summary>
    private void SnapToScreenEdgeIfClose()
    {
        var workArea = SystemParameters.WorkArea;

        if (Math.Abs(Left - workArea.Left) < SnapThreshold)
        {
            Left = workArea.Left;
        }
        else if (Math.Abs((Left + Width) - workArea.Right) < SnapThreshold)
        {
            Left = workArea.Right - Width;
        }

        if (Math.Abs(Top - workArea.Top) < SnapThreshold)
        {
            Top = workArea.Top;
        }
        else if (Math.Abs((Top + Height) - workArea.Bottom) < SnapThreshold)
        {
            Top = workArea.Bottom - Height;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }
}
