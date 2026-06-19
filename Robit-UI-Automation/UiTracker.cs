using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.IO;
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

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    private const uint GA_ROOT = 2;

    private UIA3Automation _automation;
    public UIA3Automation Automation => _automation;
    private volatile List<CachedElement> _elements = new List<CachedElement>();
    private OverlayForm? _overlay;
    private readonly CustomMetrics _refreshMetrics = new CustomMetrics("Refresh");
    private readonly UiTreeTraverser _traverser;
    private readonly bool _measureRefresh;
    private readonly bool _measureVisibility;
    private readonly bool _focusOnly;

    private readonly ConcurrentDictionary<IntPtr, List<CachedElement>> _windowCaches =
        new ConcurrentDictionary<IntPtr, List<CachedElement>>();

    private readonly ConcurrentDictionary<IntPtr, WindowTrackingSession> _trackingSessions =
        new ConcurrentDictionary<IntPtr, WindowTrackingSession>();

    private readonly ConcurrentDictionary<IntPtr, CancellationTokenSource> _windowRefreshCts =
        new ConcurrentDictionary<IntPtr, CancellationTokenSource>();

    private readonly object _elementsLock = new object();

    private readonly object _scanLock = new object();
    private CancellationTokenSource? _scanCts;
    private IDisposable? _desktopStructureChangedHandler;

    private class WindowTrackingSession : IDisposable
    {
        public IntPtr Hwnd;
        public readonly List<IDisposable> Disposables = new List<IDisposable>();

        public void Dispose()
        {
            foreach (var disp in Disposables)
            {
                try { disp.Dispose(); } catch { }
            }
            Disposables.Clear();
        }
    }

    public UiTracker(bool measureRefresh, bool measureVisibility, bool enableWindowPolling, bool focusOnly = false)
    {
        _automation = new UIA3Automation();
        _measureRefresh = measureRefresh;
        _measureVisibility = measureVisibility;
        _focusOnly = focusOnly;
        _traverser = new UiTreeTraverser(measureVisibility);

        _automation.RegisterFocusChangedEvent(el =>
        {
            if (el != null)
            {
                try
                {
                    string name = "[unknown]";
                    try { name = el.Name ?? "[none]"; } catch { }

                    string controlType = "[unknown]";
                    try { controlType = el.ControlType.ToString(); } catch { }

                    int processId = 0;
                    try { processId = el.Properties.ProcessId.ValueOrDefault; } catch { }

                    if (TabTipManager.IsTouchKeyboardProcess(processId))
                    {
                        return;
                    }

                    string automationId = "[none]";
                    try { automationId = el.AutomationId ?? "[none]"; } catch { }

                    Console.WriteLine($"[FocusChanged] Element focused: Name='{name}', ControlType={controlType}, ProcessId={processId}, AutomationId='{automationId}'");

                    bool isTypeable = IsTypeableElement(el);
                    if (isTypeable)
                    {
                        System.Threading.Tasks.Task.Run(() =>
                        {
                            Thread.Sleep(500);
                            Console.WriteLine($"[FocusChanged] Typeable element focused. Opening TabTip...");
                            TabTipManager.Open();
                            RefreshKeyboardWindow();
                        });
                    }
                    else
                    {
                        Console.WriteLine($"[FocusChanged] Non-typeable element focused. Closing TabTip...");
                        TabTipManager.Close();
                        RefreshKeyboardWindow();
                    }

                    IntPtr focusedWindowHwnd = GetTopLevelWindowHandle(el);
                    if (focusedWindowHwnd != IntPtr.Zero)
                    {
                        if (!_windowCaches.ContainsKey(focusedWindowHwnd))
                        {
                            TrackWindow(focusedWindowHwnd);
                        }
                        else
                        {
                            RequestWindowRefresh(focusedWindowHwnd);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FocusChanged] Failed to log focused element details: {ex.Message}");
                }
            }
        });

        if (!_focusOnly)
        {
            try
            {
                var desktop = _automation.GetDesktop();
                _desktopStructureChangedHandler = desktop.RegisterStructureChangedEvent(
                    TreeScope.Children,
                    (element, type, runtimeId) =>
                    {
                        if (type == StructureChangeType.ChildAdded || type == StructureChangeType.ChildRemoved ||
                            type == StructureChangeType.ChildrenBulkAdded || type == StructureChangeType.ChildrenBulkRemoved ||
                            type == StructureChangeType.ChildrenInvalidated)
                        {
                            ScheduleFullScan();
                        }
                    }
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UiTracker] Failed to register desktop structure changed event: {ex.Message}");
            }

            Task.Run(() => PerformFullScan());
        }

        if (enableWindowPolling && !_focusOnly)
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    PollWindowTitles();
                    await Task.Delay(1000);
                }
            });
        }
    }

    public void SafeRefresh()
    {
        ScheduleFullScan();
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
                    if (IsWindowVisible(hwnd) && !IsIconic(hwnd))
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

    private void ScheduleFullScan()
    {
        lock (_scanLock)
        {
            try { _scanCts?.Cancel(); _scanCts?.Dispose(); } catch { }
            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;

            Task.Delay(300, token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    PerformFullScan();
                }
            }, token);
        }
    }

    private void PerformFullScan()
    {
        try
        {
            var visibleHwnds = GetVisibleWindowHandles();
            var currentCaches = _windowCaches.Keys.ToList();

            foreach (var hwnd in currentCaches)
            {
                if (!visibleHwnds.Contains(hwnd) && !TabTipManager.IsTouchKeyboardWindow(hwnd))
                {
                    RemoveWindow(hwnd);
                }
            }

            foreach (var hwnd in visibleHwnds)
            {
                if (TabTipManager.IsTouchKeyboardWindow(hwnd))
                {
                    continue;
                }

                if (!_windowCaches.ContainsKey(hwnd))
                {
                    TrackWindow(hwnd);
                }
            }

            RefreshKeyboardWindow();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UiTracker] Full scan failed: {ex.Message}");
        }
    }

    private void TrackWindow(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            {
                return;
            }

            var win = _automation.FromHandle(hwnd);
            if (win == null) return;

            var session = new WindowTrackingSession { Hwnd = hwnd };

            try
            {
                var structHandler = win.RegisterStructureChangedEvent(
                    TreeScope.Subtree,
                    (element, type, runtimeId) =>
                    {
                        RequestWindowRefresh(hwnd);
                    }
                );
                if (structHandler != null)
                {
                    session.Disposables.Add(structHandler);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UiTracker] Failed to register StructureChanged event on window {hwnd.ToInt64():X}: {ex.Message}");
            }

            try
            {
                var propHandler = win.RegisterPropertyChangedEvent(
                    TreeScope.Element,
                    (element, prop, val) =>
                    {
                        RequestWindowRefresh(hwnd);
                    },
                    _automation.PropertyLibrary.Element.BoundingRectangle,
                    _automation.PropertyLibrary.Element.IsEnabled,
                    _automation.PropertyLibrary.Element.IsOffscreen
                );
                if (propHandler != null)
                {
                    session.Disposables.Add(propHandler);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UiTracker] Failed to register PropertyChanged event on window {hwnd.ToInt64():X}: {ex.Message}");
            }

            if (_trackingSessions.TryAdd(hwnd, session))
            {
                Console.WriteLine($"[UiTracker] Started tracking window {hwnd.ToInt64():X} ('{GetWindowTitle(hwnd)}')");
                RefreshWindow(hwnd);
            }
            else
            {
                session.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UiTracker] TrackWindow failed for {hwnd.ToInt64():X}: {ex.Message}");
        }
    }

    private void RequestWindowRefresh(IntPtr hwnd)
    {
        var newCts = new CancellationTokenSource();
        _windowRefreshCts.AddOrUpdate(hwnd,
            newCts,
            (key, oldCts) =>
            {
                try { oldCts.Cancel(); oldCts.Dispose(); } catch { }
                return newCts;
            }
        );

        Task.Delay(150, newCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                RefreshWindow(hwnd);
            }
        }, newCts.Token);
    }

    private void RefreshWindow(IntPtr hwnd)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (hwnd == IntPtr.Zero) return;

            if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
            {
                if (!TabTipManager.IsTouchKeyboardWindow(hwnd))
                {
                    RemoveWindow(hwnd);
                    return;
                }
            }

            AutomationElement? win = null;
            try
            {
                win = _automation.FromHandle(hwnd);
            }
            catch { }

            if (win == null)
            {
                if (!TabTipManager.IsTouchKeyboardWindow(hwnd))
                {
                    RemoveWindow(hwnd);
                }
                return;
            }

            var elements = _traverser.Traverse(win);
            _windowCaches[hwnd] = elements;
            UpdateFlatElementsList();

            sw.Stop();
            if (_measureRefresh)
            {
                _refreshMetrics.RecordTiming(
                    sw.ElapsedMilliseconds,
                    $"window={hwnd.ToInt64():X}, cached={elements.Count}"
                );
            }

            _overlay?.BeginInvoke((Action)(() => _overlay.Invalidate()));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UiTracker] RefreshWindow failed for {hwnd.ToInt64():X}: {ex.Message}");
        }
    }

    private void RefreshKeyboardWindow()
    {
        try
        {
            IntPtr keyboardHwnd = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "IPTip_Main_Window", null!);
            if (keyboardHwnd != IntPtr.Zero)
            {
                RefreshWindow(keyboardHwnd);
            }
            else
            {
                foreach (var key in _windowCaches.Keys)
                {
                    if (TabTipManager.IsTouchKeyboardWindow(key))
                    {
                        RemoveWindow(key);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UiTracker] RefreshKeyboardWindow failed: {ex.Message}");
        }
    }

    private void RemoveWindow(IntPtr hwnd)
    {
        if (_windowCaches.TryRemove(hwnd, out _))
        {
            Console.WriteLine($"[UiTracker] Removed window {hwnd.ToInt64():X} from cache");
            UpdateFlatElementsList();
        }
        if (_trackingSessions.TryRemove(hwnd, out var session))
        {
            session.Dispose();
        }
    }

    private void UpdateFlatElementsList()
    {
        lock (_elementsLock)
        {
            _elements = _windowCaches.Values.SelectMany(x => x).ToList();
        }
    }

    private IntPtr GetTopLevelWindowHandle(AutomationElement el)
    {
        try
        {
            var current = el;
            while (current != null)
            {
                var hwnd = current.Properties.NativeWindowHandle.ValueOrDefault;
                if (hwnd != IntPtr.Zero)
                {
                    var root = GetAncestor(hwnd, GA_ROOT);
                    if (root != IntPtr.Zero)
                    {
                        return root;
                    }
                    return hwnd;
                }
                current = current.Parent;
            }
        }
        catch { }
        return IntPtr.Zero;
    }
    internal static string GetWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "[none]";
        }

        var builder = new StringBuilder(256);
        if (GetWindowText(hwnd, builder, builder.Capacity) > 0)
        {
            return builder.ToString();
        }

        return "[no title]";
    }

    internal static List<IntPtr> GetVisibleWindowHandles()
    {
        var windowHandles = new List<IntPtr>();
        var touchKeyboardPids = new HashSet<uint>();

        EnumWindows((hwnd, lParam) =>
        {
            try
            {
                if (IsWindowVisible(hwnd) && !IsIconic(hwnd))
                {
                    if (Native.GetWindowRect(hwnd, out var rect))
                    {
                        int width = rect.Right - rect.Left;
                        int height = rect.Bottom - rect.Top;
                        if (width > 0 && height > 0)
                        {
                            windowHandles.Add(hwnd);

                            if (TabTipManager.IsTouchKeyboardWindow(hwnd))
                            {
                                GetWindowThreadProcessId(hwnd, out uint pid);
                                if (pid != 0)
                                {
                                    touchKeyboardPids.Add(pid);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        // Find all CoreWindows belonging to the touch keyboard processes and add them
        foreach (var pid in touchKeyboardPids)
        {
            IntPtr childHwnd = IntPtr.Zero;
            while (true)
            {
                childHwnd = FindWindowEx(IntPtr.Zero, childHwnd, "Windows.UI.Core.CoreWindow", null!);
                if (childHwnd == IntPtr.Zero)
                {
                    break;
                }

                GetWindowThreadProcessId(childHwnd, out uint childPid);
                if (childPid == pid)
                {
                    if (!windowHandles.Contains(childHwnd))
                    {
                        windowHandles.Add(childHwnd);
                    }
                }
            }
        }

        return windowHandles;
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



    private static bool IsTypeableElement(AutomationElement el)
    {
        if (el == null) return false;
        try
        {
            var controlType = el.ControlType;
            if (controlType == ControlType.Edit)
            {
                return true;
            }
            if (controlType == ControlType.ComboBox)
            {
                return true;
            }

            // if (el.Patterns.Value.IsSupported)
            // {
            //     try
            //     {
            //         if (!el.Patterns.Value.Pattern.IsReadOnly.Value)
            //         {
            //             return true;
            //         }
            //     }
            //     catch { }
            // }
        }
        catch { }
        return false;
    }



}
