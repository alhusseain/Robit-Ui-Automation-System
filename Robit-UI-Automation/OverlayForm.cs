using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class OverlayForm : Form
{
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    public List<(Rectangle Rect, int Index)> Rects = new List<(Rectangle Rect, int Index)>();

    public OverlayForm()
    {
        // 🔥 CRITICAL: apply styles BEFORE handle creation
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint, true);

        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;

        BackColor = Color.LimeGreen;
        TransparencyKey = Color.LimeGreen;

        WindowState = FormWindowState.Maximized;

        // 🔥 prevent activation + click-through + layered window
        Load += (s, e) =>
        {
            int exStyle = Native.GetWindowLong(this.Handle, -20);

            Native.SetWindowLong(this.Handle, -20,
                exStyle |
                WS_EX_LAYERED |
                WS_EX_TRANSPARENT |
                WS_EX_NOACTIVATE |
                WS_EX_TOOLWINDOW
            );

            EnsureTopMost();
            this.Invalidate();
            this.Update();
        };
    }

    public void SetRects(List<(Rectangle Rect, int Index)> rects)
    {
        Rects = rects;
        EnsureTopMost();
        Invalidate();
    }

    private void EnsureTopMost()
    {
        if (IsHandleCreated)
        {
            SetWindowPos(this.Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        var g = e.Graphics;

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var pen = new Pen(Color.Red, 2);
        using var bg = new SolidBrush(Color.FromArgb(180, Color.Black));
        using var textBrush = new SolidBrush(Color.White);
        using var font = new Font("Arial", 14, FontStyle.Bold);

        foreach (var item in Rects)
        {
            var rect = item.Rect;
            int index = item.Index;

            g.DrawRectangle(pen, rect);

            string text = index.ToString();
            var size = g.MeasureString(text, font);

            var box = new Rectangle(
                rect.Left,
                rect.Top,
                (int)size.Width + 6,
                (int)size.Height + 4
            );

            g.FillRectangle(bg, box);

            g.DrawString(
                text,
                font,
                textBrush,
                rect.Left + 3,
                rect.Top + 2
            );
        }
    }
}