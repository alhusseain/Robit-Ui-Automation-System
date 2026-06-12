using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Windows.Forms;

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X;
    public int Y;
}

internal class UiTracker
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out bool pvAttribute, int cbAttribute);

    private const int DWMWA_CLOAKED = 14;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private static bool IsWindowCloaked(IntPtr hwnd)
    {
        try
        {
            int hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out bool isCloaked, Marshal.SizeOf<bool>());
            return hr == 0 && isCloaked;
        }
        catch
        {
            return false;
        }
    }

    private UIA3Automation _automation;
    private volatile List<CachedElement> _elements = new List<CachedElement>();
    private OverlayForm _overlay;
    private volatile bool _isRefreshing = false;
    private readonly CustomMetrics _refreshMetrics = new CustomMetrics("Refresh");
    private readonly UiTreeTraverser _traverser;
    private readonly bool _measureRefresh;
    private readonly bool _measureVisibility;
    private const int POLLING_MS = 500;


    public UiTracker(bool measureRefresh, bool measureVisibility)
    {
        _automation = new UIA3Automation();
        _measureRefresh = measureRefresh;
        _measureVisibility = measureVisibility;
        _traverser = new UiTreeTraverser(measureVisibility);

        _automation.RegisterFocusChangedEvent(el => TriggerRefresh());


        Task.Run(async () =>
        {
            while (true)
            {
                TriggerRefresh();
                await Task.Delay(POLLING_MS);
            }
        });

        Task.Run(async () =>
        {
            while (true)
            {
                PollWindowTitles();
                await Task.Delay(1000);
            }
        });
    }

    public void SafeRefresh()
    {
        TriggerRefresh();
    }

    private void PollWindowTitles()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Window Titles: $");

            EnumWindows((hwnd, lParam) =>
            {
                try
                {
                    if (IsWindowVisible(hwnd) && !IsIconic(hwnd) && !IsWindowCloaked(hwnd))
                    {
                        if (Native.GetWindowRect(hwnd, out var rect))
                        {
                            int width = rect.Right - rect.Left;
                            int height = rect.Bottom - rect.Top;
                            if (width > 0 && height > 0)
                            {
                                var title = GetWindowTitle(hwnd);
                                sb.AppendLine(title);
                            }
                        }
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);

            sb.AppendLine("$");

            Console.Write(sb.ToString());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Window title polling failed: {ex.Message}");
        }
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
            var windowHandles = new List<IntPtr>();
            EnumWindows((hwnd, lParam) =>
            {
                try
                {
                    if (IsWindowVisible(hwnd) && !IsIconic(hwnd) && !IsWindowCloaked(hwnd))
                    {
                        if (Native.GetWindowRect(hwnd, out var rect))
                        {
                            int width = rect.Right - rect.Left;
                            int height = rect.Bottom - rect.Top;
                            if (width > 0 && height > 0)
                            {
                                windowHandles.Add(hwnd);
                            }
                        }
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);

            var windows = new List<AutomationElement>();
            foreach (var hwnd in windowHandles)
            {
                try
                {
                    var win = _automation.FromHandle(hwnd);
                    if (win != null)
                    {
                        windows.Add(win);
                    }
                }
                catch { }
            }
            windowCount = windows.Count;

            var newElements = new ConcurrentBag<CachedElement>();

            foreach (var win in windows)
            {
                try
                {
                    var elements = _traverser.Traverse(win);
                    descendantCount += elements.Count;

                    foreach (var el in elements)
                    {
                        newElements.Add(el);
                        cachedCount++;
                    }
                }
                catch { }
            }

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
    private static string GetWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "[none]";
        }

        int len = GetWindowTextLength(hwnd);
        if (len <= 0)
        {
            return "[no title]";
        }

        var builder = new StringBuilder(len + 1);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static bool NearlySameRect(Rectangle a, Rectangle b)
    {
        const int posTolerance = 5;
        const double sizeRatioTolerance = 0.15;

        if (Math.Abs(a.Left - b.Left) > posTolerance ||
            Math.Abs(a.Top - b.Top) > posTolerance)
        {
            return false;
        }

        double widthRatio =
            Math.Abs(a.Width - b.Width) / (double)Math.Max(a.Width, b.Width);

        double heightRatio =
            Math.Abs(a.Height - b.Height) / (double)Math.Max(a.Height, b.Height);

        return widthRatio < sizeRatioTolerance &&
            heightRatio < sizeRatioTolerance;
    }
    public List<CachedElement> GetClosest6()
    {
        var mouse = Cursor.Position;

        var candidates = _elements
            .OrderBy(e => DistanceToRect(mouse, e.Rect))
            .Where(e => _traverser.IsActuallyVisible(e))
            .Take(12)
            .ToList();

        // Keep smaller rectangles when duplicates exist
        candidates = candidates
            .OrderBy(e => e.Rect.Width * e.Rect.Height)
            .ToList();

        var deduped = new List<CachedElement>();

        foreach (var candidate in candidates)
        {
            bool duplicate = deduped.Any(existing =>
                NearlySameRect(candidate.Rect, existing.Rect));

            if (!duplicate)
            {
                deduped.Add(candidate);
            }
        }

        return deduped
            .OrderBy(e => DistanceToRect(mouse, e.Rect))
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
