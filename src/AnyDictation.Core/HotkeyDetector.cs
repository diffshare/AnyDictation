namespace AnyDictation;

/// <summary>検出結果。Toggle は録音の開始/停止要求、InjectMask は Win メニュー抑制用のダミーキー送出要求。</summary>
public readonly record struct HotkeyResult(bool Toggle, bool InjectMask);

/// <summary>
/// Ctrl + Win の modifier-only 同時押しを検出する純粋な状態機械。イベントは一切抑制しない。
/// 両キーが揃った時点で「成立」とし、他のキーを挟まずにどちらかが離された時点で Toggle を返す。
/// 他のキー(Ctrl+Win+矢印など)が押された場合は、全 modifier が離されるまで無効。
/// </summary>
public sealed class HotkeyDetector
{
    public const int VkLControl = 0xA2;
    public const int VkRControl = 0xA3;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;

    readonly HashSet<int> _ctrl = new();
    readonly HashSet<int> _win = new();
    readonly HashSet<int> _others = new(); // Ctrl/Win 以外で押下中のキー(Shift/Alt/AltGr/通常キー)
    bool _armed;
    bool _dirty;
    bool _fired;

    public HotkeyResult Process(int vk, bool isDown)
    {
        bool isCtrl = vk is VkLControl or VkRControl;
        bool isWin = vk is VkLWin or VkRWin;
        if (!isCtrl && !isWin)
        {
            if (!isDown)
            {
                _others.Remove(vk);
                return default;
            }
            _others.Add(vk);
            if (_ctrl.Count > 0 || _win.Count > 0)
            {
                _dirty = true;
                _armed = false;
            }
            return default;
        }

        var set = isCtrl ? _ctrl : _win;
        if (isDown)
        {
            if (!set.Add(vk)) return default; // キーリピート
            // Shift→Ctrl→Win など、先に他のキーが押されていた場合は「Ctrl+Win だけ」ではない
            if (_others.Count > 0) _dirty = true;
            if (!_dirty && !_fired && !_armed && _ctrl.Count > 0 && _win.Count > 0)
            {
                _armed = true;
                return new HotkeyResult(false, true);
            }
            return default;
        }

        if (!set.Remove(vk)) return default;
        bool toggle = _armed;
        _armed = false;
        if (toggle) _fired = true;
        if (_ctrl.Count == 0 && _win.Count == 0)
        {
            _dirty = false;
            _fired = false;
        }
        return new HotkeyResult(toggle, false);
    }

    /// <summary>
    /// フックが up を取りこぼした場合に備え、物理的に離れているキーを内部状態から外す。
    /// WH_KEYBOARD_LL のコールバック時点では処理中のキー自身の物理状態が未更新なので、exceptVk は対象外にする。
    /// </summary>
    public void Prune(Func<int, bool> isPhysicallyDown, int exceptVk)
    {
        _ctrl.RemoveWhere(k => k != exceptVk && !isPhysicallyDown(k));
        _win.RemoveWhere(k => k != exceptVk && !isPhysicallyDown(k));
        _others.RemoveWhere(k => k != exceptVk && !isPhysicallyDown(k));
        if (_ctrl.Count == 0 && _win.Count == 0)
        {
            _armed = false;
            _dirty = false;
            _fired = false;
        }
    }

    public void Reset()
    {
        _ctrl.Clear();
        _win.Clear();
        _others.Clear();
        _armed = _dirty = _fired = false;
    }
}
