using System;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace AnyDictation.App;

/// <summary>キーの押下状態の取得と、SendInput によるキー入力の送出。</summary>
internal static class KeyboardInput
{
    public const ushort VK_CONTROL = 0x11, VK_V = 0x56, VK_RETURN = 0x0D, VK_MASK = 0xE8;
    public const ushort VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    /// <summary>自身が SendInput したイベントを識別する dwExtraInfo。フックはこの値を持つイベントを無視する。</summary>
    public static readonly UIntPtr OwnMarker = new(0x414E5944); // "ANYD"

    public static bool IsPhysicallyDown(int vk) => (PInvoke.GetAsyncKeyState(vk) & 0x8000) != 0;

    public static bool AnyModifierDown() =>
        IsPhysicallyDown(0xA2) || IsPhysicallyDown(0xA3) || IsPhysicallyDown(VK_LWIN) || IsPhysicallyDown(VK_RWIN)
        || IsPhysicallyDown(0xA0) || IsPhysicallyDown(0xA1) || IsPhysicallyDown(0xA4) || IsPhysicallyDown(0xA5);

    static INPUT Key(ushort vk, bool up, UIntPtr marker) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            ki = new KEYBDINPUT { wVk = (VIRTUAL_KEY)vk, dwFlags = up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0, dwExtraInfo = marker },
        },
    };

    static unsafe bool Send(params INPUT[] inputs) => PInvoke.SendInput(inputs, sizeof(INPUT)) == inputs.Length;

    /// <summary>Win メニューを抑制するための未割り当てキー(0xE8)の押下と解放。marker は受け取るフックが自分の送出として無視する印。</summary>
    public static bool SendMaskKey(UIntPtr marker) => Send(Key(VK_MASK, false, marker), Key(VK_MASK, true, marker));

    public static bool SendCtrlV() => Send(Key(VK_CONTROL, false, OwnMarker), Key(VK_V, false, OwnMarker), Key(VK_V, true, OwnMarker), Key(VK_CONTROL, true, OwnMarker));

    public static bool SendEnter() => Send(Key(VK_RETURN, false, OwnMarker), Key(VK_RETURN, true, OwnMarker));
}
