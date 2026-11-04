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

        private static int _tick;

        public static void Start()
        {
            _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _watch.Tick += (a, b) =>
            {
                Apply();
                if (Active) FullScreenArea(true);
                // Image recente de l'appli au premier plan : l'animation de fermeture demarre sans attendre
                if (Active && ++_tick % 2 == 0 && AppMotion.Current == null) AppMotion.Cache(AppInFront());
            };
            _watch.Start();
            // Contour et coins arrondis des fenetres : surveille l'appli au premier plan (dans tous les modes)
            _hookProc ??= OnForeground;
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookProc, 0, 0, WINEVENT_OUTOFCONTEXT);
            // Ecran tourne (portrait / paysage) : la petite barre du bas se replace
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (a, b) =>
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => { if (Active) { _edge?.Start(); Log.I("Mode tablette : ecran tourne"); } }));
            Apply();
            Border(GetForegroundWindow());
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
            _edge ??= new EdgeWindow();
            _edge.Start();
            FullScreenArea(true);
            MaximizeIfApp(GetForegroundWindow());
            EdgeSwipes.AskOnce();
        }

        private static void Leave()
        {
            Active = false;
            Log.I("Mode tablette : desactive");
            App.Taskbar?.SetTablet(false);
            _edge?.Stop();
            FullScreenArea(false);
        }

        public static void Stop()
        {
            if (Active) Leave();
            _watch?.Stop();
            if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        }

        /// <summary>Apres une relance de l'Explorateur : on remet la barre d'etat (sa place reservee a ete perdue).</summary>
        public static void Reattach()
        {
            if (!Active) return;
            Leave();
            Apply();
        }

        // ================== Toute la hauteur de l'ecran pour les applis ==================

        [DllImport("user32.dll")] private static extern bool SystemParametersInfo(int a, int b, ref Native.RECT r, int c);
        private static Native.RECT? _savedArea;

        /// <summary>
        /// Mode tablette : les applis vont jusqu'en bas de l'ecran (plus de bande vide sous elles) ;
        /// la petite barre et le dock passent par-dessus. L'ancienne zone est remise en quittant le mode.
        /// </summary>
        private static void FullScreenArea(bool on)
        {
            try
            {
                var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                var cur = new Native.RECT();
                SystemParametersInfo(0x30 /* SPI_GETWORKAREA */, 0, ref cur, 0);
                if (on)
                {
                    if (cur.Bottom >= sb.Bottom && cur.Top <= sb.Top) return;
                    _savedArea ??= cur;
                    var full = new Native.RECT { Left = sb.Left, Top = sb.Top, Right = sb.Right, Bottom = sb.Bottom };
                    SystemParametersInfo(0x2F /* SPI_SETWORKAREA */, 0, ref full, 0x2 /* SPIF_SENDCHANGE */);
                    Log.I("Mode tablette : les applis prennent toute la hauteur de l'ecran");
                    // Les applis deja agrandies se remettent a la nouvelle taille
                    var fg = GetForegroundWindow();
                    if (IsZoomed(fg)) { Native.ShowWindow(fg, 9 /* SW_RESTORE */); Native.ShowWindow(fg, 3 /* SW_MAXIMIZE */); }
                }
                else if (_savedArea is Native.RECT old)
                {
                    _savedArea = null;
                    SystemParametersInfo(0x2F, 0, ref old, 0x2);
                }
            }
            catch { }
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
            if (idObject != 0) return;
            Border(hwnd);
            if (Active)
            {
                MaximizeIfApp(hwnd);
                // Image de l'appli des qu'elle est affichee : son apercu est pret avant meme d'ouvrir la pile
                var ui = System.Windows.Application.Current?.Dispatcher;
                System.Threading.Tasks.Task.Delay(700).ContinueWith(_ => ui?.BeginInvoke(new Action(() => AppMotion.Cache(hwnd))));
            }
        }

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        private static readonly HashSet<IntPtr> _bordered = new HashSet<IntPtr>();

        /// <summary>Coins arrondis + contour noir (ou blanc) autour de chaque fenetre d'appli (Windows 11).</summary>
        internal static void Border(IntPtr h)
        {
            try
            {
                string mode = Settings.Current.WindowBorder ?? "black";
                if (h == IntPtr.Zero || mode == "none" || _bordered.Contains(h)) return;
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == (uint)Environment.ProcessId) return;
                if (GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return;
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(h, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, 4);
                int color = mode == "white" ? 0x00FFFFFF : 0x00000000;
                DwmSetWindowAttribute(h, 34 /* DWMWA_BORDER_COLOR */, ref color, 4);
                if (_bordered.Count > 2000) _bordered.Clear();
                _bordered.Add(h);
            }
            catch { }
        }

        /// <summary>Reglage change : on remet le contour de Windows ou on applique le nouveau a toutes les fenetres.</summary>
        public static void ReapplyBorders()
        {
            string mode = Settings.Current.WindowBorder ?? "black";
            foreach (var w in TaskbarWindow.AppWindows())
            {
                if (mode == "none")
                {
                    int def = unchecked((int)0xFFFFFFFF); // DWMWA_COLOR_DEFAULT
                    DwmSetWindowAttribute(w.h, 34, ref def, 4);
                }
                else { _bordered.Remove(w.h); Border(w.h); }
            }
            if (mode == "none") _bordered.Clear();
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
            // Une appli reduite (rangee dans le dock) peut rester « au premier plan » : on est alors sur le bureau
            if (fg == IntPtr.Zero || Native.IsIconic(fg) || !IsWindowVisible(fg)) return IntPtr.Zero;
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
            if (g.App == IntPtr.Zero)
            {
                // Sur le bureau : glisser la petite barre vers le haut ouvre directement la pile des applis
                if (!g.Fired && dy > 40 && AppMotion.Current == null) { g.Fired = true; AppMotion.BeginSwitcher(); }
                return;
            }
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
        /// <summary>Place libre au-dessus de la zone tactile pour que la petite barre puisse monter avec le doigt.</summary>
        private const double Room = 16;
        private readonly ScaleTransform _pillScale = new ScaleTransform(1, 1);
        private readonly TranslateTransform _pillMove = new TranslateTransform();
        private double _downY = double.NaN;
        private readonly Border _pill;
        private DispatcherTimer _t;
        private IntPtr _hwnd;

        public EdgeWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            var g = new Grid();
            // Seule la bande du bas est touchable (presque invisible) ; au-dessus, les clics passent a l'appli
            g.Children.Add(new Border { Height = EH, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) });
            _pill = new Border
            {
                Width = 134, Height = 5, CornerRadius = new CornerRadius(2.5), VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 7), IsHitTestVisible = false,
                Background = new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.5 }
            };
            _pill.RenderTransformOrigin = new Point(0.5, 1);
            var tg = new TransformGroup();
            tg.Children.Add(_pillScale);
            tg.Children.Add(_pillMove);
            _pill.RenderTransform = tg;
            g.Children.Add(_pill);
            Content = g;
            SourceInitialized += (a, b) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                Native.ApplyCaptureMode(_hwnd);   // la couleur de la barre se base sur ce qu'il y a derriere elle
                int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            };
            // Doigt (tactile) et souris : memes gestes
            TouchDown += (a, e) => { CaptureTouch(e.TouchDevice); PillDown(e.GetTouchPoint(this).Position); TabletShell.Down(Screen(e.GetTouchPoint(this).Position)); e.Handled = true; };
            TouchMove += (a, e) => { PillMove(e.GetTouchPoint(this).Position); TabletShell.Move(Screen(e.GetTouchPoint(this).Position)); e.Handled = true; };
            TouchUp += (a, e) => { ReleaseTouchCapture(e.TouchDevice); PillUp(); TabletShell.Up(Screen(e.GetTouchPoint(this).Position)); e.Handled = true; };
            MouseLeftButtonDown += (a, e) => { if (e.StylusDevice != null) return; CaptureMouse(); PillDown(e.GetPosition(this)); TabletShell.Down(Screen(e.GetPosition(this))); };
            MouseMove += (a, e) => { if (IsMouseCaptured) { PillMove(e.GetPosition(this)); TabletShell.Move(Screen(e.GetPosition(this))); } };
            MouseLeftButtonUp += (a, e) => { if (!IsMouseCaptured) return; ReleaseMouseCapture(); PillUp(); TabletShell.Up(Screen(e.GetPosition(this))); };
        }

        private Point Screen(Point p) => new Point(Left + p.X, Top + p.Y);

        // La petite barre suit le doigt sur ~3 mm en s'etirant comme une goutte, puis revient avec un rebond

        private void PillDown(Point p)
        {
            _downY = p.Y;
            _pillMove.BeginAnimation(TranslateTransform.YProperty, null);
            _pillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _pillScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }

        private void PillMove(Point p)
        {
            if (double.IsNaN(_downY)) return;
            double dy = Math.Max(0, _downY - p.Y);
            const double max = 12;   // ~3 mm
            double t = max * (1 - Math.Exp(-dy / max));   // freine en approchant de la limite
            double k = t / max;
            _pillMove.Y = -t;
            // Goutte : elle s'affine et s'etire vers le haut
            _pillScale.ScaleX = 1 - 0.22 * k;
            _pillScale.ScaleY = 1 + 1.3 * k;
        }

        private void PillUp()
        {
            _downY = double.NaN;
            var ease = new SpringEase();
            var d = TimeSpan.FromMilliseconds(520);
            _pillMove.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(0, d) { EasingFunction = ease });
            _pillScale.BeginAnimation(ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(1, d) { EasingFunction = ease });
            _pillScale.BeginAnimation(ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(1, d) { EasingFunction = ease });
        }

        public void Start()
        {
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            Show();
            double k = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Width = sb.Width / k;
            Height = EH + Room;
            Left = sb.Left / k;
            Top = sb.Bottom / k - EH - Room;
            if (_t == null)
            {
                _t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _t.Tick += (a, b) => Refresh();
            }
            _t.Start();
            Refresh();
        }

        public void Stop() { _t?.Stop(); Hide(); }

        private readonly ScreenCapture _cap = new ScreenCapture();
        private bool _darkPill;

        /// <summary>
        /// La petite barre est toujours la (bureau compris), par-dessus l'appli sans lui prendre de place.
        /// Sa couleur suit le fond juste derriere : noire sur un fond clair, blanche sur un fond fonce.
        /// </summary>
        private void Refresh()
        {
            _pill.Opacity = 1;
            if (_hwnd == IntPtr.Zero) return;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            try
            {
                if (!Native.GetWindowRect(_hwnd, out var r)) return;
                int cx = (r.Left + r.Right) / 2, pw = 220, ph = 26;
                // Ce qu'il y a derriere la petite barre (la fenetre est exclue des captures)
                _cap.Grab(cx - pw / 2, r.Bottom - ph, pw, ph);
                double l = _cap.Luma;
                bool dark = _darkPill ? l > 0.45 : l > 0.58;
                if (dark == _darkPill) return;
                _darkPill = dark;
                var to = dark ? Color.FromArgb(230, 20, 22, 28) : Color.FromArgb(235, 255, 255, 255);
                var b = _pill.Background as SolidColorBrush;
                if (b == null || b.IsFrozen) { b = new SolidColorBrush(((SolidColorBrush)_pill.Background).Color); _pill.Background = b; }
                b.BeginAnimation(SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation(to, TimeSpan.FromMilliseconds(220)));
                if (_pill.Effect is System.Windows.Media.Effects.DropShadowEffect fx) fx.Color = dark ? Colors.White : Colors.Black;
            }
            catch { }
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
