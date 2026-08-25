using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LoopbackRecorder;

public partial class OverlayWindow : Window
{
    // 行の並び順の計算(Id順への挿入・上限行数超過時の間引き)はOverlayLineOrderer
    // (WPF非依存の純粋ロジック、単体テスト可能)に委譲する。このクラスはOrdererの結果を
    // TranslatedListBox(WPFのUI要素)へ反映する役割に専念する。
    private readonly OverlayLineOrderer _orderer = new(maxLines: 4);

    /// <summary>オーバーレイの「🔍」ボタンが押された時に発火する。実際のOCR範囲選択〜翻訳の処理は
    /// MainWindow側(StartOcrCapture)が持っているため、このクラスは通知に専念する
    /// (ホットキーからの起動と処理を一本化し、二重実装を避けるため)。</summary>
    public event Action? OcrRequested;

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

        // 表示行数の上限を減らした場合、既に表示済みの行が新しい上限を超えている可能性がある。
        // OrdererのMaxLinesを更新した上でTrimToMax()を呼び、間引かれた件数だけUI側からも削除する。
        _orderer.MaxLines = System.Math.Max(1, maxLines);
        var removedCount = _orderer.TrimToMax();
        for (int i = 0; i < removedCount; i++)
        {
            TranslatedListBox.Items.RemoveAt(0);
        }

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

        RefreshEmphasis();
    }

    /// <summary>訳文を1行、発話順(Id昇順)の正しい位置に挿入(または既存行を更新)する。
    /// UI要素(TranslatedListBox)を直接操作するため、必ずUIスレッドから呼ぶ必要がある。
    /// Dispatcher.CheckAccess()で明示的にチェックし、UIスレッド以外から呼ばれた場合は
    /// 自動的にUIスレッドへ委譲することで、呼び出し側のミスによるクラッシュを防ぐ。
    ///
    /// 以前は「届いた順にそのまま追記する」実装(AddTranslatedLine)だったが、翻訳ワーカーを
    /// 複数並列で動かすようになったことで、翻訳の完了順が発話順と一致しなくなる場合が生じた
    /// (例: 先に話した内容がDeepL失敗でOllamaへフォールバックして遅れている間に、後から話した
    /// 内容が先に翻訳完了する)。届いた順にそのまま追記すると、ゲーム実況の字幕として
    /// 時系列が入れ替わって表示されてしまうため、Id(発話順に単調増加する識別子)を基準に
    /// 正しい位置へ挿入する方式に変更した(実際の並び替え計算はOverlayLineOrdererが行う)。</summary>
    public void UpsertTranslatedLine(long id, string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpsertTranslatedLine(id, text));
            return;
        }

        var result = _orderer.Upsert(id, text);

        if (result.IsUpdate)
        {
            TranslatedListBox.Items[result.Index] = text;
        }
        else
        {
            // 先頭からの間引きは、新しい行を挿入する「前」に計算されたものなので、
            // UI側でも同じ順序(先に間引き→その後に挿入)で反映する。
            for (int i = 0; i < result.RemovedFromFrontCount; i++)
            {
                TranslatedListBox.Items.RemoveAt(0);
            }

            if (!result.WasTrimmedAway)
            {
                TranslatedListBox.Items.Insert(result.Index, text);
            }
            // WasTrimmedAwayがtrueの場合(=挿入した行が、既にMaxLines件の新しい発話で
            // 埋まっていたため即座に間引かれた場合)は、UI側にも何も挿入しない。
        }

        RefreshEmphasis();

        if (!result.WasTrimmedAway && result.IsLatest && TranslatedListBox.Items.Count > 0)
        {
            TranslatedListBox.ScrollIntoView(TranslatedListBox.Items[^1]);
        }
    }

    /// <summary>最新行だけを目立たせる(不透明度・太字)見た目を再計算する。
    /// 行の増減自体はOverlayLineOrderer側の責務であり、ここでは見た目の更新のみを行う。</summary>
    private void RefreshEmphasis()
    {
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

        _orderer.Clear();
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

    private void OcrButton_Click(object sender, RoutedEventArgs e)
    {
        OcrRequested?.Invoke();
    }
}
