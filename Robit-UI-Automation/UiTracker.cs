using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows.Forms;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

internal class UiTracker
{
    private UIA3Automation _automation;
    private volatile List<CachedElement> _elements = new List<CachedElement>();
    private OverlayForm _overlay;
    private volatile bool _isRefreshing = false;
    private readonly CustomMetrics _refreshMetrics = new CustomMetrics("Refresh");
    private readonly CustomMetrics _visibilityMetrics = new CustomMetrics("IsActuallyVisible");
    private readonly bool _measureRefresh;
    private readonly bool _measureVisibility;

    public UiTracker(bool measureRefresh, bool measureVisibility)
    {
        _automation = new UIA3Automation();
        _measureRefresh = measureRefresh;
        _measureVisibility = measureVisibility;

        _automation.RegisterFocusChangedEvent(el => TriggerRefresh());


        Task.Run(async () =>
        {
            while (true)
            {
                TriggerRefresh();
                await Task.Delay(2000); // Adjust polling rate as needed
            }
        });
    }

    public void SafeRefresh()
    {
        TriggerRefresh();
    }

    private void TriggerRefresh()
    {
        if (_isRefreshing) return;
        
        Task.Run(() => 
        {
            try
            {
                _isRefreshing = true;
                RefreshInternal();
            }
            finally
            {
                _isRefreshing = false;
            }
        });
    }

    private void RefreshInternal()
    {
        var sw = Stopwatch.StartNew();
        int windowCount = 0;
        int descendantCount = 0;
        int cachedCount = 0;

        try
        {
        var desktop = _automation.GetDesktop();

        var windows = desktop.FindAllChildren()
            .Where(w => !w.Properties.IsOffscreen.ValueOrDefault)
            .ToList();
            windowCount = windows.Count;

            var newElements = new ConcurrentBag<CachedElement>();

            // 🔥 3. Parallelism: Scrape multiple windows simultaneously
            Parallel.ForEach(windows, win =>
        {
            try
            {
                var winRect = win.BoundingRectangle;
                if (winRect.IsEmpty || winRect.Width <= 0 || winRect.Height <= 0)
                        return; // return replaces continue in Parallel.ForEach

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
                Interlocked.Add(ref descendantCount, elements.Count());

                foreach (var el in elements)
                {
                    try
                    {
                        var rect = el.BoundingRectangle;
                        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                            continue;

                        if (el.Properties.IsOffscreen.ValueOrDefault)
                            continue;

                            newElements.Add(new CachedElement
                        {
                            Element = el,
                            Rect = rect
                        });
                        Interlocked.Increment(ref cachedCount);
                    }
                    catch { }
                }
            }
            catch { }
            });

            _elements = newElements.ToList(); // Atomically swap in the new list to ensure thread safety
        Console.WriteLine($"Cached {_elements.Count} visible UI elements across all windows");
            sw.Stop();
            if (_measureRefresh)
            {
                _refreshMetrics.RecordTiming(
                    sw.ElapsedMilliseconds,
                    $"windows={windowCount}, descendants={descendantCount}, cached={cachedCount}"
                );
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            if (_measureRefresh)
            {
                _refreshMetrics.RecordTiming(
                    sw.ElapsedMilliseconds,
                    $"failed after windows={windowCount}, descendants={descendantCount}, cached={cachedCount}"
                );
            }
            Console.WriteLine($"Refresh failed: {ex.Message}");
        }
    }

    private bool IsActuallyVisible(AutomationElement el)
    {
        var sw = Stopwatch.StartNew();
        long fromPointMs = 0;
        int pointsChecked = 0;
        int parentSteps = 0;

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
                pointsChecked++;
                var fromPointSw = Stopwatch.StartNew();
                var top = _automation.FromPoint(p);
                fromPointSw.Stop();
                fromPointMs += fromPointSw.ElapsedMilliseconds;

                if (top == null)
                    continue;

                var current = top;
                while (current != null)
                {
                    parentSteps++;
                    // If the sampled pixel belongs to the element OR any child inside of it
                    if (current.Equals(el))
                    {
                        sw.Stop();
                        if (_measureVisibility)
                        {
                            _visibilityMetrics.RecordTiming(sw.ElapsedMilliseconds, FormatVisibilityDetails(true, pointsChecked, parentSteps, fromPointMs));
                        }
                        return true;
                    }

                    current = current.Parent;
                }
            }

            sw.Stop();
            if (_measureVisibility)
            {
                _visibilityMetrics.RecordTiming(sw.ElapsedMilliseconds, FormatVisibilityDetails(false, pointsChecked, parentSteps, fromPointMs));
            }
            return false;
        }
        catch
        {
            sw.Stop();
            if (_measureVisibility)
            {
                _visibilityMetrics.RecordTiming(sw.ElapsedMilliseconds, FormatVisibilityDetails(null, pointsChecked, parentSteps, fromPointMs));
            }
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

    private string FormatVisibilityDetails(bool? result, int pointsChecked, int parentSteps, long fromPointMs)
    {
        string resultText = result.HasValue ? result.Value.ToString().ToLowerInvariant() : "error";
        return $"result={resultText}, points={pointsChecked}, parentSteps={parentSteps}, fromPointMs={fromPointMs}";
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
