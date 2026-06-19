using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class TabTipManager
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

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

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    private const uint GA_ROOT = 2;



    [ComImport, Guid("4ce576fa-83dc-4F88-951c-9d0782b4e376")]
    private class UIHostNoLaunch { }

    [ComImport, Guid("37c994e7-432b-4834-a2f7-dce1f13b834b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation { void Toggle(IntPtr hwnd); }

    private static DateTime _lastOpenTime = DateTime.MinValue;

    public static bool IsTouchKeyboardWindow(IntPtr hwnd)
    {
        try
        {
            var className = new StringBuilder(256);
            if (GetClassName(hwnd, className, className.Capacity) > 0)
            {
                var cls = className.ToString();
                if (cls.Equals("IPTip_TextBox_Window", StringComparison.OrdinalIgnoreCase) ||
                    cls.Equals("IPTip_Main_Window", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var title = UiTracker.GetWindowTitle(hwnd);
            if (title.Equals("Microsoft Text Input Application", StringComparison.OrdinalIgnoreCase) ||
                title.Equals("Windows Input Experience", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch { }
        return false;
    }

    public static bool IsTouchKeyboardProcess(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using (var proc = Process.GetProcessById(pid))
            {
                string procName = proc.ProcessName;
                if (procName.Equals("TabTip", StringComparison.OrdinalIgnoreCase) ||
                    procName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    internal static bool IsTouchKeyboardVisible()
    {
        bool visible = false;
        EnumWindows((hwnd, lParam) =>
        {
            if (IsTouchKeyboardWindow(hwnd))
            {
                if (GetWindowRect(hwnd, out RECT rect))
                {
                    int width = rect.Right - rect.Left;
                    int height = rect.Bottom - rect.Top;
                    if (width > 0 && height > 0)
                    {
                        POINT p = new POINT { X = rect.Left + width / 2, Y = rect.Top + height / 2 };
                        IntPtr hwndAtPoint = WindowFromPoint(p);
                        string nameAtPoint = UiTracker.GetWindowTitle(hwndAtPoint);
                        Console.WriteLine($"[TabTipManager] Keyboard HWND: {hwnd.ToInt64():X}. WindowFromPoint at ({p.X}, {p.Y}) returned HWND: {hwndAtPoint.ToInt64():X}, Title: '{nameAtPoint}'");

                        IntPtr rootAtPoint = GetAncestor(hwndAtPoint, GA_ROOT);

                        if (hwndAtPoint == hwnd || rootAtPoint == hwnd || IsTouchKeyboardWindow(hwndAtPoint) || IsTouchKeyboardWindow(rootAtPoint))
                        {
                            visible = true;
                            return false; // Stop enumeration
                        }
                    }
                }
            }
            return true;
        }, IntPtr.Zero);
        return visible;
    }

    public static void Open()
    {
        // Debounce/cooldown: prevent double-toggling in quick succession (1000ms threshold)
        if ((DateTime.UtcNow - _lastOpenTime).TotalMilliseconds < 1000)
        {
            return;
        }

        // 1. Make sure TabTip.exe process is running first
        try
        {
            var processes = Process.GetProcessesByName("TabTip");
            if (processes.Length == 0)
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), @"microsoft shared\ink\TabTip.exe");
                if (!File.Exists(path))
                {
                    path = @"C:\Program Files\Common Files\microsoft shared\ink\TabTip.exe";
                }

                if (File.Exists(path))
                {
                    Console.WriteLine("[TabTip] Process not running. Pre-launching TabTip.exe...");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                    // Wait a bit for it to open and register COM classes
                    System.Threading.Thread.Sleep(500);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TabTip] Error checking/pre-launching TabTip.exe process: {ex.Message}");
        }

        for (int i = 0; i < 5; i++)
        {
            // If the keyboard is already visible, do not toggle it (which would hide it)
            if (IsTouchKeyboardVisible())
            {
                Console.WriteLine($"[TabTip] Open loop: Keyboard is now visible (attempt {i}).");
                return;
            }

            Console.WriteLine($"[TabTip] Open loop: Keyboard not visible. Toggling... (attempt {i + 1}/5)");

            // Record the attempt time before triggering the toggle
            _lastOpenTime = DateTime.UtcNow;

            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                hwnd = GetDesktopWindow();
            }

            // 3. Try the ITipInvocation COM interface
            try
            {
                var uiHost = new UIHostNoLaunch();
                var tip = (ITipInvocation)uiHost;
                tip.Toggle(hwnd);
                Marshal.ReleaseComObject(uiHost);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TabTip] COM invocation failed: {ex.Message}. Falling back to Process.Start...");
                // 4. Fallback: Kill and restart TabTip.exe to force it to show up
                try
                {
                    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), @"microsoft shared\ink\TabTip.exe");
                    if (!File.Exists(path))
                    {
                        path = @"C:\Program Files\Common Files\microsoft shared\ink\TabTip.exe";
                    }

                    if (File.Exists(path))
                    {
                        Console.WriteLine("[TabTip] COM failed. Restarting TabTip.exe to force show...");
                        foreach (var proc in Process.GetProcessesByName("TabTip"))
                        {
                            try { proc.Kill(); } catch { }
                        }
                        System.Threading.Thread.Sleep(100);
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = path,
                            UseShellExecute = true
                        });
                    }
                }
                catch (Exception ex2)
                {
                    Console.WriteLine($"[TabTip] Failed to start TabTip process: {ex2.Message}");
                }
            }

            // Wait a bit before checking/toggling again
            System.Threading.Thread.Sleep(200);
        }
    }

    public static void Close()
    {
        // Debounce/cooldown: prevent double-toggling in quick succession (1000ms threshold)
        if ((DateTime.UtcNow - _lastOpenTime).TotalMilliseconds < 1000)
        {
            return;
        }

        for (int i = 0; i < 5; i++)
        {
            // If the keyboard is already hidden, do not toggle it (which would open it)
            if (!IsTouchKeyboardVisible())
            {
                Console.WriteLine($"[TabTip] Close loop: Keyboard is now hidden (attempt {i}).");
                return;
            }

            Console.WriteLine($"[TabTip] Close loop: Keyboard is still visible. Toggling... (attempt {i + 1}/5)");

            // Record the attempt time before triggering the toggle
            _lastOpenTime = DateTime.UtcNow;

            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                hwnd = GetDesktopWindow();
            }

            // Try the ITipInvocation COM interface
            try
            {
                var uiHost = new UIHostNoLaunch();
                var tip = (ITipInvocation)uiHost;
                tip.Toggle(hwnd);
                Marshal.ReleaseComObject(uiHost);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TabTip] COM close invocation failed: {ex.Message}. Falling back to killing process...");
                // Fallback: Kill TabTip.exe process to force hide
                try
                {
                    foreach (var proc in Process.GetProcessesByName("TabTip"))
                    {
                        try { proc.Kill(); } catch { }
                    }
                }
                catch (Exception ex2)
                {
                    Console.WriteLine($"[TabTip] Failed to kill TabTip process: {ex2.Message}");
                }
            }

            // Wait a bit before checking/toggling again
            System.Threading.Thread.Sleep(200);
        }
    }
}
