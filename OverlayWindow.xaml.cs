using System.Windows;
using System.Windows.Input;

namespace LoopbackRecorder;

public partial class OverlayWindow : Window
{
    // 溜め込みすぎるとメモリを圧迫するので、保持する行数の上限を設ける
    private const int MaxLines = 50;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    /// <summary>訳文を1行追加する。呼び出し元でDispatcher経由にすること</summary>
    public void AddTranslatedLine(string text)
    {
        TranslatedListBox.Items.Add(text);

        while (TranslatedListBox.Items.Count > MaxLines)
        {
            TranslatedListBox.Items.RemoveAt(0);
        }

        if (TranslatedListBox.Items.Count > 0)
        {
            TranslatedListBox.ScrollIntoView(TranslatedListBox.Items[^1]);
        }
    }

    /// <summary>表示している訳文をすべてクリアする</summary>
    public void ClearLines()
    {
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
