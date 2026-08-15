using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace LoopbackRecorder;

/// <summary>
/// Win32のRegisterHotKeyを使い、ウィンドウがフォーカスを持っていなくても(ゲーム中でAlt-Tabしなくても)
/// 反応するグローバルホットキーを提供する。
/// 従来は開始/停止・オーバーレイ切り替えのいずれもアプリをアクティブにしないと操作できず、
/// ゲームプレイ中の実用性が低かった問題への対応。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    [Flags]
    public enum Modifiers : uint
    {
        None = 0x0000,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 1;
    private bool _disposed;

    public HotkeyManager(Window window)
    {
        var helper = new WindowInteropHelper(window);
        // ウィンドウハンドルが未生成の場合(Loadedより前)はここで生成される
        helper.EnsureHandle();
        _source = HwndSource.FromHwnd(helper.Handle)
            ?? throw new InvalidOperationException("ウィンドウハンドルの取得に失敗しました。");
        _source.AddHook(WndProc);
    }

    /// <summary>キーの組み合わせにハンドラーを登録する。同じ組み合わせが他アプリで
    /// 既に使われている場合は例外を投げる(呼び出し側でcatchして続行することを想定)</summary>
    public void Register(Modifiers modifiers, Key key, Action handler)
    {
        int id = _nextId++;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

        if (!RegisterHotKey(_source.Handle, id, (uint)modifiers, vk))
        {
            throw new InvalidOperationException(
                $"ホットキー({modifiers}+{key})の登録に失敗しました。他のアプリで既に使われている可能性があります。");
        }

        _handlers[id] = handler;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handled = true;
            handler();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var id in _handlers.Keys)
        {
            UnregisterHotKey(_source.Handle, id);
        }
        _handlers.Clear();
        _source.RemoveHook(WndProc);
    }
}
