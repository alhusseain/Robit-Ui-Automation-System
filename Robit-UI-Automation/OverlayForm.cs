using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

public class OverlayForm : Form
{
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
                0x80000 | // WS_EX_LAYERED
                0x20      // WS_EX_TRANSPARENT (click-through)
            );

            // 🔥 force initial clean paint
            this.Invalidate();
            this.Update();
        };
    }

    public void SetRects(List<(Rectangle Rect, int Index)> rects)
    {
        Rects = rects;
        Invalidate();
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