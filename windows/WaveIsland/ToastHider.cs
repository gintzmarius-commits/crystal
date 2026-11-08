using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace WaveIsland
{
    /// <summary>
    /// Cache les bulles de notification de Windows en bas a droite : l'ilot en verre les affiche deja
    /// (elles restent dans le centre de notifications). La bulle de Windows est une fenetre « Nouvelle notification »
    /// de ShellExperienceHost : des qu'elle apparait, on la pousse hors de l'ecran.
    /// </summary>
    public static class ToastHider
    {
        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

        private const uint EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_LOCATIONCHANGE = 0x800B, WINEVENT_OUTOFCONTEXT = 0;
        private const string Cls = "Windows.UI.Core.CoreWindow";
        private static readonly string[] Titles = { "Nouvelle notification", "New notification", "Neue Benachrichtigung", "Nueva notificación", "Nuova notifica" };

        private static WinEventProc _proc;
        private static IntPtr _hook;
        private static DispatcherTimer _poll;
        private static int _hidden;

        private static bool On => Settings.Current.HideWinToasts && Settings.Current.Notifications;

        public static void Start(Dispatcher ui)
        {
            _proc = OnEvent;
            _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
            // Filet de securite : verifie aussi regulierement (la bulle peut apparaitre sans evenement)
            _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (a, b) => Sweep(), ui);
            _poll.Start();
            Log.I("Bulles de Windows : masquage " + (On ? "actif" : "inactif"));
        }

        private static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
        {
            if (obj != 0 || hwnd == IntPtr.Zero || !On) return;
            if (IsToast(hwnd)) Push(hwnd);
        }

        private static void Sweep()
        {
            if (!On) return;
            foreach (var t in Titles)
            {
                var h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, Cls, t);
                if (h != IntPtr.Zero) Push(h);
            }
        }

        private static bool IsToast(IntPtr h)
        {
            var sb = new StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() != Cls) return false;
            sb.Clear();
            GetWindowText(h, sb, 64);
            string title = sb.ToString();
            foreach (var t in Titles) if (title == t) return true;
            return false;
        }

        /// <summary>Hors de l'ecran (on ne la ferme pas : la notification reste dans le centre de notifications).</summary>
        private static void Push(IntPtr h)
        {
            if (!GetWindowRect(h, out var r) || r.L <= -30000) return;
            SetWindowPos(h, IntPtr.Zero, -32000, -32000, 0, 0, 0x0001 /* NOSIZE */ | 0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */);
            if (_hidden++ < 5) Log.I("Bulle de notification de Windows cachee (l'ilot l'affiche)");
        }
    }
}
