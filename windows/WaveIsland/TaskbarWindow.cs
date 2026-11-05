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
        private const double H = 64, BarH = 52, Gap = 8, Cell = 48;
        /// <summary>Marge invisible au-dessus des bulles : la place pour que la goutte s'etire vers le haut.</summary>
        private const double Pad = 54, WH = H + Pad, EdgeY = Pad + 6;

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
            // Le verre n'est calcule que sur la zone de la bulle (avant : sur toute la largeur de l'ecran, 4 fois)
            public readonly System.Windows.Shapes.Rectangle Img = new System.Windows.Shapes.Rectangle { IsHitTestVisible = false };
            public readonly ImageBrush Brush = new ImageBrush { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute };
            public BitmapSource Source;
            public readonly GlassEffect Fx = new GlassEffect();
            public readonly Canvas Content = new Canvas { Background = Brushes.Transparent };
            public double X, W, Hh = BarH;
            /// <summary>Descente (0 = en place) et etirement de la goutte (1 = forme normale) de cette bulle.</summary>
            public readonly SpringValue Off = new SpringValue(0), St = new SpringValue(1);
            public Bubble() { Img.Effect = Fx; Img.Fill = Brush; }
        }

        private readonly Bubble _win = new Bubble(), _apps = new Bubble(), _status = new Bubble(), _clock = new Bubble();
        private readonly SpringValue _appsW = new SpringValue(Cell + 16);
        private bool _dropped;
        private DateTime _riseAt = DateTime.MinValue;
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

            _capTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(66) };
            _capTimer.Tick += (a, b) => Capture();
            _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _tick.Tick += (a, b) => Tick();

            // Glisser une appli (menu Windows) ou un raccourci / programme (bureau, Explorateur) sur la barre : epinglee
            AllowDrop = true;
            DragOver += (a, e) =>
            {
                bool ok = e.Data.GetDataPresent("WaveIslandApp") || e.Data.GetDataPresent(DataFormats.FileDrop);
                e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            DragEnter += (a, e) => Pop(_appsPanel, 1.06);
            DragLeave += (a, e) => Pop(_appsPanel, 1);
            base.Drop += (a, e) => { Pop(_appsPanel, 1); DropPin(e.Data); e.Handled = true; };

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
            Height = WH;
            Left = sb.Left / _k;
            Top = (sb.Bottom / _k) - WH;
            foreach (var b in new[] { _win, _apps, _status, _clock }) { b.Img.Height = WH; }
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
            int hp = (int)Math.Round(WH * _k);
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
            // Nos propres animations plein ecran (vue des applis, fermeture, geste retour) ne doivent pas cacher le dock,
            // et en mode tablette c'est le glissement du bas qui decide (le dock descend deja sur les applis)
            if (full && (Tablet || AppMotion.Current != null || OwnWindowCovers()))
            {
                Log.I("Wave Theme : plein ecran ignore (animation de Wave Island ou mode tablette)");
                if (_hiddenFull) { _hiddenFull = false; Visibility = Visibility.Visible; }
                return;
            }
            if (full == _hiddenFull) return;
            _hiddenFull = full;
            // Une appli en plein ecran (video, jeu) : la barre s'efface comme celle de Windows
            Visibility = full ? Visibility.Hidden : Visibility.Visible;
            if (!full && _hwnd != IntPtr.Zero)
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);

        /// <summary>La fenetre plein ecran au milieu de l'ecran est-elle a Wave Island ?</summary>
        private static bool OwnWindowCovers()
        {
            try
            {
                var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                var h = GetAncestor(WindowFromPoint(new Native.POINT { X = (sb.Left + sb.Right) / 2, Y = (sb.Top + sb.Bottom) / 2 }), 2 /* GA_ROOT */);
                GetWindowThreadProcessId(h, out uint pid);
                return pid == (uint)Environment.ProcessId;
            }
            catch { return false; }
        }

        // ================== Verre liquide ==================

        private void Capture()
        {
            if (_hwnd == IntPtr.Zero || _hiddenFull) return;
            if (!_excluded || !Settings.Current.Refraction)
            {
                if (_win.Source == null)
                {
                    var px = new byte[] { 40, 34, 30, 255 };
                    var flat = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, px, 4);
                    flat.Freeze();
                    foreach (var b in new[] { _win, _apps, _status, _clock })
                    {
                        b.Source = flat; b.Brush.ImageSource = flat;
                        b.Brush.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox; b.Brush.Viewbox = new Rect(0, 0, 1, 1);
                    }
                }
                return;
            }
            if (!Native.GetWindowRect(_hwnd, out var r)) return;
            try
            {
                if (_cap.Grab(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) && _win.Source != _cap.Bitmap)
                    foreach (var b in new[] { _win, _apps, _status, _clock })
                    {
                        b.Source = _cap.Bitmap; b.Brush.ImageSource = _cap.Bitmap;
                        b.Brush.ViewboxUnits = BrushMappingMode.Absolute;
                        Geo(b);
                    }
                ApplyInk();
            }
            catch { }
        }

        private bool _darkInk;
        /// <summary>Fond tres clair derriere la barre : textes et icones passent en fonce.</summary>
        private void ApplyInk()
        {
            bool dark = _darkInk ? _cap.Luma > 0.5 : _cap.Luma > 0.6;
            if (dark == _darkInk) return;
            _darkInk = dark;
            var ink = dark ? new SolidColorBrush(Color.FromRgb(24, 26, 32)) : Brushes.White;
            foreach (var t in new[] { _time, _date, _wifi, _vol, _bat }) t.Foreground = ink;
            foreach (var r in _winSquares) r.Fill = ink;
            foreach (var d in _dots.Values) d.Background = dark ? new SolidColorBrush(Color.FromArgb(170, 24, 26, 32)) : new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
        }

        private void Geo(Bubble b)
        {
            double k = _k, Hp = WH * k;
            // Goutte : la bulle s'etire vers le haut et s'affine (ou s'ecrase et s'elargit au rebond)
            double st = Math.Max(0.55, b.St.Value), off = b.Off.Value;
            double w = b.W / Math.Sqrt(st), h = b.Hh * st, cx = b.X + b.W / 2, cy = EdgeY + b.Hh - h / 2 + off;
            double rad = Math.Min(h / 2, w / 2);
            const double mag = 0.95;
            // Zone de calcul du verre : la bulle et une petite marge (les bords courbent la lumiere de juste a cote)
            double half = Math.Max(w, b.W) / 2 + 22;
            double rx0 = Math.Max(0, cx - half), rx1 = Math.Min(_w, cx + half), rw = Math.Max(4, rx1 - rx0);
            Canvas.SetLeft(b.Img, rx0);
            b.Img.Width = rw;
            if (b.Brush.ViewboxUnits == BrushMappingMode.Absolute) b.Brush.Viewbox = new Rect(rx0 * k, 0, rw * k, Hp);
            double W = rw * k, lx = (cx - rx0) * k;
            b.Fx.Geo = new Point4D(lx, cy * k, Math.Max(0, w / 2 - rad) * k, Math.Max(0, h / 2 - rad) * k);
            b.Fx.Sizes = new Point4D(W, Hp, rad * k, 1.0 / (Math.Min(18, h * 0.42) * k));
            double strength = 14 * k;
            b.Fx.Bend = new Point4D(strength / W, strength / Hp, lx / W * (1 - mag), cy * k / Hp * (1 - mag));
            b.Fx.Tint = new Point4D(0.05, 0.06, 0.08, 0);   // verre transparent : ni voile ni flou
            b.Fx.Disp = new Point4D(1.14, 0.86, 0, 0);
            b.Fx.Light = new Point4D(-0.5, -0.866, b == _win && _bubbleHidden ? 0 : 1, mag);
            Canvas.SetLeft(b.Content, b.X);
            Canvas.SetTop(b.Content, EdgeY + off);
            b.Content.Width = b.W;
            b.Content.Height = b.Hh;
            b.Content.Clip = new RectangleGeometry(new Rect((b.W - w) / 2, b.Hh - h, w, h), rad, rad);
            b.Content.Opacity = (b == _win && _bubbleHidden) ? 0 : Math.Max(0, Math.Min(1, 1 - off / (H * 0.7)) * Math.Min(1, 2.2 - st));
            b.Content.IsHitTestVisible = off < 10;
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
            foreach (var b in new[] { _win, _apps, _status, _clock }) Geo(b);
            // Chaque icone a une place qui peut s'ouvrir ou se refermer (appli rangee / affichee en apercu)
            double sum = 0;
            foreach (var it in _items)
            {
                if (!_buttons.TryGetValue(it.Key, out var btn)) continue;
                double f = _slots.TryGetValue(it.Key, out var sv) ? Math.Max(0, sv.Value) : 1;
                double bw = Cell * f;
                if (Math.Abs(btn.Width - bw) > 0.05) btn.Width = bw;
                var vis = bw < 0.5 ? Visibility.Collapsed : Visibility.Visible;
                if (btn.Visibility != vis) btn.Visibility = vis;
                if (btn is Grid gg && gg.Children.Count > 0 && f < 0.999)
                {
                    // Place qui s'ouvre / se ferme : l'icone apparait avec elle (on retire l'animation d'arrivee qui la garderait visible)
                    var ic = gg.Children[0];
                    if (ic.HasAnimatedProperties) ic.BeginAnimation(OpacityProperty, null);
                    ic.Opacity = Math.Max(0, Math.Min(1, f * 1.6 - 0.6));
                }
                else if (btn is Grid g2 && g2.Children.Count > 0 && !g2.Children[0].HasAnimatedProperties && g2.Children[0].Opacity < 1) g2.Children[0].Opacity = 1;
                sum += bw;
            }
            // Les icones restent centrees dans la barre pendant qu'elle s'elargit
            Canvas.SetLeft(_appsPanel, Math.Round((appsW - sum) / 2));
        }

        private void Kick()
        {
            if (_animating) return;
            _animating = true;
            _capTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Min(60, Frames.Fps));
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object s, EventArgs e)
        {
            if (!_gate.Ready()) return;
            _appsW.Tick();
            bool moving = _appsW.Moving;
            foreach (var sv in _slots.Values) { sv.Tick(); moving |= sv.Moving; }
            foreach (var b in new[] { _win, _apps, _status, _clock }) { b.Off.Tick(); b.St.Tick(); moving |= b.Off.Moving || b.St.Moving; }
            Layout();
            if (!moving)
            {
                CompositionTarget.Rendering -= OnFrame; _animating = false;
                // Au repos : le fond n'est recopie que 15 fois par seconde (economise le processeur des petits PC)
                _capTimer.Interval = TimeSpan.FromMilliseconds(66);
            }
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
        private bool _winAway;

        /// <summary>
        /// Vue des applis ouvertes (mode tablette) : le dock reste, seule la bulle Windows descend en goutte ;
        /// elle remonte quand on quitte la vue.
        /// </summary>
        public void WinAway(bool away)
        {
            if (_winAway == away) return;
            _winAway = away;
            if (away)
            {
                _win.St.AnimateTo(1.45, new Spring(0.62, 0.22));
                _win.Off.AnimateTo(H + 40, new Spring(0.85, 0.38));
            }
            else if (!_dropped)
            {
                _win.St.Set(1.7);
                _win.Off.AnimateTo(0, new Spring(0.5, 0.42));
                _win.St.AnimateTo(1.0, new Spring(0.38, 0.5));
            }
            Kick();
        }

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
            string pin = live.Pin ?? (live.Aumid != null ? "app|" + live.Aumid : live.Lnk != null ? "lnk|" + live.Lnk : "exe|" + live.Exe);
            var items = new List<(string, string, Action)>
            {
                (live.Pinned ? "\uE77A" : "\uE718", live.Pinned ? "Désépingler du dock" : "Épingler au dock", () => TogglePin(pin)),
                ("\uE8A7", "Nouvelle fenêtre", () => Launch(live)),
            };
            if (live.Windows.Count > 0)
                items.Add(("\uE8BB", live.Windows.Count > 1 ? "Fermer toutes les fenêtres" : "Fermer la fenêtre", () =>
                {
                    foreach (var h in live.Windows) PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                }));
            Point p;
            try { p = at.TranslatePoint(new Point(0, 0), _root); } catch { p = new Point(_apps.X, 0); }
            TaskbarPanel.Menu(new Rect(Left + p.X - 120, Top + EdgeY, 290, BarH), live.Name, items.ToArray());
        }

        /// <summary>Icone de l'appli de cette fenetre (pour les apercus qui se changent en icone).</summary>
        public ImageSource IconFor(IntPtr hwnd)
        {
            var it = _items.FirstOrDefault(i => i.Windows.Contains(hwnd));
            return it != null ? IconOf(it) : null;
        }

        /// <summary>L'appli vient de tomber dans son icone : l'icone rebondit (comme une goutte qui tombe dans l'eau).</summary>
        public void Splash(IntPtr hwnd)
        {
            var it = _items.FirstOrDefault(i => i.Windows.Contains(hwnd));
            if (it == null || !_buttons.TryGetValue(it.Key, out var b) || !(b is Grid g) || g.Children.Count == 0) return;
            if (!(g.Children[0] is FrameworkElement icon)) return;
            if (!(icon.RenderTransform is ScaleTransform st)) { st = new ScaleTransform(1, 1); icon.RenderTransform = st; icon.RenderTransformOrigin = new Point(0.5, 0.5); }
            var a = new DoubleAnimation(1.35, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = new SpringEase() };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.75, 1, TimeSpan.FromMilliseconds(560)) { EasingFunction = new SpringEase() });
        }

        private void DropPin(IDataObject d)
        {
            try
            {
                var pins = Pins();
                if (d.GetDataPresent("WaveIslandApp") && d.GetData("WaveIslandApp") is string id)
                {
                    if (!pins.Contains("app|" + id)) TogglePin("app|" + id);
                    Log.I("Wave Theme : appli epinglee par glisser-deposer");
                    return;
                }
                if (d.GetData(DataFormats.FileDrop) is string[] files)
                    foreach (var f in files)
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        string pin = ext == ".lnk" ? "lnk|" + f : ext == ".exe" ? "exe|" + f : null;
                        if (pin != null && !Pins().Contains(pin)) TogglePin(pin);
                    }
            }
            catch (Exception e) { Log.I("Wave Theme : depot impossible : " + e.Message); }
        }

        /// <summary>Applis epinglees au dock (pour les reglages) : nom + valeur interne.</summary>
        public List<(string name, string pin)> PinnedList() => Pins().Select(p => (FromPin(p)?.Name ?? p, p)).ToList();

        /// <summary>Retire / ajoute une appli du dock (reglages).</summary>
        public void Unpin(string pin) { if (Pins().Contains(pin)) TogglePin(pin); }
        public void PinApp(AppEntry app) { if (!IsPinned(app)) TogglePin("app|" + app.Id); }

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
        [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IVirtualDesktopManager
        {
            [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool onCurrent);
            [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
            [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
        }
        private static IVirtualDesktopManager _vdm;
        private static bool _vdmFailed;

        private static bool OnCurrentDesktop(IntPtr h)
        {
            if (_vdmFailed) return true;
            try
            {
                _vdm ??= (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")));
                return _vdm.IsWindowOnCurrentVirtualDesktop(h, out bool on) != 0 || on;
            }
            catch { _vdmFailed = true; return true; }
        }

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
                // Plusieurs bureaux : seulement les fenetres du bureau affiche
                if (!OnCurrentDesktop(h)) return true;
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

        private readonly List<string> _runOrder = new List<string>();

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
            // Ordre stable : les epinglees dans leur ordre, puis les autres dans l'ordre ou elles se sont ouvertes
            // (avant, elles suivaient l'ordre des fenetres a l'ecran et changeaient de place a chaque clic)
            foreach (var it in items) if (!it.Pinned && !_runOrder.Contains(it.Key)) _runOrder.Add(it.Key);
            _runOrder.RemoveAll(k => !items.Any(i => i.Key == k && !i.Pinned));
            items = items.Where(i => i.Pinned).Concat(items.Where(i => !i.Pinned).OrderBy(i => _runOrder.IndexOf(i.Key))).ToList();
            foreach (var it in items) it.Windows.Sort((x, y) => x.ToInt64().CompareTo(y.ToInt64()));
            var fg = GetForegroundWindow();
            string sig = string.Join("|", items.Select(i => i.Key));
            _items = items;
            if (sig != _sig)
            {
                _sig = sig;
                BuildApps();
            }
            else UpdateSlots(animate: !_dropped);
            _hiddenWins.RemoveWhere(h => !items.Any(i => i.Windows.Contains(h)));
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
            // Les icones deja la gardent leur bouton (seules les nouvelles arrivent avec un rebond)
            var old = new Dictionary<string, FrameworkElement>(_buttons);
            var oldDots = new Dictionary<string, Border>(_dots);
            _appsPanel.Children.Clear();
            _dots.Clear();
            _buttons.Clear();
            if (!_apps.Content.Children.Contains(_appsPanel)) _apps.Content.Children.Add(_appsPanel);
            int i = 0;
            foreach (var it in _items)
            {
                if (old.TryGetValue(it.Key, out var b) && oldDots.TryGetValue(it.Key, out var d))
                {
                    _buttons[it.Key] = b; _dots[it.Key] = d;
                    _appsPanel.Children.Add(b);
                }
                else _appsPanel.Children.Add(AppButton(it, i++));
            }
            foreach (var k in _slots.Keys.Where(k => !_items.Any(x => x.Key == k)).ToList()) _slots.Remove(k);
            UpdateSlots(animate: true);
        }

        // ================== Places des icones (mode tablette) ==================
        // Les applis montrees en apercu (vue des applis, appli en cours au 1er glissement) n'ont pas d'icone dans le dock :
        // quand elles y sont rangees, leur place s'ouvre et l'icone vient s'y poser.

        private readonly Dictionary<string, SpringValue> _slots = new Dictionary<string, SpringValue>();
        private readonly HashSet<IntPtr> _hiddenWins = new HashSet<IntPtr>();
        private static readonly Spring SlotSpring = new Spring(0.62, 0.42);

        private bool IsHidden(AppItem it) => !it.Pinned && it.Windows.Count > 0 && it.Windows.All(w => _hiddenWins.Contains(w));

        private double Target(AppItem it) => IsHidden(it) ? 0 : 1;

        private void UpdateSlots(bool animate)
        {
            double total = 0;
            foreach (var it in _items)
            {
                double t = Target(it);
                total += t;
                if (!_slots.TryGetValue(it.Key, out var sv)) { _slots[it.Key] = sv = new SpringValue(t); continue; }
                if (Math.Abs(sv.Target - t) < 0.001) continue;
                if (animate) sv.AnimateTo(t, SlotSpring); else sv.Set(t);
            }
            double w = Math.Max(1, total) * Cell + 16;
            if (animate) _appsW.AnimateTo(w, Spring.Soft); else _appsW.Set(w);
            Kick();
        }

        /// <summary>Ces fenetres sont montrees en apercu : leurs icones sortent du dock (sans animation si le dock est cache).</summary>
        public void HideWindows(IEnumerable<IntPtr> hs)
        {
            bool changed = false;
            foreach (var h in hs) if (h != IntPtr.Zero && _hiddenWins.Add(h)) changed = true;
            if (changed) UpdateSlots(animate: !_dropped);
        }

        /// <summary>Ces fenetres rentrent dans le dock : leur place s'ouvre avec un rebond.</summary>
        public void RevealWindows(IEnumerable<IntPtr> hs)
        {
            bool changed = false;
            foreach (var h in hs) if (_hiddenWins.Remove(h)) changed = true;
            if (changed) UpdateSlots(animate: !_dropped);
        }

        /// <summary>Tout revient (fin de geste). Sans animation quand le dock est cache.</summary>
        public void RevealAll(bool animate)
        {
            if (_hiddenWins.Count == 0) return;
            _hiddenWins.Clear();
            UpdateSlots(animate && !_dropped);
        }

        /// <summary>Position (ecran) ou l'icone de cette fenetre sera une fois les places ouvertes (pour y faire voler l'appli).</summary>
        public Rect FinalIconRect(IntPtr hwnd)
        {
            var it = _items.FirstOrDefault(i => i.Windows.Contains(hwnd));
            if (it == null) return IconRect(hwnd);
            double before = 0, total = 0;
            bool found = false;
            foreach (var x in _items)
            {
                double t = Target(x);
                if (x == it) found = true;
                else if (!found) before += t;
                total += t;
            }
            double appsW = Math.Max(1, total) * Cell + 16;
            double gx = Math.Round((_w - (BarH + Gap + appsW)) / 2);
            double appsX = gx + BarH + Gap;
            double x0 = appsX + Math.Round((appsW - total * Cell) / 2) + before * Cell;
            return new Rect(Left + x0 + 8, Top + EdgeY + 6, Cell - 16, Cell - 16);
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
            // Mode tablette : l'appli ressort de son icone en goutte
            if (TabletShell.Active && AppMotion.Current == null)
            {
                var fg0 = GetForegroundWindow();
                if (live.Windows.Count == 0)
                {
                    var r0 = _buttons.TryGetValue(live.Key, out var b0) ? IconRectOf(b0) : IconRect(IntPtr.Zero);
                    AppMotion.OpenFromDock(IntPtr.Zero, r0, IconOf(live), () => Launch(live));
                    return;
                }
                if (!live.Windows.Contains(fg0))
                {
                    var h0 = live.Windows[0];
                    AppMotion.OpenFromDock(h0, IconRect(h0), IconOf(live), () =>
                    {
                        if (Native.IsIconic(h0)) Native.ShowWindow(h0, Native.SW_RESTORE);
                        Native.SetForegroundWindow(h0);
                    });
                    return;
                }
            }
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
            BringToTop();
        }

        /// <summary>Temps (ms) avant que le dock soit completement sorti et pose (0 s'il est deja la).</summary>
        public int DockSettleMs()
        {
            if (_dropped) return 800;
            double done = (DateTime.Now - _riseAt).TotalMilliseconds;
            return (int)Math.Max(0, 700 - done);
        }

        /// <summary>Fin d'une animation : le dock suit de nouveau les applis (sans bouger tout de suite).</summary>
        public void ReleaseDock() => _shownUntil = DateTime.MinValue;

        public void HideDock()
        {
            _shownUntil = DateTime.MinValue;
            if (!_dropped) Drop();
        }

        private Rect IconRectOf(FrameworkElement b)
        {
            try { var p = b.TranslatePoint(new Point(0, 0), _root); return new Rect(Left + p.X + 8, Top + EdgeY + 6, Cell - 16, Cell - 16); }
            catch { return IconRect(IntPtr.Zero); }
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
            // Une appli reduite (rangee dans le dock) peut rester « au premier plan » : on est sur le bureau
            if (Native.IsIconic(fg)) return false;
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

        /// <summary>Les bulles, de la plus proche du centre de l'ecran a la plus eloignee (la goutte part du milieu).</summary>
        private IEnumerable<(Bubble b, int order)> FromCenter()
            => new[] { _win, _apps, _status, _clock }.OrderBy(x => Math.Abs(x.X + x.W / 2 - _w / 2)).Select((x, i) => (x, i));

        /// <summary>
        /// Goutte : chaque bulle s'etire vers le haut en s'affinant (la goutte se forme), puis tombe sous le bord
        /// de l'ecran en s'allongeant. Les bulles partent l'une apres l'autre, depuis le centre.
        /// </summary>
        public void Drop()
        {
            if (_dropped) return;
            _dropped = true;
            Log.I("Wave Theme : la barre descend en goutte");
            // Dock cache : les icones retirees reviennent (sans animation, invisible)
            if (_hiddenWins.Count > 0 && AppMotion.Current == null) { _hiddenWins.Clear(); UpdateSlots(animate: false); }
            foreach (var (b, i) in FromCenter()) DropOne(b, i * 55);
        }

        private async void DropOne(Bubble b, int delayMs)
        {
            if (delayMs > 0) await System.Threading.Tasks.Task.Delay(delayMs);
            if (!_dropped) return;
            b.St.AnimateTo(1.45, new Spring(0.62, 0.22));
            b.Off.AnimateTo(-6, new Spring(0.7, 0.22));
            Kick();
            await System.Threading.Tasks.Task.Delay(130);
            if (!_dropped) return;
            b.Off.AnimateTo(H + 40, new Spring(0.9, 0.38));
            b.St.AnimateTo(1.75, new Spring(0.85, 0.4));
            Kick();
        }

        /// <summary>Elles remontent par-dessus l'appli, depassent un peu, s'ecrasent puis rebondissent a leur forme.</summary>
        public void Rise()
        {
            if (!_dropped) return;
            _dropped = false;
            _riseAt = DateTime.Now;
            Log.I("Wave Theme : la barre remonte en goutte");
            foreach (var (b, i) in FromCenter()) RiseOne(b, i * 45);
            BringToTop();
        }

        private async void RiseOne(Bubble b, int delayMs)
        {
            if (delayMs > 0) await System.Threading.Tasks.Task.Delay(delayMs);
            if (_dropped) return;
            if (b == _win && _winAway) return;   // la bulle Windows reste cachee pendant la vue des applis
            b.St.Set(1.7);
            b.Off.AnimateTo(0, new Spring(0.5, 0.42));
            b.St.AnimateTo(1.0, new Spring(0.38, 0.5));
            Kick();
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
