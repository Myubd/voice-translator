using System.Windows;

namespace LoopbackRecorder;

/// <summary>
/// OCR単発翻訳の結果を表示する非モーダルなポップアップ。
/// OCR→翻訳は(特にDeepL/Ollamaへの通信を含むため)体感できる時間がかかるため、
/// コンストラクタ呼び出し直後は「認識中...」を表示してすぐウィンドウを出し、
/// 完了後にSetResult/SetErrorで内容を更新する(結果が揃うまでユーザーを待たせない)。
/// </summary>
public partial class OcrResultWindow : Window
{
    public OcrResultWindow()
    {
        InitializeComponent();
    }

    public void SetResult(string originalText, string translatedText)
    {
        StatusText.Text = "完了";
        OriginalTextBox.Text = originalText;
        TranslatedTextBox.Text = translatedText;
    }

    public void SetError(string message)
    {
        StatusText.Text = "エラー";
        OriginalTextBox.Text = string.Empty;
        TranslatedTextBox.Text = message;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TranslatedTextBox.Text))
        {
            Clipboard.SetText(TranslatedTextBox.Text);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
