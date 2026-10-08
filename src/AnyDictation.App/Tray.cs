using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Controls;
using H.NotifyIcon;
using NotificationIcon = H.NotifyIcon.Core.NotificationIcon;

namespace AnyDictation.App;

/// <summary>タスクトレイのアイコンとメニュー。アイコンは状態ごとに色を変える。</summary>
internal sealed class TrayIcon : IDisposable
{
    readonly TaskbarIcon _icon;
    readonly Dictionary<SessionState, Icon> _icons = new();
    readonly MenuItem _stateItem = new() { IsEnabled = false };
    readonly MenuItem _updateItem = new() { Visibility = System.Windows.Visibility.Collapsed };

    public TrayIcon(Action openSettings, Action openHistory, Action toggle, Action restartToUpdate, Action exit)
    {
        _icons[SessionState.Idle] = Draw(Color.FromArgb(0x54, 0x6E, 0x7A));
        _icons[SessionState.Recording] = Draw(Color.FromArgb(0xE5, 0x39, 0x35));
        _icons[SessionState.Recognizing] = Draw(Color.FromArgb(0xFB, 0x8C, 0x00));
        _icons[SessionState.RetryPending] = Draw(Color.FromArgb(0x8E, 0x0F, 0x0F));

        var menu = new ContextMenu();
        menu.Items.Add(_stateItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("録音の開始 / 停止", toggle));
        menu.Items.Add(Item("設定を開く", openSettings));
        menu.Items.Add(Item("履歴を開く", openHistory));
        menu.Items.Add(new Separator());
        _updateItem.Click += (_, _) => restartToUpdate();
        menu.Items.Add(_updateItem);
        menu.Items.Add(Item("Any Dictation を終了", exit));

        _icon = new TaskbarIcon { ContextMenu = menu };
        _icon.TrayMouseDoubleClick += (_, _) => openSettings();
        _icon.ForceCreate();
        SetState(SessionState.Idle);
    }

    public void SetState(SessionState state)
    {
        _icon.Icon = _icons[state];
        string text = state switch
        {
            SessionState.Recording => "録音中",
            SessionState.Recognizing => "認識中",
            SessionState.RetryPending => "失敗(再送待ち)",
            _ => "待機中(Ctrl+Win で録音)",
        };
        _stateItem.Header = "状態: " + text;
        _icon.ToolTipText = "Any Dictation - " + text;
    }

    public void Balloon(string title, string text) => _icon.ShowNotification(title, text, NotificationIcon.Info);

    public void ShowUpdateReady(string version)
    {
        _updateItem.Header = $"再起動して更新（{version}）";
        _updateItem.Visibility = System.Windows.Visibility.Visible;
    }

    static MenuItem Item(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    static Icon Draw(Color fill)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(fill);
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var white = new SolidBrush(Color.White);
            using var pen = new Pen(Color.White, 2.5f);
            g.FillRectangle(white, 12, 6, 8, 12);                 // マイク本体
            g.DrawArc(pen, 9, 9, 14, 13, 0, 180);                  // 受け
            g.DrawLine(pen, 16, 22, 16, 26);
            g.DrawLine(pen, 12, 26, 20, 26);
        }
        IntPtr h = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(h).Clone(); // Clone で GDI ハンドルから切り離す
        }
        finally
        {
            Native.DestroyIcon(h);
        }
    }

    public void Dispose()
    {
        _icon.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
    }
}
