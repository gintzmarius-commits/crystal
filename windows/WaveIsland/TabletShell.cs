using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WaveIsland
{
    /// <summary>
    /// Version 4.0 : le PC en mode tablette se comporte (un peu) comme un iPad.
    /// - Barre d'etat en haut (heure a gauche, Wi-Fi / son / batterie a droite), l'ilot au milieu.
    /// - Chaque appli s'ouvre en plein ecran ; le dock disparait quand une appli est ouverte.
    /// - Petite barre en bas de l'ecran. 1er glissement vers le haut : le dock monte.
    ///   2e glissement : l'appli suit le doigt (le bas de la fenetre sous le doigt) et, au lacher, rentre dans
    ///   son icone du dock. Doigt garde en haut : toutes les applis ouvertes, en cartes superposees (iPhone).
    ///   Dans cette vue : glisser a droite pour voir les cartes en dessous, toucher pour ouvrir,
    ///   glisser vers le bas pour tout ranger dans le dock.
    /// </summary>
    public static class TabletShell
    {
        public static bool Active { get; private set; }
        private static DispatcherTimer _watch;
        private static EdgeWindow _edge;
        private static TopBarWindow _top;
        private static IntPtr _hook;
        private static WinEventProc _hookProc;

        private const int SM_CONVERTIBLESLATEMODE = 0x2003;
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);

        /// <summary>Ecran retourne / clavier replie (PC convertible) ?</summary>
        public static bool SlateNow()
        {
            try
            {
                // Un PC de bureau (ou sans ecran tactile) repond aussi « 0 » : on exige un ecran tactile integre
                int dig = GetSystemMetrics(94 /* SM_DIGITIZER */);
                bool touch = (dig & 0x01 /* NID_INTEGRATED_TOUCH */) != 0 && (dig & 0x80 /* NID_READY */) != 0;
                return touch && GetSystemMetrics(SM_CONVERTIBLESLATEMODE) == 0;
            }
            catch { return false; }
        }

        public static void Start()
        {
            _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _watch.Tick += (a, b) => Apply();
            _watch.Start();
            Apply();
        }

        /// <summary>Choisit le mode selon le reglage : « auto » (ecran retourne), « on » ou « off ».</summary>
        public static void Apply()
        {
            string m = Settings.Current.TabletMode ?? "auto";
            bool want = m == "on" || (m == "auto" && SlateNow());
            if (App.Taskbar == null || !App.Taskbar.Active) want = false;
            if (want == Active) return;
            if (want) Enter(); else Leave();
        }

        private static void Enter()
        {
            Active = true;
            Log.I("Mode tablette : active (facon iPad)");
            App.Taskbar.SetTablet(true);
            _top ??= new TopBarWindow();
            _top.Start();
            _edge ??= new EdgeWindow();
            _edge.Start();
            _hookProc ??= OnForeground;
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookProc, 0, 0, WINEVENT_OUTOFCONTEXT);
            MaximizeIfApp(GetForegroundWindow());
            EdgeSwipes.AskOnce();
        }

        private static void Leave()
        {
            Active = false;
            Log.I("Mode tablette : desactive");
            App.Taskbar?.SetTablet(false);
            _top?.Stop();
            _edge?.Stop();
            if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        }

        public static void Stop() { if (Active) Leave(); _watch?.Stop(); }

        /// <summary>Apres une relance de l'Explorateur : on remet la barre d'etat (sa place reservee a ete perdue).</summary>
        public static void Reattach()
        {
            if (!Active) return;
            Leave();
            Apply();
        }

        // ================== Applis en plein ecran ==================

        private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr h);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        private const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;
        private const int GWL_STYLE = -16, WS_MAXIMIZEBOX = 0x10000, WS_THICKFRAME = 0x40000;

        private static void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || !Active) return;
            MaximizeIfApp(hwnd);
        }

        /// <summary>Comme sur iPad : une vraie fenetre d'appli s'ouvre en plein ecran.</summary>
        private static void MaximizeIfApp(IntPtr h)
        {
            if (h == IntPtr.Zero || !IsWindowVisible(h) || IsZoomed(h) || Native.IsIconic(h)) return;
            if (GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == (uint)Environment.ProcessId) return;
            int style = Native.GetWindowLong(h, GWL_STYLE);
            if ((style & WS_MAXIMIZEBOX) == 0 || (style & WS_THICKFRAME) == 0) return; // boites de dialogue
            int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return;
            Native.ShowWindow(h, 3 /* SW_MAXIMIZE */);
        }

        /// <summary>Une appli est au premier plan (et pas le bureau) ?</summary>
        internal static IntPtr AppInFront()
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return IntPtr.Zero;
            GetWindowThreadProcessId(fg, out uint pid);
            if (pid == (uint)Environment.ProcessId) return IntPtr.Zero;
            var cls = new StringBuilder(64);
            GetClassName(fg, cls, 64);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return IntPtr.Zero;
            return TaskbarWindow.AppWindows().Any(w => w.h == fg) ? fg : IntPtr.Zero;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);

        // ================== Gestes du bas ==================

        private static Gesture _g;

        /// <summary>Doigt pose sur la petite barre du bas (coordonnees ecran, unites WPF).</summary>
        internal static void Down(Point p)
        {
            _g = new Gesture { Start = p, Last = p, App = AppInFront(), DockWasVisible = App.Taskbar?.DockVisible ?? false, At = DateTime.Now };
        }

        internal static void Move(Point p)
        {
            var g = _g;
            if (g == null) return;
            double dy = g.Start.Y - p.Y;
            if (Math.Abs(p.Y - g.Last.Y) > 6 || Math.Abs(p.X - g.Last.X) > 6) { g.StillSince = DateTime.Now; }
            g.Last = p;
            if (g.App == IntPtr.Zero) return; // sur le bureau, le dock est deja la
            if (!g.DockWasVisible)
            {
                // 1er glissement : le dock monte
                if (!g.Fired && dy > 30) { g.Fired = true; App.Taskbar?.ShowDock(); Log.I("Mode tablette : dock affiche (1er glissement)"); }
                return;
            }
            // 2e glissement : l'appli suit le doigt
            if (dy > 12 && AppMotion.Current == null) AppMotion.Begin(g.App);
            AppMotion.Current?.Drag(p, g.Start);
            // Doigt immobile assez haut : toutes les applis ouvertes
            if (AppMotion.Current != null && !AppMotion.Current.Switcher && dy > 120 && (DateTime.Now - g.StillSince).TotalMilliseconds > 260)
                AppMotion.Current.ShowSwitcher();
        }

        internal static void Up(Point p)
        {
            var g = _g;
            _g = null;
            if (g == null) return;
            var m = AppMotion.Current;
            if (m == null) return;
            double dy = g.Start.Y - p.Y;
            double v = dy / Math.Max(0.05, (DateTime.Now - g.At).TotalSeconds);
            if (m.Switcher) { m.SettleSwitcher(); return; }
            if (dy > 70 || v > 900) m.CloseToDock();
            else m.Cancel();
        }

        private sealed class Gesture
        {
            public Point Start, Last;
            public IntPtr App;
            public bool DockWasVisible, Fired;
            public DateTime At, StillSince = DateTime.Now;
        }
    }

    /// <summary>Petite fenetre invisible tout en bas de l'ecran : la petite barre (facon iPad) et les gestes.</summary>
    public sealed class EdgeWindow : Window
    {
        private const double EH = 18;
        private readonly Border _pill;
        private DispatcherTimer _t;
        private IntPtr _hwnd;

        public EdgeWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // presque invisible mais touchable
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            var g = new Grid();
            _pill = new Border
            {
                Width = 134, Height = 5, CornerRadius = new CornerRadius(2.5), VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 7), IsHitTestVisible = false,
                Background = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.5 }
            };
            g.Children.Add(_pill);
            Content = g;
            SourceInitialized += (a, b) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            };
            // Doigt (tactile) et souris : memes gestes
            TouchDown += (a, e) => { CaptureTouch(e.TouchDevice); TabletShell.Down(Screen(e.GetTouchPoint(this).Position)); e.Handled = true; };
            TouchMove += (a, e) => { TabletShell.Move(Screen(e.GetTouchPoint(this).Position)); e.Handled = true; };
            TouchUp += (a, e) => { ReleaseTouchCapture(e.TouchDevice); TabletShell.Up(Screen(e.GetTouchPoint(this).Position)); e.Handled = true; };
            MouseLeftButtonDown += (a, e) => { if (e.StylusDevice != null) return; CaptureMouse(); TabletShell.Down(Screen(e.GetPosition(this))); };
            MouseMove += (a, e) => { if (IsMouseCaptured) TabletShell.Move(Screen(e.GetPosition(this))); };
            MouseLeftButtonUp += (a, e) => { if (!IsMouseCaptured) return; ReleaseMouseCapture(); TabletShell.Up(Screen(e.GetPosition(this))); };
        }

        private Point Screen(Point p) => new Point(Left + p.X, Top + p.Y);

        public void Start()
        {
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            Show();
            double k = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Width = sb.Width / k;
            Height = EH;
            Left = sb.Left / k;
            Top = sb.Bottom / k - EH;
            _t ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _t.Tick += (a, b) => Refresh();
            _t.Start();
            Refresh();
        }

        public void Stop() { _t?.Stop(); Hide(); }

        /// <summary>La petite barre n'apparait que quand une appli est ouverte (sur le bureau, le dock est la).</summary>
        private void Refresh()
        {
            bool app = TabletShell.AppInFront() != IntPtr.Zero || AppMotion.Current != null;
            _pill.Opacity = app ? 1 : 0;
            if (_hwnd != IntPtr.Zero)
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    /// <summary>Barre d'etat du haut (mode tablette) : heure a gauche, Wi-Fi / son / batterie a droite.</summary>
    public sealed class TopBarWindow : Window
    {
        private const double TH = 30;
        private readonly TextBlock _time = new TextBlock(), _date = new TextBlock(), _icons = new TextBlock(), _pct = new TextBlock();
        private DispatcherTimer _t;
        private IntPtr _hwnd;
        private bool _appBar;

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public int uCallbackMessage, uEdge; public Native.RECT rc; public IntPtr lParam; }
        [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(int msg, ref APPBARDATA d);

        public TopBarWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            var shadow = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.45 };
            var g = new Grid();
            var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.Hand, Effect = shadow };
            _time.FontSize = 14; _time.FontWeight = FontWeights.SemiBold; _time.Foreground = Brushes.White;
            _date.FontSize = 13; _date.Foreground = Brushes.White; _date.Margin = new Thickness(8, 0, 0, 0);
            left.Children.Add(_time); left.Children.Add(_date);
            left.MouseLeftButtonUp += (a, b) => TaskbarPanel.Toggle("notes", new Rect(Left + 10, Top, 120, TH));
            left.TouchUp += (a, b) => TaskbarPanel.Toggle("notes", new Rect(Left + 10, Top, 120, TH));
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, Effect = shadow };
            _icons.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"); _icons.FontSize = 14; _icons.Foreground = Brushes.White;
            _pct.FontSize = 13; _pct.Foreground = Brushes.White; _pct.Margin = new Thickness(6, 0, 0, 0);
            right.Children.Add(_icons); right.Children.Add(_pct);
            right.MouseLeftButtonUp += (a, b) => TaskbarPanel.Toggle("quick", new Rect(Left + Width - 200, Top, 190, TH));
            right.TouchUp += (a, b) => TaskbarPanel.Toggle("quick", new Rect(Left + Width - 200, Top, 190, TH));
            g.Children.Add(left);
            g.Children.Add(right);
            Content = g;
            SourceInitialized += (a, b) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            };
        }

        public void Start()
        {
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            Show();
            double k = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Width = sb.Width / k; Height = TH; Left = sb.Left / k; Top = sb.Top / k;
            // On reserve le haut de l'ecran : les applis en plein ecran commencent sous la barre (comme sur iPad)
            if (!_appBar && _hwnd != IntPtr.Zero)
            {
                var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
                SHAppBarMessage(0 /* ABM_NEW */, ref d);
                int hp = (int)Math.Round(TH * k);
                d.uEdge = 1; // ABE_TOP
                d.rc = new Native.RECT { Left = sb.Left, Right = sb.Right, Top = sb.Top, Bottom = sb.Top + hp };
                SHAppBarMessage(2 /* ABM_QUERYPOS */, ref d);
                d.rc.Bottom = d.rc.Top + hp;
                SHAppBarMessage(3 /* ABM_SETPOS */, ref d);
                _appBar = true;
            }
            _t ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _t.Tick += (a, b) => Tick();
            _t.Start();
            Tick();
            App.Island?.BringToTop();
        }

        public void Stop()
        {
            _t?.Stop();
            if (_appBar)
            {
                var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
                SHAppBarMessage(1 /* ABM_REMOVE */, ref d);
                _appBar = false;
            }
            Hide();
        }

        private void Tick()
        {
            var now = DateTime.Now;
            _time.Text = now.ToString("HH:mm");
            _date.Text = now.ToString("ddd d MMM", new System.Globalization.CultureInfo("fr-FR"));
            bool wifi = false, eth = false;
            try
            {
                foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211) wifi = true;
                    else if (n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet) eth = true;
                }
            }
            catch { }
            var (lvl, muted) = App.Volume?.Read() ?? (0.5f, false);
            string net = wifi ? "" : eth ? "" : "";
            string vol = muted || lvl < 0.01 ? "" : lvl < 0.34 ? "" : lvl < 0.67 ? "" : "";
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            bool hasBat = (ps.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryLifePercent <= 1.0f;
            string bat = "";
            _pct.Text = "";
            if (hasBat)
            {
                int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
                bool charging = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                bat = "   " + ((char)((charging ? 0xE85A : 0xE850) + Math.Min(10, pct / 10))).ToString();
                _pct.Text = pct + " %";
            }
            _icons.Text = net + "   " + vol + bat;
        }
    }

    /// <summary>Glissements des bords de Windows (tactile) : coupes en mode tablette pour que le bas soit a nous.</summary>
    public static class EdgeSwipes
    {
        private const string Path = @"SOFTWARE\Policies\Microsoft\Windows\EdgeUI";

        public static bool Disabled
        {
            get
            {
                try
                {
                    return Equals(Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\" + Path, "AllowEdgeSwipe", 1), 0)
                        || Equals(Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\" + Path, "AllowEdgeSwipe", 1), 0);
                }
                catch { return false; }
            }
        }

        /// <summary>Premiere fois en mode tablette : on coupe les glissements des bords (et on relance l'Explorateur pour l'appliquer).</summary>
        public static void AskOnce()
        {
            var s = Settings.Current;
            if (s.EdgeSwipeDone || Program.Demo) return;
            s.EdgeSwipeDone = true;
            s.Save();
            Set(false);
        }

        /// <summary>
        /// allow = false : coupe les glissements des bords de Windows (bas = menu Demarrer / barre, droite = notifications,
        /// gauche = widgets...). Une seule demande d'autorisation administrateur ; l'Explorateur est relance pour que ce soit
        /// pris en compte tout de suite (sinon il fallait redemarrer le PC).
        /// </summary>
        public static void Set(bool allow)
        {
            string hk = @"HKLM\" + Path, hc = @"HKCU\" + Path;
            string cmd = allow
                ? $"/c reg delete \"{hk}\" /v AllowEdgeSwipe /f & reg delete \"{hc}\" /v AllowEdgeSwipe /f"
                : $"/c reg add \"{hk}\" /v AllowEdgeSwipe /t REG_DWORD /d 0 /f & reg add \"{hc}\" /v AllowEdgeSwipe /t REG_DWORD /d 0 /f";
            var ui = System.Windows.Application.Current.Dispatcher;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var p = Process.Start(new ProcessStartInfo("cmd.exe", cmd) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                    p?.WaitForExit(20000);
                    Log.I("Glissements des bords de Windows : " + (allow ? "remis" : "coupes") + " (code " + (p?.ExitCode.ToString() ?? "?") + ")");
                    RestartExplorer(ui);
                }
                catch (Exception e) { Log.I("Glissements des bords : refus ou erreur (" + e.Message + ")"); }
            });
        }

        /// <summary>Relance l'Explorateur (la barre Wave Theme et le mode tablette se remettent en place tout seuls).</summary>
        private static void RestartExplorer(System.Windows.Threading.Dispatcher ui)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("explorer")) { try { p.Kill(); } catch { } }
                System.Threading.Thread.Sleep(1200);
                if (Process.GetProcessesByName("explorer").Length == 0)
                    Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true });
                Log.I("Explorateur relance pour appliquer le reglage");
                System.Threading.Thread.Sleep(3500);
                ui.BeginInvoke(new Action(TabletShell.Reattach));
            }
            catch (Exception e) { Log.I("Relance de l'Explorateur impossible : " + e.Message); }
        }
    }
}
