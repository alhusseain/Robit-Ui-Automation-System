using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

internal class UiTracker
{
    private UIA3Automation _automation;
    private List<CachedElement> _elements = new List<CachedElement>();
    private OverlayForm _overlay;

    public UiTracker()
    {
        _automation = new UIA3Automation();
    }

    // ============================
    // SAFE REFRESH PIPELINE
    // ============================
    public void SafeRefresh()
    {
        // 1. REMOVE OVERLAY (critical)
        if (_overlay != null)
            _overlay.Hide();

        RefreshInternal();

        // 2. RENDER OVERLAY AFTER SCAN
        if (_overlay != null)
        {
            _overlay.SetRects(_elements.Select(e => e.Rect).ToList());
            _overlay.Show();
        }
    }

    // ============================
    // UIA SCAN (NO OVERLAY HERE)
    // ============================
    private void RefreshInternal()
    {
        _elements.Clear();

        var desktop = _automation.GetDesktop();

        var windows = desktop.FindAllChildren()
            .Where(w => !w.Properties.IsOffscreen.ValueOrDefault)
            .ToList();

        var occludingRects = new List<Rectangle>();

        foreach (var win in windows)
        {
            try
            {
                var winRect = win.BoundingRectangle;
                if (winRect.IsEmpty)
                    continue;

                var blockers = occludingRects.ToList();
                occludingRects.Add(winRect);

                var elements = win.FindAllDescendants(cf =>
                    cf.ByControlType(ControlType.Button)
                    .Or(cf.ByControlType(ControlType.Edit))
                    .Or(cf.ByControlType(ControlType.ListItem))
                    .Or(cf.ByControlType(ControlType.MenuItem))
                );

                foreach (var el in elements)
                {
                    try
                    {
                        if (el.Properties.IsOffscreen.ValueOrDefault)
                            continue;

                        var rect = el.BoundingRectangle;
                        if (rect.IsEmpty)
                            continue;

                        // light occlusion check (safe version)
                        if (IsFullyCovered(rect, blockers))
                            continue;

                        _elements.Add(new CachedElement
                        {
                            Element = el,
                            Rect = rect
                        });
                    }
                    catch
                    {
                        // ignore stale elements
                    }
                }
            }
            catch
            {
                // ignore broken windows
            }
        }

        Console.WriteLine($"Cached {_elements.Count} visible elements");
    }

    // ============================
    // OCCLUSION CHECK (SAFE)
    // ============================
    private bool IsFullyCovered(Rectangle target, List<Rectangle> blockers)
    {
        foreach (var b in blockers)
        {
            if (b.Contains(target))
                return true;
        }
        return false;
    }

    // ============================
    // CLOSEST 6 TO MOUSE
    // ============================
    public List<CachedElement> GetClosest6()
    {
        var mouse = Cursor.Position;

        return _elements
            .OrderBy(e => DistanceToRect(mouse, e.Rect))
            .Take(6)
            .ToList();
    }

    private double DistanceToRect(Point p, Rectangle r)
    {
        double dx = Math.Max(r.Left - p.X, 0);
        dx = Math.Max(dx, p.X - r.Right);

        double dy = Math.Max(r.Top - p.Y, 0);
        dy = Math.Max(dy, p.Y - r.Bottom);

        return Math.Sqrt(dx * dx + dy * dy);
    }

    public void AttachOverlay(OverlayForm overlay)
    {
        _overlay = overlay;
    }

    public List<CachedElement> GetAll()
    {
        return _elements;
    }
}