using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace WaveIsland
{
    /// <summary>
    /// Le menu des applis ouvert par la touche Windows : il « coule » depuis la Wave Island.
    /// L'ilot s'etire vers le bas, la goutte se detache en deux : l'ilot reprend sa taille avec un petit rebond,
    /// l'autre partie descend et s'ouvre en grand avec toutes les applis.
    /// </summary>
    public sealed class LauncherWindow : Window
    {
        private const double LW = 780;      // largeur de la fenetre
        private const double PW = 700;      // largeur du menu
        private const double Blend = 30;    // douceur de la fusion entre les deux gouttes

        private readonly IslandWindow _island;
        private readonly Grid _root = new Grid();
        private readonly Image _glass = new Image { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly DropEffect _fx = new DropEffect();
        private readonly ScreenCapture _cap = new ScreenCapture();
        private readonly Border _content = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly RectangleGeometry _clip = new RectangleGeometry();
        private readonly DispatcherTimer _captureTimer;

        private readonly SpringValue _aw = new SpringValue(0), _ah = new SpringValue(0), _bx = new SpringValue(0), _by = new SpringValue(0),
            _bw = new SpringValue(0), _bh = new SpringValue(0), _alpha = new SpringValue(0);
        private double _acx, _top, _w0, _h0, _ph, _scale = 1;
        private IntPtr _hwnd;
        private bool _animating, _closing, _open, _excluded;
        private int _gen;

        // Contenu
        private TextBox _search;
        private TextBlock _placeholder;
        private WrapPanel _grid, _favRow;
        private StackPanel _favBlock;
        private ScrollViewer _scroll;
        private readonly Dictionary<AppEntry, FrameworkElement> _cells = new Dictionary<AppEntry, FrameworkElement>();
        private Brush _ink, _inkSoft;
        private TextBlock _empty;
        private string _builtTheme;

        private static readonly FontFamily Ui = new FontFamily("Segoe UI Variable Text, Segoe UI");
        private static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public bool IsOpen => _open;

        public LauncherWindow(IslandWindow island)
        {
            _island = island;
            Title = "Wave Island — Applis";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = true;
            UseLayoutRounding = true;
            FontFamily = Ui;

            _glass.Effect = _fx;
            _root.Children.Add(_glass);
            _content.Clip = _clip;
            _root.Children.Add(_content);
            Content = _root;

            _captureTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
            _captureTimer.Tick += (a, b) => Capture();

            SourceInitialized += (a, b) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW);
                _excluded = Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
            };
            Deactivated += (a, b) => { if (_open && !_closing && !Program.Demo) Close2(); };
            PreviewKeyDown += OnKey;
            AppCatalog.Loaded += () => { if (_grid != null) FillApps(); };
        }

        // ================== Ouverture / fermeture ==================

        public void Toggle()
        {
            if (_open && !_closing) Close2();
            else if (!_open) Open();
        }

        public async void Open()
        {
            if (_open) return;
            _open = true;
            _closing = false;
            int gen = ++_gen;
            AppCatalog.Refresh(Dispatcher);
            Log.I("Menu des applis : ouverture");

            var (cx, top, w0, h0) = _island.RestShape();
            _scale = VisualTreeHelper.GetDpi(_island).DpiScaleX;
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            double sl = sb.Left / _scale, st = sb.Top / _scale, sw = sb.Width / _scale, sh = sb.Height / _scale;
            _top = top; _w0 = w0; _h0 = h0;
            _ph = Math.Max(320, Math.Min(600, sh - top - h0 - 70));
            Width = LW;
            Height = top + h0 + 18 + _ph + 30;
            Left = Math.Max(sl, Math.Min(sl + sw - LW, cx - LW / 2));
            Top = st;
            _acx = cx - Left;
            _glass.Width = LW;
            _glass.Height = Height;

            BuildContent();
            _search.Text = "";
            _content.Opacity = 0;
            _scroll.ScrollToVerticalOffset(0);

            // Depart : la goutte du menu est cachee dans l'ilot
            _aw.Set(w0); _ah.Set(h0);
            _bx.Set(_acx); _by.Set(top + h0 / 2); _bw.Set(w0 * 0.8); _bh.Set(h0 * 0.7);
            _alpha.Set(0);
            Render();
            _island.HideForLauncher(true);
            Show();
            ForceForeground();
            _captureTimer.Start();
            Capture();
            Kick();

            // 1) L'ilot s'etire vers le bas
            var stretch = new Spring(0.75, 0.24);
            _by.AnimateTo(top + h0 + 20, stretch);
            _bw.AnimateTo(w0 * 0.9, stretch);
            _bh.AnimateTo(Math.Max(h0, 34), stretch);
            _aw.AnimateTo(w0 * 0.94, stretch);
            _ah.AnimateTo(h0 * 1.06, stretch);
            Kick();
            await Task.Delay(150);
            if (gen != _gen) return;

            // 2) La goutte se detache : l'ilot rebondit a sa taille, le menu descend et s'ouvre
            double bcx = Math.Max(PW / 2 + 16, Math.Min(LW - PW / 2 - 16, _acx));
            _by.AnimateTo(top + h0 + 18 + _ph / 2, new Spring(0.7, 0.5));
            _bx.AnimateTo(bcx, new Spring(0.75, 0.45));
            _bw.AnimateTo(PW, new Spring(0.72, 0.46));
            _bh.AnimateTo(_ph, new Spring(0.7, 0.5));
            _aw.AnimateTo(w0, Spring.Pop);
            _ah.AnimateTo(h0, Spring.Pop);
            Kick();
            await Task.Delay(170);
            if (gen != _gen) return;
            _alpha.AnimateTo(1, Spring.Soft);
            Kick();
            _search.Focus();
            Keyboard.Focus(_search);
        }

        /// <summary>Le menu remonte et se fond dans l'ilot.</summary>
        public async void Close2()
        {
            if (!_open || _closing) return;
            _closing = true;
            int gen = ++_gen;
            Log.I("Menu des applis : fermeture");
            _alpha.AnimateTo(0, new Spring(0.95, 0.16));
            Kick();
            await Task.Delay(70);
            if (gen != _gen) return;
            var rise = new Spring(0.86, 0.36);
            _bx.AnimateTo(_acx, rise);
            _by.AnimateTo(_top + _h0 * 0.7, rise);
            _bw.AnimateTo(_w0 * 0.8, rise);
            _bh.AnimateTo(_h0 * 0.75, rise);
            Kick();
            await Task.Delay(190);
            if (gen != _gen) return;
            // L'ilot avale la goutte : il gonfle un peu puis reprend sa forme
            _ah.AnimateTo(_h0 * 1.16, new Spring(0.6, 0.2));
            _aw.AnimateTo(_w0 * 1.05, new Spring(0.6, 0.2));
            Kick();
            await Task.Delay(110);
            if (gen != _gen) return;
            _ah.AnimateTo(_h0, Spring.Pop);
            _aw.AnimateTo(_w0, Spring.Pop);
            Kick();
            await Task.Delay(420);
            if (gen != _gen) return;
            _captureTimer.Stop();
            Hide();
            _island.HideForLauncher(false);
            _open = false;
            _closing = false;
        }

        // ================== Rendu ==================

        private void Kick()
        {
            if (_animating) return;
            _animating = true;
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object s, EventArgs e)
        {
            _aw.Tick(); _ah.Tick(); _bx.Tick(); _by.Tick(); _bw.Tick(); _bh.Tick(); _alpha.Tick();
            Render();
            if (!_aw.Moving && !_ah.Moving && !_bx.Moving && !_by.Moving && !_bw.Moving && !_bh.Moving && !_alpha.Moving)
            {
                CompositionTarget.Rendering -= OnFrame;
                _animating = false;
            }
        }

        private void Render()
        {
            double k = _scale;
            double W = LW * k, H = Height * k;
            if (double.IsNaN(H) || H < 2) return;
            double aw = Math.Max(4, _aw.Value), ah = Math.Max(4, _ah.Value), bw = Math.Max(4, _bw.Value), bh = Math.Max(4, _bh.Value);
            double ra = Math.Min(ah / 2, 30), rb = Math.Min(bh / 2, 30);
            _fx.A = new Point4D(_acx * k, (_top + ah / 2) * k, (aw / 2 - ra) * k, (ah / 2 - ra) * k);
            _fx.B = new Point4D(_bx.Value * k, _by.Value * k, Math.Max(0, bw / 2 - rb) * k, Math.Max(0, bh / 2 - rb) * k);
            _fx.Rad = new Point4D(ra * k, rb * k, Blend * k, 1.0 / (Math.Min(18, Math.Min(ah, bh) * 0.42) * k));
            double strength = 14 * k;
            _fx.Sz = new Point4D(W, H, strength / W, strength / H);
            string theme = Settings.Current.Theme;
            bool light = theme == "light";
            double tintA = theme == "dark" ? 0.8 : light ? 0.7 : 0.0;
            double tintB = theme == "dark" ? 0.86 : light ? 0.82 : 0.62;
            _fx.Tint = light ? new Point4D(0.97, 0.98, 1.0, tintA) : new Point4D(0.05, 0.06, 0.08, tintA);
            _fx.Light = new Point4D(-0.5, -0.866, 1, tintB);
            _fx.Blend = new Point4D(0.5 / (Blend * k), Blend * k, 0, 0);
            _fx.Inv = new Point4D(1.0 / (aw / 2 * k), 1.0 / (ah / 2 * k), 1.0 / (bw / 2 * k), 1.0 / (bh / 2 * k));

            // Le contenu suit la goutte du menu (meme forme), a taille fixe pour ne pas se reorganiser
            _content.Width = PW;
            _content.Height = _ph;
            double left = _bx.Value - PW / 2, topY = _by.Value - _ph / 2;
            _content.Margin = new Thickness(left, topY, 0, 0);
            _clip.Rect = new Rect((PW - bw) / 2, (_ph - bh) / 2, bw, bh);
            _clip.RadiusX = _clip.RadiusY = rb;
            _content.Opacity = Math.Max(0, Math.Min(1, _alpha.Value));
            _content.IsHitTestVisible = _alpha.Value > 0.5;
        }

        private void Capture()
        {
            if (_hwnd == IntPtr.Zero || !_excluded || !Settings.Current.Refraction) { if (_glass.Source == null) _glass.Source = Flat(); return; }
            if (!Native.GetWindowRect(_hwnd, out var r)) return;
            try
            {
                if (_cap.Grab(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) && _glass.Source != _cap.Bitmap)
                    _glass.Source = _cap.Bitmap;
            }
            catch { }
        }

        private static BitmapSource Flat()
        {
            var px = new byte[] { 36, 31, 28, 255 };
            var b = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, px, 4);
            b.Freeze();
            return b;
        }

        // ================== Contenu ==================

        private void BuildContent()
        {
            bool light = Settings.Current.Theme == "light";
            var ink = light ? new SolidColorBrush(Color.FromRgb(20, 22, 26)) : Brushes.White;
            var soft = new SolidColorBrush(light ? Color.FromArgb(150, 20, 22, 26) : Color.FromArgb(170, 255, 255, 255));
            if (_grid != null && _builtTheme == Settings.Current.Theme) return;
            _builtTheme = Settings.Current.Theme;
            _ink = ink;
            _inkSoft = soft;
            _cells.Clear();

            var dock = new DockPanel { Margin = new Thickness(26, 22, 26, 18) };

            // Recherche
            var searchBox = new Border
            {
                Height = 44, CornerRadius = new CornerRadius(22), Margin = new Thickness(0, 0, 0, 14),
                Background = new SolidColorBrush(light ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(34, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(light ? Color.FromArgb(60, 0, 0, 0) : Color.FromArgb(50, 255, 255, 255)),
                BorderThickness = new Thickness(1)
            };
            var sg = new Grid();
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sg.ColumnDefinitions.Add(new ColumnDefinition());
            sg.Children.Add(new TextBlock { Text = "", FontFamily = Icons, FontSize = 15, Foreground = soft, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 10, 0) });
            _search = new TextBox
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = ink, CaretBrush = ink,
                FontSize = 15, VerticalAlignment = VerticalAlignment.Center, FontFamily = Ui, Margin = new Thickness(0, 0, 16, 0)
            };
            _placeholder = new TextBlock { Text = "Rechercher une appli", Foreground = soft, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 0) };
            _search.TextChanged += (a, b) => { _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Filter(); };
            Grid.SetColumn(_search, 1);
            Grid.SetColumn(_placeholder, 1);
            sg.Children.Add(_search);
            sg.Children.Add(_placeholder);
            searchBox.Child = sg;
            DockPanel.SetDock(searchBox, Dock.Top);
            dock.Children.Add(searchBox);

            // Barre du bas : reglages, fichiers, verrouiller, veille, redemarrer, eteindre
            var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var user = new TextBlock { Text = Environment.UserName, Foreground = ink, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(RoundButton("", "Paramètres", () => { Close2(); Shortcuts.Start("ms-settings:"); }));
            actions.Children.Add(RoundButton("", "Fichiers", () => { Close2(); Shortcuts.Start("explorer.exe"); }));
            actions.Children.Add(RoundButton("", "Verrouiller", () => { Close2(); Native.LockWorkStation(); }));
            actions.Children.Add(RoundButton("", "Veille", () => { Close2(); Power("sleep"); }));
            actions.Children.Add(RoundButton("", "Redémarrer", () => Power("restart"), confirm: true));
            actions.Children.Add(RoundButton("", "Éteindre", () => Power("shutdown"), confirm: true));
            DockPanel.SetDock(actions, Dock.Right);
            bottom.Children.Add(actions);
            bottom.Children.Add(user);
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(bottom);

            // Applis
            var list = new StackPanel();
            _favBlock = new StackPanel();
            _favBlock.Children.Add(SectionTitle("Les plus utilisées"));
            _favRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            _favBlock.Children.Add(_favRow);
            list.Children.Add(_favBlock);
            list.Children.Add(SectionTitle("Toutes les applis"));
            _grid = new WrapPanel();
            list.Children.Add(_grid);
            _empty = new TextBlock { Text = "Chargement des applis…", Foreground = soft, FontSize = 14, Margin = new Thickness(6, 10, 0, 0) };
            list.Children.Add(_empty);
            _scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, CanContentScroll = false };
            _scroll.PreviewMouseWheel += SmoothWheel;
            dock.Children.Add(_scroll);
            _content.Child = dock;
            FillApps();
        }

        private TextBlock SectionTitle(string s) => new TextBlock
        {
            Text = s, Foreground = _inkSoft, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 4, 0, 8)
        };

        private void FillApps()
        {
            _grid.Children.Clear();
            _cells.Clear();
            foreach (var a in AppCatalog.Apps)
            {
                var c = Cell(a);
                _cells[a] = c;
                _grid.Children.Add(c);
            }
            _favRow.Children.Clear();
            foreach (var a in AppCatalog.Favorites(7)) _favRow.Children.Add(Cell(a));
            _empty.Visibility = AppCatalog.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Filter();
        }

        private void Filter()
        {
            if (_grid == null) return;
            string q = _search?.Text ?? "";
            int shown = 0;
            foreach (var kv in _cells)
            {
                bool ok = AppCatalog.Matches(kv.Key, q);
                kv.Value.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
                if (ok) shown++;
            }
            _favBlock.Visibility = string.IsNullOrWhiteSpace(q) && _favRow.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (AppCatalog.Apps.Count > 0)
            {
                _empty.Text = "Aucune appli trouvée";
                _empty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private FrameworkElement Cell(AppEntry a)
        {
            var img = new Image { Width = 40, Height = 40, Source = a.Icon, HorizontalAlignment = HorizontalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var letter = new TextBlock
            {
                Text = a.Name.Substring(0, 1).ToUpper(), Foreground = _ink, FontSize = 18, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Visibility = a.Icon == null ? Visibility.Visible : Visibility.Collapsed
            };
            var iconHost = new Grid { Width = 48, Height = 48 };
            iconHost.Children.Add(letter);
            iconHost.Children.Add(img);
            a.IconReady += () => { img.Source = a.Icon; letter.Visibility = Visibility.Collapsed; };
            var label = new TextBlock
            {
                Text = a.Name, Foreground = _ink, FontSize = 11.5, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 84, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center
            };
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(iconHost);
            sp.Children.Add(label);
            var hover = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            var cell = new Border
            {
                Width = 92, Height = 90, CornerRadius = new CornerRadius(16), Child = sp, Background = Brushes.Transparent,
                Cursor = Cursors.Hand, ToolTip = a.Name, Margin = new Thickness(1)
            };
            var scale = new ScaleTransform(1, 1, 46, 45);
            cell.RenderTransform = scale;
            cell.MouseEnter += (x, y) => cell.Background = hover;
            cell.MouseLeave += (x, y) => { cell.Background = Brushes.Transparent; scale.ScaleX = scale.ScaleY = 1; };
            cell.MouseLeftButtonDown += (x, y) => { scale.ScaleX = scale.ScaleY = 0.92; y.Handled = true; };
            cell.MouseLeftButtonUp += (x, y) =>
            {
                y.Handled = true;
                scale.ScaleX = scale.ScaleY = 1;
                Close2();
                AppCatalog.Launch(a);
            };
            return cell;
        }

        private FrameworkElement RoundButton(string glyph, string tip, Action act, bool confirm = false)
        {
            var g = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 15, Foreground = _ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var normal = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
            var b = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = normal, Child = g, Cursor = Cursors.Hand, ToolTip = tip, Margin = new Thickness(6, 0, 0, 0) };
            bool armed = false;
            b.MouseEnter += (x, y) => { if (!armed) b.Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)); };
            b.MouseLeave += (x, y) => { if (!armed) b.Background = normal; };
            b.MouseLeftButtonUp += (x, y) =>
            {
                y.Handled = true;
                if (confirm && !armed)
                {
                    // Deuxieme clic pour confirmer (pas d'extinction par erreur)
                    armed = true;
                    b.Background = new SolidColorBrush(Color.FromRgb(230, 80, 70));
                    b.ToolTip = tip + " : clique encore pour confirmer";
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    t.Tick += (p, q) => { t.Stop(); armed = false; b.Background = normal; b.ToolTip = tip; };
                    t.Start();
                    return;
                }
                act();
            };
            return b;
        }

        [DllImport("powrprof.dll")] private static extern bool SetSuspendState(bool hibernate, bool force, bool wakeup);

        private static void Power(string what)
        {
            try
            {
                Log.I("Alimentation : " + what);
                if (what == "sleep") SetSuspendState(false, false, false);
                else Process.Start(new ProcessStartInfo("shutdown.exe", what == "restart" ? "/r /t 0" : "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
            }
            catch (Exception e) { Log.I("Alimentation impossible : " + e.Message); }
        }

        private void OnKey(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close2(); }
            else if (e.Key == Key.Enter)
            {
                var first = _cells.Where(kv => kv.Value.Visibility == Visibility.Visible).Select(kv => kv.Key).FirstOrDefault();
                if (first != null) { e.Handled = true; Close2(); AppCatalog.Launch(first); }
            }
            else if (!_search.IsKeyboardFocused) _search.Focus();
        }

        // Defilement doux
        private double _scrollTarget;
        private bool _scrolling;

        private void SmoothWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            if (!_scrolling) _scrollTarget = _scroll.VerticalOffset;
            _scrollTarget = Math.Max(0, Math.Min(_scroll.ScrollableHeight, _scrollTarget - e.Delta * 0.5));
            if (_scrolling) return;
            _scrolling = true;
            CompositionTarget.Rendering += ScrollFrame;
        }

        private void ScrollFrame(object sender, EventArgs e)
        {
            double cur = _scroll.VerticalOffset, next = cur + (_scrollTarget - cur) * 0.22;
            if (Math.Abs(_scrollTarget - next) < 0.5) { next = _scrollTarget; _scrolling = false; CompositionTarget.Rendering -= ScrollFrame; }
            _scroll.ScrollToVerticalOffset(next);
        }

        // ================== Premier plan ==================

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);

        /// <summary>Prend le clavier (pour taper la recherche tout de suite), meme si une autre appli etait devant.</summary>
        private void ForceForeground()
        {
            if (_hwnd == IntPtr.Zero) return;
            try
            {
                uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
                if (fg != me) AttachThreadInput(fg, me, true);
                BringWindowToTop(_hwnd);
                Native.SetForegroundWindow(_hwnd);
                if (fg != me) AttachThreadInput(fg, me, false);
            }
            catch { }
            Activate();
        }

        // ================== Captures pour les tests automatiques ==================

        public void Snap(string name, string folder)
        {
            try
            {
                Render();
                UpdateLayout();
                Native.GetWindowRect(_hwnd, out var wr);
                int ww = wr.Right - wr.Left, wh = wr.Bottom - wr.Top;
                var bg = new ScreenCapture();
                bg.Grab(wr.Left, wr.Top, ww, wh);
                var fg = new RenderTargetBitmap(ww, wh, 96 * _scale, 96 * _scale, PixelFormats.Pbgra32);
                fg.Render(_root);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawImage(bg.Bitmap, new Rect(0, 0, ww, wh));
                    dc.DrawImage(fg, new Rect(0, 0, ww, wh));
                }
                var outBmp = new RenderTargetBitmap(ww, wh, 96, 96, PixelFormats.Pbgra32);
                outBmp.Render(dv);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(outBmp));
                Directory.CreateDirectory(folder);
                using var f = File.Create(System.IO.Path.Combine(folder, name + ".png"));
                enc.Save(f);
                bg.Dispose();
            }
            catch (Exception e) { Log.I("Capture du menu impossible : " + e.Message); }
        }
    }
}
