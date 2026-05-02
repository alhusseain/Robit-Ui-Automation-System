using System;
using System.Linq;
using System.Windows.Forms;
using Gma.System.MouseKeyHook;

static class Program
{
    static UiTracker tracker;
    static OverlayForm overlay;
    static IKeyboardMouseEvents hook;

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        tracker = new UiTracker();
        overlay = new OverlayForm();

        tracker.AttachOverlay(overlay);

        overlay.Show();

        hook = Hook.GlobalEvents();

        hook.MouseDownExt += (s, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;

            // 🔥 STEP 1: rebuild UI snapshot (no timer anymore)
            tracker.SafeRefresh();

            // 🔥 STEP 2: get closest 6 to mouse
            var closest = tracker.GetClosest6();

            Console.WriteLine("\n--- Closest 6 Elements ---");

            foreach (var el in closest)
            {
                Console.WriteLine(el.Element.Name + " | " + el.Element.ControlType);
            }

            // 🔥 STEP 3: highlight ONLY those 6
            overlay.SetRects(closest.Select(c => c.Rect).ToList());
        };

        Application.Run();
    }
}