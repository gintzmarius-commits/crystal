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
            /// <summary>0 = apercu, 1 = icone de l'appli (pour tout ranger dans le dock).</summary>
            public readonly SpringValue Mix = new SpringValue(0);
            public Border View, Shade;
            public TextBlock Label;
            public Image Icon;
            /// <summary>En train de se fermer (jetee vers le haut) : elle ne suit plus la pile.</summary>
            public bool Leaving;
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
        private readonly System.Diagnostics.Stopwatch _frameClock = new System.Diagnostics.Stopwatch();
        /// <summary>Lancee de la pile apres le lacher (en cartes par seconde).</summary>
        private double _vel;
        private bool _inertia;

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
                bool ok = PrintWindow(h, dc, 2 /* PW_RENDERFULLCONTENT */);
                // Certaines applis (Parametres, Explorateur, Xbox...) donnent une image toute noire :
                // si l'appli est a l'ecran, on copie l'ecran a la place ; sinon pas d'image (carte avec son icone)
                if (!ok || Blank(bits, w, hh))
                {
                    if (h != Native_GetForeground() || !CopyScreen(dc, r, w, hh) || Blank(bits, w, hh)) return null;
                }
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

        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern IntPtr Native_GetForeground();

        /// <summary>Image presque entierement noire (copie ratee) ?</summary>
        private static unsafe bool Blank(IntPtr bits, int w, int h)
        {
            uint* p = (uint*)bits;
            int dark = 0, n = 0;
            for (int y = 2; y < h; y += Math.Max(1, h / 40))
                for (int x = 2; x < w; x += Math.Max(1, w / 40))
                {
                    uint c = p[y * w + x];
                    if ((c & 0xFF) < 10 && ((c >> 8) & 0xFF) < 10 && ((c >> 16) & 0xFF) < 10) dark++;
                    n++;
                }
            return n > 0 && dark > n * 0.97;
        }

        /// <summary>Copie de la zone de l'ecran ou est la fenetre (quand elle est devant).</summary>
        private static bool CopyScreen(IntPtr dc, Native.RECT r, int w, int h)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            try { return Native.BitBlt(dc, 0, 0, w, h, screen, r.Left, r.Top, Native.SRCCOPY); }
            finally { Native.ReleaseDC(IntPtr.Zero, screen); }
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
            string before = Title(h);
            var b = await Task.Run(() => Capture(h));
            if (b == null) return;
            Shots[h] = b;
            // Image de la page actuelle (pour le geste retour), seulement si la page n'a pas change pendant la copie
            if (Title(h) == before) { var cur = Page.TryGetValue(h, out var p) ? p : default; if (cur.title == null || cur.title == before) Page[h] = (before, b); }
            Remember(h, b);
        }

        // ---- Pages precedentes (geste retour) : chaque fois que le titre de la fenetre change, la page d'avant est gardee ----

        private static string Title(IntPtr h) { var sb = new StringBuilder(300); GetWindowText(h, sb, 300); return sb.ToString(); }

        private static readonly Dictionary<IntPtr, (string title, BitmapSource img)> Page = new Dictionary<IntPtr, (string, BitmapSource)>();
        private static readonly Dictionary<IntPtr, List<(string title, BitmapSource img)>> BackPages = new Dictionary<IntPtr, List<(string, BitmapSource)>>();

        /// <summary>Le titre de la fenetre a change : nouvelle page (on garde l'ancienne) ou retour a la page d'avant.</summary>
        public static void TitleChanged(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            string now = Title(h);
            var cur = Page.TryGetValue(h, out var p) ? p : default;
            if (cur.title == now) return;
            if (!BackPages.TryGetValue(h, out var stack)) BackPages[h] = stack = new List<(string, BitmapSource)>();
            if (stack.Count > 0 && stack[stack.Count - 1].title == now)
            {
                // Retour en arriere (geste, bouton du navigateur...) : la page d'avant redevient la page actuelle
                Page[h] = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
            }
            else
            {
                if (cur.img != null && !string.IsNullOrEmpty(cur.title))
                {
                    stack.Add(cur);
                    if (stack.Count > 10) stack.RemoveAt(0);
                }
                Page[h] = (now, null);
            }
            if (BackPages.Count > 60) { BackPages.Clear(); Page.Clear(); }
            // Image de la nouvelle page une fois affichee
            var ui = Application.Current?.Dispatcher;
            Task.Delay(800).ContinueWith(_ => ui?.BeginInvoke(new Action(() => Cache(h))));
        }

        // ---- Images recentes (pour les applis dont le titre ne change pas d'une page a l'autre) ----

        private static readonly Dictionary<IntPtr, List<(BitmapSource img, byte[] sig)>> Recent = new Dictionary<IntPtr, List<(BitmapSource, byte[])>>();

        /// <summary>Petite empreinte de l'image (16 x 12 en gris) pour reconnaitre une autre page.</summary>
        private static byte[] Sig(BitmapSource b)
        {
            try
            {
                var small = new TransformedBitmap(b, new ScaleTransform(16.0 / b.PixelWidth, 12.0 / b.PixelHeight));
                var g = new FormatConvertedBitmap(small, PixelFormats.Gray8, null, 0);
                var px = new byte[g.PixelWidth * g.PixelHeight];
                g.CopyPixels(px, g.PixelWidth, 0);
                return px;
            }
            catch { return null; }
        }

        private static double Diff(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return 0;
            double d = 0;
            for (int i = 0; i < a.Length; i++) d += Math.Abs(a[i] - b[i]);
            return d / a.Length;
        }

        private static void Remember(IntPtr h, BitmapSource b)
        {
            var sig = Sig(b);
            if (sig == null) return;
            if (!Recent.TryGetValue(h, out var list)) Recent[h] = list = new List<(BitmapSource, byte[])>();
            // Meme page qu'avant (presque la meme image) : on remplace juste par la plus recente
            if (list.Count > 0 && Diff(list[list.Count - 1].sig, sig) < 6) list[list.Count - 1] = (b, sig);
            else { list.Add((b, sig)); if (list.Count > 8) list.RemoveAt(0); }
            if (Recent.Count > 40) Recent.Clear();
        }

        /// <summary>Image de la page precedente de cette fenetre (sinon null).</summary>
        public static BitmapSource PreviousPage(IntPtr h, BitmapSource current = null)
        {
            if (BackPages.TryGetValue(h, out var s) && s.Count > 0 && s[s.Count - 1].img != null) return s[s.Count - 1].img;
            // Sinon : la derniere image vraiment differente de la page actuelle
            if (!Recent.TryGetValue(h, out var list) || list.Count == 0) return null;
            var cur = current != null ? Sig(current) : list[list.Count - 1].sig;
            for (int i = list.Count - 1; i >= 0; i--)
                if (Diff(list[i].sig, cur) >= 12) return list[i].img;
            return null;
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
            // Taille et place AVANT d'afficher la fenetre (sinon un flash a la mauvaise taille)
            double k = App.Island != null ? VisualTreeHelper.GetDpi(App.Island).DpiScaleX : 1;
            _sw = sb.Width / k; _sh = sb.Height / k;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = sb.Left / k; Top = sb.Top / k; Width = _sw; Height = _sh;
            Show();
            // Le dock reste au-dessus (comme sur iPad). Windows remet parfois cette fenetre devant juste apres
            // son affichage : on repasse le dock devant plusieurs fois au debut, et des que l'image est dessinee
            // L'appli en cours n'a pas d'icone dans le dock tant qu'elle est a l'ecran
            if (_app != IntPtr.Zero) App.Taskbar?.HideWindows(new[] { _app });
            App.Taskbar?.ShowDock(30);
            DockOnTop();
            ContentRendered += (a, b) => DockOnTop();
            Dispatcher.BeginInvoke(new Action(DockOnTop), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            foreach (var ms in new[] { 60, 180, 400 }) Task.Delay(ms).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(DockOnTop)));
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
                Background = shot != null ? new ImageBrush(shot) { Stretch = Stretch.Fill } : (Brush)new SolidColorBrush(Color.FromArgb(120, 230, 236, 245)),
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
            c.Icon = new Image { Source = appIcon ?? App.Taskbar?.IconFor(src) ?? IconFromExe(src), Opacity = 0, IsHitTestVisible = false, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(c.Icon, BitmapScalingMode.HighQuality);
            _canvas.Children.Add(c.View);
            _canvas.Children.Add(c.Shade);
            _canvas.Children.Add(c.Label);
            _canvas.Children.Add(c.Icon);
            _cards.Add(c);
            return c;
        }

        private static ImageSource IconFromExe(IntPtr src)
        {
            try
            {
                var exe = src != IntPtr.Zero ? TaskbarWindow.AppWindows().FirstOrDefault(w => w.h == src).exe : null;
                return exe != null ? AppCatalog.LoadShellIcon(exe, 128) : null;
            }
            catch { return null; }
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
                double mix = Math.Max(0, Math.Min(1, c.Mix.Value));
                c.View.Opacity = op * (1 - mix);
                c.Shade.Visibility = mix > 0.01 ? Visibility.Collapsed : Visibility.Visible;
                Canvas.SetLeft(c.Label, x + 4); Canvas.SetTop(c.Label, y - 24);
                c.Label.MaxWidth = Math.Max(10, w - 8);
                c.Label.Opacity = Switcher ? op * (1 - mix) : 0;
                // L'icone de l'appli apparait au centre quand l'apercu se change en icone
                double isz = Math.Max(4, Math.Min(Math.Min(w, h), IconSize * 1.15));   // jamais une icone geante et floue
                Canvas.SetLeft(c.Icon, x + (w - isz) / 2); Canvas.SetTop(c.Icon, y + (h - isz) / 2);
                c.Icon.Width = isz; c.Icon.Height = isz;
                c.Icon.Opacity = op * mix;
            }
        }

        private void Kick()
        {
            if (_animating) return;
            _animating = true;
            _frameClock.Restart();
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object s, EventArgs e)
        {
            if (!_gate.Ready()) return;
            bool moving = false;
            double dt = Math.Min(0.05, _frameClock.Elapsed.TotalSeconds);
            _frameClock.Restart();
            if (_inertia)
            {
                // Defilement libre : la pile continue sur sa lancee et ralentit doucement (comme sur iPhone)
                double v = _scroll.Value + _vel * dt;
                _vel *= Math.Exp(-3.4 * dt);
                double max = Math.Max(0, _cards.Count(c => !c.Leaving) - 1);
                if (v < 0 || v > max)
                {
                    _inertia = false;
                    _scroll.Set(v);
                    _scroll.AnimateTo(v < 0 ? 0 : max, new Spring(0.85, 0.42));
                }
                else if (Math.Abs(_vel) < 0.04) { _inertia = false; _scroll.Set(v); }
                else _scroll.Set(v);
                moving = true;
            }
            if (_layoutLive)
            {
                _scroll.Tick();
                moving |= _scroll.Moving;
                Stack(_scroll.Value, animate: false);
            }
            foreach (var c in _cards) { c.X.Tick(); c.Y.Tick(); c.W.Tick(); c.H.Tick(); c.Op.Tick(); c.Mix.Tick(); moving |= c.X.Moving || c.Y.Moving || c.W.Moving || c.H.Moving || c.Op.Moving || c.Mix.Moving; }
            Render();
            if (!moving) { CompositionTarget.Rendering -= OnFrame; _animating = false; }
        }

        private async Task Settled()
        {
            for (int i = 0; i < 90; i++)
            {
                if (!_scroll.Moving && !_inertia && !_cards.Any(c => c.X.Moving || c.Y.Moving || c.W.Moving || c.H.Moving || c.Op.Moving || c.Mix.Moving)) return;
                await Task.Delay(16);
            }
        }

        private void Finish()
        {
            if (_closing) return;
            _closing = true;
            if (Current == this) Current = null;
            App.Taskbar?.WinAway(false);
            // Les icones des applis encore en apercu reviennent dans le dock
            App.Taskbar?.RevealAll(animate: !_openedApp);
            App.Taskbar?.ReleaseDock();
            // Retour au bureau : le dock reste la, sans disparaitre puis revenir
            if (!_openedApp) App.Taskbar?.ShowDock(1.5);
            CompositionTarget.Rendering -= OnFrame;
            try { Close(); } catch { }
        }

        // ================== L'appli suit le doigt, puis rentre dans le dock ==================

        /// <summary>Le bas de la fenetre reste sous le doigt ; plus on monte, plus elle rapetisse.</summary>
        public void Drag(Point finger, Point start)
        {
            if (Switcher || _cards.Count == 0) return;
            if ((DateTime.Now - _lastTop).TotalMilliseconds > 150) { _lastTop = DateTime.Now; DockOnTop(); }
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

        /// <summary>Doigt leve : l'appli se change en son icone et va se ranger dans le dock avec un rebond.</summary>
        public async void CloseToDock()
        {
            if (_cards.Count == 0) { Finish(); return; }
            var c = _cards[0];
            bool wasOut = App.Taskbar?.DockVisible ?? true;
            App.Taskbar?.ShowDock(10);
            DockOnTop();
            Log.I("Mode tablette : appli rangee dans le dock (icone)");
            // 1) l'apercu se change en icone, la ou il est
            double cx = c.X.Value + c.W.Value / 2, cy = c.Y.Value + c.H.Value / 2;
            var morph = new Spring(0.85, 0.26);
            c.Mix.AnimateTo(1, morph);
            c.W.AnimateTo(IconSize, morph); c.H.AnimateTo(IconSize, morph);
            c.X.AnimateTo(cx - IconSize / 2, morph); c.Y.AnimateTo(cy - IconSize / 2, morph);
            Kick();
            await Task.Delay(170);
            // Le dock doit etre completement sorti avant que l'icone y aille
            int wait = App.Taskbar?.DockSettleMs() ?? 0;
            if (wait > 0) await Task.Delay(wait);
            // 2) l'icone vole a sa place dans le dock et rebondit
            DockOnTop();
            // Sa place s'ouvre dans le dock pendant que l'icone y vole
            App.Taskbar?.RevealWindows(new[] { _app }, holdIcon: true);
            var target = App.Taskbar?.FinalIconRect(_app) ?? new Rect(_sw / 2 - 20, _sh - 60, 40, 40);
            Native.ShowWindow(_app, 7 /* SW_SHOWMINNOACTIVE */);
            ToDesktop();
            await LandIn(c, _app, target);
            await Settled();
            Finish();
        }

        /// <summary>Pas assez glisse : l'appli reprend tout l'ecran.</summary>
        public async void Cancel()
        {
            _openedApp = true;
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
            m._openedApp = true;
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

        /// <summary>Depuis le bureau (glisser la petite barre vers le haut) : la pile s'ouvre directement.</summary>
        public static void BeginSwitcher()
        {
            if (Current != null) return;
            if (!TaskbarWindow.AppWindows().Any()) return;
            var m = new AppMotion(IntPtr.Zero);
            Current = m;
            m.Place();
            m.ShowSwitcher();
        }

        private List<Card> Live => _cards.Where(c => !c.Leaving).ToList();

        public void ShowSwitcher()
        {
            if (Switcher) return;
            Switcher = true;
            // Le dock reste affiche ; seule la bulle Windows descend
            App.Taskbar?.ShowDock(60);
            App.Taskbar?.WinAway(true);
            // Les applis montrees en apercu sortent du dock (leurs places se referment)
            App.Taskbar?.HideWindows(TaskbarWindow.AppWindows().Select(w => w.h));
            Log.I("Mode tablette : vue des applis ouvertes");
            var first = _cards.FirstOrDefault();
            var others = TaskbarWindow.AppWindows().Select(w => w.h).Where(h => h != _app).ToList();
            // Les images manquantes arrivent en arriere-plan (la vue s'ouvre tout de suite)
            foreach (var h in others) if (!Shots.ContainsKey(h)) Refresh(h);
            var added = new List<Card>();
            foreach (var h in others) added.Add(NewCard(h, null));
            // _cards : la plus recente en premier
            _cards.Clear();
            if (first != null) _cards.Add(first);
            _cards.AddRange(added);
            _scroll.Set(0);
            _layoutLive = false;
            // Les cartes arrivent depuis la gauche, l'une apres l'autre, et se rangent avec un petit rebond
            for (int i = 0; i < _cards.Count; i++)
            {
                var c = _cards[i];
                var r = Slot(i, 0);
                if (c == first)
                {
                    var sp = new Spring(0.8, 0.42);
                    c.X.AnimateTo(r.X, sp); c.Y.AnimateTo(r.Y, sp); c.W.AnimateTo(r.Width, sp); c.H.AnimateTo(r.Height, sp);
                }
                else
                {
                    c.X.Set(-r.Width - 60 - i * 40); c.Y.Set(r.Y); c.W.Set(r.Width); c.H.Set(r.Height); c.Op.Set(1);
                    Arrive(c, r, 40 + (first == null ? i : i - 1) * 55);
                }
            }
            Depth(0);
            Render();
            Kick();
        }

        private async void Arrive(Card c, Rect r, int delay)
        {
            await Task.Delay(delay);
            if (_closing || c.Leaving || _gathered) return;
            var bounce = new Spring(0.58, 0.46);
            c.X.AnimateTo(r.X, bounce); c.Y.AnimateTo(r.Y, bounce); c.W.AnimateTo(r.Width, bounce); c.H.AnimateTo(r.Height, bounce);
            Kick();
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

        /// <summary>Voile et ordre d'affichage selon la profondeur.</summary>
        private void Depth(double scroll)
        {
            var live = Live;
            for (int i = 0; i < live.Count; i++)
            {
                var c = live[i];
                double p = i - scroll;
                c.Shade.Opacity = Math.Max(0, Math.Min(0.45, p * 0.16));
                int z = 1000 - (int)Math.Round(p * 10);
                Panel.SetZIndex(c.View, z); Panel.SetZIndex(c.Shade, z + 1); Panel.SetZIndex(c.Label, z + 2); Panel.SetZIndex(c.Icon, z + 3);
            }
        }

        /// <summary>Range les cartes selon le defilement (en nombre de cartes).</summary>
        private void Stack(double scroll, bool animate)
        {
            var sp = new Spring(0.8, 0.42);
            var live = Live;
            for (int i = 0; i < live.Count; i++)
            {
                double p = i - scroll;
                var r = Slot(p, 0);
                var c = live[i];
                double op = p > 3.2 ? Math.Max(0, 1 - (p - 3.2)) : 1;
                if (animate)
                {
                    c.X.AnimateTo(r.X, sp); c.Y.AnimateTo(r.Y, sp); c.W.AnimateTo(r.Width, sp); c.H.AnimateTo(r.Height, sp); c.Op.AnimateTo(op, Spring.Soft);
                    c.Mix.AnimateTo(0, new Spring(0.85, 0.3));
                }
                else
                {
                    c.X.Set(r.X); c.Y.Set(r.Y); c.W.Set(r.Width); c.H.Set(r.Height); c.Op.Set(op);
                }
            }
            Depth(scroll);
            if (animate) Kick();
        }

        /// <summary>Doigt leve apres l'ouverture de la pile : elle reste ou elle est (pas de recentrage force).</summary>
        public void SettleSwitcher()
        {
            double max = Math.Max(0, Live.Count - 1);
            if (_scroll.Value < 0 || _scroll.Value > max)
            {
                _layoutLive = true;
                _scroll.AnimateTo(Math.Max(0, Math.Min(max, _scroll.Value)), new Spring(0.85, 0.42));
                Kick();
            }
        }

        // ================== Gestes dans la pile ==================

        private enum Mode { None, Scroll, Fling, Gather }
        private Mode _mode;
        private Point _pd, _pl;
        private DateTime _pt;
        private bool _pmoved, _stopped, _gathered;
        private Card _fling;
        private Rect _flingFrom;
        private double _flingVel;

        private Card HitCard(Point p)
        {
            // De la plus devant a la plus loin
            var live = Live;
            foreach (var c in live.OrderBy(c => Panel.GetZIndex(c.View) * -1))
                if (new Rect(c.X.Value, c.Y.Value, c.W.Value, c.H.Value).Contains(p)) return c;
            return null;
        }

        /// <summary>Toucher la fenetre de l'animation la remet au-dessus de tout : le dock et l'ilot repassent devant.</summary>
        private DateTime _lastTop = DateTime.MinValue;

        private void DockOnTop()
        {
            App.Taskbar?.BringToTop();
            App.Island?.BringToTop();
        }

        private void PDown(Point p)
        {
            DockOnTop();
            _pd = _pl = p; _pt = DateTime.Now; _pmoved = false; _mode = Mode.None; _flingVel = 0;
            // Toucher pendant que la pile file : elle s'arrete (sans ouvrir d'appli)
            _stopped = _inertia || _scroll.Moving;
            _inertia = false; _vel = 0;
            if (_scroll.Moving) _scroll.Set(_scroll.Value);
        }

        private void PMove(Point p)
        {
            if (!Switcher || Live.Count == 0) return;
            double dt = Math.Max(0.001, (DateTime.Now - _pt).TotalSeconds);
            double dx = p.X - _pl.X, dyStep = p.Y - _pl.Y;
            _pl = p; _pt = DateTime.Now;
            double tx = p.X - _pd.X, ty = p.Y - _pd.Y;
            if (!_pmoved && (Math.Abs(tx) > 10 || Math.Abs(ty) > 10))
            {
                _pmoved = true;
                if (Math.Abs(tx) > Math.Abs(ty)) _mode = Mode.Scroll;
                else if (ty < 0) { _fling = HitCard(_pd); _mode = _fling != null ? Mode.Fling : Mode.None; if (_fling != null) _flingFrom = new Rect(_fling.X.Value, _fling.Y.Value, _fling.W.Value, _fling.H.Value); }
                else _mode = Mode.Gather;
            }
            switch (_mode)
            {
                case Mode.Scroll:
                {
                    // Defilement libre : la carte de devant part vite, celle de derriere suit plus lentement
                    double unit = _sw * 0.64 * 0.95;
                    double max = Live.Count - 1;
                    double v = _scroll.Value + dx / unit;
                    double raw = v;
                    if (v < 0 && dx < 0) v = _scroll.Value + dx / unit * 0.35;
                    else if (v > max && dx > 0) v = _scroll.Value + dx / unit * 0.35;
                    _vel = _vel * 0.6 + (raw - _scroll.Value) / dt * 0.4;
                    _scroll.Set(v);
                    _layoutLive = false;
                    Stack(v, animate: false);
                    Render();
                    break;
                }
                case Mode.Fling:
                {
                    // La carte suit le doigt vers le haut
                    _flingVel = _flingVel * 0.6 + dyStep / dt * 0.4;
                    double up = Math.Min(0, ty);
                    _fling.Y.Set(_flingFrom.Y + up + Math.Max(0, ty) * 0.2);
                    Render();
                    break;
                }
                case Mode.Gather:
                    Gather(p, ty);
                    break;
            }
        }

        private void PUp(Point p)
        {
            if (!Switcher) return;
            if (!_pmoved)
            {
                if (_stopped) return;
                var hit = HitCard(p);
                if (hit != null) OpenCard(hit);
                else Finish();   // toucher le fond d'ecran : retour au bureau
                return;
            }
            switch (_mode)
            {
                case Mode.Scroll:
                    _layoutLive = true;
                    _vel = Math.Max(-14, Math.Min(14, _vel));
                    _inertia = Math.Abs(_vel) > 0.15;
                    if (!_inertia) SettleSwitcher();
                    Kick();
                    break;
                case Mode.Fling:
                {
                    var c = _fling;
                    _fling = null;
                    if (p.Y - _pd.Y < -_sh * 0.16 || _flingVel < -1100) CloseCard(c);
                    else { var sp = new Spring(0.7, 0.4); c.X.AnimateTo(_flingFrom.X, sp); c.Y.AnimateTo(_flingFrom.Y, sp); Kick(); }
                    break;
                }
                case Mode.Gather:
                    if (_gathered && p.Y > _sh * 0.5) IconsToDock();
                    else { Ungather(); }
                    break;
                default:
                    SettleSwitcher();
                    break;
            }
            _mode = Mode.None;
        }

        // ---- Tout ranger : les apercus deviennent des icones regroupees sous le doigt ----

        private const double IconSize = 64;

        private void Gather(Point finger, double down)
        {
            var live = Live;
            if (!_gathered && down > 90)
            {
                _gathered = true;
                foreach (var c in live) c.Mix.AnimateTo(1, new Spring(0.8, 0.3));
                // Le dock sort tout de suite : les icones auront leur place ou se ranger
                App.Taskbar?.ShowDock(15);
                DockOnTop();
                Log.I("Mode tablette : apercus changes en icones");
            }
            else if (_gathered && down < 55)
            {
                Ungather();
                return;
            }
            if (!_gathered)
            {
                // Avant le seuil : la pile descend un peu avec le doigt
                for (int i = 0; i < live.Count; i++) { var r = Slot(i - _scroll.Value, 0); live[i].Y.Set(r.Y + down * 0.5); }
                Render();
                return;
            }
            // Les icones suivent le doigt en petit tas (la plus devant arrive la premiere, les autres un peu apres)
            for (int i = 0; i < live.Count; i++)
            {
                var c = live[i];
                int k = Math.Min(i, 5);
                double ox = (k % 2 == 0 ? 1 : -1) * ((k + 1) / 2) * 14, oy = -k * 6;
                var follow = new Spring(0.85, 0.2 + 0.03 * k);
                c.X.AnimateTo(finger.X - IconSize / 2 + ox, follow); c.Y.AnimateTo(finger.Y - IconSize / 2 + oy, follow);
                c.W.AnimateTo(IconSize, follow); c.H.AnimateTo(IconSize, follow);
                c.Op.AnimateTo(k < 5 ? 1 : 0, Spring.Soft);
                Panel.SetZIndex(c.Icon, 3000 - i);
            }
            Kick();
        }

        private void Ungather()
        {
            _gathered = false;
            _layoutLive = false;
            Stack(_scroll.Value, animate: true);
        }

        /// <summary>Doigt lache assez bas : chaque icone vole a sa place dans le dock et y rebondit.</summary>
        private async void IconsToDock()
        {
            Log.I("Mode tablette : toutes les applis rangees dans le dock");
            _layoutLive = false;
            bool wasOut = App.Taskbar?.DockVisible ?? true;
            App.Taskbar?.ShowDock(10);
            DockOnTop();
            var live = Live;
            foreach (var c in live) if (c.Src != IntPtr.Zero) Native.ShowWindow(c.Src, 7 /* SW_SHOWMINNOACTIVE */);
            ToDesktop();
            // On attend que le dock soit completement sorti (et ses icones en place) avant d'y envoyer les applis
            await Task.Delay(Math.Max(60, App.Taskbar?.DockSettleMs() ?? 0));
            // Toutes les places s'ouvrent ; chaque icone vole vers la sienne
            App.Taskbar?.RevealWindows(live.Select(c => c.Src), holdIcon: true);
            int i = 0;
            foreach (var c in live) Fly(c, i++);
            await Task.Delay(120 + live.Count * 50);
            await Settled();
            await Task.Delay(120);
            Finish();
        }

        /// <summary>
        /// L'icone vole jusqu'a sa place du dock (sans depasser), puis passe le relais a l'icone du dock
        /// qui rebondit : une seule icone visible a la fois.
        /// </summary>
        private async Task LandIn(Card c, IntPtr src, Rect target)
        {
            double tx = target.X - Left, ty = target.Y - Top;
            var fly = new Spring(0.42, 0.86);
            c.Op.Set(1);
            c.X.AnimateTo(tx, fly); c.Y.AnimateTo(ty, fly); c.W.AnimateTo(target.Width, fly); c.H.AnimateTo(target.Height, fly);
            Kick();
            var until = DateTime.Now.AddMilliseconds(650);
            while (DateTime.Now < until && !_closing)
            {
                await Task.Delay(16);
                if (Math.Abs(c.X.Value - tx) < 1.5 && Math.Abs(c.Y.Value - ty) < 1.5 && Math.Abs(c.W.Value - target.Width) < 1.5) break;
            }
            // Relais : l'icone du dock apparait a la meme place et rebondit, celle qui volait disparait
            App.Taskbar?.Land(src);
            c.Op.Set(0);
            Render();
        }

        private async void Fly(Card c, int i)
        {
            await Task.Delay(i * 50);
            DockOnTop();
            var target = App.Taskbar?.FinalIconRect(c.Src) ?? new Rect(_sw / 2 - 20, _sh - 60, 40, 40);
            await LandIn(c, c.Src, target);
        }

        // ---- Jeter une carte vers le haut : l'appli se ferme vraiment ----

        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr h);

        private static bool Gone(IntPtr h) => !IsWindow(h) || !IsWindowVisible(h);

        /// <summary>Fenetre de dialogue de l'appli (« Voulez-vous enregistrer ? »...) ou zero.</summary>
        private static IntPtr Dialog(IntPtr h)
        {
            var pop = GetLastActivePopup(h);
            if (pop != IntPtr.Zero && pop != h && IsWindowVisible(pop)) return pop;
            return IsWindowEnabled(h) ? IntPtr.Zero : (pop != h ? pop : IntPtr.Zero);
        }

        private async void CloseCard(Card c)
        {
            if (c == null || c.Leaving) return;
            c.Leaving = true;
            Log.I("Mode tablette : fermeture de « " + c.Title + " »");
            // La carte file vers le haut ; les autres se resserrent
            var off = new Spring(0.95, 0.24);
            c.Y.AnimateTo(-c.H.Value - 60, off);
            c.Op.AnimateTo(0.9, Spring.Soft);
            CloseGap();
            if (c.Src != IntPtr.Zero) PostMessage(c.Src, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            // On regarde ce que fait l'appli
            bool parked = false;
            var until = DateTime.Now.AddSeconds(1.6);
            while (!_closing)
            {
                await Task.Delay(90);
                if (c.Src == IntPtr.Zero || Gone(c.Src)) { Remove(c); return; }
                var dlg = Dialog(c.Src);
                if (dlg != IntPtr.Zero)
                {
                    if (!parked)
                    {
                        // L'appli demande quelque chose : la carte attend tout en haut, sa question passe devant
                        parked = true;
                        Log.I("Mode tablette : l'appli pose une question avant de se fermer");
                        var sp = new Spring(0.7, 0.42);
                        double w = _sw * 0.3, h = _sh * 0.3;
                        c.W.AnimateTo(w, sp); c.H.AnimateTo(h, sp); c.X.AnimateTo((_sw - w) / 2, sp); c.Y.AnimateTo(-h * 0.55, sp);
                        c.Op.AnimateTo(1, Spring.Soft);
                        Panel.SetZIndex(c.View, 4000); Panel.SetZIndex(c.Icon, 4001);
                        Kick();
                    }
                    Native.SetWindowPos(dlg, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
                    Native.SetForegroundWindow(dlg);
                    until = DateTime.Now.AddSeconds(1.2);   // apres la reponse, on laisse le temps a l'appli de se fermer
                    continue;
                }
                if (DateTime.Now > until)
                {
                    // L'appli ne s'est pas fermee (« Annuler ») : la carte reprend sa place dans la pile
                    Log.I("Mode tablette : « " + c.Title + " » reste ouverte");
                    c.Leaving = false;
                    c.Op.AnimateTo(1, Spring.Soft);
                    _layoutLive = false;
                    Stack(_scroll.Value, animate: true);
                    _layoutLive = false;
                    var back = new Spring(0.6, 0.45);
                    var live = Live;
                    var r = Slot(live.IndexOf(c) - _scroll.Value, 0);
                    c.X.AnimateTo(r.X, back); c.Y.AnimateTo(r.Y, back); c.W.AnimateTo(r.Width, back); c.H.AnimateTo(r.Height, back);
                    Kick();
                    return;
                }
            }
        }

        private void CloseGap()
        {
            double max = Math.Max(0, Live.Count - 1);
            if (_scroll.Value > max) _scroll.Set(max);
            _layoutLive = false;
            Stack(_scroll.Value, animate: true);
        }

        private void Remove(Card c)
        {
            Shots.Remove(c.Src);
            _cards.Remove(c);
            foreach (var el in new UIElement[] { c.View, c.Shade, c.Label, c.Icon }) _canvas.Children.Remove(el);
            if (Live.Count == 0 && !_cards.Any()) { Finish(); return; }
            CloseGap();
        }

        private async void OpenCard(Card c)
        {
            if (c == null) { Finish(); return; }
            Log.I("Mode tablette : ouverture de « " + c.Title + " »");
            _layoutLive = false;
            _inertia = false;
            var sp = new Spring(0.78, 0.38);
            foreach (var o in _cards) if (o != c) o.Op.AnimateTo(0, Spring.Soft);
            Panel.SetZIndex(c.View, 5000); Panel.SetZIndex(c.Shade, 5001);
            c.Shade.Opacity = 0;
            c.X.AnimateTo(0, sp); c.Y.AnimateTo(0, sp); c.W.AnimateTo(_sw, sp); c.H.AnimateTo(_sh, sp);
            Kick();
            if (Native.IsIconic(c.Src)) Native.ShowWindow(c.Src, Native.SW_RESTORE);
            Native.SetForegroundWindow(c.Src);
            await Settled();
            _openedApp = true;
            App.Taskbar?.HideDock();
            Finish();
        }

        private bool _openedApp;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string name);

        /// <summary>
        /// Les applis rangees : le bureau passe au premier plan (sinon Windows garde l'appli reduite « devant »
        /// et un toucher sur son icone du dock ne la rouvrait pas).
        /// </summary>
        private static void ToDesktop()
        {
            var desk = FindWindow("Progman", null);
            if (desk != IntPtr.Zero) Native.SetForegroundWindow(desk);
        }

        /// <summary>Image a jour d'une fenetre (pour le geste retour), sinon la derniere connue.</summary>
        public static BitmapSource Fresh(IntPtr h)
        {
            var t = Task.Run(() => Capture(h));
            if (t.Wait(350) && t.Result != null) { Shots[h] = t.Result; return t.Result; }
            return Shots.TryGetValue(h, out var b) ? b : null;
        }

        /// <summary>Pour les tests : tout ranger dans le dock d'un coup.</summary>
        public void AllToDock()
        {
            var live = Live;
            if (live.Count == 0) { Finish(); return; }
            _gathered = true;
            var f = new Point(_sw / 2, _sh * 0.62);
            foreach (var c in live) c.Mix.AnimateTo(1, new Spring(0.8, 0.3));
            Gather(f, 200);
            Task.Delay(450).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(IconsToDock)));
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
