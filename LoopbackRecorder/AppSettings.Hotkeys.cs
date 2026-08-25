using System;
using System.Windows.Input;

/// <summary>
/// AppSettingsのうち、System.Windows.Input(WPF)に依存するホットキー関連メソッドのみを
/// 分離したpartialクラス。AppSettings.cs本体をWPF非依存に保つため、あえて別ファイルにしている
/// (理由の詳細はAppSettings.csのクラスコメントを参照)。
/// </summary>
public partial class AppSettings
{
    /// <summary>「翻訳開始/停止」に割り当てられたショートカットキーを取得する。値が不正な場合は既定値(Ctrl+Alt+R)を返す</summary>
    public (ModifierKeys Modifiers, Key Key) GetStartStopHotkey() =>
        ParseHotkey(StartStopHotkeyModifiers, StartStopHotkeyKey, ModifierKeys.Control | ModifierKeys.Alt, Key.R);

    /// <summary>「オーバーレイ表示切り替え」に割り当てられたショートカットキーを取得する。値が不正な場合は既定値(Ctrl+Alt+O)を返す</summary>
    public (ModifierKeys Modifiers, Key Key) GetOverlayHotkey() =>
        ParseHotkey(OverlayHotkeyModifiers, OverlayHotkeyKey, ModifierKeys.Control | ModifierKeys.Alt, Key.O);

    /// <summary>「OCR単発翻訳」に割り当てられたショートカットキーを取得する。値が不正な場合は既定値(Ctrl+Alt+T)を返す</summary>
    public (ModifierKeys Modifiers, Key Key) GetOcrHotkey() =>
        ParseHotkey(OcrHotkeyModifiers, OcrHotkeyKey, ModifierKeys.Control | ModifierKeys.Alt, Key.T);

    private static (ModifierKeys, Key) ParseHotkey(string modifiersStr, string keyStr, ModifierKeys defaultModifiers, Key defaultKey)
    {
        var modifiers = Enum.TryParse<ModifierKeys>(modifiersStr, ignoreCase: true, out var parsedModifiers) ? parsedModifiers : defaultModifiers;
        var key = Enum.TryParse<Key>(keyStr, ignoreCase: true, out var parsedKey) ? parsedKey : defaultKey;
        return (modifiers, key);
    }
}
