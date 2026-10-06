using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AnyDictation.App;

/// <summary>タスクトレイのアイコンとメニュー。アイコンは状態ごとに色を変える。</summary>
internal sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon _icon;
    readonly Dictionary<SessionState, Icon> _icons = new();
    readonly ToolStripMenuItem _stateItem = new() { Enabled = false };
    readonly ToolStripMenuItem _toggleItem = new("録音の開始 / 停止");
    readonly ToolStripMenuItem _updateItem = new() { Visible = false };

    public TrayIcon(Action openSettings, Action openHistory, Action toggle, Action restartToUpdate, Action exit)
    {
        _icons[SessionState.Idle] = Draw(Color.FromArgb(0x54, 0x6E, 0x7A));
        _icons[SessionState.Recording] = Draw(Color.FromArgb(0xE5, 0x39, 0x35));
        _icons[SessionState.Recognizing] = Draw(Color.FromArgb(0xFB, 0x8C, 0x00));
        _icons[SessionState.RetryPending] = Draw(Color.FromArgb(0x8E, 0x0F, 0x0F));

        var menu = new ContextMenuStrip();
        menu.Items.Add(_stateItem);
        menu.Items.Add(new ToolStripSeparator());
        _toggleItem.Click += (_, _) => toggle();
        menu.Items.Add(_toggleItem);
        menu.Items.Add("設定を開く", null, (_, _) => openSettings());
        menu.Items.Add("履歴を開く", null, (_, _) => openHistory());
        menu.Items.Add(new ToolStripSeparator());
        _updateItem.Click += (_, _) => restartToUpdate();
        menu.Items.Add(_updateItem);
        menu.Items.Add("Any Dictation を終了", null, (_, _) => exit());

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => openSettings();
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
        _stateItem.Text = "状態: " + text;
        _icon.Text = "Any Dictation - " + text;
    }

    public void Balloon(string title, string text) => _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);

    public void ShowUpdateReady(string version)
    {
        _updateItem.Text = $"再起動して更新（{version}）";
        _updateItem.Visible = true;
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
            DestroyIcon(h);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
    }
}
