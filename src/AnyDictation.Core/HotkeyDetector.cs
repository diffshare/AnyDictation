namespace AnyDictation;

/// <summary>
/// 検出結果。Toggle は録音の開始/停止要求、InjectMask は Win メニュー抑制用のダミーキー送出要求。
/// HoldStart は長押しの成立(押し続けている間の開始要求)、HoldEnd は長押し後の解放(停止要求)。
/// </summary>
public readonly record struct HotkeyResult(bool Toggle, bool InjectMask, bool HoldStart = false, bool HoldEnd = false);

/// <summary>
/// Ctrl + Win の modifier-only 同時押しを検出する純粋な状態機械。イベントは一切抑制しない。
/// 両キーが揃った時点で「成立」とし、他のキーを挟まずにどちらかが離された時点で Toggle を返す。
/// 他のキー(Ctrl+Win+矢印など)が押された場合は、全 modifier が離されるまで無効。
/// 揃ってから他のキーなしで <see cref="HoldThresholdMs"/> 以上押し続けると、離したときの Toggle ではなく
/// 長押し(HoldStart / HoldEnd)になる。長押しの成立は <see cref="Tick"/> で判定し、時刻は呼び出し側が渡す(時計は読まない)。
/// 長押しが成立した後に他のキーが押されても、HoldEnd は最初の modifier を離したときに返す。
/// </summary>
public sealed class HotkeyDetector
{
    public const long HoldThresholdMs = 500;
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
    bool _holding;
    long _armedAtMs;

    /// <summary>nowMs は単調増加する時刻(ミリ秒)。成立(armed)の時刻の記録にだけ使う。</summary>
    public HotkeyResult Process(int vk, bool isDown, long nowMs = 0)
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
                _armedAtMs = nowMs;
                return new HotkeyResult(false, true);
            }
            return default;
        }

        if (!set.Remove(vk)) return default;
        // タイマーの余裕で Tick が遅れても、しきい値を超えて離したものは短押しにしない(長押しが始まる前に離した操作は無視する)
        bool late = _armed && nowMs - _armedAtMs >= HoldThresholdMs;
        bool toggle = _armed && !late;
        bool holdEnd = _holding;
        _armed = false;
        _holding = false;
        if (toggle || late) _fired = true;
        if (_ctrl.Count == 0 && _win.Count == 0)
        {
            _dirty = false;
            _fired = false;
        }
        return new HotkeyResult(toggle, false, HoldEnd: holdEnd);
    }

    /// <summary>成立から <see cref="HoldThresholdMs"/> 以上たっていれば長押しにする。キー入力がなくても呼べる。</summary>
    public HotkeyResult Tick(long nowMs)
    {
        if (!_armed || nowMs - _armedAtMs < HoldThresholdMs) return default;
        _armed = false;
        _holding = true;
        _fired = true; // 長押し中は、離したときの Toggle や押し直しでの再成立をさせない
        return new HotkeyResult(false, false, HoldStart: true);
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
            _holding = false;
            _dirty = false;
            _fired = false;
        }
    }

    public void Reset()
    {
        _ctrl.Clear();
        _win.Clear();
        _others.Clear();
        _armed = _dirty = _fired = _holding = false;
    }
}
