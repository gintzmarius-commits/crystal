using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace WaveIsland
{
    /// <summary>
    /// Wave Theme : la barre des taches de Windows remplacee par des bulles de verre liquide
    /// (transparent, sans flou ni voile sombre). Au centre : la bulle Windows seule, puis la barre des applis
    /// (epinglees + ouvertes). A droite : Wi-Fi / son / batterie, et l'heure. Le menu des applis monte de la bulle Windows.
    /// </summary>
    public sealed class TaskbarWindow : Window
    {
        private const double H = 64, BarH = 52, Gap = 8, Cell = 48, EdgeY = 6;

        private readonly Canvas _root = new Canvas();
        private readonly ScreenCapture _cap = new ScreenCapture();
        private readonly DispatcherTimer _capTimer, _tick;
        private readonly FrameGate _gate = new FrameGate();
        private IntPtr _hwnd, _tray;
        private bool _excluded, _animating, _bubbleHidden, _hiddenFull;
        private double _k = 1, _w;
        private int _appBarMsg, _taskbarCreatedMsg;
        private bool _appBar;

        private sealed class Bubble
        {
            public readonly Image Img = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
            public readonly GlassEffect Fx = new GlassEffect();
            public readonly Canvas Content = new Canvas { Background = Brushes.Transparent };
            public double X, W, Hh = BarH;
            public Bubble() { Img.Effect = Fx; }
        }

        private readonly Bubble _win = new Bubble(), _apps = new Bubble(), _status = new Bubble(), _clock = new Bubble();
        private readonly SpringValue _appsW = new SpringValue(Cell + 16);
        /// <summary>Descente de la barre (0 = visible, H + 20 = cachee sous l'ecran) et etirement de la goutte.</summary>
        private readonly SpringValue _off = new SpringValue(0), _stretch = new SpringValue(1);
        private bool _dropped;
        private DateTime _awaySince = DateTime.MaxValue;
        private DispatcherTimer _mouse;
        private readonly TextBlock _time = new TextBlock(), _date = new TextBlock(), _wifi = new TextBlock(), _vol = new TextBlock(), _bat = new TextBlock();
        private static readonly FontFamily Ui = new FontFamily("Segoe UI Variable Text, Segoe UI");
        private static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public bool Active { get; private set; }

        public TaskbarWindow()
        {
            Title = "Wave Theme — Barre des tâches";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            UseLayoutRounding = true;
            FontFamily = Ui;
            Content = _root;

            foreach (var b in new[] { _win, _apps, _status, _clock })
            {
                _root.Children.Add(b.Img);
            }
            foreach (var b in new[] { _win, _apps, _status, _clock }) _root.Children.Add(b.Content);

            BuildWin();
            BuildStatus();
            BuildClock();

            _capTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(Math.Max(1000.0 / Frames.Fps, 1000.0 / 60)) };
            _capTimer.Tick += (a, b) => Capture();
            _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _tick.Tick += (a, b) => Tick();

            SourceInitialized += (a, b) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
                _excluded = Native.ApplyCaptureMode(_hwnd);
                HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            };
        }

        // ================== Mise en place / retrait ==================

        public void Start()
        {
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            Show();
            _k = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _w = sb.Width / _k;
            Width = _w;
            Height = H;
            Left = sb.Left / _k;
            Top = (sb.Bottom / _k) - H;
            foreach (var b in new[] { _win, _apps, _status, _clock }) { b.Img.Width = _w; b.Img.Height = H; }
            HideSystemTaskbar();
            RegisterAppBar();
            Active = true;
            RefreshApps();
            Tick();
            Layout();
            _capTimer.Start();
            _tick.Start();
            _mouse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            _mouse.Tick += (a, b) => AutoHide();
            _mouse.Start();
            Log.I("Wave Theme : barre des taches active");
        }

        /// <summary>Rend la barre de Windows (fermeture de l'appli, option coupee, desinstallation).</summary>
        public void Stop()
        {
            if (!Active) return;
            Active = false;
            _capTimer.Stop();
            _tick.Stop();
            _mouse?.Stop();
            UnregisterAppBar();
            RestoreSystemTaskbar();
            Hide();
            Log.I("Wave Theme : barre de Windows rendue");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uCallbackMessage, uEdge;
            public Native.RECT rc;
            public IntPtr lParam;
        }

        private const int ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_SETSTATE = 10;
        private const int ABE_BOTTOM = 3, ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2, ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;

        [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(int msg, ref APPBARDATA d);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string s);

        /// <summary>La barre de Windows passe en masquage automatique (elle libere sa place) et disparait.</summary>
        private void HideSystemTaskbar()
        {
            _tray = FindWindow("Shell_TrayWnd", null);
            if (_tray == IntPtr.Zero) return;
            var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _tray };
            int state = (int)SHAppBarMessage(ABM_GETSTATE, ref d);
            var s = Settings.Current;
            if (s.TaskbarPrevState < 0) { s.TaskbarPrevState = state; s.Save(); }
            d.lParam = new IntPtr(ABS_AUTOHIDE);
            SHAppBarMessage(ABM_SETSTATE, ref d);
            Native.ShowWindow(_tray, 0);
        }

        /// <summary>Remet la barre de Windows comme avant (meme apres un arret brutal : l'etat est garde dans les reglages).</summary>
        public static void RestoreSystemTaskbar()
        {
            try
            {
                var s = Settings.Current;
                IntPtr tray = FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero) return;
                if (s.TaskbarPrevState >= 0)
                {
                    var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = tray, lParam = new IntPtr(s.TaskbarPrevState == 0 ? ABS_ALWAYSONTOP : s.TaskbarPrevState) };
                    SHAppBarMessage(ABM_SETSTATE, ref d);
                    s.TaskbarPrevState = -1;
                    s.Save();
                }
                Native.ShowWindow(tray, 5);
            }
            catch (Exception e) { Log.I("Wave Theme : retour de la barre de Windows impossible : " + e.Message); }
        }

        /// <summary>
        /// Comme sur Mac : on ne reserve PAS de place en bas (pas de grand vide sous les applis agrandies),
        /// la barre passe par-dessus. L'inscription sert seulement a etre prevenu des applis en plein ecran.
        /// </summary>
        private void RegisterAppBar()
        {
            if (_hwnd == IntPtr.Zero || _appBar) return;
            _appBarMsg = RegisterWindowMessage("WaveThemeAppBar");
            _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
            var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd, uCallbackMessage = _appBarMsg };
            SHAppBarMessage(ABM_NEW, ref d);
            _appBar = true;
            PlaceAppBar();
        }

        private void PlaceAppBar()
        {
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            int hp = (int)Math.Round(H * _k);
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, sb.Left, sb.Bottom - hp, sb.Width, hp, Native.SWP_NOACTIVATE);
        }

        private void UnregisterAppBar()
        {
            if (!_appBar) return;
            var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
            SHAppBarMessage(ABM_REMOVE, ref d);
            _appBar = false;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == _appBarMsg && _appBarMsg != 0)
            {
                if (wParam.ToInt32() == ABN_POSCHANGED) PlaceAppBar();
                else if (wParam.ToInt32() == ABN_FULLSCREENAPP) SetFullscreen(lParam != IntPtr.Zero);
            }
            else if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0 && Active)
            {
                // L'Explorateur a redemarre : sa barre est revenue
                Dispatcher.BeginInvoke(new Action(() => { HideSystemTaskbar(); PlaceAppBar(); }));
            }
            else if (msg == 0x007E /* WM_DISPLAYCHANGE */ && Active)
            {
                Dispatcher.BeginInvoke(new Action(() => { Stop(); Start(); }));
            }
            return IntPtr.Zero;
        }

        private void SetFullscreen(bool full)
        {
            if (full == _hiddenFull) return;
            _hiddenFull = full;
            // Une appli en plein ecran (video, jeu) : la barre s'efface comme celle de Windows
            Visibility = full ? Visibility.Hidden : Visibility.Visible;
            if (!full && _hwnd != IntPtr.Zero)
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        // ================== Verre liquide ==================

        private void Capture()
        {
            if (_hwnd == IntPtr.Zero || _hiddenFull) return;
            if (!_excluded || !Settings.Current.Refraction)
            {
                if (_win.Img.Source == null)
                {
                    var px = new byte[] { 40, 34, 30, 255 };
                    var flat = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, px, 4);
                    flat.Freeze();
                    foreach (var b in new[] { _win, _apps, _status, _clock }) b.Img.Source = flat;
                }
                return;
            }
            if (!Native.GetWindowRect(_hwnd, out var r)) return;
            try
            {
                if (_cap.Grab(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) && _win.Img.Source != _cap.Bitmap)
                    foreach (var b in new[] { _win, _apps, _status, _clock }) b.Img.Source = _cap.Bitmap;
                ApplyInk();
            }
            catch { }
        }

        private bool _darkInk;
        /// <summary>Fond tres clair derriere la barre : textes et icones passent en fonce.</summary>
        private void ApplyInk()
        {
            bool dark = _cap.Luma > 0.62;
            if (dark == _darkInk) return;
            _darkInk = dark;
            var ink = dark ? new SolidColorBrush(Color.FromRgb(24, 26, 32)) : Brushes.White;
            foreach (var t in new[] { _time, _date, _wifi, _vol, _bat }) t.Foreground = ink;
            foreach (var r in _winSquares) r.Fill = ink;
            foreach (var d in _dots.Values) d.Background = dark ? new SolidColorBrush(Color.FromArgb(170, 24, 26, 32)) : new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
        }

        private void Geo(Bubble b)
        {
            double k = _k, W = _w * k, Hp = H * k;
            // Goutte : la bulle s'etire en hauteur et s'affine quand la barre descend ou remonte
            double st = _stretch.Value;
            double w = b.W / Math.Sqrt(st), h = b.Hh * st, cx = b.X + b.W / 2, cy = EdgeY + b.Hh / 2 + _off.Value + (h - b.Hh) / 2;
            double rad = Math.Min(h / 2, w / 2);
            const double mag = 0.95;
            b.Fx.Geo = new Point4D(cx * k, cy * k, Math.Max(0, w / 2 - rad) * k, Math.Max(0, h / 2 - rad) * k);
            b.Fx.Sizes = new Point4D(W, Hp, rad * k, 1.0 / (Math.Min(18, h * 0.42) * k));
            double strength = 14 * k;
            b.Fx.Bend = new Point4D(strength / W, strength / Hp, cx * k / W * (1 - mag), cy * k / Hp * (1 - mag));
            b.Fx.Tint = new Point4D(0.05, 0.06, 0.08, 0);   // verre transparent : ni voile ni flou
            b.Fx.Disp = new Point4D(1.14, 0.86, 0, 0);
            b.Fx.Light = new Point4D(-0.5, -0.866, b == _win && _bubbleHidden ? 0 : 1, mag);
            Canvas.SetLeft(b.Content, b.X);
            Canvas.SetTop(b.Content, EdgeY + _off.Value);
            b.Content.Width = b.W;
            b.Content.Height = b.Hh;
            b.Content.Clip = new RectangleGeometry(new Rect((b.W - w) / 2, 0, w, h), rad, rad);
            b.Content.Opacity = (b == _win && _bubbleHidden) ? 0 : Math.Max(0, 1 - _off.Value / (H * 0.8));
            b.Content.IsHitTestVisible = _off.Value < 10;
        }

        /// <summary>Place les bulles : Windows + applis au centre, etat et heure a droite.</summary>
        private void Layout()
        {
            double appsW = _appsW.Value;
            double group = BarH + Gap + appsW;
            double gx = Math.Round((_w - group) / 2);
            _win.X = gx; _win.W = BarH;
            _apps.X = gx + BarH + Gap; _apps.W = appsW;
            _clock.W = 104; _clock.X = _w - 10 - _clock.W;
            _status.W = 122; _status.X = _clock.X - Gap - _status.W;
            // Mode tablette : l'heure et l'etat passent dans la barre du haut, il ne reste que le dock
            foreach (var b in new[] { _status, _clock })
            {
                var v = Tablet ? Visibility.Collapsed : Visibility.Visible;
                if (b.Img.Visibility != v) { b.Img.Visibility = v; b.Content.Visibility = v; }
            }
            foreach (var b in new[] { _win, _apps, _status, _clock }) Geo(b);
            // Les icones restent centrees dans la barre pendant qu'elle s'elargit
            Canvas.SetLeft(_appsPanel, Math.Round((appsW - _appsPanel.Children.Count * Cell) / 2));
        }

        private void Kick()
        {
            if (_animating) return;
            _animating = true;
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object s, EventArgs e)
        {
            if (!_gate.Ready()) return;
            _appsW.Tick(); _off.Tick(); _stretch.Tick();
            Layout();
            if (!_appsW.Moving && !_off.Moving && !_stretch.Moving) { CompositionTarget.Rendering -= OnFrame; _animating = false; }
        }

        // ================== Bulle Windows (le menu des applis en sort) ==================

        private readonly List<System.Windows.Shapes.Rectangle> _winSquares = new List<System.Windows.Shapes.Rectangle>();

        private void BuildWin()
        {
            var g = new Canvas { Width = 20, Height = 20 };
            for (int i = 0; i < 4; i++)
            {
                var r = new System.Windows.Shapes.Rectangle { Width = 9, Height = 9, RadiusX = 1.6, RadiusY = 1.6, Fill = Brushes.White };
                Canvas.SetLeft(r, (i % 2) * 11);
                Canvas.SetTop(r, (i / 2) * 11);
                g.Children.Add(r);
                _winSquares.Add(r);
            }
            Canvas.SetLeft(g, (BarH - 20) / 2);
            Canvas.SetTop(g, (BarH - 20) / 2);
            g.RenderTransformOrigin = new Point(0.5, 0.5);
            g.RenderTransform = new ScaleTransform(1, 1);
            _win.Content.Children.Add(g);
            _win.Content.Cursor = Cursors.Hand;
            _win.Content.ToolTip = "Démarrer";
            _win.Content.MouseEnter += (a, b) => Pop(g, 1.12);
            _win.Content.MouseLeave += (a, b) => Pop(g, 1);
            _win.Content.MouseLeftButtonDown += (a, b) => Pop(g, 0.85);
            _win.Content.MouseLeftButtonUp += (a, b) => { Pop(g, 1.12); App.Launcher?.Toggle(); };
        }

        /// <summary>Forme de la bulle Windows (centre et taille, en unites de l'ecran) pour l'animation du menu.</summary>
        public (double cx, double cy, double w, double h) BubbleShape()
            => (Left + _win.X + _win.W / 2, Top + EdgeY + _win.Hh / 2, _win.W, _win.Hh);

        /// <summary>Pendant l'animation, c'est le menu qui dessine la bulle (la goutte qui se detache).</summary>
        public void HideBubble(bool hide)
        {
            _bubbleHidden = hide;
            _win.Content.Opacity = hide ? 0 : 1;
            Geo(_win);
        }

        public void BringToTop()
        {
            if (_hwnd != IntPtr.Zero)
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        private static void Pop(FrameworkElement e, double to)
        {
            if (!(e.RenderTransform is ScaleTransform st)) { st = new ScaleTransform(1, 1); e.RenderTransform = st; e.RenderTransformOrigin = new Point(0.5, 0.5); }
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(420)) { EasingFunction = new SpringEase() };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        // ================== Applis : epinglees + ouvertes ==================

        private sealed class AppItem
        {
            public string Key, Name, Lnk, Exe, Aumid, Pin;
            public bool Pinned;
            public readonly List<IntPtr> Windows = new List<IntPtr>();
        }

        private readonly StackPanel _appsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly Dictionary<string, Border> _dots = new Dictionary<string, Border>();
        private readonly Dictionary<string, ImageSource> _iconCache = new Dictionary<string, ImageSource>();
        private List<AppItem> _items = new List<AppItem>();
        private string _sig = "";
        private int _cycle;

        private static string PinnedFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar");

        private List<AppItem> _pinned;
        private DateTime _pinnedAt;

        /// <summary>Nos epingles (au premier lancement : celles de la barre de Windows).</summary>
        private List<string> Pins()
        {
            var s = Settings.Current;
            if (!s.TaskbarPinsInit)
            {
                s.TaskbarPinsInit = true;
                try
                {
                    if (Directory.Exists(PinnedFolder))
                        foreach (var f in Directory.GetFiles(PinnedFolder, "*.lnk").OrderBy(f => File.GetCreationTime(f)))
                            s.TaskbarPins.Add("lnk|" + f);
                }
                catch { }
                if (s.TaskbarPins.Count == 0)
                    s.TaskbarPins.Add("exe|" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
                s.Save();
            }
            return s.TaskbarPins;
        }

        private List<AppItem> Pinned()
        {
            if (_pinned != null && (DateTime.Now - _pinnedAt).TotalSeconds < 30) return _pinned;
            var list = new List<AppItem>();
            foreach (var p in Pins().ToList())
            {
                var it = FromPin(p);
                if (it != null && !list.Any(x => x.Key == it.Key)) list.Add(it);
            }
            _pinned = list;
            _pinnedAt = DateTime.Now;
            return list;
        }

        private static AppItem FromPin(string p)
        {
            int bar = p.IndexOf('|');
            if (bar < 0) return null;
            string kind = p.Substring(0, bar), v = p.Substring(bar + 1);
            switch (kind)
            {
                case "lnk":
                    if (!File.Exists(v)) return null;
                    string exe = LinkTarget(v);
                    return new AppItem { Key = !string.IsNullOrEmpty(exe) ? exe.ToLowerInvariant() : "lnk:" + v.ToLowerInvariant(), Name = Path.GetFileNameWithoutExtension(v), Lnk = v, Exe = exe, Pinned = true, Pin = p };
                case "exe":
                    if (!File.Exists(v)) return null;
                    string name = Path.GetFileNameWithoutExtension(v);
                    try { var d = FileVersionInfo.GetVersionInfo(v).FileDescription; if (!string.IsNullOrWhiteSpace(d)) name = d; } catch { }
                    return new AppItem { Key = v.ToLowerInvariant(), Name = name, Exe = v, Pinned = true, Pin = p };
                case "app":
                    var e2 = Shortcuts.ResolveExe(v);
                    var entry = AppCatalog.Apps.FirstOrDefault(a => a.Id == v);
                    return new AppItem { Key = e2 != null ? e2.ToLowerInvariant() : "aumid:" + v.ToLowerInvariant(), Name = entry?.Name ?? v, Aumid = v, Exe = e2, Pinned = true, Pin = p };
            }
            return null;
        }

        public bool IsPinned(AppEntry app) => Pins().Contains("app|" + app.Id);

        /// <summary>Epingler / desepingler depuis le menu des applis.</summary>
        public void TogglePin(AppEntry app) => TogglePin("app|" + app.Id);

        private void TogglePin(string pin)
        {
            var s = Settings.Current;
            Pins();
            if (s.TaskbarPins.Contains(pin)) s.TaskbarPins.Remove(pin); else s.TaskbarPins.Add(pin);
            s.Save();
            Log.I("Wave Theme : epingles = " + s.TaskbarPins.Count);
            _pinned = null;
            _sig = "";
            RefreshApps();
        }

        /// <summary>Clic droit sur une icone de la barre : epingler, nouvelle fenetre, fermer.</summary>
        private void ShowMenu(AppItem it, FrameworkElement at)
        {
            var live = _items.FirstOrDefault(x => x.Key == it.Key) ?? it;
            var m = new ContextMenu
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 28, 31, 36)), Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), BorderThickness = new Thickness(1),
                FontFamily = Ui, FontSize = 13, PlacementTarget = at, Placement = System.Windows.Controls.Primitives.PlacementMode.Top
            };
            MenuItem Item(string text, Action a)
            {
                var mi = new MenuItem { Header = text, Foreground = Brushes.White, Padding = new Thickness(6, 6, 12, 6) };
                mi.Click += (x, y) => a();
                return mi;
            }
            m.Items.Add(new MenuItem { Header = live.Name, IsEnabled = false, Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)) });
            m.Items.Add(new Separator());
            string pin = live.Pin ?? (live.Aumid != null ? "app|" + live.Aumid : live.Lnk != null ? "lnk|" + live.Lnk : "exe|" + live.Exe);
            m.Items.Add(Item(live.Pinned ? "Désépingler de la barre des tâches" : "Épingler à la barre des tâches", () => TogglePin(pin)));
            m.Items.Add(Item("Nouvelle fenêtre", () => Launch(live)));
            if (live.Windows.Count > 0)
                m.Items.Add(Item(live.Windows.Count > 1 ? "Fermer toutes les fenêtres" : "Fermer la fenêtre", () =>
                {
                    foreach (var h in live.Windows) PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                }));
            m.IsOpen = true;
        }

        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        private static readonly Dictionary<string, string> TargetCache = new Dictionary<string, string>();
        private static string LinkTarget(string lnk)
        {
            if (TargetCache.TryGetValue(lnk, out var t)) return t;
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                dynamic sh = Activator.CreateInstance(type);
                dynamic sc = sh.CreateShortcut(lnk);
                t = (string)sc.TargetPath;
                Marshal.FinalReleaseComObject(sc);
                Marshal.FinalReleaseComObject(sh);
            }
            catch { t = null; }
            if (t != null && !t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) t = null;
            TargetCache[lnk] = t;
            return t;
        }

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc fn, IntPtr l);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr p, int flags, StringBuilder s, ref int n);
        [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr h, ref Guid iid, out IPropertyStore store);

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            int GetCount(out uint c);
            int GetAt(uint i, out PROPERTYKEY k);
            int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
            int SetValue(ref PROPERTYKEY k, ref PROPVARIANT v);
            int Commit();
        }
        [StructLayout(LayoutKind.Sequential)] private struct PROPERTYKEY { public Guid fmtid; public uint pid; }
        [StructLayout(LayoutKind.Explicit)] private struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; [FieldOffset(16)] public IntPtr p2; }

        private static string WindowAumid(IntPtr h)
        {
            try
            {
                var iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreForWindow(h, ref iid, out var store) != 0 || store == null) return null;
                try
                {
                    var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                    if (store.GetValue(ref key, out var v) != 0) return null;
                    return v.vt == 31 /* VT_LPWSTR */ && v.p != IntPtr.Zero ? Marshal.PtrToStringUni(v.p) : null;
                }
                finally { Marshal.ReleaseComObject(store); }
            }
            catch { return null; }
        }

        private static readonly Dictionary<uint, string> ExeCache = new Dictionary<uint, string>();
        private static string ProcessExe(uint pid)
        {
            if (ExeCache.TryGetValue(pid, out var e)) return e;
            string path = null;
            IntPtr p = OpenProcess(0x1000, false, pid);
            if (p != IntPtr.Zero)
            {
                try { var sb = new StringBuilder(1024); int n = sb.Capacity; if (QueryFullProcessImageName(p, 0, sb, ref n)) path = sb.ToString(); }
                finally { CloseHandle(p); }
            }
            if (ExeCache.Count > 500) ExeCache.Clear();
            ExeCache[pid] = path;
            return path;
        }

        /// <summary>Fenetres qui apparaitraient dans la barre de Windows.</summary>
        /// <summary>Fenetres d'applis ouvertes, de la plus recente (au premier plan) a la plus ancienne.</summary>
        internal static List<(IntPtr h, string exe, string aumid, string title)> AppWindows() => OpenWindows();

        private static List<(IntPtr h, string exe, string aumid, string title)> OpenWindows()
        {
            var list = new List<(IntPtr, string, string, string)>();
            uint me = (uint)Environment.ProcessId;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & 0x40000 /* WS_EX_APPWINDOW */) == 0) return true;
                if (GetWindowTextLength(h) == 0) return true;
                if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == me) return true;
                string exe = ProcessExe(pid);
                if (exe == null) return true;
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, 256);
                string aumid = exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) ? WindowAumid(h) : null;
                list.Add((h, exe, aumid, sb.ToString()));
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private void RefreshApps()
        {
            var items = Pinned().Select(p => new AppItem { Key = p.Key, Name = p.Name, Lnk = p.Lnk, Exe = p.Exe, Aumid = p.Aumid, Pin = p.Pin, Pinned = true }).ToList();
            foreach (var w in OpenWindows())
            {
                string key = w.aumid != null ? "aumid:" + w.aumid.ToLowerInvariant() : w.exe.ToLowerInvariant();
                var it = items.FirstOrDefault(i => i.Key == key);
                if (it == null)
                {
                    it = new AppItem { Key = key, Exe = w.exe, Aumid = w.aumid, Name = w.aumid != null ? w.title : Path.GetFileNameWithoutExtension(w.exe) };
                    try { var d = FileVersionInfo.GetVersionInfo(w.exe).FileDescription; if (!string.IsNullOrWhiteSpace(d) && w.aumid == null) it.Name = d; } catch { }
                    items.Add(it);
                }
                it.Windows.Add(w.h);
            }
            var fg = GetForegroundWindow();
            string sig = string.Join("|", items.Select(i => i.Key));
            _items = items;
            if (sig != _sig)
            {
                _sig = sig;
                BuildApps();
            }
            // Points sous les applis ouvertes (plus large pour celle au premier plan)
            foreach (var it in items)
            {
                if (!_dots.TryGetValue(it.Key, out var dot)) continue;
                bool open = it.Windows.Count > 0, front = it.Windows.Contains(fg);
                double w = front ? 16 : open ? 6 : 0;
                if (Math.Abs(dot.Width - w) > 0.1 || double.IsNaN(dot.Width))
                {
                    var a = new DoubleAnimation(w, TimeSpan.FromMilliseconds(380)) { EasingFunction = new SpringEase() };
                    dot.BeginAnimation(WidthProperty, a);
                }
            }
        }

        private void BuildApps()
        {
            _appsPanel.Children.Clear();
            _dots.Clear();
            _buttons.Clear();
            if (!_apps.Content.Children.Contains(_appsPanel)) _apps.Content.Children.Add(_appsPanel);
            int i = 0;
            foreach (var it in _items) _appsPanel.Children.Add(AppButton(it, i++));
            _appsW.AnimateTo(Math.Max(1, _items.Count) * Cell + 16, Spring.Soft);
            Kick();
        }

        private ImageSource IconOf(AppItem it)
        {
            if (_iconCache.TryGetValue(it.Key, out var img)) return img;
            img = it.Aumid != null ? AppCatalog.LoadIcon(it.Aumid, 64)
                : it.Lnk != null ? AppCatalog.LoadShellIcon(it.Lnk, 64)
                : it.Exe != null ? AppCatalog.LoadShellIcon(it.Exe, 64) : null;
            _iconCache[it.Key] = img;
            return img;
        }

        private FrameworkElement AppButton(AppItem it, int index)
        {
            var g = new Grid { Width = Cell, Height = BarH, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = it.Name };
            var icon = new Image { Width = 28, Height = 28, Source = IconOf(it), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -4, 0, 0) };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            g.Children.Add(icon);
            var dot = new Border
            {
                Height = 3, Width = 0, CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 5),
                Background = _darkInk ? new SolidColorBrush(Color.FromArgb(170, 24, 26, 32)) : new SolidColorBrush(Color.FromArgb(200, 255, 255, 255))
            };
            g.Children.Add(dot);
            _dots[it.Key] = dot;
            _buttons[it.Key] = g;
            g.MouseEnter += (a, b) => Pop(icon, 1.16);
            g.MouseLeave += (a, b) => Pop(icon, 1);
            g.MouseLeftButtonDown += (a, b) => Pop(icon, 0.82);
            g.MouseLeftButtonUp += (a, b) => { Pop(icon, 1.16); Click(it); };
            g.MouseUp += (a, b) => { if (b.ChangedButton == MouseButton.Middle) Launch(it); };
            g.MouseRightButtonUp += (a, b) => { b.Handled = true; ShowMenu(it, g); };
            // Arrivee avec un rebond
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            icon.RenderTransform = new ScaleTransform(0.3, 0.3);
            icon.Opacity = 0;
            var sa = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(520)) { BeginTime = TimeSpan.FromMilliseconds(30 * index), EasingFunction = new SpringEase() };
            ((ScaleTransform)icon.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, sa);
            ((ScaleTransform)icon.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, sa);
            icon.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = TimeSpan.FromMilliseconds(30 * index) });
            return g;
        }

        private void Click(AppItem it)
        {
            var live = _items.FirstOrDefault(x => x.Key == it.Key) ?? it;
            if (live.Windows.Count == 0) { Launch(live); return; }
            var fg = GetForegroundWindow();
            IntPtr h;
            if (live.Windows.Count == 1)
            {
                h = live.Windows[0];
                // Deja devant : on la reduit (comme la barre de Windows)
                if (h == fg) { Native.ShowWindow(h, 6 /* SW_MINIMIZE */); return; }
            }
            else
            {
                int i = live.Windows.IndexOf(fg);
                h = live.Windows[(i + 1 + (i < 0 ? _cycle++ % live.Windows.Count : 0)) % live.Windows.Count];
            }
            if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
            Native.SetForegroundWindow(h);
            Dispatcher.BeginInvoke(new Action(RefreshApps), DispatcherPriority.Background);
        }

        private void Launch(AppItem it)
        {
            try
            {
                if (it.Lnk != null) Process.Start(new ProcessStartInfo(it.Lnk) { UseShellExecute = true });
                else if (it.Aumid != null) Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + it.Aumid) { UseShellExecute = true });
                else if (it.Exe != null) Process.Start(new ProcessStartInfo(it.Exe) { UseShellExecute = true });
                Log.I("Wave Theme : lancement de " + it.Name);
            }
            catch (Exception e) { Log.I("Wave Theme : lancement impossible : " + e.Message); }
        }

        /// <summary>Capture de test : la barre posee sur ce qu'il y a derriere.</summary>
        public void Snap(string name, string folder)
        {
            try
            {
                UpdateLayout();
                Native.GetWindowRect(_hwnd, out var wr);
                int ww = wr.Right - wr.Left, wh = wr.Bottom - wr.Top;
                var bg = new ScreenCapture();
                bg.Grab(wr.Left, wr.Top - (int)(140 * _k), ww, wh + (int)(140 * _k));
                var fg = new RenderTargetBitmap(ww, wh, 96 * _k, 96 * _k, PixelFormats.Pbgra32);
                fg.Render(_root);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawImage(bg.Bitmap, new Rect(0, 0, ww, wh + (int)(140 * _k)));
                    dc.DrawImage(fg, new Rect(0, (int)(140 * _k), ww, wh));
                }
                var outBmp = new RenderTargetBitmap(ww, wh + (int)(140 * _k), 96, 96, PixelFormats.Pbgra32);
                outBmp.Render(dv);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(outBmp));
                Directory.CreateDirectory(folder);
                using var f = File.Create(Path.Combine(folder, name + ".png"));
                enc.Save(f);
            }
            catch (Exception e) { Log.I("Capture barre : " + e.Message); }
        }

        // ================== Wi-Fi, son, batterie et heure ==================

        private static TextBlock Glyph(TextBlock t, string g)
        {
            t.Text = g; t.FontFamily = Icons; t.FontSize = 15; t.Foreground = Brushes.White;
            t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(7, 0, 7, 0);
            t.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.35 };
            return t;
        }

        private void BuildStatus()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Height = BarH, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(Glyph(_wifi, ""));
            row.Children.Add(Glyph(_vol, ""));
            row.Children.Add(Glyph(_bat, ""));
            var host = new Grid { Width = 122, Height = BarH, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Réglages rapides" };
            host.Children.Add(row);
            _status.Content.Children.Add(host);
            host.MouseLeftButtonUp += (a, b) => { Pop(row, 1); TaskbarPanel.Toggle("quick", ScreenRect(_status)); };
            host.MouseLeftButtonDown += (a, b) => Pop(row, 0.9);
            host.MouseEnter += (a, b) => Pop(row, 1.06);
            host.MouseLeave += (a, b) => Pop(row, 1);
            // Molette sur la bulle : le volume
            host.MouseWheel += (a, b) => App.Volume?.Wheel(b.Delta);
        }

        private void BuildClock()
        {
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var (t, size, w) in new[] { (_time, 14.0, FontWeights.SemiBold), (_date, 11.5, FontWeights.Normal) })
            {
                t.FontSize = size; t.FontWeight = w; t.Foreground = Brushes.White; t.HorizontalAlignment = HorizontalAlignment.Center;
                t.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.35 };
                col.Children.Add(t);
            }
            var host = new Grid { Width = 104, Height = BarH, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Notifications et calendrier" };
            host.Children.Add(col);
            _clock.Content.Children.Add(host);
            host.MouseLeftButtonDown += (a, b) => Pop(col, 0.9);
            host.MouseLeftButtonUp += (a, b) => { Pop(col, 1); TaskbarPanel.Toggle("notes", ScreenRect(_clock)); };
            host.MouseEnter += (a, b) => Pop(col, 1.05);
            host.MouseLeave += (a, b) => Pop(col, 1);
        }

        // ================== Mode tablette (4.0) ==================

        /// <summary>Mode tablette : la barre devient un dock facon iPad (il disparait quand une appli est ouverte).</summary>
        public bool Tablet { get; private set; }
        private DateTime _shownUntil = DateTime.MinValue;
        private readonly Dictionary<string, FrameworkElement> _buttons = new Dictionary<string, FrameworkElement>();

        public void SetTablet(bool on)
        {
            if (Tablet == on) return;
            Tablet = on;
            _shownUntil = DateTime.MinValue;
            Layout();
            if (!on && _dropped) Rise();
        }

        /// <summary>Le dock est-il visible a l'ecran ?</summary>
        public bool DockVisible => Active && !_dropped;

        /// <summary>1er glissement du bas : le dock monte par-dessus l'appli (quelques secondes).</summary>
        public void ShowDock(double seconds = 4)
        {
            _shownUntil = DateTime.Now.AddSeconds(seconds);
            Rise();
        }

        /// <summary>Fin d'une animation : le dock suit de nouveau les applis (sans bouger tout de suite).</summary>
        public void ReleaseDock() => _shownUntil = DateTime.MinValue;

        public void HideDock()
        {
            _shownUntil = DateTime.MinValue;
            if (!_dropped) Drop();
        }

        /// <summary>Position (ecran) de l'icone de l'appli de cette fenetre dans le dock, sinon le centre du dock.</summary>
        public Rect IconRect(IntPtr hwnd)
        {
            var it = _items.FirstOrDefault(i => i.Windows.Contains(hwnd));
            if (it != null && _buttons.TryGetValue(it.Key, out var b) && b.IsLoaded)
            {
                try
                {
                    var p = b.TranslatePoint(new Point(0, 0), _root);
                    return new Rect(Left + p.X + 8, Top + EdgeY + 6, Cell - 16, Cell - 16);
                }
                catch { }
            }
            return new Rect(Left + _apps.X + _apps.W / 2 - 20, Top + EdgeY + 6, 40, 40);
        }

        /// <summary>Ouvre le panneau des reglages rapides (« quick ») ou des notifications (« notes »).</summary>
        public void OpenPanel(string kind) => TaskbarPanel.Toggle(kind, ScreenRect(kind == "quick" ? _status : _clock));

        private Rect ScreenRect(Bubble b) => new Rect(Left + b.X, Top + EdgeY, b.W, b.Hh);

        // ================== Disparition en goutte (option) ==================

        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);

        /// <summary>Une appli occupe tout l'ecran (agrandie) : la barre peut descendre.</summary>
        private bool AppCovers()
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == _hwnd) return false;
            GetWindowThreadProcessId(fg, out uint pid);
            if (pid == (uint)Environment.ProcessId) return false;
            var cls = new StringBuilder(64);
            GetClassName(fg, cls, 64);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false;
            if (IsZoomed(fg)) return true;
            if (!Native.GetWindowRect(fg, out var r)) return false;
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            return r.Left <= sb.Left + 4 && r.Right >= sb.Right - 4 && r.Bottom >= sb.Bottom - (int)(H * _k);
        }

        private void AutoHide()
        {
            if (!Active) return;
            bool panelOpen = TaskbarPanel.Current != null || (App.Launcher?.IsOpen ?? false);
            // Mode tablette (4.0) : le dock disparait des qu'une appli est ouverte ; il revient d'un glissement du bas
            bool want = Tablet
                ? !panelOpen && AppCovers() && DateTime.Now > _shownUntil
                : Settings.Current.TaskbarAutoHide && !panelOpen && AppCovers();
            if (Tablet && want && !_dropped) { Drop(); return; }
            if (!want) { _awaySince = DateTime.MaxValue; if (_dropped) Rise(); return; }
            Native.GetCursorPos(out var p);
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            bool atBottom = p.Y >= sb.Bottom - 3 && p.X >= sb.Left && p.X < sb.Right;
            bool overBar = p.Y >= sb.Bottom - (int)((H + 20) * _k);
            if (_dropped)
            {
                // Souris tout en bas : la barre ressort par-dessus l'appli
                if (atBottom) { _awaySince = DateTime.MaxValue; Rise(); }
                return;
            }
            if (overBar) { _awaySince = DateTime.MaxValue; return; }
            if (_awaySince == DateTime.MaxValue) _awaySince = DateTime.Now;
            if ((DateTime.Now - _awaySince).TotalMilliseconds > 700) Drop();
        }

        /// <summary>La barre s'etire comme une goutte puis glisse sous le bord de l'ecran.</summary>
        public async void Drop()
        {
            if (_dropped) return;
            _dropped = true;
            Log.I("Wave Theme : la barre descend (appli en grand ecran)");
            _stretch.AnimateTo(1.22, new Spring(0.7, 0.2));
            Kick();
            await System.Threading.Tasks.Task.Delay(110);
            if (!_dropped) return;
            _off.AnimateTo(H + 24, new Spring(0.9, 0.34));
            _stretch.AnimateTo(1.0, new Spring(0.8, 0.4));
            Kick();
        }

        /// <summary>Elle remonte par-dessus l'appli, s'ecrase un peu puis rebondit a sa forme.</summary>
        public void Rise()
        {
            if (!_dropped) return;
            _dropped = false;
            Log.I("Wave Theme : la barre remonte");
            _stretch.Set(1.25);
            _off.AnimateTo(0, Spring.Pop);
            _stretch.AnimateTo(1.0, new Spring(0.45, 0.5));
            Kick();
            BringToTop();
        }

        /// <summary>Win + touche (les touches injectees ne passent pas par le filtre de la touche Windows).</summary>
        private static void Keys(byte vk)
        {
            Native.keybd_event((byte)Native.VK_LWIN, 0, 0, UIntPtr.Zero);
            Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
            Native.keybd_event(vk, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Native.keybd_event((byte)Native.VK_LWIN, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private void Tick()
        {
            var now = DateTime.Now;
            _time.Text = now.ToString("HH:mm");
            _date.Text = now.ToString("dd/MM/yyyy");

            // Reseau
            bool wifi = false, eth = false;
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != OperationalStatus.Up) continue;
                    if (n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) wifi = true;
                    else if (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet && !n.Description.Contains("Virtual") && !n.Description.Contains("Hyper-V")) eth = true;
                }
            }
            catch { }
            _wifi.Text = wifi ? "" : eth ? "" : "";
            _wifi.Opacity = wifi || eth ? 1 : 0.55;

            // Son
            var (lvl, muted) = App.Volume?.Read() ?? (0.5f, false);
            _vol.Text = muted || lvl < 0.01 ? "" : lvl < 0.34 ? "" : lvl < 0.67 ? "" : "";
            _status.Content.ToolTip = $"Volume {Math.Round(lvl * 100)} %";

            // Batterie (cachee sur un PC de bureau)
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            bool hasBat = (ps.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryLifePercent <= 1.0f;
            _bat.Visibility = hasBat ? Visibility.Visible : Visibility.Collapsed;
            if (hasBat)
            {
                int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
                bool charging = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                int step = Math.Max(0, Math.Min(10, pct / 10));
                _bat.Text = ((char)((charging ? 0xE85A : 0xE850) + step)).ToString();
                _bat.ToolTip = $"Batterie {pct} %{(charging ? " (en charge)" : "")}";
            }

            // La barre de Windows ne doit pas revenir
            if (_tray != IntPtr.Zero && IsWindowVisible(_tray)) Native.ShowWindow(_tray, 0);
            RefreshApps();
            if (!_animating) Layout();
        }
    }
}
