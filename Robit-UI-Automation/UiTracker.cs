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

    public void SafeRefresh()
    {
        if (_overlay != null)
            _overlay.Hide();

        RefreshInternal();

        if (_overlay != null)
        {
            _overlay.SetRects(_elements.Select(e => e.Rect).ToList());
            _overlay.Show();
        }
    }

    private void RefreshInternal()
    {
        _elements.Clear();

        var desktop = _automation.GetDesktop();

        var windows = desktop.FindAllChildren()
            .Where(w => !w.Properties.IsOffscreen.ValueOrDefault)
            .ToList();

        foreach (var win in windows)
        {
            try
            {
                var winRect = win.BoundingRectangle;
                if (winRect.IsEmpty || winRect.Width <= 0 || winRect.Height <= 0)
                    continue;

                var elements = win.FindAllDescendants(cf => 
                    cf.ByControlType(ControlType.Button)
                    .Or(cf.ByControlType(ControlType.CheckBox))
                    .Or(cf.ByControlType(ControlType.ComboBox))
                    .Or(cf.ByControlType(ControlType.Edit))
                    .Or(cf.ByControlType(ControlType.Hyperlink))
                    .Or(cf.ByControlType(ControlType.ListItem))
                    .Or(cf.ByControlType(ControlType.MenuItem))
                    .Or(cf.ByControlType(ControlType.RadioButton))
                    .Or(cf.ByControlType(ControlType.Slider))
                    .Or(cf.ByControlType(ControlType.TabItem))
                    .Or(cf.ByControlType(ControlType.TreeItem))
                );

                foreach (var el in elements)
                {
                    try
                    {
                        var rect = el.BoundingRectangle;
                        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                            continue;

                        if (el.Properties.IsOffscreen.ValueOrDefault)
                            continue;

                        _elements.Add(new CachedElement
                        {
                            Element = el,
                            Rect = rect
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }

        Console.WriteLine($"Cached {_elements.Count} visible UI elements across all windows");
    }

    private bool IsActuallyVisible(AutomationElement el)
    {
        try
        {
            var r = el.BoundingRectangle;

            var points = new[]
            {
                new Point(r.Left + r.Width / 2, r.Top + r.Height / 2),
                new Point(r.Left + 2, r.Top + 2),
                new Point(r.Right - 2, r.Bottom - 2)
            };

            foreach (var p in points)
            {
                var top = _automation.FromPoint(p);

                if (top == null)
                    continue;

                var current = top;
                while (current != null)
                {
                    // If the sampled pixel belongs to the element OR any child inside of it
                    if (current.Equals(el))
                        return true;

                    current = current.Parent;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public List<CachedElement> GetClosest6()
    {
        var mouse = Cursor.Position;

        return _elements
            .OrderBy(e => DistanceToRect(mouse, e.Rect))
            .ThenBy(e => e.Rect.Width * e.Rect.Height) // 3. Fallback size-sort allows innermost items to top the list when overlaps happen
            .Where(e => IsActuallyVisible(e.Element)) // 🔥 Lazy evaluation: only hit-tests the closest items!
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