using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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
    public sealed partial class LauncherWindow : Window
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
        /// <summary>+1 : le menu coule vers le bas depuis l'ilot ; -1 : il monte depuis la bulle Windows de la barre des taches.</summary>
        private double _dir = 1;
        private bool _fromBar;
        /// <summary>Centre vertical de la goutte de depart (ilot ou bulle), bord d'accroche fixe.</summary>
        private double ACy(double ah) => _top + _dir * ah / 2;

        // La forme de depart : la Wave Island, ou la bulle Windows de la barre des taches (Wave Theme)
        private void AnchorHide(bool hide) { if (_fromBar) App.Taskbar?.HideBubble(hide); else _island.HideForLauncher(hide); }
        private void AnchorTop() { if (_fromBar) App.Taskbar?.BringToTop(); else _island.BringToTop(); }
        private (double w, double h) AnchorNow()
        {
            if (_fromBar && App.Taskbar != null) { var b = App.Taskbar.BubbleShape(); return (b.w, b.h); }
            var c = _island.CurrentShape(); return (c.w, c.h);
        }
        private IntPtr _hwnd;
        private bool _animating, _closing, _open, _excluded;
        /// <summary>true : c'est la vraie Wave Island qui s'affiche (et se clique) au-dessus du menu.</summary>
        private bool _islandLive;
        private readonly FrameGate _gate = new FrameGate();
        private int _gen;

        // Contenu
        private TextBox _search;
        private TextBlock _placeholder;
        private ScrollViewer _scroll;

        private static readonly FontFamily Ui = new FontFamily("Segoe UI Variable Text, Segoe UI");
        private static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public bool IsOpen => _open;

        // Wave Drop : le meme panneau qui coule depuis l'ilot, avec un autre contenu
        private bool _drop, _dialog;
        private WaveDropService.Request _dropRequest;

        /// <summary>Ouvre Wave Drop (avec un fichier deja choisi, ou une demande recue a accepter).</summary>
        public void OpenDrop(string file, WaveDropService.Request request = null)
        {
            if (file != null && App.Drop != null) App.Drop.Pending = WaveDropService.Describe(file);
            _dropRequest = request;
            if (_open && !_closing)
            {
                _drop = true;
                BuildDropContent();
                ApplyInk();
                PopIn(new List<FrameworkElement>(_popQueue), 0);
                _popQueue.Clear();
                return;
            }
            _drop = true;
            Open();
        }

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

            _captureTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = Frames.Interval };
            _captureTimer.Tick += (a, b) => Capture();

            SourceInitialized += (a, b) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW);
                _excluded = Native.ApplyCaptureMode(_hwnd);
            };
            Deactivated += (a, b) => { if (_open && !_closing && !_dialog && !Program.Demo) Close2(); };
            PreviewKeyDown += OnKey;
            AppCatalog.Loaded += () => { if (_sections != null) { FillSections(); if (_open) { PopIn(new List<FrameworkElement>(_popQueue), 0); _popQueue.Clear(); } } };
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

            _fromBar = !_drop && App.Taskbar != null && App.Taskbar.Active;
            _dir = _fromBar ? -1 : 1;
            _islandLive = false;
            _scale = VisualTreeHelper.GetDpi(_island).DpiScaleX;
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            double sl = sb.Left / _scale, st = sb.Top / _scale, sw = sb.Width / _scale, sh = sb.Height / _scale;
            double cx, top, w0, h0;
            (double w, double h) now;
            Width = LW;
            if (_fromBar)
            {
                // Le menu monte depuis la bulle Windows, au-dessus de la barre des taches
                var b = App.Taskbar.BubbleShape();
                cx = b.cx; w0 = b.w; h0 = b.h;
                now = (b.w, b.h);
                double bottom = b.cy + h0 / 2;          // bord bas de la bulle (ecran)
                _ph = Math.Max(320, Math.Min(600, bottom - h0 - st - 70));
                Height = 30 + _ph + 18 + h0 + 12;
                Top = bottom + 12 - Height;
                top = bottom - Top;                     // bord d'accroche (bas) dans la fenetre
            }
            else
            {
                var r = _island.RestShape();
                var c = _island.CurrentShape();
                cx = r.cx; top = r.top; w0 = r.w; h0 = r.h;
                now = (c.w, c.h);
                _ph = _drop ? Math.Min(460, sh - top - h0 - 70) : Math.Max(320, Math.Min(600, sh - top - h0 - 70));
                Height = top + h0 + 18 + _ph + 30;
                Top = st;
            }
            _top = top; _w0 = w0; _h0 = h0;
            Left = Math.Max(sl, Math.Min(sl + sw - LW, cx - LW / 2));
            _acx = cx - Left;
            _glass.Width = LW;
            _glass.Height = Height;

            if (_drop) BuildDropContent(); else BuildContent();
            _content.Opacity = 0;

            // Depart : la goutte du menu est cachee dans l'ilot (exactement la ou il est affiche)
            _aw.Set(now.w); _ah.Set(now.h);
            _bx.Set(_acx); _by.Set(ACy(h0)); _bw.Set(w0 * 0.8); _bh.Set(h0 * 0.7);
            _alpha.Set(0);
            Render();
            if (_hwnd != IntPtr.Zero) _excluded = Native.ApplyCaptureMode(_hwnd);
            _captureTimer.Interval = Frames.Interval;
            Show();
            // Au-dessus de tout, y compris d'une video ou d'un jeu en plein ecran
            if (_hwnd != IntPtr.Zero)
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE);
            AnchorHide(true);
            ForceForeground();
            _captureTimer.Start();
            Capture();
            ApplyInk();
            Kick();

            // 1) L'ilot s'etire vers le bas
            var stretch = new Spring(0.75, 0.24);
            _by.AnimateTo(top + _dir * (h0 + 20), stretch);
            _bw.AnimateTo(w0 * 0.9, stretch);
            _bh.AnimateTo(Math.Max(h0, 34), stretch);
            _aw.AnimateTo(w0 * 0.94, stretch);
            _ah.AnimateTo(h0 * 1.06, stretch);
            Kick();
            await Task.Delay(150);
            if (gen != _gen) return;

            // 2) La goutte se detache : l'ilot rebondit a sa taille, le menu descend et s'ouvre
            double bcx = Math.Max(PW / 2 + 16, Math.Min(LW - PW / 2 - 16, _acx));
            _by.AnimateTo(top + _dir * (h0 + 18 + _ph / 2), new Spring(0.7, 0.5));
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
            // Les icones arrivent une par une avec un rebond
            PopIn(new List<FrameworkElement>(_popQueue), 0);
            _popQueue.Clear();
            if (!_drop) { _search.Focus(); Keyboard.Focus(_search); }

            // Une fois l'ilot immobile, la vraie Wave Island reprend sa place au-dessus du menu :
            // on peut cliquer dessus (raccourcis, son...) pendant que le menu est ouvert.
            await Settled(gen, a: true);
            if (gen != _gen || !_open || _closing) return;
            AnchorHide(false);
            AnchorTop();
            _islandLive = true;
            Render();
        }

        /// <summary>Attend que les ressorts aient fini de bouger (sans couper un rebond).</summary>
        private async Task Settled(int gen, bool a)
        {
            for (int i = 0; i < 120 && gen == _gen; i++)
            {
                bool moving = _aw.Moving || _ah.Moving || (!a && (_bx.Moving || _by.Moving || _bw.Moving || _bh.Moving));
                if (!moving) return;
                await Task.Delay(16);
            }
        }

        /// <summary>Le menu remonte et se fond dans l'ilot.</summary>
        public async void Close2()
        {
            if (!_open || _closing) return;
            _closing = true;
            int gen = ++_gen;
            Log.I("Menu des applis : fermeture");
            if (_islandLive)
            {
                var cur = AnchorNow();
                AnchorHide(true);
                var rest = AnchorNow();
                _aw.Set(cur.w); _ah.Set(cur.h);
                _aw.AnimateTo(rest.w, Spring.Soft); _ah.AnimateTo(rest.h, Spring.Soft);
                _w0 = rest.w; _h0 = rest.h;
                _islandLive = false;
                Render();
            }
            _alpha.AnimateTo(0, new Spring(0.95, 0.16));
            Kick();
            await Task.Delay(70);
            if (gen != _gen) return;
            var rise = new Spring(0.86, 0.36);
            _bx.AnimateTo(_acx, rise);
            _by.AnimateTo(_top + _dir * _h0 * 0.7, rise);
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
            // On attend la fin du rebond (avant : on coupait au milieu, d'ou le petit saut a la fin)
            await Settled(gen, a: false);
            if (gen != _gen) return;
            _captureTimer.Stop();
            _drop = false;
            _dropRequest = null;
            Hide();
            AnchorHide(false);
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
            if (!_gate.Ready()) return;
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
            _fx.A = _islandLive
                ? new Point4D(-5000, -5000, 0, 0) // la vraie Wave Island est affichee : le menu ne la dessine plus
                : new Point4D(_acx * k, ACy(ah) * k, (aw / 2 - ra) * k, (ah / 2 - ra) * k);
            _fx.B = new Point4D(_bx.Value * k, _by.Value * k, Math.Max(0, bw / 2 - rb) * k, Math.Max(0, bh / 2 - rb) * k);
            // Memes reglages que le verre de l'ilot (courbure, lentille, loupe, couleurs, lumiere)
            _fx.Rad = new Point4D(ra * k, rb * k, Blend * k, 1.0 / (Math.Min(18, Math.Min(ah, bh) * 0.42) * k));
            double strength = Math.Min(ah * 0.4, 14) * k;
            const double mag = 0.95;
            _fx.Magc = new Point4D(mag, _acx * k / W * (1 - mag), ACy(ah) * k / H * (1 - mag), 0);
            _fx.Disp = new Point4D(1.14, 0.86, 0, 0);
            _fx.Sz = new Point4D(W, H, strength / W, strength / H);
            string theme = Settings.Current.Theme;
            bool light = theme == "light";
            double tintA = theme == "dark" ? 0.8 : light ? 0.7 : 0.0;
            double tintB = theme == "dark" ? 0.86 : light ? 0.82 : 0.0; // transparent comme l'ilot
            _fx.Tint = light ? new Point4D(0.97, 0.98, 1.0, tintA) : new Point4D(0.05, 0.06, 0.08, tintA);
            var ld = _fromBar ? new Point(-0.5, -0.866) : _island.LightDir;
            if (Math.Abs(ld.X) + Math.Abs(ld.Y) < 0.01) ld = new Point(-0.5, -0.866);
            _fx.Light = new Point4D(ld.X, ld.Y, 1, tintB);
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
                // Seulement la zone utile : les deux gouttes (ilot + menu) et la marge du verre
                double k = _scale, pad = 30;
                double x0 = Math.Min(_acx - _aw.Value / 2, _bx.Value - _bw.Value / 2) - pad;
                double x1 = Math.Max(_acx + _aw.Value / 2, _bx.Value + _bw.Value / 2) + pad;
                double y0 = Math.Max(0, Math.Min(ACy(_ah.Value) - _ah.Value / 2, _by.Value - _bh.Value / 2) - pad);
                double y1 = Math.Max(ACy(_ah.Value) + _ah.Value / 2, _by.Value + _bh.Value / 2) + pad;
                int ww = r.Right - r.Left, wh = r.Bottom - r.Top;
                int sx = Math.Max(0, (int)(x0 * k) & ~15), sw = Math.Min(ww - sx, (int)Math.Ceiling((x1 - x0) * k) + 32);
                int sy = Math.Max(0, (int)(y0 * k) & ~15);
                var sub = new Int32Rect(sx, sy, Math.Max(16, sw), Math.Max(16, Math.Min(wh - sy, (int)Math.Ceiling(y1 * k) + 16 - sy)));
                if (_cap.Grab(r.Left, r.Top, ww, wh, sub) && _glass.Source != _cap.Bitmap)
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

        // ================== Contenu (comme le menu Demarrer de Windows 11) ==================

        private readonly List<(AppEntry app, Image img, TextBlock letter)> _iconSlots = new List<(AppEntry, Image, TextBlock)>();
        private readonly List<FrameworkElement> _popQueue = new List<FrameworkElement>();
        private StackPanel _sections, _results, _allHost;
        private WrapPanel _resultsGrid;
        private Border _overlay;
        private Canvas _overlayLayer;
        private readonly List<Border> _viewButtons = new List<Border>();
        private string _openCategory;
        private bool _darkInk;

        private void BuildContent()
        {
            _iconSlots.Clear();
            _viewButtons.Clear();
            _openCategory = null;
            AppCatalog.IconLoaded -= OnIconLoaded;
            AppCatalog.IconLoaded += OnIconLoaded;

            var root = new Grid();
            var dock = new DockPanel { Margin = new Thickness(26, 22, 26, 16) };

            // Recherche
            var searchBox = new Border
            {
                Height = 44, CornerRadius = new CornerRadius(22), Margin = new Thickness(0, 0, 0, 10),
                Background = _plate, BorderBrush = _plateLine, BorderThickness = new Thickness(1)
            };
            var sg = new Grid();
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sg.ColumnDefinitions.Add(new ColumnDefinition());
            sg.Children.Add(new TextBlock { Text = "", FontFamily = Icons, FontSize = 15, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 10, 0) });
            _search = new TextBox
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 15, VerticalAlignment = VerticalAlignment.Center,
                FontFamily = Ui, Margin = new Thickness(0, 0, 16, 0)
            };
            _search.SetBinding(TextBox.ForegroundProperty, new System.Windows.Data.Binding { Path = new PropertyPath(TextElement.ForegroundProperty), Source = _content });
            _search.SetBinding(TextBox.CaretBrushProperty, new System.Windows.Data.Binding { Path = new PropertyPath(TextElement.ForegroundProperty), Source = _content });
            _placeholder = new TextBlock { Text = "Rechercher des applis", Opacity = 0.7, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 0) };
            _search.TextChanged += (a, b) => { _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Search(); };
            Grid.SetColumn(_search, 1);
            Grid.SetColumn(_placeholder, 1);
            sg.Children.Add(_search);
            sg.Children.Add(_placeholder);
            searchBox.Child = sg;
            DockPanel.SetDock(searchBox, Dock.Top);
            dock.Children.Add(searchBox);

            // Barre du bas : compte a gauche ; Fichiers, Parametres et alimentation a droite (comme Windows)
            var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(IconButton("", "Explorateur de fichiers", () => { Close2(); Shortcuts.Start("explorer.exe"); }));
            right.Children.Add(IconButton("", "Paramètres", () => { Close2(); Shortcuts.Start("ms-settings:"); }));
            FrameworkElement power = null;
            power = IconButton("", "Marche/Arrêt", () => ShowOverlay(power, new (string, string, Action)[]
            {
                ("", "Mettre en veille", () => { Close2(); Power("sleep"); }),
                ("", "Arrêter", () => Power("shutdown")),
                ("", "Redémarrer", () => Power("restart")),
            }, alignRight: true));
            right.Children.Add(power);
            DockPanel.SetDock(right, Dock.Right);
            bottom.Children.Add(right);
            FrameworkElement user = null;
            user = UserButton(() => ShowOverlay(user, new (string, string, Action)[]
            {
                ("", "Modifier les paramètres du compte", () => { Close2(); Shortcuts.Start("ms-settings:yourinfo"); }),
                ("", "Verrouiller", () => { Close2(); Native.LockWorkStation(); }),
                ("", "Se déconnecter", () => Power("logoff")),
            }, alignRight: false));
            bottom.Children.Add(user);
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(bottom);

            // Corps : Epinglees, Recommandations, Toutes les applis (categories / grille / liste)
            var body = new StackPanel();
            _sections = new StackPanel();
            _results = new StackPanel { Visibility = Visibility.Collapsed };
            _resultsGrid = new WrapPanel();
            _results.Children.Add(Header("Meilleures correspondances", null));
            _results.Children.Add(_resultsGrid);
            body.Children.Add(_sections);
            body.Children.Add(_results);
            _scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, CanContentScroll = false };
            _scroll.PreviewMouseWheel += SmoothWheel;
            dock.Children.Add(_scroll);

            root.Children.Add(dock);
            _overlayLayer = new Canvas();
            root.Children.Add(_overlayLayer);
            root.PreviewMouseLeftButtonDown += (a, b) =>
            {
                if (_overlay != null && !_overlay.IsMouseOver) HideOverlay();
            };
            _content.Child = root;
            FillSections();
        }

        /// <summary>Texte clair ou fonce selon le fond (le menu est en verre transparent, comme l'ilot).</summary>
        private void ApplyInk()
        {
            bool dark = Settings.Current.Theme == "light" || (Settings.Current.Theme == "clear" && _cap.Luma > 0.6);
            _darkInk = dark;
            var ink = dark ? new SolidColorBrush(Color.FromRgb(18, 20, 24)) : Brushes.White;
            TextElement.SetForeground(_content, ink);
            PaintPlates(dark);
            // Plus d'ombre sous tout le contenu (tres couteux pendant les animations) : les petits carres suffisent
            ((FrameworkElement)_content.Child).Effect = null;
        }

        private void FillSections()
        {
            if (_sections == null) return;
            _sections.Children.Clear();
            _iconSlots.Clear();
            _popQueue.Clear();

            // Epinglees
            var pinned = AppCatalog.Pinned();
            if (pinned.Count > 0)
            {
                _sections.Children.Add(Header("Épinglé", null));
                var pg = new WrapPanel();
                foreach (var a in pinned) pg.Children.Add(Track(Cell(a)));
                _sections.Children.Add(pg);
            }

            // Recommandations : fichiers ouverts recemment
            var recent = RecentFiles(6);
            if (recent.Count > 0)
            {
                _sections.Children.Add(Header("Recommandé", null));
                var rg = new UniformGrid { Columns = 2 };
                foreach (var f in recent) rg.Children.Add(Track(RecentRow(f)));
                _sections.Children.Add(rg);
            }

            // Toutes les applis + choix de l'affichage
            var views = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (key, label) in new[] { ("categories", "Catégorie"), ("grid", "Grille"), ("list", "Liste") })
            {
                var b = Chip(label, () => { Settings.Current.StartView = key; Settings.Current.Save(); _openCategory = null; FillAll(true); });
                b.Tag = key;
                _viewButtons.Add(b);
                views.Children.Add(b);
            }
            _sections.Children.Add(Header("Toutes", views));
            _allHost = new StackPanel();
            _sections.Children.Add(_allHost);
            FillAll(false);
            if (AppCatalog.Apps.Count == 0)
                _sections.Children.Add(PlateText("Chargement des applis…", 14, new Thickness(6, 10, 0, 0)));
        }

        private void FillAll(bool pop)
        {
            if (_allHost == null) return;
            _allHost.Children.Clear();
            string view = Settings.Current.StartView;
            foreach (var b in _viewButtons)
                b.Background = (string)b.Tag == view ? _plateOn : _plate;
            var made = new List<FrameworkElement>();

            if (view == "categories" && _openCategory == null)
            {
                var wrap = new WrapPanel();
                foreach (var cat in AppCatalog.Categories)
                {
                    var apps = AppCatalog.Apps.Where(a => a.Category == cat).ToList();
                    if (apps.Count == 0) continue;
                    var tile = CategoryTile(cat, apps);
                    wrap.Children.Add(tile);
                    made.Add(tile);
                }
                _allHost.Children.Add(wrap);
            }
            else if (view == "categories")
            {
                var back = Chip("‹  " + _openCategory, () => { _openCategory = null; FillAll(true); });
                back.HorizontalAlignment = HorizontalAlignment.Left;
                back.Margin = new Thickness(4, 0, 0, 8);
                _allHost.Children.Add(back);
                var wrap = new WrapPanel();
                foreach (var a in AppCatalog.Apps.Where(x => x.Category == _openCategory)) { var c = Cell(a); wrap.Children.Add(c); made.Add(c); }
                _allHost.Children.Add(wrap);
            }
            else if (view == "grid")
            {
                var wrap = new WrapPanel();
                foreach (var a in AppCatalog.Apps) { var c = Cell(a); wrap.Children.Add(c); made.Add(c); }
                _allHost.Children.Add(wrap);
            }
            else
            {
                char letter = '\0';
                foreach (var a in AppCatalog.Apps)
                {
                    char l = char.ToUpperInvariant(RemoveAccent(a.Name[0]));
                    if (!char.IsLetter(l)) l = '#';
                    if (l != letter)
                    {
                        letter = l;
                        _allHost.Children.Add(PlateText(l.ToString(), 13, new Thickness(8, 10, 0, 4), bold: true));
                    }
                    var r = ListRow(a);
                    _allHost.Children.Add(r);
                    made.Add(r);
                }
            }
            if (pop) PopIn(made, 0);
            else _popQueue.AddRange(made);
        }

        private static char RemoveAccent(char c)
        {
            string d = c.ToString().Normalize(System.Text.NormalizationForm.FormD);
            return d.Length > 0 ? d[0] : c;
        }

        private FrameworkElement Track(FrameworkElement e) { _popQueue.Add(e); return e; }

        /// <summary>Chaque icone apparait avec un petit rebond, les unes apres les autres.</summary>
        private void PopIn(List<FrameworkElement> items, int delayMs)
        {
            var sp = new Spring(0.5, 0.44);
            int i = 0;
            foreach (var e in items)
            {
                var st = e.RenderTransform as ScaleTransform;
                if (st == null) continue;
                if (i > 42) { st.ScaleX = st.ScaleY = 1; e.Opacity = 1; continue; } // hors de l'ecran : pas d'animation
                var begin = TimeSpan.FromMilliseconds(delayMs + i * 16);
                st.ScaleX = st.ScaleY = 0.3;
                e.Opacity = 0;
                var grow = new System.Windows.Media.Animation.DoubleAnimation(0.3, 1, TimeSpan.FromSeconds(sp.Seconds))
                {
                    BeginTime = begin, EasingFunction = new SpringEase(), FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
                };
                grow.Completed += (a, b) => { st.ScaleX = st.ScaleY = 1; };
                var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
                {
                    BeginTime = begin, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
                };
                fade.Completed += (a, b) => e.Opacity = 1;
                st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                e.BeginAnimation(OpacityProperty, fade);
                i++;
            }
        }

        private void OnIconLoaded(AppEntry a)
        {
            foreach (var (app, img, letter) in _iconSlots)
                if (app == a) { img.Source = a.Icon; letter.Visibility = Visibility.Collapsed; }
        }

        private FrameworkElement Header(string title, FrameworkElement right)
        {
            var g = new DockPanel { Margin = new Thickness(6, 8, 4, 6), LastChildFill = true };
            if (right != null) { DockPanel.SetDock(right, Dock.Right); g.Children.Add(right); }
            g.Children.Add(new Border
            {
                Background = _plate, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold }
            });
            return g;
        }

        /// <summary>Texte pose sur un petit carre (lisible sur n'importe quel fond).</summary>
        private Border PlateText(string text, double size, Thickness margin, bool bold = false) => new Border
        {
            Background = _plate, CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 3, 9, 4), Margin = margin,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal }
        };

        private Border Chip(string label, Action click)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(13), Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand,
                Background = _plate,
                Child = new TextBlock { Text = label, FontSize = 12.5 }
            };
            b.MouseLeftButtonUp += (x, y) => { y.Handled = true; click(); };
            return b;
        }

        private (Image img, TextBlock letter) IconOf(AppEntry a, double size)
        {
            var img = new Image { Width = size, Height = size, Source = a.Icon, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var letter = new TextBlock
            {
                Text = a.Name.Substring(0, 1).ToUpper(), FontSize = size * 0.45, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Visibility = a.Icon == null ? Visibility.Visible : Visibility.Collapsed
            };
            _iconSlots.Add((a, img, letter));
            return (img, letter);
        }

        /// <summary>Une appli : icone + nom. Clic : ouvrir. Clic droit : epingler / desepingler.</summary>
        private FrameworkElement Cell(AppEntry a)
        {
            var (img, letter) = IconOf(a, 40);
            var iconHost = new Grid { Width = 48, Height = 48 };
            iconHost.Children.Add(letter);
            iconHost.Children.Add(img);
            var label = new TextBlock
            {
                Text = a.Name, FontSize = 11.5, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 84, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center
            };
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(iconHost);
            sp.Children.Add(label);
            return Clickable(sp, 92, 88, a.Name, () => { Close2(); AppCatalog.Launch(a); }, a);
        }

        private FrameworkElement ListRow(AppEntry a)
        {
            var (img, letter) = IconOf(a, 26);
            var g = new Grid { Width = 32, Height = 32, Margin = new Thickness(10, 0, 12, 0) };
            g.Children.Add(letter);
            g.Children.Add(img);
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(g);
            sp.Children.Add(new TextBlock { Text = a.Name, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
            var cell = Clickable(sp, double.NaN, 42, a.Name, () => { Close2(); AppCatalog.Launch(a); }, a);
            ((Border)cell).HorizontalAlignment = HorizontalAlignment.Stretch;
            return cell;
        }

        /// <summary>Tuile de categorie : 4 icones et le nom, comme dans Windows 11. Clic : les applis de la categorie.</summary>
        private FrameworkElement CategoryTile(string cat, List<AppEntry> apps)
        {
            var icons = new UniformGrid { Columns = 2, Rows = 2, Width = 92, Height = 92, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var a in apps.OrderByDescending(x => Settings.Current.LaunchCounts.TryGetValue(x.Id, out int n) ? n : 0).Take(4))
            {
                var (img, letter) = IconOf(a, 34);
                var gg = new Grid();
                gg.Children.Add(letter);
                gg.Children.Add(img);
                icons.Children.Add(gg);
            }
            var sp = new StackPanel();
            var box = new Border
            {
                CornerRadius = new CornerRadius(20), Width = 116, Height = 116, HorizontalAlignment = HorizontalAlignment.Center,
                Background = _plate, BorderBrush = _plateLine, BorderThickness = new Thickness(1),
                Child = icons, Padding = new Thickness(8)
            };
            sp.Children.Add(box);
            sp.Children.Add(new Border
            {
                Background = _plate, CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock { Text = cat, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140 }
            });
            return Clickable(sp, 160, 156, cat + $" ({apps.Count})", () => { _openCategory = cat; FillAll(true); _scroll.ScrollToVerticalOffset(Math.Max(0, _allHost.TranslatePoint(new Point(0, 0), (UIElement)_scroll.Content).Y - 40)); }, null);
        }

        private class RecentFile { public string Path, Name; public DateTime When; }

        private static List<RecentFile> RecentFiles(int n)
        {
            try
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                return new DirectoryInfo(dir).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTime)
                    .Where(f => !f.Name.StartsWith("ms-"))
                    .Take(n).Select(f => new RecentFile { Path = f.FullName, Name = System.IO.Path.GetFileNameWithoutExtension(f.Name), When = f.LastWriteTime }).ToList();
            }
            catch { return new List<RecentFile>(); }
        }

        private static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return "À l'instant";
            if (d.TotalMinutes < 60) return $"Il y a {(int)d.TotalMinutes} min";
            if (d.TotalHours < 24) return $"Il y a {(int)d.TotalHours} h";
            if (d.TotalDays < 2) return "Hier";
            return t.ToString("d MMMM", new System.Globalization.CultureInfo("fr-FR"));
        }

        private FrameworkElement RecentRow(RecentFile f)
        {
            var img = new Image { Width = 30, Height = 30, Margin = new Thickness(10, 0, 12, 0) };
            Task.Run(() => AppCatalog.LoadShellIcon(f.Path, 32)).ContinueWith(t => { if (t.Result != null) img.Source = t.Result; }, TaskScheduler.FromCurrentSynchronizationContext());
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = f.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230 });
            texts.Children.Add(new TextBlock { Text = Ago(f.When), FontSize = 11, Opacity = 0.7 });
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(img);
            sp.Children.Add(texts);
            return Clickable(sp, double.NaN, 50, f.Name, () =>
            {
                Close2();
                try { Process.Start(new ProcessStartInfo(f.Path) { UseShellExecute = true }); } catch { }
            }, null);
        }

        /// <summary>Zone cliquable avec survol, petit enfoncement au clic et rebond d'apparition.</summary>
        private FrameworkElement Clickable(FrameworkElement child, double w, double h, string tip, Action click, AppEntry app)
        {
            var press = new ScaleTransform(1, 1);
            child.RenderTransformOrigin = new Point(0.5, 0.5);
            child.RenderTransform = press;
            var cell = new Border
            {
                Width = w, Height = h, CornerRadius = new CornerRadius(14), Child = child, Background = app != null || double.IsNaN(w) ? _plate : Brushes.Transparent,
                Cursor = Cursors.Hand, ToolTip = tip, Margin = new Thickness(3),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1)
            };
            var rest = cell.Background;
            cell.MouseEnter += (x, y) => cell.Background = _plateHover;
            cell.MouseLeave += (x, y) => { cell.Background = rest; press.ScaleX = press.ScaleY = 1; };
            cell.MouseLeftButtonDown += (x, y) => { press.ScaleX = press.ScaleY = 0.9; y.Handled = true; };
            cell.MouseLeftButtonUp += (x, y) => { y.Handled = true; press.ScaleX = press.ScaleY = 1; click(); };
            if (app != null)
            {
                cell.MouseRightButtonUp += (x, y) =>
                {
                    y.Handled = true;
                    bool pinned = Settings.Current.Pinned.Contains(app.Id);
                    ShowOverlay(cell, new (string, string, Action)[]
                    {
                        ("", "Ouvrir", () => { Close2(); AppCatalog.Launch(app); }),
                        (pinned ? "" : "", pinned ? "Désépingler" : "Épingler", () => { AppCatalog.TogglePin(app); FillSections(); PopIn(new List<FrameworkElement>(_popQueue), 0); _popQueue.Clear(); }),
                    }.Concat(App.Taskbar != null && App.Taskbar.Active
                        ? new (string, string, Action)[] { ("\uE718", App.Taskbar.IsPinned(app) ? "Retirer de la barre des tâches" : "Épingler à la barre des tâches", () => App.Taskbar.TogglePin(app)) }
                        : new (string, string, Action)[0]).ToArray(), alignRight: false, atMouse: true);
                };
            }
            return cell;
        }

        private FrameworkElement IconButton(string glyph, string tip, Action click)
        {
            var g = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var b = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = _plate, Child = g, Cursor = Cursors.Hand, ToolTip = tip, Margin = new Thickness(6, 0, 0, 0) };
            b.MouseEnter += (x, y) => b.Background = _plateHover;
            b.MouseLeave += (x, y) => b.Background = _plate;
            b.MouseLeftButtonUp += (x, y) => { y.Handled = true; click(); };
            return b;
        }

        private FrameworkElement UserButton(Action click)
        {
            string name = Environment.UserName;
            var avatar = new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(Color.FromRgb(79, 93, 240)),
                Child = new TextBlock { Text = name.Substring(0, 1).ToUpper(), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(avatar);
            sp.Children.Add(new TextBlock { Text = name, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) });
            var b = new Border { CornerRadius = new CornerRadius(20), Padding = new Thickness(6, 4, 12, 4), Child = sp, Cursor = Cursors.Hand, Background = _plate, HorizontalAlignment = HorizontalAlignment.Left };
            b.MouseEnter += (x, y) => b.Background = _plateHover;
            b.MouseLeave += (x, y) => b.Background = _plate;
            b.MouseLeftButtonUp += (x, y) => { y.Handled = true; click(); };
            return b;
        }

        /// <summary>Petit menu en verre (alimentation, compte, epingler).</summary>
        private void ShowOverlay(FrameworkElement anchor, (string glyph, string label, Action act)[] items, bool alignRight, bool atMouse = false)
        {
            HideOverlay();
            var col = new StackPanel { Margin = new Thickness(6) };
            foreach (var (glyph, label, act) in items)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 14, Width = 26, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = label, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
                var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 18, 8), Child = row, Cursor = Cursors.Hand, Background = Brushes.Transparent };
                b.MouseEnter += (x, y) => b.Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
                b.MouseLeave += (x, y) => b.Background = Brushes.Transparent;
                b.MouseLeftButtonUp += (x, y) => { y.Handled = true; HideOverlay(); act(); };
                col.Children.Add(b);
            }
            bool dark = _darkInk;
            _overlay = new Border
            {
                CornerRadius = new CornerRadius(16), Child = col,
                Background = new SolidColorBrush(dark ? Color.FromArgb(235, 248, 249, 252) : Color.FromArgb(235, 28, 31, 36)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), BorderThickness = new Thickness(1),
                RenderTransformOrigin = new Point(0.5, 1), RenderTransform = new ScaleTransform(0.85, 0.85), Opacity = 0
            };
            _overlayLayer.Children.Add(_overlay);
            _overlay.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var sz = _overlay.DesiredSize;
            Point p = atMouse ? Mouse.GetPosition(_overlayLayer) : anchor.TranslatePoint(new Point(alignRight ? anchor.ActualWidth : 0, 0), _overlayLayer);
            double x = alignRight ? p.X - sz.Width : p.X;
            double y = atMouse ? p.Y : p.Y - sz.Height - 6;
            Canvas.SetLeft(_overlay, Math.Max(4, Math.Min(PW - sz.Width - 4, x)));
            Canvas.SetTop(_overlay, Math.Max(4, Math.Min(_ph - sz.Height - 4, y)));
            var sp = new Spring(0.6, 0.32);
            var grow = new System.Windows.Media.Animation.DoubleAnimation(0.85, 1, TimeSpan.FromSeconds(sp.Seconds)) { EasingFunction = new SpringEase() };
            _overlay.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            _overlay.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            _overlay.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        }

        private void HideOverlay()
        {
            if (_overlay == null) return;
            _overlayLayer.Children.Remove(_overlay);
            _overlay = null;
        }

        /// <summary>Recherche : les meilleures correspondances (debut du nom d'abord).</summary>
        private void Search()
        {
            if (_sections == null) return;
            string q = _search.Text.Trim();
            bool searching = q.Length > 0;
            _sections.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
            _results.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
            if (!searching) return;
            _resultsGrid.Children.Clear();
            var ci = System.Globalization.CultureInfo.CurrentCulture.CompareInfo;
            var opts = System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;
            var found = AppCatalog.Apps.Where(a => AppCatalog.Matches(a, q))
                .OrderBy(a => ci.IsPrefix(a.Name, q, opts) ? 0 : 1).ThenBy(a => a.Name).Take(28).ToList();
            _firstResult = found.FirstOrDefault();
            foreach (var a in found) _resultsGrid.Children.Add(Cell(a));
            if (found.Count == 0)
                _resultsGrid.Children.Add(PlateText("Aucune appli trouvée", 14, new Thickness(6, 6, 0, 0)));
        }

        private AppEntry _firstResult;

        // Petits carres sous chaque icone et chaque texte : clairs si le texte est fonce, sombres s'il est clair
        private readonly SolidColorBrush _plate = new SolidColorBrush(Color.FromArgb(110, 20, 22, 26));
        private readonly SolidColorBrush _plateHover = new SolidColorBrush(Color.FromArgb(160, 20, 22, 26));
        private readonly SolidColorBrush _plateOn = new SolidColorBrush(Color.FromArgb(200, 20, 22, 26));
        private readonly SolidColorBrush _plateLine = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));

        private void PaintPlates(bool darkInk)
        {
            if (darkInk)
            {
                _plate.Color = Color.FromArgb(150, 255, 255, 255);
                _plateHover.Color = Color.FromArgb(205, 255, 255, 255);
                _plateOn.Color = Color.FromArgb(235, 255, 255, 255);
                _plateLine.Color = Color.FromArgb(90, 255, 255, 255);
            }
            else
            {
                _plate.Color = Color.FromArgb(110, 18, 20, 24);
                _plateHover.Color = Color.FromArgb(160, 18, 20, 24);
                _plateOn.Color = Color.FromArgb(205, 18, 20, 24);
                _plateLine.Color = Color.FromArgb(50, 255, 255, 255);
            }
        }

        [DllImport("powrprof.dll")] private static extern bool SetSuspendState(bool hibernate, bool force, bool wakeup);

        private static void Power(string what)
        {
            try
            {
                Log.I("Alimentation : " + what);
                if (what == "sleep") SetSuspendState(false, false, false);
                else
                {
                    string args = what == "restart" ? "/r /t 0" : what == "logoff" ? "/l" : "/s /t 0";
                    Process.Start(new ProcessStartInfo("shutdown.exe", args) { CreateNoWindow = true, UseShellExecute = false });
                }
            }
            catch (Exception e) { Log.I("Alimentation impossible : " + e.Message); }
        }

        private void OnKey(object s, KeyEventArgs e)
        {
            if (_drop || _search == null)
            {
                if (e.Key == Key.Escape) { e.Handled = true; if (_overlay != null) HideOverlay(); else Close2(); }
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (_overlay != null) HideOverlay();
                else if (_search.Text.Length > 0) _search.Text = "";
                else Close2();
            }
            else if (e.Key == Key.Enter && _search.Text.Trim().Length > 0 && _firstResult != null)
            {
                e.Handled = true;
                Close2();
                AppCatalog.Launch(_firstResult);
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
