using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Definitions;
using Gma.System.MouseKeyHook;
using Newtonsoft.Json;

static class Program
{
    static UiTracker tracker;
    static OverlayForm overlay;
    static IKeyboardMouseEvents globalHook;
    static AppSettings settings;

    static CustomMetrics getClosestMetrics;

    static List<CachedElement> lastClosest = new List<CachedElement>();
    static bool suppressTestMouseClick;

    [STAThread]
    static void Main()
    {
        settings = AppSettings.Load();

        if (settings.InputMode == "io")
        {
            Console.SetOut(new FilteredTextWriter(Console.Out));
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        tracker = new UiTracker(settings.MeasureRefresh, settings.MeasureVisibility, settings.InputMode == "test");
        overlay = new OverlayForm();

        tracker.AttachOverlay(overlay);
        overlay.Show();

        getClosestMetrics = new CustomMetrics("GetClosest");

        Console.WriteLine($"Input mode: {settings.InputMode}");
        Console.WriteLine($"GetClosest timing enabled: {settings.MeasureGetClosest}");
        Console.WriteLine($"Refresh tracing enabled: {settings.MeasureRefresh}");
        Console.WriteLine($"Visibility tracing enabled: {settings.MeasureVisibility}");

        if (settings.InputMode == "test")
        {
            SetupTestMode();
        }
        else
        {
            Task.Run(StartStandardIOServer);
        }

        Application.Run();
    }

    static void SetupTestMode()
    {
        tracker.SafeRefresh();
        globalHook = Hook.GlobalEvents();

        globalHook.MouseDownExt += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || suppressTestMouseClick)
            {
                Console.WriteLine("debounced bozo");
                return;
            }

            ShowClosest();
        };

        globalHook.KeyDown += (_, e) =>
        {
            int index = KeyToIndex(e.KeyCode);
            if (index >= 0)
            {
                InvokeByIndex(index);
                return;
            }

            if (e.KeyCode == Keys.Escape)
            {
                ClearOverlay();
            }
        };

        Application.ApplicationExit += (_, __) => globalHook?.Dispose();
        Console.WriteLine("Test mode active: left click shows closest, 1-6 invokes, Esc clears");
    }

    static void StartStandardIOServer()
    {
        tracker.SafeRefresh();
        Console.WriteLine("Standard I/O server running. Ready for commands.");

        while (true)
        {
            try
            {
                string message = Console.ReadLine();
                if (string.IsNullOrEmpty(message)) continue;

                // Log the receipt if you want, but for I/O bridge, be careful not to spam Unity unless needed.
                Console.WriteLine("Received: " + message);

                dynamic msg = JsonConvert.DeserializeObject(message);
                string type = msg.type;

                if (type == "getClosest")
                {
                    overlay.Invoke((Action)(() =>
                    {
                        ShowClosestCore();
                    }));
                }
                else if (type == "invokeIndex")
                {
                    int index = (int)msg.index;
                    InvokeByIndex(index);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error processing command: " + ex.Message);
            }
        }
    }

    static void ShowClosest()
    {
        if (settings.InputMode == "test")
        {
            suppressTestMouseClick = true;
        }
        overlay.BeginInvoke((Action)(() =>
        {
            ShowClosestCore();
        }));
    }

    static bool IsOffScreen(Rectangle rect)
    {
        bool intersectsAnyScreen = false;
        foreach (var screen in Screen.AllScreens)
        {
            if (screen.Bounds.IntersectsWith(rect))
            {
                intersectsAnyScreen = true;
                break;
            }
        }
        return !intersectsAnyScreen;
    }

    static void ShowClosestCore()
    {
        var sw = Stopwatch.StartNew();
        var closest = tracker.GetClosest6().ToList();
        sw.Stop();

        if (settings.MeasureGetClosest)
        {
            getClosestMetrics.RecordTiming(sw.ElapsedMilliseconds);
        }

        lastClosest = closest;

        // Keep existing console output for debugging; Unity can ignore it or log it
        Console.WriteLine(closest.Count);
        Console.WriteLine("\n--- Closest Elements ---");

        for (int i = 0; i < closest.Count; i++)
        {
            var el = closest[i];
            var name = el.Name ?? "[No Name]";
            var windowTitle = UiTracker.GetWindowTitle(el.Hwnd);
            bool offScreen = el.IsOffscreen || IsOffScreen(el.Rect);
            Console.WriteLine($"{i + 1}: {name}");
            Console.WriteLine($"   Process ID: {el.ProcessId}");
            Console.WriteLine($"   Window: {windowTitle} (HWND: {el.Hwnd})");
            Console.WriteLine($"   Off-screen: {offScreen}");
        }

        // Write structured output for Unity so it knows the elements
        // (Just as it used to over WebSocket)
        var msgObj = new {
            type = "closestElements",
            elements = closest.Select(c => new {
                x = c.Rect.X,
                y = c.Rect.Y,
                width = c.Rect.Width,
                height = c.Rect.Height,
                processId = c.ProcessId,
                windowTitle = UiTracker.GetWindowTitle(c.Hwnd),
                hwnd = c.Hwnd.ToInt64(),
                offScreen = c.IsOffscreen || IsOffScreen(c.Rect)
            }).ToArray()
        };
        Console.WriteLine("CMD_RESPONSE:" + JsonConvert.SerializeObject(msgObj));
        overlay.SetRects(
            closest.Select((c, i) => (c.Rect, i + 1)).ToList()
        );
        Console.WriteLine("CMD_RESPONSE:hold done");
        if (settings.InputMode == "test")
        {
            Task.Delay(500).ContinueWith(_ => suppressTestMouseClick = false);
        }
    }

    static void InvokeByIndex(int index)
    {
        overlay.BeginInvoke((Action)(() =>
        {
            if (index < 0 || index >= lastClosest.Count)
            {
                Console.WriteLine("Invalid index");
                return;
            }

            var item = lastClosest[index];


            try
            {
                // Resolve the live element from the center point of the cached item to avoid COM HRESULT E_FAIL issues with cached elements
                AutomationElement targetEl = item.Element;
                try
                {
                    AutomationElement windowEl = tracker.Automation.FromHandle(item.Hwnd);
                    if (windowEl != null)
                    {
                        var windowRuntimeId = windowEl.Properties.RuntimeId.ValueOrDefault;
                        if (item.RuntimeId != null && windowRuntimeId != null && Enumerable.SequenceEqual(windowRuntimeId, item.RuntimeId))
                        {
                            targetEl = windowEl;
                        }
                        else if (item.RuntimeId != null)
                        {
                            var runtimeIdCond = new FlaUI.Core.Conditions.PropertyCondition(
                                tracker.Automation.PropertyLibrary.Element.RuntimeId,
                                item.RuntimeId
                            );
                            var liveEl = windowEl.FindFirstDescendant(runtimeIdCond);
                            if (liveEl != null)
                            {
                                targetEl = liveEl;
                            }
                        }

                        if (targetEl == item.Element && !string.IsNullOrEmpty(item.AutomationId))
                        {
                            var liveEl = windowEl.FindFirstDescendant(cf =>
                                cf.ByAutomationId(item.AutomationId!).And(cf.ByControlType(item.ControlType))
                            );
                            if (liveEl != null)
                            {
                                targetEl = liveEl;
                            }
                        }
                    }

                    if (targetEl == item.Element)
                    {
                        int x = item.Rect.Left + item.Rect.Width / 2;
                        int y = item.Rect.Top + item.Rect.Height / 2;
                        var liveEl = tracker.Automation.FromPoint(new Point(x, y));
                        if (liveEl != null)
                        {
                            int livePid = 0;
                            try { livePid = liveEl.Properties.ProcessId.ValueOrDefault; } catch { }

                            bool isOverlay = (liveEl.Properties.NativeWindowHandle.ValueOrDefault == overlay.Handle) ||
                                             (livePid == Process.GetCurrentProcess().Id);

                            if (!isOverlay)
                            {
                                targetEl = liveEl;
                            }
                            else
                            {
                                Console.WriteLine("FromPoint hit overlay, ignoring.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to resolve live element: " + ex.Message);
                }

                string action = SmartInvoke(targetEl, item.Rect);

                var logObj = new {
                    type = "invoked",
                    index = index,
                    name = item.Name,
                    controlType = item.ControlType.ToString(),
                    action = action,
                    rect = new { x = item.Rect.X, y = item.Rect.Y, width = item.Rect.Width, height = item.Rect.Height }
                };
                Console.WriteLine("CMD_RESPONSE:" + JsonConvert.SerializeObject(logObj));
            }
            finally
            {
                overlay.SetRects(new List<(Rectangle Rect, int Index)>());
                lastClosest.Clear();

                if (settings.InputMode == "test")
                {
                    Task.Delay(500).ContinueWith(_ => suppressTestMouseClick = false);
                }
            }
        }));
    }

    static void ClearOverlay()
    {
        overlay.BeginInvoke((Action)(() =>
        {
            lastClosest.Clear();
            overlay.SetRects(new List<(Rectangle Rect, int Index)>());
        }));
    }

    static int KeyToIndex(Keys key)
    {
        switch (key)
        {
            case Keys.D1:
            case Keys.NumPad1:
                return 0;
            case Keys.D2:
            case Keys.NumPad2:
                return 1;
            case Keys.D3:
            case Keys.NumPad3:
                return 2;
            case Keys.D4:
            case Keys.NumPad4:
                return 3;
            case Keys.D5:
            case Keys.NumPad5:
                return 4;
            case Keys.D6:
            case Keys.NumPad6:
                return 5;
            default:
                return -1;
        }
    }



    static string SmartInvoke(AutomationElement el, Rectangle rect)
    {
        ControlType controlType = ControlType.Custom;
        try
        {
            controlType = el.ControlType;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to get ControlType: " + ex.Message);
        }

        try
        {
            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
            {
                var bounding = el.Properties.BoundingRectangle.ValueOrDefault;
                if (!bounding.IsEmpty)
                {
                    rect = bounding;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to get BoundingRectangle: " + ex.Message);
        }

        if (controlType == ControlType.Edit || controlType == ControlType.Document)
        {
            try
            {
                el.Focus();
            }
            catch { }
        }

        if (TryInvokePattern(el))
        {
            return "InvokePattern";
        }

        if (TrySelectionPattern(el))
        {
            return "SelectionPattern";
        }

        if (TryTogglePattern(el))
        {
            return "TogglePattern";
        }

        if (TryExpandCollapsePattern(el))
        {
            return "ExpandCollapsePattern";
        }

        if (TryLegacyDefaultAction(el))
        {
            return "LegacyDefaultAction";
        }

        try
        {
            int x = rect.Left + rect.Width / 2;
            int y = rect.Top + rect.Height / 2;

            bool requiresDoubleClick = controlType == ControlType.ListItem ||
                                        controlType == ControlType.TreeItem ||
                                        controlType == ControlType.DataItem ||
                                        controlType == ControlType.Edit ||
                                        controlType == ControlType.Document;

            if (requiresDoubleClick)
            {
                Console.WriteLine($"SmartInvoke: Fallback Double Click at ({x}, {y}) for {controlType}");
                DoubleClickAt(x, y);
                return "FallbackDoubleClick";
            }
            else
            {
                Console.WriteLine($"SmartInvoke: Fallback Click at ({x}, {y}) for {controlType}");
                ClickAt(x, y);
                return "FallbackClick";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Fallback click failed: " + ex.Message);
            return "Failed: " + ex.Message;
        }
    }

    static bool TryInvokePattern(AutomationElement el)
    {
        try
        {
            if (!el.Patterns.Invoke.IsSupported)
            {
                return false;
            }

            Console.WriteLine("SmartInvoke: Invoke pattern");
            el.Patterns.Invoke.Pattern.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("TryInvokePattern failed: " + ex.Message);
            return false;
        }
    }

    static bool TrySelectionPattern(AutomationElement el)
    {
        try
        {
            if (!el.Patterns.SelectionItem.IsSupported)
            {
                return false;
            }

            Console.WriteLine("SmartInvoke: SelectionItem pattern");
            el.Patterns.SelectionItem.Pattern.Select();
            try
            {
                el.Focus();
            }
            catch { }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("TrySelectionPattern failed: " + ex.Message);
            return false;
        }
    }

    static bool TryTogglePattern(AutomationElement el)
    {
        try
        {
            if (!el.Patterns.Toggle.IsSupported)
            {
                return false;
            }

            Console.WriteLine("SmartInvoke: Toggle pattern");
            el.Patterns.Toggle.Pattern.Toggle();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("TryTogglePattern failed: " + ex.Message);
            return false;
        }
    }

    static bool TryExpandCollapsePattern(AutomationElement el)
    {
        try
        {
            if (!el.Patterns.ExpandCollapse.IsSupported)
            {
                return false;
            }

            Console.WriteLine("SmartInvoke: ExpandCollapse pattern");
            var pattern = el.Patterns.ExpandCollapse.Pattern;
            var state = pattern.ExpandCollapseState.Value;
            if (state == ExpandCollapseState.Collapsed)
            {
                pattern.Expand();
            }
            else
            {
                pattern.Collapse();
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("TryExpandCollapsePattern failed: " + ex.Message);
            return false;
        }
    }

    static bool TryLegacyDefaultAction(AutomationElement el)
    {
        try
        {
            if (!el.Patterns.LegacyIAccessible.IsSupported)
            {
                return false;
            }

            Console.WriteLine("SmartInvoke: LegacyIAccessible pattern");
            el.Patterns.LegacyIAccessible.Pattern.DoDefaultAction();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("TryLegacyDefaultAction failed: " + ex.Message);
            return false;
        }
    }

    static void ClickAt(int x, int y)
    {
        Mouse.LeftClick(new Point(x, y));
    }

    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    const uint LEFTDOWN = 0x02;
    const uint LEFTUP = 0x04;

    static void DoubleClickAt(int x, int y)
    {
        Mouse.LeftDoubleClick(new Point(x, y));
    }

    sealed class AppSettings
    {
        public string InputMode { get; private set; } = "io";
        public bool MeasureGetClosest { get; private set; } = true;
        public bool MeasureRefresh { get; private set; } = true;
        public bool MeasureVisibility { get; private set; } = true;

        public static AppSettings Load()
        {
            var values = LoadDotEnv();
            return new AppSettings
            {
                InputMode = NormalizeMode(GetValue(values, "INPUT_MODE", "io")),
                MeasureGetClosest = ParseBool(GetValue(values, "MEASURE_GET_CLOSEST", "true")),
                MeasureRefresh = ParseBool(GetValue(values, "MEASURE_REFRESH", "true")),
                MeasureVisibility = ParseBool(GetValue(values, "MEASURE_VISIBILITY", "true"))
            };
        }

        static Dictionary<string, string> LoadDotEnv()
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string envPath = FindEnvFile();

            if (envPath == null)
            {
                Console.WriteLine("No .env file found. Using defaults.");
                return values;
            }

            foreach (var rawLine in File.ReadAllLines(envPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }

                int separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, separatorIndex).Trim();
                string value = line.Substring(separatorIndex + 1).Trim().Trim('"');
                values[key] = value;
            }

            Console.WriteLine($".env loaded from {envPath}");
            return values;
        }

        static string FindEnvFile()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new[]
            {
                Directory.GetCurrentDirectory(),
                Application.StartupPath
            };

            foreach (var root in roots)
            {
                var dir = new DirectoryInfo(root);
                while (dir != null && seen.Add(dir.FullName))
                {
                    string candidate = Path.Combine(dir.FullName, ".env");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    dir = dir.Parent;
                }
            }

            return null;
        }

        static string GetValue(Dictionary<string, string> values, string key, string fallback)
        {
            if (values.TryGetValue(key, out string value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return fallback;
        }

        static string NormalizeMode(string mode)
        {
            string normalized = mode.Trim().ToLowerInvariant();
            if (normalized == "test")
            {
                return "test";
            }

            return "io";
        }

        static bool ParseBool(string value)
        {
            string normalized = value.Trim().ToLowerInvariant();
            return normalized == "1" || normalized == "true" || normalized == "yes" || normalized == "on";
        }
    }
}

public class FilteredTextWriter : TextWriter
{
    private readonly TextWriter _original;
    private readonly System.Text.StringBuilder _lineBuffer = new System.Text.StringBuilder();
    private readonly object _lock = new object();

    public FilteredTextWriter(TextWriter original)
    {
        _original = original;
    }

    public override System.Text.Encoding Encoding => _original.Encoding;

    public override void Write(char value)
    {
        lock (_lock)
        {
            if (value == '\n')
            {
                FlushBuffer();
            }
            else if (value != '\r')
            {
                _lineBuffer.Append(value);
            }
        }
    }

    public override void Write(string value)
    {
        if (value == null) return;
        lock (_lock)
        {
            foreach (char c in value)
            {
                if (c == '\n')
                {
                    FlushBuffer();
                }
                else if (c != '\r')
                {
                    _lineBuffer.Append(c);
                }
            }
        }
    }

    public override void WriteLine(string value)
    {
        if (value == null) return;
        lock (_lock)
        {
            _lineBuffer.Append(value);
            FlushBuffer();
        }
    }

    private void FlushBuffer()
    {
        string line = _lineBuffer.ToString();
        _lineBuffer.Clear();

        if (line.StartsWith("CMD_RESPONSE:", StringComparison.OrdinalIgnoreCase))
        {
            _original.WriteLine(line);
        }
    }
}
