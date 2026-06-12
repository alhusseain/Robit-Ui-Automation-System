using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

internal class UiTreeTraverser
{
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWndChild);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out bool pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    private const int DWMWA_CLOAKED = 14;
    private const uint GA_ROOT = 2;
    private const uint GA_ROOTOWNER = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

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

    // List of control types we want to collect
    private static readonly HashSet<ControlType> TargetControlTypes = new HashSet<ControlType>
    {
        ControlType.Button,
        ControlType.CheckBox,
        ControlType.ComboBox,
        ControlType.Hyperlink,
        ControlType.ListItem,
        ControlType.MenuItem,
        ControlType.RadioButton,
        ControlType.Slider,
        ControlType.TabItem,
        ControlType.TreeItem,
        ControlType.DataItem
    };

    // List of control types that are definitely leaf nodes and don't need their children traversed
    private static readonly HashSet<ControlType> LeafControlTypes = new HashSet<ControlType>
    {
        ControlType.Button,
        ControlType.CheckBox,
        ControlType.RadioButton,
        ControlType.Slider,
        ControlType.Hyperlink,
        ControlType.Text,
        ControlType.Image,
        ControlType.Edit,
        ControlType.ProgressBar,
        ControlType.Thumb,
        ControlType.ToolTip,
        ControlType.ScrollBar,
        ControlType.Separator,
        ControlType.Spinner,
        ControlType.SplitButton,
        ControlType.StatusBar,
        ControlType.Header,
        ControlType.HeaderItem
    };

    private readonly CustomMetrics _visibilityMetrics = new CustomMetrics("IsActuallyVisible");
    private readonly bool _measureVisibility;

    public UiTreeTraverser(bool measureVisibility)
    {
        _measureVisibility = measureVisibility;
    }

    private bool IsExplorerOrFileDialog(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        // 1. Check window class name
        var className = new StringBuilder(256);
        if (GetClassName(hwnd, className, className.Capacity) > 0)
        {
            var cls = className.ToString();
            if (cls.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase) || 
                cls.Equals("#32770", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 2. Check process name as fallback
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0)
            {
                using (var proc = Process.GetProcessById((int)pid))
                {
                    return string.Equals(proc.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
            // Ignore
        }

        return false;
    }

    /// <summary>
    /// Traverses the subtree of a window and retrieves visible target elements.
    /// </summary>
    public List<CachedElement> Traverse(AutomationElement rootElement)
    {
        var result = new List<CachedElement>();
        try
        {
            bool isExplorer = false;
            var hwnd = rootElement.Properties.NativeWindowHandle.ValueOrDefault;
            if (hwnd != IntPtr.Zero)
            {
                // Prune if window is minimized, hidden, cloaked, or completely covered
                if (!IsWindowVisibleSimple(hwnd) || IsWindowCloaked(hwnd) || IsWindowCovered(hwnd))
                {
                    return result;
                }

                isExplorer = IsExplorerOrFileDialog(hwnd);
            }

            var cacheRequest = new CacheRequest();
            cacheRequest.TreeScope = TreeScope.Subtree;

            var propertyLibrary = rootElement.Automation.PropertyLibrary;
            cacheRequest.Add(propertyLibrary.Element.Name);
            cacheRequest.Add(propertyLibrary.Element.ControlType);
            cacheRequest.Add(propertyLibrary.Element.NativeWindowHandle);
            cacheRequest.Add(propertyLibrary.Element.AutomationId);
            cacheRequest.Add(propertyLibrary.Element.IsEnabled);
            cacheRequest.Add(propertyLibrary.Element.IsKeyboardFocusable);
            cacheRequest.Add(propertyLibrary.Element.BoundingRectangle);
            cacheRequest.Add(propertyLibrary.Element.IsOffscreen);

            using (cacheRequest.Activate())
            {
                var cachedRoot = rootElement.Automation.FromHandle(hwnd);
                if (cachedRoot != null)
                {
                    var children = cachedRoot.CachedChildren;
                    foreach (var child in children)
                    {
                        TraverseRecursive(child, hwnd, hwnd, 1, result, isExplorer, false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Traversal failed at root: {ex.Message}");
        }
        return result;
    }

    private void TraverseRecursive(
        AutomationElement element, 
        IntPtr windowHwnd, 
        IntPtr parentHwnd, 
        int depth, 
        List<CachedElement> result,
        bool isExplorer,
        bool insideListItem)
    {
        if (depth > 50) return; // Prevent stack overflow on extremely deep trees

        try
        {
            var controlType = element.ControlType;

            // Check if this element has its own native window handle (HWND)
            // If the window/control itself is hidden or minimized, prune its subtree.
            // To optimize, we only query HWND for container-like controls to avoid cross-process overhead on simple elements.
            bool isContainer = controlType == ControlType.Window || 
                               controlType == ControlType.Pane || 
                               controlType == ControlType.Group || 
                               controlType == ControlType.TabItem || 
                               controlType == ControlType.Document ||
                               controlType == ControlType.List ||
                               controlType == ControlType.DataGrid ||
                               controlType == ControlType.Tree ||
                               controlType == ControlType.Custom;

            var elementHwnd = IntPtr.Zero;
            if (isContainer)
            {
                elementHwnd = element.Properties.NativeWindowHandle.ValueOrDefault;
                if (elementHwnd != IntPtr.Zero)
                {
                    if (!IsWindowVisible(elementHwnd) || IsIconic(elementHwnd))
                    {
                        return; // Prune hidden/minimized child windows (e.g. inactive tabs)
                    }
                }
            }

            // 2. Collect if it matches target controls
            if (TargetControlTypes.Contains(controlType))
            {
                // Special filter for Edit controls to avoid clashing with non-interactive text elements
                bool isInteractiveEdit = true;
                if (controlType == ControlType.Edit)
                {
                    var autoId = element.Properties.AutomationId.ValueOrDefault;
                    bool isSystemField = autoId != null && autoId.StartsWith("System.", StringComparison.OrdinalIgnoreCase);

                    isInteractiveEdit = !isSystemField && 
                                        !(isExplorer && insideListItem) &&
                                        element.Properties.IsEnabled.ValueOrDefault && 
                                        element.Properties.IsKeyboardFocusable.ValueOrDefault;
                }

                if (isInteractiveEdit)
                {
                    var rect = element.BoundingRectangle;
                    if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                    {
                        var ownerHwnd = elementHwnd != IntPtr.Zero ? elementHwnd : parentHwnd;
                        var name = element.Properties.Name.ValueOrDefault ?? "[No Name]";

                        GetWindowThreadProcessId(ownerHwnd, out uint pid);
                        var cached = new CachedElement
                        {
                            Element = element,
                            Rect = rect,
                            Hwnd = ownerHwnd,
                            Name = name,
                            ControlType = controlType,
                            ProcessId = pid,
                            IsOffscreen = element.Properties.IsOffscreen.ValueOrDefault
                        };

                        if (IsActuallyVisible(cached))
                        {
                            result.Add(cached);
                        }
                    }
                }
            }

            // 3. Prune child traversal if it's a leaf node type
            if (LeafControlTypes.Contains(controlType))
            {
                return;
            }

            // 4. Traverse children
            var children = element.CachedChildren;
            bool isListOrDataItem = controlType == ControlType.ListItem || 
                                    controlType == ControlType.DataItem || 
                                    controlType == ControlType.TreeItem;

            bool nextInsideListItem = insideListItem || isListOrDataItem;

            foreach (var child in children)
            {
                var childControlType = child.ControlType;
                bool childIsContainer = childControlType == ControlType.Window || 
                                        childControlType == ControlType.Pane || 
                                        childControlType == ControlType.Group || 
                                        childControlType == ControlType.TabItem || 
                                        childControlType == ControlType.Document ||
                                        childControlType == ControlType.List ||
                                        childControlType == ControlType.DataGrid ||
                                        childControlType == ControlType.Tree ||
                                        childControlType == ControlType.Custom;

                var childHwnd = IntPtr.Zero;
                if (childIsContainer)
                {
                    childHwnd = child.Properties.NativeWindowHandle.ValueOrDefault;
                }

                if (childHwnd == IntPtr.Zero)
                    childHwnd = parentHwnd;

                TraverseRecursive(child, windowHwnd, childHwnd, depth + 1, result, isExplorer, nextInsideListItem);
            }
        }
        catch
        {
            // Silent catch
        }
    }

    /// <summary>
    /// Performs a simple check if the window handle is visible and not minimized.
    /// </summary>
    private bool IsWindowVisibleSimple(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (IsIconic(hwnd)) return false;
        if (!IsWindowVisible(hwnd)) return false;
        return true;
    }

    /// <summary>
    /// Checks if the window is cloaked (e.g., in a suspended state or on a different virtual desktop).
    /// </summary>
    private bool IsWindowCloaked(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            int hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out bool isCloaked, Marshal.SizeOf<bool>());
            if (hr == 0) // S_OK
            {
                return isCloaked;
            }
        }
        catch
        {
            // If DwmGetWindowAttribute fails or is unsupported
        }
        return false;
    }

    /// <summary>
    /// Checks if a window is completely covered by another window.
    /// </summary>
    private bool IsWindowCovered(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (!GetWindowRect(hwnd, out RECT r)) return false;

        int width = r.Right - r.Left;
        int height = r.Bottom - r.Top;

        if (width <= 0 || height <= 0) return true;

        // Check center and 4 corners (slightly offset inwards to avoid border hit-testing edge cases)
        var points = new[]
        {
            new POINT { X = r.Left + width / 2, Y = r.Top + height / 2 },
            new POINT { X = r.Left + 5, Y = r.Top + 5 },
            new POINT { X = r.Right - 5, Y = r.Bottom - 5 },
            new POINT { X = r.Right - 5, Y = r.Top + 5 },
            new POINT { X = r.Left + 5, Y = r.Bottom - 5 }
        };

        var windowRoot = GetAncestor(hwnd, GA_ROOT);

        foreach (var p in points)
        {
            IntPtr hwndAtPoint = WindowFromPoint(p);
            if (hwndAtPoint == IntPtr.Zero)
                continue;

            if (hwndAtPoint == hwnd)
                return false; // Point belongs to window itself

            IntPtr rootAtPoint = GetAncestor(hwndAtPoint, GA_ROOT);
            if (rootAtPoint == hwnd || (windowRoot != IntPtr.Zero && rootAtPoint == windowRoot))
            {
                return false; // Point belongs to window or one of its child windows/popups
            }
        }

        return true; // None of the points belong to the window -> completely covered
    }

    /// <summary>
    /// Checks if the cached element is actually visible via window-from-point testing.
    /// </summary>
    public bool IsActuallyVisible(CachedElement cached)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var el = cached.Element;
            var r = cached.Rect;

            if (r.IsEmpty)
                return false;

            if (cached.IsOffscreen)
                return false;

            var name = cached.Name;
            var controlType = cached.ControlType;
            var isMenuItem = controlType == ControlType.MenuItem || 
                             controlType == ControlType.ListItem || 
                             controlType == ControlType.Button;

            var elHwnd = cached.Hwnd;

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

                bool isVisible = false;

                if (elHwnd != IntPtr.Zero)
                {
                    IntPtr pointRoot = GetAncestor(hwnd, GA_ROOT);
                    if (hwnd == elHwnd)
                    {
                        isVisible = true;
                    }
                    else if (IsChild(hwnd, elHwnd))
                    {
                        isVisible = true;
                    }
                    else if (IsChild(elHwnd, hwnd))
                    {
                        // hwnd is a child/descendant of elHwnd.
                        // If elHwnd is the main/root window, a child window (like Chrome Legacy Window)
                        // might be overlaying/covering the parent window's browser UI elements.
                        // We check the class name of the window at the point to see if it is a viewport.
                        bool isMainRootWindow = GetAncestor(elHwnd, GA_ROOTOWNER) == elHwnd;
                        if (isMainRootWindow)
                        {
                            var className = new StringBuilder(256);
                            if (GetClassName(hwnd, className, className.Capacity) > 0)
                            {
                                var cls = className.ToString();
                                bool isViewport = cls.IndexOf("RenderWidget", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 cls.IndexOf("D3D", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 cls.IndexOf("Mozilla", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 cls.IndexOf("Gecko", StringComparison.OrdinalIgnoreCase) >= 0;
                                if (!isViewport)
                                {
                                    isVisible = true;
                                }
                            }
                            else
                            {
                                isVisible = true;
                            }
                        }
                        else
                        {
                            isVisible = true;
                        }
                    }
                    else if (isMenuItem)
                    {
                        IntPtr root = GetAncestor(hwnd, GA_ROOT);
                        if (IsMenuPopupVisible(root, elHwnd, elRoot))
                        {
                            isVisible = true;
                        }
                    }
                }

                if (isVisible)
                {
                    sw.Stop();
                    if (_measureVisibility)
                    {
                        _visibilityMetrics.RecordTiming(
                            sw.ElapsedMilliseconds,
                            $"name='{name}', type={controlType}, result=true"
                        );
                    }
                    return true;
                }
            }

            sw.Stop();
            if (_measureVisibility)
            {
                _visibilityMetrics.RecordTiming(
                    sw.ElapsedMilliseconds,
                    $"name='{name}', type={controlType}, result=false"
                );
            }
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
                return false;
            }

            uint threadId = GetWindowThreadProcessId(pointRoot, out _);
            var guiInfo = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(threadId, ref guiInfo))
            {
                return false;
            }

            var targetRoot = elRoot != IntPtr.Zero ? elRoot : GetAncestor(elHwnd, GA_ROOT);
            if (targetRoot == IntPtr.Zero)
            {
                return false;
            }

            if (guiInfo.hwndMenuOwner != IntPtr.Zero)
            {
                var menuOwnerRoot = GetAncestor(guiInfo.hwndMenuOwner, GA_ROOT);
                if (menuOwnerRoot != IntPtr.Zero)
                {
                    if (menuOwnerRoot == targetRoot)
                    {
                        return true;
                    }
                }
            }

            var pointRootOwner = GetAncestor(pointRoot, GA_ROOTOWNER);
            var targetRootOwner = GetAncestor(targetRoot, GA_ROOTOWNER);

            if (pointRootOwner != IntPtr.Zero && pointRootOwner == targetRoot)
            {
                return true;
            }

            if (pointRootOwner != IntPtr.Zero && targetRootOwner != IntPtr.Zero && pointRootOwner == targetRootOwner)
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
