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
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    private const uint GA_ROOT = 2;
    private const uint GA_ROOTOWNER = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    private UIA3Automation _automation;
    private volatile List<CachedElement> _elements = new List<CachedElement>();
    private OverlayForm _overlay;
    private volatile bool _isRefreshing = false;
    private readonly CustomMetrics _refreshMetrics = new CustomMetrics("Refresh");
    private readonly CustomMetrics _visibilityMetrics = new CustomMetrics("IsActuallyVisible");
    private readonly bool _measureRefresh;
    private readonly bool _measureVisibility;
    private const int POLLING_MS = 2000;


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
                await Task.Delay(POLLING_MS);
            }
        });
    }

    public void SafeRefresh()
    {
        TriggerRefresh();
    }

    private void LogAllMenuItems()
    {
        try
        {
            var desktop = _automation.GetDesktop();
            var windows = desktop.FindAllChildren()
                .Where(w => !w.Properties.IsOffscreen.ValueOrDefault)
                .ToList();

            Console.WriteLine();
            Console.WriteLine("========== MENU ITEMS ==========");

            int totalMenuItems = 0;
            foreach (var win in windows)
            {
                var windowHwnd = win.Properties.NativeWindowHandle.ValueOrDefault;
                var menuItems = win.FindAllDescendants(cf =>
                    cf.ByControlType(ControlType.MenuItem));

                foreach (var item in menuItems)
                {
                    try
                    {
                        totalMenuItems++;
                        var rect = item.BoundingRectangle;
                        var itemHwnd = item.Properties.NativeWindowHandle.ValueOrDefault;
                        var isOffscreen = item.Properties.IsOffscreen.ValueOrDefault;

                        Console.WriteLine(
                            $"MenuItem: '{item.Name}' | " +
                            $"WindowHWND=0x{windowHwnd.ToInt64():X} | " +
                            $"ItemHWND=0x{itemHwnd.ToInt64():X} | " +
                            $"Offscreen={isOffscreen} | " +
                            $"Rect=[L={rect.Left},T={rect.Top},W={rect.Width},H={rect.Height}]");

                        var parent = item.Parent;
                        while (parent != null)
                        {
                            try
                            {
                                var parentHwnd = parent.Properties.NativeWindowHandle.ValueOrDefault;
                                Console.WriteLine(
                                    $"   -> {parent.ControlType} '{parent.Name}' | " +
                                    $"HWND=0x{parentHwnd.ToInt64():X}");
                                parent = parent.Parent;
                            }
                            catch
                            {
                                break;
                            }
                        }

                        Console.WriteLine("--------------------------------");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed MenuItem: {ex.Message}");
                    }
                }
            }

            Console.WriteLine($"Total MenuItems found: {totalMenuItems}");
            Console.WriteLine("================================");
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LogAllMenuItems failed: {ex.Message}");
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
        var desktop = _automation.GetDesktop();
        // LogAllMenuItems();

        var windows = desktop.FindAllChildren()
            .Where(w => !w.Properties.IsOffscreen.ValueOrDefault)
            .ToList();
            windowCount = windows.Count;

            var newElements = new ConcurrentBag<CachedElement>();

            Parallel.ForEach(windows, win =>
        {
            try
            {
                var winRect = win.BoundingRectangle;
                if (winRect.IsEmpty || winRect.Width <= 0 || winRect.Height <= 0)
                        return;

                var windowHwnd = win.Properties.NativeWindowHandle.ValueOrDefault;
                var elements = win.FindAllDescendants(cf => 
                    cf.ByControlType(ControlType.Button)
                    .Or(cf.ByControlType(ControlType.CheckBox))
                    .Or(cf.ByControlType(ControlType.ComboBox))
                    .Or(cf.ByControlType(ControlType.Edit))
                    .Or(cf.ByControlType(ControlType.Hyperlink))
                    .Or(cf.ByControlType(ControlType.ListItem))
                    // .Or(cf.ByControlType(ControlType.Menu))
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

                        var ownerHwnd = el.Properties.NativeWindowHandle.ValueOrDefault;
                        if (ownerHwnd == IntPtr.Zero)
                            ownerHwnd = windowHwnd;

                        newElements.Add(new CachedElement
                        {
                            Element = el,
                            Rect = rect,
                            Hwnd = ownerHwnd
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

    private bool IsActuallyVisible(CachedElement cached)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var el = cached.Element;
            var r = cached.Rect;

            if (r.IsEmpty)
                return false;

            var name = el.Properties.Name.ValueOrDefault ?? "[No Name]";
            var isMenuItem = el.ControlType == ControlType.MenuItem;

            var elHwnd = cached.Hwnd;

            IntPtr lastHwnd = IntPtr.Zero;
            IntPtr lastRoot = IntPtr.Zero;

            var points = new[]
            {
                new POINT { X = (int)(r.Left + r.Width / 2), Y = (int)(r.Top + r.Height / 2) },
                new POINT { X = (int)(r.Left + 2), Y = (int)(r.Top + 2) },
                new POINT { X = (int)(r.Right - 2), Y = (int)(r.Bottom - 2) }
            };

            var elRoot = elHwnd != IntPtr.Zero ? GetAncestor(elHwnd, GA_ROOT) : IntPtr.Zero;

            foreach (var p in points)
            {
                IntPtr hwnd = WindowFromPoint(p);
                if (hwnd == IntPtr.Zero)
                    continue;

                IntPtr root = GetAncestor(hwnd, GA_ROOT);

                lastHwnd = hwnd;
                lastRoot = root;

                bool isVisible = false;
                string matchReason = null;

                if (elHwnd != IntPtr.Zero)
                {
                    if (hwnd == elHwnd)
                    {
                        isVisible = true;
                        matchReason = "hwnd";
                    }
                    else if (root == elHwnd)
                    {
                        isVisible = true;
                        matchReason = "root==elHwnd";
                    }
                    else if (elRoot != IntPtr.Zero && root == elRoot)
                    {
                        isVisible = true;
                        matchReason = "root==elRoot";
                    }
                    else if (isMenuItem && IsMenuPopupVisible(root, elHwnd, elRoot))
                    {
                        isVisible = true;
                        matchReason = "menu-owner";
                    }
                }

                if (isVisible)
                {
                    var elWindowTitle = GetWindowTitle(elHwnd);
                    var windowTitle = GetWindowTitle(hwnd);
                    var rootTitle = GetWindowTitle(root);

                    var label = isMenuItem
                        ? $"MenuItem='{name}'"
                        : $"ElementType={el.ControlType} Name='{name}'";

                    Console.WriteLine(
                        $"IsActuallyVisible: {label} | " +
                        $"ElHwnd=0x{elHwnd.ToInt64():X} ({elWindowTitle}) | " +
                        $"Hwnd=0x{hwnd.ToInt64():X} ({windowTitle}) | " +
                        $"ElRoot=0x{elRoot.ToInt64():X} ({windowTitle}) | " +
                        $"Root=0x{root.ToInt64():X} ({rootTitle}) | " +
                        $"Match={matchReason} | " +
                        $"Result=true");

                    sw.Stop();
                    return true;
                }
            }

            if (isMenuItem)
            {
                var elWindowTitle = GetWindowTitle(elHwnd);
                var lastWindowTitle = GetWindowTitle(lastHwnd);
                var lastRootTitle = GetWindowTitle(lastRoot);
                var elRootTitle = elRoot != IntPtr.Zero ? GetWindowTitle(elRoot) : "[none]";

                Console.WriteLine(
                    $"IsActuallyVisible: MenuItem='{name}' | " +
                    $"ElHwnd=0x{elHwnd.ToInt64():X} ({elWindowTitle}) | " +
                    $"ElRoot=0x{elRoot.ToInt64():X} ({elRootTitle}) | " +
                    $"LastHwnd=0x{lastHwnd.ToInt64():X} ({lastWindowTitle}) | " +
                    $"LastRoot=0x{lastRoot.ToInt64():X} ({lastRootTitle}) | " +
                    $"Result=false\n");
            }

            sw.Stop();
            return false;
        }
        catch
        {
            sw.Stop();
            return false;
        }
    }

    private bool IsMenuPopupVisible(IntPtr pointRoot, IntPtr elHwnd, IntPtr elRoot)
    {
        try
        {
            if (pointRoot == IntPtr.Zero || elHwnd == IntPtr.Zero)
            {
                Console.WriteLine($"IsMenuPopupVisible: invalid args pointRoot=0x{pointRoot.ToInt64():X} elHwnd=0x{elHwnd.ToInt64():X}");
                return false;
            }

            uint threadId = GetWindowThreadProcessId(pointRoot, out _);
            var guiInfo = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(threadId, ref guiInfo))
            {
                Console.WriteLine($"IsMenuPopupVisible: GetGUIThreadInfo failed threadId={threadId} pointRoot=0x{pointRoot.ToInt64():X}");
                return false;
            }

            var targetRoot = elRoot != IntPtr.Zero ? elRoot : GetAncestor(elHwnd, GA_ROOT);
            if (targetRoot == IntPtr.Zero)
            {
                Console.WriteLine($"IsMenuPopupVisible: targetRoot is zero elHwnd=0x{elHwnd.ToInt64():X} elRoot=0x{elRoot.ToInt64():X}");
                return false;
            }

            if (guiInfo.hwndMenuOwner != IntPtr.Zero)
            {
                var menuOwnerRoot = GetAncestor(guiInfo.hwndMenuOwner, GA_ROOT);
                if (menuOwnerRoot != IntPtr.Zero)
                {
                    Console.WriteLine($"IsMenuPopupVisible: menuOwnerRoot=0x{menuOwnerRoot.ToInt64():X} targetRoot=0x{targetRoot.ToInt64():X} hwndMenuOwner=0x{guiInfo.hwndMenuOwner.ToInt64():X}");
                    if (menuOwnerRoot == targetRoot)
                    {
                        return true;
                    }
                }
                else
                {
                    Console.WriteLine($"IsMenuPopupVisible: menuOwnerRoot is zero for hwndMenuOwner=0x{guiInfo.hwndMenuOwner.ToInt64():X}");
                }
            }
            else
            {
                Console.WriteLine($"IsMenuPopupVisible: no menu owner hwnd for threadId={threadId} pointRoot=0x{pointRoot.ToInt64():X}");
            }

            var pointRootOwner = GetAncestor(pointRoot, GA_ROOTOWNER);
            var targetRootOwner = GetAncestor(targetRoot, GA_ROOTOWNER);
            Console.WriteLine($"IsMenuPopupVisible: fallback pointRootOwner=0x{pointRootOwner.ToInt64():X} targetRootOwner=0x{targetRootOwner.ToInt64():X}");

            if (pointRootOwner != IntPtr.Zero && pointRootOwner == targetRoot)
            {
                Console.WriteLine($"IsMenuPopupVisible: fallback match pointRootOwner==targetRoot (0x{pointRootOwner.ToInt64():X})");
                return true;
            }

            if (pointRootOwner != IntPtr.Zero && targetRootOwner != IntPtr.Zero && pointRootOwner == targetRootOwner)
            {
                Console.WriteLine($"IsMenuPopupVisible: fallback match root owners pointRootOwner==targetRootOwner (0x{pointRootOwner.ToInt64():X})");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"IsMenuPopupVisible: exception {ex.GetType().Name}: {ex.Message}");
            return false;
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

    public List<CachedElement> GetClosest6()
    {
        var mouse = Cursor.Position;

        return _elements
            .OrderBy(e => DistanceToRect(mouse, e.Rect))
            .ThenBy(e => e.Rect.Width * e.Rect.Height) // 3. Fallback size-sort allows innermost items to top the list when overlaps happen
            .Where(e => IsActuallyVisible(e)) // 🔥 Lazy evaluation: only hit-tests the closest items!
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
