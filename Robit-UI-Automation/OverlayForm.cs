using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

public class OverlayForm : Form
{
    private List<Rectangle> _rects = new List<Rectangle>();

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.LimeGreen;
        TransparencyKey = Color.LimeGreen;
        WindowState = FormWindowState.Maximized;

        Load += (s, e) =>
        {
            int exStyle = Native.GetWindowLong(this.Handle, -20);
            Native.SetWindowLong(this.Handle, -20, exStyle | 0x80000 | 0x20);
        };
    }

    public void SetRects(List<Rectangle> rects)
    {
        _rects = rects;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        using var pen = new Pen(Color.Red, 2);

        foreach (var r in _rects)
        {
            e.Graphics.DrawRectangle(pen, r);
        }
    }
}