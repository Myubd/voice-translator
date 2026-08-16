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

    /// <summary>文字サイズ・背景の不透明度・最大表示行数を設定に合わせて適用する</summary>
    public void ApplyAppearance(double fontSize, double opacity, int maxLines)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ApplyAppearance(fontSize, opacity, maxLines));
            return;
        }

        TranslatedListBox.Tag = fontSize;
        _maxLines = System.Math.Max(1, maxLines);

        byte alpha = (byte)(System.Math.Clamp(opacity, 0.0, 1.0) * 255);
        BackgroundBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));

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
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }
}
