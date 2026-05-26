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

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        tracker = new UiTracker();
        overlay = new OverlayForm();

        tracker.AttachOverlay(overlay);
        overlay.Show();

        getClosestMetrics = new CustomMetrics("GetClosest");

        Console.WriteLine($"Input mode: {settings.InputMode}");
        Console.WriteLine($"GetClosest timing enabled: {settings.MeasureGetClosest}");

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

    static void ShowClosestCore()
    {
        var sw = Stopwatch.StartNew();
        var closest = tracker.GetClosest6().ToList();
        sw.Stop();

        getClosestMetrics.RecordTiming(sw.ElapsedMilliseconds);

        lastClosest = closest;

        // Keep existing console output for debugging; Unity can ignore it or log it
        // Console.WriteLine(closest.Count);
        // Console.WriteLine("\n--- Closest Elements ---");

        // for (int i = 0; i < closest.Count; i++)
        // {
        //     var el = closest[i].Element;
        //     var name = el.Properties.Name.ValueOrDefault ?? "[No Name]";
        //     Console.WriteLine($"{i + 1}: {name}");
        // }

        // Write structured output for Unity so it knows the elements
        // (Just as it used to over WebSocket)
        // var msgObj = new {
        //     type = "closestElements",
        //     elements = closest.Select(c => new {
        //         x = c.Rect.X,
        //         y = c.Rect.Y,
        //         width = c.Rect.Width,
        //         height = c.Rect.Height
        //     }).ToArray()
        // };
        // Console.WriteLine("CMD_RESPONSE:" + JsonConvert.SerializeObject(msgObj));
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
                SmartInvoke(item.Element, item.Rect);
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



    static void SmartInvoke(AutomationElement el, Rectangle rect)
    {
        try
        {
            if (el.Patterns.Invoke.IsSupported)
            {
                el.Patterns.Invoke.Pattern.Invoke();
                return;
            }

            if (el.Patterns.SelectionItem.IsSupported)
            {
                el.Patterns.SelectionItem.Pattern.Select();
                SendKeys.SendWait("{ENTER}");
                return;
            }

            int x = rect.Left + rect.Width / 2;
            int y = rect.Top + rect.Height / 2;

            DoubleClickAt(x, y);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Invoke failed: " + ex.Message);
        }
    }

    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    const uint LEFTDOWN = 0x02;
    const uint LEFTUP = 0x04;

    static void DoubleClickAt(int x, int y)
    {
        SetCursorPos(x, y);

        for (int i = 0; i < 2; i++)
        {
            mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }
    }

    sealed class AppSettings
    {
        public string InputMode { get; private set; } = "io";
        public bool MeasureGetClosest { get; private set; } = true;

        public static AppSettings Load()
        {
            var values = LoadDotEnv();
            return new AppSettings
            {
                InputMode = NormalizeMode(GetValue(values, "INPUT_MODE", "io")),
                MeasureGetClosest = ParseBool(GetValue(values, "MEASURE_GET_CLOSEST", "true"))
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