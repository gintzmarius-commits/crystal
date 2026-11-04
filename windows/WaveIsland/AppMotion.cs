using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WaveIsland
{
    /// <summary>
    /// Mode tablette (4.0) : l'appli qui se ferme suit le doigt, puis rentre dans son icone du dock (iPad ou goutte) ;
    /// la vue de toutes les applis ouvertes (cartes superposees facon iPhone).
    /// Les apercus sont des vignettes en direct de Windows (DWM) : elles bougent sans copier les images.
    /// </summary>
    public sealed class AppMotion : Window
    {
        public static AppMotion Current { get; private set; }

        private sealed class Card
        {
            public IntPtr Src, Thumb;
            public readonly SpringValue X = new SpringValue(0), Y = new SpringValue(0), W = new SpringValue(0), H = new SpringValue(0), Op = new SpringValue(1);
            public Border Back;
            public TextBlock Label;
            public string Title;
        }

        private readonly Canvas _canvas = new Canvas();
        private readonly List<Card> _cards = new List<Card>();
        private readonly IntPtr _app;
        private IntPtr _hwnd;
        private double _k = 1, _sw, _sh;
        private bool _animating, _closing;
        private readonly FrameGate _gate = new FrameGate();
        public bool Switcher { get; private set; }
        private double _scroll;

        // ================== DWM : vignettes en direct ==================

        [StructLayout(LayoutKind.Sequential)]
        private struct THUMBPROPS
        {
            public int dwFlags;
            public Native.RECT rcDestination;
            public Native.RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }
        [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref THUMBPROPS p);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SystemParametersInfo(int a, int b, StringBuilder s, int c);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public static void Begin(IntPtr app)
        {
            if (Current != null) return;
            var m = new AppMotion(app);
            Current = m;
            m.Open();
        }

        private AppMotion(IntPtr app)
        {
            _app = app;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Background = Brushes.Black;
            Content = _canvas;
            _canvas.Background = Wallpaper();
            SourceInitialized += (a, b) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            };
            // Dans la vue des applis : glisser, toucher, glisser vers le bas
            MouseLeftButtonDown += (a, e) => { if (e.StylusDevice != null) return; CaptureMouse(); PDown(e.GetPosition(this)); };
            MouseMove += (a, e) => { if (IsMouseCaptured) PMove(e.GetPosition(this)); };
            MouseLeftButtonUp += (a, e) => { if (!IsMouseCaptured) return; ReleaseMouseCapture(); PUp(e.GetPosition(this)); };
            TouchDown += (a, e) => { CaptureTouch(e.TouchDevice); PDown(e.GetTouchPoint(this).Position); e.Handled = true; };
            TouchMove += (a, e) => { PMove(e.GetTouchPoint(this).Position); e.Handled = true; };
            TouchUp += (a, e) => { ReleaseTouchCapture(e.TouchDevice); PUp(e.GetTouchPoint(this).Position); e.Handled = true; };
        }

        /// <summary>Le fond d'ecran derriere les applis (comme l'ecran d'accueil de l'iPad).</summary>
        private static Brush Wallpaper()
        {
            try
            {
                var sb = new StringBuilder(520);
                if (SystemParametersInfo(0x73 /* SPI_GETDESKWALLPAPER */, 520, sb, 0) && File.Exists(sb.ToString()))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(sb.ToString());
                    bmp.DecodePixelWidth = 1600;
                    bmp.EndInit();
                    bmp.Freeze();
                    return new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                }
            }
            catch { }
            return new LinearGradientBrush(Color.FromRgb(40, 60, 110), Color.FromRgb(120, 60, 140), 45);
        }

        private void Open()
        {
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            Show();
            _k = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _sw = sb.Width / _k; _sh = sb.Height / _k;
            Left = sb.Left / _k; Top = sb.Top / _k; Width = _sw; Height = _sh;
            var c = NewCard(_app);
            c.X.Set(0); c.Y.Set(0); c.W.Set(_sw); c.H.Set(_sh);
            Render();
            // Le dock (et la petite barre) restent au-dessus, comme sur iPad
            App.Taskbar?.ShowDock(30);
            App.Taskbar?.BringToTop();
            App.Island?.BringToTop();
        }

        private Card NewCard(IntPtr src)
        {
            var c = new Card { Src = src };
            var title = new StringBuilder(256);
            GetWindowText(src, title, 256);
            c.Title = title.ToString();
            // Sous la vignette : une carte (visible si l'appli est reduite et n'a pas d'apercu)
            c.Back = new Border
            {
                CornerRadius = new CornerRadius(18), Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = c.Title, Foreground = Brushes.White, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(16) }
            };
            c.Label = new TextBlock { Text = c.Title, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.5 } };
            _canvas.Children.Add(c.Back);
            _canvas.Children.Add(c.Label);
            if (_hwnd != IntPtr.Zero && DwmRegisterThumbnail(_hwnd, src, out var t) == 0) c.Thumb = t;
            _cards.Add(c);
            return c;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

        private void Render()
        {
            foreach (var c in _cards)
            {
                double x = c.X.Value, y = c.Y.Value, w = Math.Max(1, c.W.Value), h = Math.Max(1, c.H.Value), op = Math.Max(0, Math.Min(1, c.Op.Value));
                Canvas.SetLeft(c.Back, x); Canvas.SetTop(c.Back, y);
                c.Back.Width = w; c.Back.Height = h; c.Back.Opacity = op;
                Canvas.SetLeft(c.Label, x + 6); Canvas.SetTop(c.Label, y - 24);
                c.Label.MaxWidth = Math.Max(10, w - 12);
                c.Label.Opacity = Switcher ? op : 0;
                if (c.Thumb == IntPtr.Zero) continue;
                var p = new THUMBPROPS
                {
                    dwFlags = 0x1 | 0x4 | 0x8 | 0x10, // destination, opacite, visible, zone client
                    rcDestination = new Native.RECT { Left = (int)(x * _k), Top = (int)(y * _k), Right = (int)((x + w) * _k), Bottom = (int)((y + h) * _k) },
                    opacity = (byte)(255 * op), fVisible = op > 0.01, fSourceClientAreaOnly = false
                };
                DwmUpdateThumbnailProperties(c.Thumb, ref p);
            }
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
            bool moving = false;
            foreach (var c in _cards) { c.X.Tick(); c.Y.Tick(); c.W.Tick(); c.H.Tick(); c.Op.Tick(); moving |= c.X.Moving || c.Y.Moving || c.W.Moving || c.H.Moving || c.Op.Moving; }
            Render();
            if (!moving) { CompositionTarget.Rendering -= OnFrame; _animating = false; }
        }

        private async Task Settled()
        {
            for (int i = 0; i < 90; i++)
            {
                if (!_cards.Any(c => c.X.Moving || c.Y.Moving || c.W.Moving || c.H.Moving || c.Op.Moving)) return;
                await Task.Delay(16);
            }
        }

        private void Finish()
        {
            if (_closing) return;
            _closing = true;
            foreach (var c in _cards) if (c.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(c.Thumb);
            _cards.Clear();
            if (Current == this) Current = null;
            App.Taskbar?.ReleaseDock();
            CompositionTarget.Rendering -= OnFrame;
            try { Close(); } catch { }
        }

        // ================== L'appli suit le doigt ==================

        /// <summary>Le bas de la fenetre reste sous le doigt ; plus on monte, plus elle rapetisse.</summary>
        public void Drag(Point finger, Point start)
        {
            if (Switcher || _cards.Count == 0) return;
            var c = _cards[0];
            double dy = Math.Max(0, start.Y - finger.Y);
            double prog = Math.Min(1, dy / (_sh * 0.6));
            double s = 1 - 0.62 * prog;
            double w = _sw * s, h = _sh * s;
            double cx = _sw / 2 + (finger.X - start.X) * 0.8;
            double bottom = Math.Min(_sh, finger.Y - Top + 14);
            c.X.Set(cx - w / 2); c.Y.Set(bottom - h); c.W.Set(w); c.H.Set(h);
            Render();
        }

        /// <summary>Doigt leve : l'appli rentre dans son icone du dock (animation iPad ou goutte).</summary>
        public async void CloseToDock()
        {
            if (_cards.Count == 0) { Finish(); return; }
            var c = _cards[0];
            var target = App.Taskbar?.IconRect(_app) ?? new Rect(_sw / 2 - 20, _sh - 60, 40, 40);
            double tx = target.X - Left, ty = target.Y - Top;
            bool drop = Settings.Current.TabletCloseAnim == "drop";
            Log.I("Mode tablette : appli fermee vers le dock (" + (drop ? "goutte" : "iPad") + ")");
            if (drop)
            {
                // Goutte : la fenetre s'etire, s'affine, puis tombe dans son icone
                double cxm = c.X.Value + c.W.Value / 2;
                c.W.AnimateTo(c.W.Value * 0.72, new Spring(0.7, 0.18));
                c.H.AnimateTo(c.H.Value * 1.12, new Spring(0.7, 0.18));
                c.X.AnimateTo(cxm - c.W.Value * 0.36, new Spring(0.7, 0.18));
                Kick();
                await Task.Delay(120);
                var fall = new Spring(0.82, 0.34);
                c.X.AnimateTo(tx, fall); c.Y.AnimateTo(ty, fall); c.W.AnimateTo(target.Width, fall); c.H.AnimateTo(target.Height * 1.3, fall);
                c.Op.AnimateTo(0, new Spring(1, 0.4));
            }
            else
            {
                // iPad : la carte vole jusqu'a l'icone en se reduisant, avec un leger rebond
                var fly = new Spring(0.86, 0.42);
                c.X.AnimateTo(tx, fly); c.Y.AnimateTo(ty, fly); c.W.AnimateTo(target.Width, fly); c.H.AnimateTo(target.Height, fly);
                c.Op.AnimateTo(0, new Spring(1, 0.5));
            }
            Kick();
            await Task.Delay(260);
            Native.ShowWindow(_app, 7 /* SW_SHOWMINNOACTIVE */);
            await Settled();
            await Task.Delay(120);
            Finish();
        }

        /// <summary>Pas assez glisse : l'appli reprend tout l'ecran.</summary>
        public async void Cancel()
        {
            if (_cards.Count == 0) { Finish(); return; }
            var c = _cards[0];
            var back = Spring.Pop;
            c.X.AnimateTo(0, back); c.Y.AnimateTo(0, back); c.W.AnimateTo(_sw, back); c.H.AnimateTo(_sh, back);
            Kick();
            await Settled();
            Finish();
        }

        // ================== Toutes les applis ouvertes (cartes superposees) ==================

        public void ShowSwitcher()
        {
            if (Switcher) return;
            Switcher = true;
            Log.I("Mode tablette : vue des applis ouvertes");
            var first = _cards[0];
            // On recree les vignettes de la plus ancienne a la plus recente : la plus recente est dessus
            var list = TaskbarWindow.AppWindows().Select(w => w.h).Where(h => h != _app).ToList();
            var keep = (first.X.Value, first.Y.Value, first.W.Value, first.H.Value);
            foreach (var c in _cards) { if (c.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(c.Thumb); _canvas.Children.Remove(c.Back); _canvas.Children.Remove(c.Label); }
            _cards.Clear();
            var order = new List<IntPtr> { _app };
            order.AddRange(list);
            var made = new List<Card>();
            for (int i = order.Count - 1; i >= 0; i--) made.Add(NewCard(order[i]));
            // _cards : la plus recente en premier
            _cards.Clear();
            made.Reverse();
            _cards.AddRange(made);
            for (int i = 0; i < _cards.Count; i++)
            {
                var c = _cards[i];
                if (i == 0) { c.X.Set(keep.Item1); c.Y.Set(keep.Item2); c.W.Set(keep.Item3); c.H.Set(keep.Item4); }
                else { var r = Slot(i); c.X.Set(r.X - 120); c.Y.Set(r.Y); c.W.Set(r.Width); c.H.Set(r.Height); c.Op.Set(0); }
            }
            SettleSwitcher();
        }

        /// <summary>Place des cartes : la plus recente devant, les autres en dessous, decalees vers la gauche.</summary>
        private Rect Slot(int i)
        {
            double cw = _sw * 0.6, ch = _sh * 0.6;
            double step = cw * 0.42;
            double x = (_sw - cw) / 2 + 60 - i * step + _scroll;
            double y = (_sh - ch) / 2 + 10;
            return new Rect(x, y, cw, ch);
        }

        public void SettleSwitcher()
        {
            var sp = new Spring(0.78, 0.42);
            for (int i = 0; i < _cards.Count; i++)
            {
                var r = Slot(i);
                var c = _cards[i];
                c.X.AnimateTo(r.X, sp); c.Y.AnimateTo(r.Y, sp); c.W.AnimateTo(r.Width, sp); c.H.AnimateTo(r.Height, sp);
                c.Op.AnimateTo(1, Spring.Soft);
            }
            Kick();
        }

        private Point _pd, _pl;
        private bool _pmoved;

        private void PDown(Point p) { _pd = _pl = p; _pmoved = false; }

        private void PMove(Point p)
        {
            if (!Switcher) return;
            double dx = p.X - _pl.X;
            _pl = p;
            if (Math.Abs(p.X - _pd.X) > 10 || Math.Abs(p.Y - _pd.Y) > 10) _pmoved = true;
            if (Math.Abs(p.X - _pd.X) > Math.Abs(p.Y - _pd.Y))
            {
                // Glisser a droite : on fait defiler pour voir les cartes du dessous
                _scroll = Math.Max(0, Math.Min((_cards.Count - 1) * _sw * 0.6 * 0.42, _scroll + dx));
                for (int i = 0; i < _cards.Count; i++) { var r = Slot(i); _cards[i].X.Set(r.X); }
                Render();
            }
            else if (p.Y > _pd.Y)
            {
                // Glisser vers le bas : toutes les cartes descendent avec le doigt
                double d = p.Y - _pd.Y;
                for (int i = 0; i < _cards.Count; i++) { var r = Slot(i); _cards[i].Y.Set(r.Y + d * 0.8); }
                Render();
            }
        }

        private void PUp(Point p)
        {
            if (!Switcher) return;
            double dy = p.Y - _pd.Y;
            if (!_pmoved)
            {
                // Toucher une carte : on l'ouvre (celle du dessus en premier)
                foreach (var c in _cards)
                {
                    var r = new Rect(c.X.Value, c.Y.Value, c.W.Value, c.H.Value);
                    if (r.Contains(p)) { OpenCard(c); return; }
                }
                OpenCard(_cards.FirstOrDefault());
                return;
            }
            if (dy > 120 && Math.Abs(dy) > Math.Abs(p.X - _pd.X)) { AllToDock(); return; }
            SettleSwitcher();
        }

        private async void OpenCard(Card c)
        {
            if (c == null) { Finish(); return; }
            Log.I("Mode tablette : ouverture de « " + c.Title + " »");
            var sp = new Spring(0.8, 0.38);
            foreach (var o in _cards) if (o != c) o.Op.AnimateTo(0, Spring.Soft);
            c.X.AnimateTo(0, sp); c.Y.AnimateTo(0, sp); c.W.AnimateTo(_sw, sp); c.H.AnimateTo(_sh, sp);
            Kick();
            if (Native.IsIconic(c.Src)) Native.ShowWindow(c.Src, Native.SW_RESTORE);
            Native.SetForegroundWindow(c.Src);
            await Settled();
            App.Taskbar?.HideDock();
            Finish();
        }

        /// <summary>Glisser vers le bas dans la vue des applis : elles tombent toutes dans le dock (goutte).</summary>
        public async void AllToDock()
        {
            Log.I("Mode tablette : toutes les applis rangees dans le dock");
            int i = 0;
            foreach (var c in _cards)
            {
                var target = App.Taskbar?.IconRect(c.Src) ?? new Rect(_sw / 2 - 20, _sh - 60, 40, 40);
                double tx = target.X - Left, ty = target.Y - Top;
                var fall = new Spring(0.8, 0.36 + 0.03 * i++);
                c.W.AnimateTo(target.Width, fall); c.H.AnimateTo(target.Height * 1.3, fall);
                c.X.AnimateTo(tx, fall); c.Y.AnimateTo(ty, fall);
                c.Op.AnimateTo(0, new Spring(1, 0.45 + 0.03 * i));
            }
            Kick();
            App.Taskbar?.ShowDock(10);
            await Task.Delay(250);
            foreach (var c in _cards) Native.ShowWindow(c.Src, 7 /* SW_SHOWMINNOACTIVE */);
            await Settled();
            Finish();
        }

        // ================== Tests automatiques ==================

        internal static async Task Demo(Action<string> shot)
        {
            var app = TabletShell.AppInFront();
            if (app == IntPtr.Zero) { Log.I("DEMO : pas d'appli au premier plan pour le mode tablette"); return; }
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            double k = VisualTreeHelper.GetDpi(App.Island).DpiScaleX;
            var bottom = new Point(sb.Width / k / 2, sb.Height / k - 4);
            // 1er glissement : le dock monte
            TabletShell.Down(bottom);
            for (int i = 1; i <= 6; i++) { TabletShell.Move(new Point(bottom.X, bottom.Y - i * 12)); await Task.Delay(16); }
            TabletShell.Up(new Point(bottom.X, bottom.Y - 72));
            await Task.Delay(900);
            shot("28-tablette-dock");
            // 2e glissement : l'appli suit le doigt
            TabletShell.Down(bottom);
            for (int i = 1; i <= 20; i++) { TabletShell.Move(new Point(bottom.X + i * 3, bottom.Y - i * 18)); await Task.Delay(16); }
            await Task.Delay(120);
            shot("29-tablette-appli-suit-le-doigt");
            // Doigt garde en haut : toutes les applis
            await Task.Delay(400);
            TabletShell.Move(new Point(bottom.X + 60, bottom.Y - 360));
            await Task.Delay(1100);
            TabletShell.Up(new Point(bottom.X + 60, bottom.Y - 360));
            await Task.Delay(900);
            shot("30-tablette-applis-ouvertes");
            // Glisser vers le bas : tout dans le dock
            Current?.AllToDock();
            await Task.Delay(1500);
            shot("31-tablette-bureau");
        }
    }
}
