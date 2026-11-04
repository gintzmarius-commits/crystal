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
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace WaveIsland
{
    /// <summary>
    /// Mode tablette (4.0) : animations facon iPad / iPhone avec des apercus aux coins arrondis.
    /// - L'appli qui se ferme suit le doigt puis tombe en goutte dans son icone du dock (ou vole, facon iPad).
    /// - Rouverte depuis le dock, elle ressort de son icone en goutte.
    /// - Vue des applis ouvertes facon iPhone : la carte de devant au premier plan, les autres empilees derriere
    ///   (plus petites, a moitie cachees). En glissant vers la droite, la carte de devant part vite et celle de
    ///   derriere la suit plus lentement (effet de profondeur), jusqu'a passer devant.
    /// Les apercus sont des images des fenetres (copiees au debut du geste) : coins arrondis, ombres et ordre libres.
    /// </summary>
    public sealed class AppMotion : Window
    {
        public static AppMotion Current { get; private set; }

        /// <summary>Derniere image connue de chaque fenetre (une fenetre reduite ne se laisse plus copier).</summary>
        private static readonly Dictionary<IntPtr, BitmapSource> Shots = new Dictionary<IntPtr, BitmapSource>();

        private sealed class Card
        {
            public IntPtr Src;
            public string Title;
            public readonly SpringValue X = new SpringValue(0), Y = new SpringValue(0), W = new SpringValue(0), H = new SpringValue(0), Op = new SpringValue(1);
            public Border View, Shade;
            public TextBlock Label;
        }

        private readonly Canvas _canvas = new Canvas();
        private readonly List<Card> _cards = new List<Card>();
        private readonly IntPtr _app;
        private double _sw, _sh;
        private bool _animating, _closing, _layoutLive;
        private readonly FrameGate _gate = new FrameGate();
        public bool Switcher { get; private set; }
        /// <summary>Position dans la pile (0 = la plus recente devant ; 1 = la suivante devant...).</summary>
        private readonly SpringValue _scroll = new SpringValue(0);

        // ================== Copie d'une fenetre ==================

        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SystemParametersInfo(int a, int b, StringBuilder s, int c);

        /// <summary>Copie l'image d'une fenetre (meme cachee derriere d'autres), en reduisant les grandes.</summary>
        private static BitmapSource Capture(IntPtr h)
        {
            if (h == IntPtr.Zero || Native.IsIconic(h)) return null;
            if (!Native.GetWindowRect(h, out var r)) return null;
            int w = r.Right - r.Left, hh = r.Bottom - r.Top;
            if (w < 40 || hh < 40 || w > 8000 || hh > 8000) return null;
            IntPtr screen = Native.GetDC(IntPtr.Zero), dc = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                dc = Native.CreateCompatibleDC(screen);
                var bmi = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -hh, biPlanes = 1, biBitCount = 32 };
                dib = Native.CreateDIBSection(dc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
                old = Native.SelectObject(dc, dib);
                if (!PrintWindow(h, dc, 2 /* PW_RENDERFULLCONTENT */)) return null;
                var src = BitmapSource.Create(w, hh, 96, 96, PixelFormats.Bgr32, null, bits, w * hh * 4, w * 4);
                BitmapSource result = src;
                if (w > 1400) result = new TransformedBitmap(src, new ScaleTransform(1400.0 / w, 1400.0 / w));
                result.Freeze();
                return result;
            }
            catch { return null; }
            finally
            {
                if (old != IntPtr.Zero) Native.SelectObject(dc, old);
                if (dib != IntPtr.Zero) Native.DeleteObject(dib);
                if (dc != IntPtr.Zero) Native.DeleteDC(dc);
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        /// <summary>Copie (avec une limite de temps : une appli bloquee ne doit pas tout figer).</summary>
        private static BitmapSource Shot(IntPtr h)
        {
            var t = Task.Run(() => Capture(h));
            if (t.Wait(400) && t.Result != null) { Shots[h] = t.Result; return t.Result; }
            return Shots.TryGetValue(h, out var b) ? b : null;
        }

        // ================== Fenetre de l'animation ==================

        public static void Begin(IntPtr app)
        {
            if (Current != null) return;
            // Pas d'attente : on part de la derniere image connue et on la rafraichit en arriere-plan
            Shots.TryGetValue(app, out var shot);
            var m = new AppMotion(app);
            Current = m;
            m.Open(shot);
            m.Refresh(app);
        }

        /// <summary>Copie a jour d'une fenetre, faite hors du fil de l'interface (aucune saccade).</summary>
        private async void Refresh(IntPtr h)
        {
            var b = await Task.Run(() => Capture(h));
            if (b == null || _closing) return;
            Shots[h] = b;
            foreach (var c in _cards.Where(c => c.Src == h)) { c.View.Background = new ImageBrush(b) { Stretch = Stretch.Fill }; c.View.Child = null; }
        }

        /// <summary>Mode tablette : garde une image recente de l'appli au premier plan (pour des animations immediates).</summary>
        public static async void Cache(IntPtr h)
        {
            if (h == IntPtr.Zero || Current != null) return;
            var b = await Task.Run(() => Capture(h));
            if (b != null) Shots[h] = b;
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
            _canvas.ClipToBounds = true;
            SourceInitialized += (a, b) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            };
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

        private void Place()
        {
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            Show();
            double k = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _sw = sb.Width / k; _sh = sb.Height / k;
            Left = sb.Left / k; Top = sb.Top / k; Width = _sw; Height = _sh;
            // Le dock reste au-dessus (comme sur iPad)
            App.Taskbar?.ShowDock(30);
            App.Taskbar?.BringToTop();
            App.Island?.BringToTop();
        }

        private void Open(BitmapSource shot)
        {
            Place();
            var c = NewCard(_app, shot);
            c.X.Set(0); c.Y.Set(0); c.W.Set(_sw); c.H.Set(_sh);
            Render();
        }

        private Card NewCard(IntPtr src, BitmapSource shot, ImageSource appIcon = null)
        {
            var c = new Card { Src = src };
            var title = new StringBuilder(256);
            GetWindowText(src, title, 256);
            c.Title = title.ToString();
            shot ??= Shots.TryGetValue(src, out var s) ? s : null;
            UIElement inner;
            if (shot != null) inner = null;
            else
            {
                // Pas d'image (appli reduite depuis longtemps) : son icone sur une carte de verre
                var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                var exe = src != IntPtr.Zero ? TaskbarWindow.AppWindows().FirstOrDefault(w => w.h == src).exe : null;
                var icon = appIcon ?? (exe != null ? AppCatalog.LoadShellIcon(exe, 128) : null);
                if (icon != null) st.Children.Add(new Image { Source = icon, Width = 72, Height = 72 });
                st.Children.Add(new TextBlock { Text = c.Title, Foreground = Brushes.White, FontSize = 15, Margin = new Thickness(12, 10, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center });
                inner = st;
            }
            c.View = new Border
            {
                Background = shot != null ? new ImageBrush(shot) { Stretch = Stretch.Fill } : (Brush)new SolidColorBrush(Color.FromArgb(150, 40, 44, 54)),
                // Contour noir (ou blanc) au lieu d'une grosse ombre : bien plus leger pour les petits PC
                BorderBrush = Settings.Current.WindowBorder == "white" ? new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(230, 0, 0, 0)),
                BorderThickness = new Thickness(Settings.Current.WindowBorder == "none" ? 0 : 1.5),
                Child = inner
            };
            // Voile pour les cartes du fond (plus elles sont loin, plus elles sont sombres)
            c.Shade = new Border { Background = Brushes.Black, Opacity = 0, IsHitTestVisible = false };
            c.Label = new TextBlock
            {
                Text = c.Title, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0,
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.5 }
            };
            _canvas.Children.Add(c.View);
            _canvas.Children.Add(c.Shade);
            _canvas.Children.Add(c.Label);
            _cards.Add(c);
            return c;
        }

        private void Render()
        {
            foreach (var c in _cards)
            {
                double x = c.X.Value, y = c.Y.Value, w = Math.Max(1, c.W.Value), h = Math.Max(1, c.H.Value), op = Math.Max(0, Math.Min(1, c.Op.Value));
                // Coins arrondis : plus la carte est petite, plus ils sont ronds (comme iOS)
                double full = Math.Min(1, w / Math.Max(1, _sw));
                double rad = full > 0.995 ? 0 : Math.Min(34, 14 + 26 * (1 - full)) * Math.Min(1, w / 160);
                foreach (var el in new FrameworkElement[] { c.View, c.Shade })
                {
                    Canvas.SetLeft(el, x); Canvas.SetTop(el, y);
                    el.Width = w; el.Height = h;
                }
                c.View.CornerRadius = new CornerRadius(rad);
                c.Shade.CornerRadius = new CornerRadius(rad);
                c.View.Opacity = op;
                Canvas.SetLeft(c.Label, x + 4); Canvas.SetTop(c.Label, y - 24);
                c.Label.MaxWidth = Math.Max(10, w - 8);
                c.Label.Opacity = Switcher ? op : 0;
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
            if (_layoutLive)
            {
                _scroll.Tick();
                moving |= _scroll.Moving;
                Stack(_scroll.Value, animate: false);
            }
            foreach (var c in _cards) { c.X.Tick(); c.Y.Tick(); c.W.Tick(); c.H.Tick(); c.Op.Tick(); moving |= c.X.Moving || c.Y.Moving || c.W.Moving || c.H.Moving || c.Op.Moving; }
            Render();
            if (!moving) { CompositionTarget.Rendering -= OnFrame; _animating = false; }
        }

        private async Task Settled()
        {
            for (int i = 0; i < 90; i++)
            {
                if (!_scroll.Moving && !_cards.Any(c => c.X.Moving || c.Y.Moving || c.W.Moving || c.H.Moving || c.Op.Moving)) return;
                await Task.Delay(16);
            }
        }

        private void Finish()
        {
            if (_closing) return;
            _closing = true;
            if (Current == this) Current = null;
            App.Taskbar?.ReleaseDock();
            CompositionTarget.Rendering -= OnFrame;
            try { Close(); } catch { }
        }

        // ================== L'appli suit le doigt, puis rentre dans le dock ==================

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

        /// <summary>Doigt leve : l'appli tombe en goutte dans son icone du dock (ou vole jusqu'a elle, facon iPad).</summary>
        public async void CloseToDock()
        {
            if (_cards.Count == 0) { Finish(); return; }
            var c = _cards[0];
            var target = App.Taskbar?.IconRect(_app) ?? new Rect(_sw / 2 - 20, _sh - 60, 40, 40);
            double tx = target.X - Left, ty = target.Y - Top;
            bool drop = Settings.Current.TabletCloseAnim2 != "ipad";
            Log.I("Mode tablette : appli fermee vers le dock (" + (drop ? "goutte" : "iPad") + ")");
            if (drop)
            {
                // Goutte : la carte s'affine et s'etire (la goutte se forme), puis tombe dans son icone en s'allongeant
                double cx = c.X.Value + c.W.Value / 2, bottom = c.Y.Value + c.H.Value;
                double w1 = c.W.Value * 0.62, h1 = c.H.Value * 1.12;
                var form = new Spring(0.62, 0.24);
                c.W.AnimateTo(w1, form); c.H.AnimateTo(h1, form);
                c.X.AnimateTo(cx - w1 / 2, form); c.Y.AnimateTo(bottom - h1, form);
                Kick();
                await Task.Delay(150);
                var fall = new Spring(0.8, 0.36);
                c.X.AnimateTo(tx + target.Width * 0.15, fall); c.Y.AnimateTo(ty, fall);
                c.W.AnimateTo(target.Width * 0.7, fall); c.H.AnimateTo(target.Height * 1.25, fall);
                c.Op.AnimateTo(0, new Spring(1, 0.42));
            }
            else
            {
                var fly = new Spring(0.86, 0.42);
                c.X.AnimateTo(tx, fly); c.Y.AnimateTo(ty, fly); c.W.AnimateTo(target.Width, fly); c.H.AnimateTo(target.Height, fly);
                c.Op.AnimateTo(0, new Spring(1, 0.5));
            }
            Kick();
            await Task.Delay(240);
            Native.ShowWindow(_app, 7 /* SW_SHOWMINNOACTIVE */);
            await Settled();
            App.Taskbar?.Splash(_app);
            await Task.Delay(80);
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

        // ================== Rouvrir depuis le dock : l'appli ressort de son icone en goutte ==================

        /// <summary>Joue l'ouverture en goutte depuis l'icone, puis remet la fenetre (deja reduite ou a lancer).</summary>
        public static async void OpenFromDock(IntPtr window, Rect icon, ImageSource appIcon, Action reallyOpen)
        {
            if (Current != null) { reallyOpen(); return; }
            var m = new AppMotion(window);
            Current = m;
            m.Place();
            var c = m.NewCard(window, window != IntPtr.Zero && Shots.TryGetValue(window, out var s) ? s : null, appIcon);
            double ix = icon.X - m.Left, iy = icon.Y - m.Top;
            // Depart : une petite goutte dans l'icone
            c.X.Set(ix + icon.Width * 0.2); c.Y.Set(iy); c.W.Set(icon.Width * 0.6); c.H.Set(icon.Height);
            c.Op.Set(0.2);
            m.Render();
            Log.I("Mode tablette : ouverture depuis le dock (goutte)");
            // 1) la goutte s'etire vers le haut en sortant de l'icone
            var rise = new Spring(0.7, 0.24);
            double w1 = m._sw * 0.22, h1 = m._sh * 0.5;
            c.X.AnimateTo(ix + icon.Width / 2 - w1 / 2, rise); c.Y.AnimateTo(iy + icon.Height - h1, rise);
            c.W.AnimateTo(w1, rise); c.H.AnimateTo(h1, rise); c.Op.AnimateTo(1, Spring.Soft);
            m.Kick();
            await Task.Delay(140);
            // 2) elle s'etale sur tout l'ecran avec un petit rebond
            var spread = new Spring(0.7, 0.42);
            c.X.AnimateTo(0, spread); c.Y.AnimateTo(0, spread); c.W.AnimateTo(m._sw, spread); c.H.AnimateTo(m._sh, spread);
            m.Kick();
            await Task.Delay(200);
            reallyOpen();
            await m.Settled();
            await Task.Delay(60);
            App.Taskbar?.HideDock();
            m.Finish();
        }

        // ================== Toutes les applis ouvertes (pile facon iPhone) ==================

        public void ShowSwitcher()
        {
            if (Switcher) return;
            Switcher = true;
            Log.I("Mode tablette : vue des applis ouvertes");
            var first = _cards[0];
            var others = TaskbarWindow.AppWindows().Select(w => w.h).Where(h => h != _app).ToList();
            // Les images manquantes arrivent en arriere-plan (la vue s'ouvre tout de suite)
            foreach (var h in others) if (!Shots.ContainsKey(h)) Refresh(h);
            // Ordre d'affichage : la plus ancienne d'abord (au fond), la plus recente en dernier (devant)
            for (int i = others.Count - 1; i >= 0; i--)
            {
                var c = NewCard(others[i], null);
                var r = Slot(i + 1, 0);
                c.X.Set(r.X - 160); c.Y.Set(r.Y); c.W.Set(r.Width); c.H.Set(r.Height); c.Op.Set(0);
            }
            // La carte de l'appli en cours passe devant toutes les autres
            _canvas.Children.Remove(first.View); _canvas.Children.Remove(first.Shade); _canvas.Children.Remove(first.Label);
            _canvas.Children.Add(first.View); _canvas.Children.Add(first.Shade); _canvas.Children.Add(first.Label);
            // _cards : la plus recente en premier
            var ordered = new List<Card> { first };
            ordered.AddRange(_cards.Where(c => c != first).Reverse());
            _cards.Clear();
            _cards.AddRange(ordered);
            _scroll.Set(0);
            Stack(0, animate: true);
        }

        /// <summary>
        /// Place d'une carte selon sa profondeur p (0 = devant, 1 = juste derriere a gauche, 2 = encore plus loin...).
        /// Derriere, les cartes se resserrent et rapetissent : on voit a peine la 2e, encore moins la 3e.
        /// Devant (p negatif, carte qu'on fait glisser a droite) : elle file vite vers la droite.
        /// </summary>
        private Rect Slot(double p, double unused)
        {
            double cw = _sw * 0.64, ch = _sh * 0.64;
            double baseX = (_sw - cw) / 2 + cw * 0.12, baseY = (_sh - ch) / 2;
            double x, s;
            if (p >= 0)
            {
                x = baseX - cw * 0.92 * (1 - Math.Pow(0.52, p));
                s = 1 - 0.07 * Math.Min(p, 4);
            }
            else
            {
                x = baseX - p * cw * 1.1;
                s = 1;
            }
            double w = cw * s, h = ch * s;
            return new Rect(x + (cw - w) * 0.15, baseY + (ch - h) / 2, w, h);
        }

        /// <summary>Range les cartes selon le defilement (en nombre de cartes).</summary>
        private void Stack(double scroll, bool animate)
        {
            var sp = new Spring(0.8, 0.42);
            for (int i = 0; i < _cards.Count; i++)
            {
                double p = i - scroll;
                var r = Slot(p, 0);
                var c = _cards[i];
                double op = p > 3.2 ? Math.Max(0, 1 - (p - 3.2)) : 1;
                if (animate)
                {
                    c.X.AnimateTo(r.X, sp); c.Y.AnimateTo(r.Y, sp); c.W.AnimateTo(r.Width, sp); c.H.AnimateTo(r.Height, sp); c.Op.AnimateTo(op, Spring.Soft);
                }
                else
                {
                    c.X.Set(r.X); c.Y.Set(r.Y); c.W.Set(r.Width); c.H.Set(r.Height); c.Op.Set(op);
                }
                c.Shade.Opacity = Math.Max(0, Math.Min(0.45, p * 0.16));
                // L'ordre d'affichage suit la profondeur : la carte la plus devant est dessinee en dernier
                Panel.SetZIndex(c.View, 1000 - (int)Math.Round(p * 10));
                Panel.SetZIndex(c.Shade, 1001 - (int)Math.Round(p * 10));
                Panel.SetZIndex(c.Label, 1002 - (int)Math.Round(p * 10));
            }
            if (animate) Kick();
        }

        public void SettleSwitcher()
        {
            _layoutLive = true;
            double target = Math.Max(0, Math.Min(_cards.Count - 1, Math.Round(_scroll.Value)));
            _scroll.AnimateTo(target, new Spring(0.8, 0.4));
            Kick();
        }

        private Point _pd, _pl;
        private bool _pmoved;

        private void PDown(Point p) { _pd = _pl = p; _pmoved = false; }

        private void PMove(Point p)
        {
            if (!Switcher || _cards.Count == 0) return;
            double dx = p.X - _pl.X;
            _pl = p;
            if (Math.Abs(p.X - _pd.X) > 10 || Math.Abs(p.Y - _pd.Y) > 10) _pmoved = true;
            if (Math.Abs(p.X - _pd.X) > Math.Abs(p.Y - _pd.Y))
            {
                // Glisser vers la droite : la carte de devant part vite, celle de derriere suit plus lentement
                double unit = _sw * 0.64 * 0.95;
                double v = _scroll.Value + dx / unit;
                v = v < 0 ? v * 0.35 : v > _cards.Count - 1 ? _cards.Count - 1 + (v - (_cards.Count - 1)) * 0.35 : v;
                _scroll.Set(v);
                _layoutLive = false;
                Stack(v, animate: false);
                Render();
            }
            else if (p.Y > _pd.Y)
            {
                // Glisser vers le bas : la pile descend avec le doigt
                double d = p.Y - _pd.Y;
                for (int i = 0; i < _cards.Count; i++) { var r = Slot(i - _scroll.Value, 0); _cards[i].Y.Set(r.Y + d * 0.8); }
                Render();
            }
        }

        private void PUp(Point p)
        {
            if (!Switcher) return;
            double dy = p.Y - _pd.Y;
            if (!_pmoved)
            {
                // Toucher une carte : on l'ouvre (de la plus devant a la plus loin)
                foreach (var c in _cards.OrderBy(c => _cards.IndexOf(c) - _scroll.Value < 0 ? 99 : _cards.IndexOf(c) - _scroll.Value))
                {
                    var r = new Rect(c.X.Value, c.Y.Value, c.W.Value, c.H.Value);
                    if (r.Contains(p)) { OpenCard(c); return; }
                }
                int front = (int)Math.Max(0, Math.Min(_cards.Count - 1, Math.Round(_scroll.Value)));
                OpenCard(_cards[front]);
                return;
            }
            if (dy > 120 && Math.Abs(dy) > Math.Abs(p.X - _pd.X)) { AllToDock(); return; }
            SettleSwitcher();
        }

        private async void OpenCard(Card c)
        {
            if (c == null) { Finish(); return; }
            Log.I("Mode tablette : ouverture de « " + c.Title + " »");
            _layoutLive = false;
            var sp = new Spring(0.78, 0.38);
            foreach (var o in _cards) if (o != c) o.Op.AnimateTo(0, Spring.Soft);
            Panel.SetZIndex(c.View, 5000); Panel.SetZIndex(c.Shade, 5001);
            c.Shade.Opacity = 0;
            c.X.AnimateTo(0, sp); c.Y.AnimateTo(0, sp); c.W.AnimateTo(_sw, sp); c.H.AnimateTo(_sh, sp);
            Kick();
            if (Native.IsIconic(c.Src)) Native.ShowWindow(c.Src, Native.SW_RESTORE);
            Native.SetForegroundWindow(c.Src);
            await Settled();
            App.Taskbar?.HideDock();
            Finish();
        }

        /// <summary>Glisser vers le bas dans la vue des applis : elles tombent toutes en goutte dans le dock.</summary>
        public async void AllToDock()
        {
            Log.I("Mode tablette : toutes les applis rangees dans le dock");
            _layoutLive = false;
            App.Taskbar?.ShowDock(10);
            int i = 0;
            foreach (var c in _cards)
            {
                var target = App.Taskbar?.IconRect(c.Src) ?? new Rect(_sw / 2 - 20, _sh - 60, 40, 40);
                double tx = target.X - Left, ty = target.Y - Top;
                var form = new Spring(0.62, 0.24);
                double w1 = c.W.Value * 0.62, h1 = c.H.Value * 1.1;
                c.W.AnimateTo(w1, form); c.H.AnimateTo(h1, form); c.X.AnimateTo(c.X.Value + (c.W.Value - w1) / 2, form);
                var fall = new Spring(0.8, 0.36 + 0.03 * i++);
                DropLater(c, tx, ty, target, fall, 130 + i * 40);
            }
            Kick();
            await Task.Delay(260);
            foreach (var c in _cards) Native.ShowWindow(c.Src, 7 /* SW_SHOWMINNOACTIVE */);
            await Task.Delay(200);
            await Settled();
            Finish();
        }

        private async void DropLater(Card c, double tx, double ty, Rect target, Spring fall, int delay)
        {
            await Task.Delay(delay);
            c.X.AnimateTo(tx + target.Width * 0.15, fall); c.Y.AnimateTo(ty, fall);
            c.W.AnimateTo(target.Width * 0.7, fall); c.H.AnimateTo(target.Height * 1.25, fall);
            c.Op.AnimateTo(0, new Spring(1, 0.42));
            Kick();
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
            // Glisser vers la droite : la carte de derriere suit, plus lentement
            var m = Current;
            if (m != null)
            {
                m.PDown(new Point(300, 400));
                for (int i = 1; i <= 10; i++) { m.PMove(new Point(300 + i * 22, 402)); await Task.Delay(16); }
                await Task.Delay(100);
                shot("30b-tablette-glisser-a-droite");
                m.PUp(new Point(520, 402));
                await Task.Delay(700);
            }
            // Glisser vers le bas : tout dans le dock
            Current?.AllToDock();
            await Task.Delay(300);
            shot("31a-tablette-goutte");
            await Task.Delay(1300);
            shot("31-tablette-bureau");
        }
    }
}
