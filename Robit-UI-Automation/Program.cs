using System;
using System.Linq;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Fleck;
using Newtonsoft.Json;
using System.Drawing;
using FlaUI.Core.AutomationElements;

static class Program
{
    static UiTracker tracker;
    static OverlayForm overlay;
    static WebSocketServer server;

    static List<CachedElement> lastClosest = new List<CachedElement>();

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        tracker = new UiTracker();
        overlay = new OverlayForm();

        tracker.AttachOverlay(overlay);
        overlay.Show();

        Task.Run(StartWebSocketServer);

        Application.Run();
    }

    static void StartWebSocketServer()
    {
        FleckLog.Level = LogLevel.Warn;
        tracker.SafeRefresh();
        server = new WebSocketServer("ws://0.0.0.0:8181");

        server.Start(socket =>
        {
            socket.OnOpen = () => Console.WriteLine("Unity connected");
            socket.OnClose = () => Console.WriteLine("Unity disconnected");

            socket.OnMessage = message =>
            {
                Console.WriteLine("Received: " + message);

                dynamic msg = JsonConvert.DeserializeObject(message);
                string type = msg.type;

                if (type == "getClosest")
                {
                    overlay.Invoke((Action)(() =>
                    {
                        // tracker.SafeRefresh();

                        var closest = tracker.GetClosest6().ToList();
                        lastClosest = closest;
                        Console.WriteLine(closest.Count);

                        Console.WriteLine("\n--- Closest Elements ---");
                        for (int i = 0; i < closest.Count; i++)
                        {
                            var el = closest[i].Element;
                            var name = el.Properties.Name.ValueOrDefault ?? "[No Name]";
                            Console.WriteLine($"{i}: {name}");
                        }

                        overlay.SetRects(
                            closest.Select((c, i) => (c.Rect, i)).ToList()
                        );
                    }));
                }
                else if (type == "invokeIndex")
                {
                    int index = (int)msg.index;

                    overlay.Invoke((Action)(() =>
                    {
                        if (index < 0 || index >= lastClosest.Count)
                        {
                            Console.WriteLine("Invalid index");
                            return;
                        }

                        var item = lastClosest[index];
                        SmartInvoke(item.Element, item.Rect);
                    }));
                }
            };
        });

        Console.WriteLine("WebSocket server running on ws://localhost:8181");
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
}