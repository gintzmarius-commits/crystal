using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace WaveIsland
{
    /// <summary>
    /// La Wave Island : une fenetre transparente toujours au-dessus, en haut de l'ecran.
    /// La fenetre garde une taille fixe ; seule la forme dessinee par le verre change (pas de saut).
    /// Les pixels transparents laissent passer la souris.
    /// </summary>
    public sealed class IslandWindow : Window
    {
        private enum Mode { Collapsed, Volume, Note, Menu }

        /// <summary>Marge autour de l'ilot (pour que le verre puisse aller chercher le fond autour).</summary>
        private const double Margin = 26;
        private const double WinW = 540;

        private readonly VolumeService _volume;
        private readonly Grid _root = new Grid();
        private readonly Image _glass = new Image { Stretch = Stretch.Fill, IsHitTestVisible = true };
        private readonly GlassEffect _fx = new GlassEffect();
        private readonly Grid _box = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly RectangleGeometry _clip = new RectangleGeometry();
        private readonly ScreenCapture _cap = new ScreenCapture();

        private readonly SpringValue _w, _h, _alpha = new SpringValue(0), _tint = new SpringValue(0), _level = new SpringValue(0);
        private readonly DispatcherTimer _captureTimer, _hideTimer, _watchTimer;
        private Mode _mode = Mode.Collapsed;
        private FrameworkElement _panel;
        private IntPtr _hwnd;
        private double _scale = 1;
        private bool _excluded, _hidden, _hover, _animating;
        private BitmapSource _flat;
        private IslandNote _note;

        // Contenus
        private readonly Grid _volPanel;
        private readonly TextBlock _volIcon, _volPct;
        private readonly Border _volFill, _volTrack;
        private readonly Grid _notePanel;
        private readonly TextBlock _noteApp, _noteTitle, _noteText, _noteLetter;
        private readonly Border _noteBadge;
        private readonly Image _noteImg;
        private readonly Border _menuPanel;
        private readonly StackPanel _menuRow;

        private static readonly FontFamily Ui = new FontFamily("Segoe UI Variable Text, Segoe UI");
        private static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public IslandWindow(VolumeService volume)
        {
            _volume = volume;
            Title = "Wave Island";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            UseLayoutRounding = true;

            var s = Settings.Current;
            _w = new SpringValue(s.Width);
            _h = new SpringValue(s.Height);

            _glass.Effect = _fx;
            _root.Children.Add(_glass);
            _box.Clip = _clip;
            _root.Children.Add(_box);
            Content = _root;

            // --- Volume (facon iPhone : icone, barre, pourcentage) ---
            _volIcon = Glyph("", 17);
            _volIcon.Margin = new Thickness(16, 0, 10, 0);
            _volTrack = new Border { Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) };
            _volFill = new Border { Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.White };
            var bar = new Grid { VerticalAlignment = VerticalAlignment.Center };
            bar.Children.Add(_volTrack);
            bar.Children.Add(_volFill);
            _volPct = Text("50", 13, FontWeights.SemiBold);
            _volPct.Margin = new Thickness(12, 0, 16, 0);
            _volPct.MinWidth = 26;
            _volPct.TextAlignment = TextAlignment.Right;
            _volPanel = new Grid { Width = 290, VerticalAlignment = VerticalAlignment.Center };
            _volPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _volPanel.ColumnDefinitions.Add(new ColumnDefinition());
            _volPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(bar, 1);
            Grid.SetColumn(_volPct, 2);
            _volPanel.Children.Add(_volIcon);
            _volPanel.Children.Add(bar);
            _volPanel.Children.Add(_volPct);

            // --- Notification ---
            _noteLetter = Text("M", 18, FontWeights.SemiBold);
            _noteLetter.HorizontalAlignment = HorizontalAlignment.Center;
            _noteLetter.VerticalAlignment = VerticalAlignment.Center;
            _noteImg = new Image { Stretch = Stretch.UniformToFill };
            _noteBadge = new Border
            {
                Width = 44, Height = 44, CornerRadius = new CornerRadius(12), Margin = new Thickness(16, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true
            };
            var badgeGrid = new Grid();
            badgeGrid.Children.Add(_noteLetter);
            badgeGrid.Children.Add(_noteImg);
            _noteBadge.Child = badgeGrid;
            _noteApp = Text("", 11.5, FontWeights.Normal);
            _noteApp.Opacity = 0.65;
            _noteTitle = Text("", 14.5, FontWeights.SemiBold);
            _noteText = Text("", 13, FontWeights.Normal);
            _noteText.Opacity = 0.85;
            var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
            lines.Children.Add(_noteApp);
            lines.Children.Add(_noteTitle);
            lines.Children.Add(_noteText);
            _notePanel = new Grid { Width = 400, Height = 80 };
            _notePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _notePanel.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(lines, 1);
            _notePanel.Children.Add(_noteBadge);
            _notePanel.Children.Add(lines);

            // --- Menu des raccourcis ---
            _menuRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _menuPanel = new Border { Child = _menuRow, Height = 84 };

            foreach (var p in new FrameworkElement[] { _volPanel, _notePanel, _menuPanel })
            {
                p.Opacity = 0;
                p.HorizontalAlignment = HorizontalAlignment.Center;
                if (p != _volPanel) p.VerticalAlignment = VerticalAlignment.Center;
                _box.Children.Add(p);
            }

            // --- Souris ---
            MouseEnter += (a, b) => { _hover = true; if (_mode == Mode.Collapsed) Resize(); _hideTimer.Stop(); };
            MouseLeave += (a, b) =>
            {
                _hover = false;
                if (_mode == Mode.Collapsed) Resize();
                else RestartHide(_mode == Mode.Menu ? 0.7 : 2.5);
            };
            MouseLeftButtonUp += (a, b) =>
            {
                if (_mode == Mode.Note) OpenNote();
                else if (_mode != Mode.Menu) ShowMenu();
            };
            MouseRightButtonUp += (a, b) =>
            {
                if (_mode == Mode.Collapsed) App.OpenSettings();
                else Collapse();
            };
            MouseWheel += (a, b) => _volume?.Wheel(b.Delta);

            _captureTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
            _captureTimer.Tick += (a, b) => CaptureFrame();
            _hideTimer = new DispatcherTimer();
            _hideTimer.Tick += (a, b) => { _hideTimer.Stop(); if (!_hover) Collapse(); };
            _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _watchTimer.Tick += (a, b) => Watch();

            SourceInitialized += (a, b) => Setup();
            DpiChanged += (a, b) => { _scale = b.NewDpi.DpiScaleX; Place(); };
            Settings.Changed += () => Dispatcher.BeginInvoke(new Action(ApplySettings));
        }

        // ================== Mise en place ==================

        private void Setup()
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            // L'ilot n'apparait pas dans les captures : on peut donc filmer ce qu'il y a derriere lui
            _excluded = Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
            Log.I(_excluded ? "Verre liquide : capture du fond active" : "Verre liquide : Windows trop ancien, fond fixe");
            _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Place();
            ApplySettings();
            _captureTimer.Start();
            _watchTimer.Start();
            BuildMenu();
        }

        /// <summary>Place la fenetre en haut de l'ecran principal (a gauche, au centre ou a droite).</summary>
        private void Place()
        {
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds; // pixels physiques
            double sl = sb.Left / _scale, st = sb.Top / _scale, sw = sb.Width / _scale;
            double h = Settings.Current.Top + 84 + Margin + 8;
            Width = WinW;
            Height = h;
            switch (Settings.Current.Position)
            {
                case "left": Left = sl + 14 - Margin; break;
                case "right": Left = sl + sw - 14 + Margin - WinW; break;
                default: Left = sl + (sw - WinW) / 2; break;
            }
            Top = st;
            _glass.Width = WinW;
            _glass.Height = h;
            Render();
        }

        private void ApplySettings()
        {
            Place();
            if (_mode == Mode.Collapsed) Resize();
            _flat = null;
            Restyle();
            BuildMenu();
            Render();
        }

        /// <summary>Couleurs du texte selon le theme.</summary>
        private void Restyle()
        {
            bool light = Settings.Current.Theme == "light";
            var ink = light ? new SolidColorBrush(Color.FromRgb(20, 22, 26)) : Brushes.White;
            foreach (var t in new[] { _volIcon, _volPct, _noteApp, _noteTitle, _noteText })
                t.Foreground = ink;
            _volFill.Background = ink;
            _volTrack.Background = new SolidColorBrush(light ? Color.FromArgb(50, 0, 0, 0) : Color.FromArgb(70, 255, 255, 255));
            foreach (var t in Texts(_menuRow)) t.Foreground = ink;
        }

        private static System.Collections.Generic.IEnumerable<TextBlock> Texts(DependencyObject e)
        {
            if (e is TextBlock tb) yield return tb;
            foreach (var c in LogicalTreeHelper.GetChildren(e).OfType<DependencyObject>())
                foreach (var x in Texts(c)) yield return x;
        }

        // ================== Etats ==================

        /// <summary>Niveau du son (0..1) affiche dans l'ilot.</summary>
        public void ShowVolume(float level, bool muted)
        {
            if (_hidden) return;
            _volIcon.Text = muted || level < 0.005 ? "" : level < 0.34 ? "" : level < 0.67 ? "" : "";
            _volPct.Text = muted ? "0" : Math.Round(level * 100).ToString();
            double target = muted ? 0 : level;
            if (_mode != Mode.Volume) _level.Set(target); else _level.AnimateTo(target, Spring.Soft);
            Switch(Mode.Volume, _volPanel);
            RestartHide(1.6);
        }

        public void ShowNote(IslandNote n)
        {
            if (_hidden && n.OnClick == null) return;
            _note = n;
            _noteApp.Text = n.App;
            _noteTitle.Text = n.Title;
            _noteText.Text = n.Text ?? "";
            _noteText.Visibility = string.IsNullOrWhiteSpace(n.Text) ? Visibility.Collapsed : Visibility.Visible;
            _noteText.TextTrimming = TextTrimming.CharacterEllipsis;
            _noteTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            _noteLetter.Text = string.IsNullOrEmpty(n.App) ? "•" : n.App.Substring(0, 1).ToUpper();
            _noteBadge.Background = new SolidColorBrush(BadgeColor(n.App));
            _noteImg.Source = LoadIcon(n.Icon);
            if (_mode == Mode.Note) { _alpha.Set(0.35); _alpha.AnimateTo(1, Spring.Soft); Kick(); }
            Switch(Mode.Note, _notePanel);
            RestartHide(5.5);
        }

        public void ShowMenu()
        {
            if (_hidden) return;
            BuildMenu();
            Switch(Mode.Menu, _menuPanel);
            RestartHide(6);
        }

        public void Collapse()
        {
            _hideTimer.Stop();
            if (_mode == Mode.Collapsed) return;
            _mode = Mode.Collapsed;
            _alpha.AnimateTo(0, Spring.Close);
            Resize();
        }

        private void Switch(Mode m, FrameworkElement panel)
        {
            bool fromCollapsed = _mode == Mode.Collapsed;
            if (_panel != panel || fromCollapsed)
            {
                _panel = panel;
                _alpha.Set(fromCollapsed ? 0 : 0.2);
                _alpha.AnimateTo(1, Spring.Soft);
            }
            _mode = m;
            Resize();
        }

        /// <summary>Taille cible selon l'etat (avec ressort).</summary>
        private void Resize()
        {
            var s = Settings.Current;
            double w, h;
            Spring sw, sh;
            switch (_mode)
            {
                case Mode.Volume: w = 290; h = Math.Max(s.Height, 40); sw = Spring.OpenW; sh = Spring.Open; break;
                case Mode.Note: w = 400; h = 80; sw = Spring.OpenW; sh = Spring.Open; break;
                case Mode.Menu: w = Math.Max(_menuRow.Children.Count * 62 + 18, s.Width); h = 84; sw = Spring.OpenW; sh = Spring.Open; break;
                default:
                    w = s.Width + (_hover ? 10 : 0);
                    h = s.Height + (_hover ? 3 : 0);
                    sw = _hover ? Spring.Pop : Spring.Close; sh = sw;
                    break;
            }
            w = Math.Min(w, WinW - 2 * Margin);
            if (Math.Abs(_w.Target - w) > 0.1) _w.AnimateTo(w, sw);
            if (Math.Abs(_h.Target - h) > 0.1) _h.AnimateTo(h, sh);
            bool expanded = _mode != Mode.Collapsed;
            double tint = TintFor(expanded);
            if (Math.Abs(_tint.Target - tint) > 0.001) _tint.AnimateTo(tint, Spring.Soft);
            Kick();
        }

        private double TintFor(bool expanded)
        {
            var s = Settings.Current;
            bool see = s.Refraction && _excluded;
            switch (s.Theme)
            {
                case "dark": return see ? (expanded ? 0.86 : 0.8) : 0.93;
                case "light": return see ? (expanded ? 0.8 : 0.7) : 0.9;
                // Transparent : pur verre ; un voile sombre leger seulement quand il y a du texte a lire
                default: return see ? (expanded ? 0.3 : 0.0) : (expanded ? 0.7 : 0.55);
            }
        }

        private void RestartHide(double seconds)
        {
            _hideTimer.Stop();
            _hideTimer.Interval = TimeSpan.FromSeconds(seconds);
            _hideTimer.Start();
        }

        private void OpenNote()
        {
            var n = _note;
            Collapse();
            if (n == null) return;
            if (n.OnClick != null) n.OnClick();
            else Shortcuts.OpenApp(n.Aumid);
        }

        // ================== Menu ==================

        private void BuildMenu()
        {
            _menuRow.Children.Clear();
            var items = Settings.Current.Shortcuts.Select(Shortcuts.Resolve).Where(i => i != null).Take(6).ToList();
            foreach (var it in items) _menuRow.Children.Add(MenuItem(it.Glyph, it.Icon, it.Label, () => Shortcuts.Run(it.Id, _volume)));
            _menuRow.Children.Add(MenuItem("", null, "Réglages", App.OpenSettings));
            Restyle();
        }

        private FrameworkElement MenuItem(string glyph, ImageSource icon, string label, Action run)
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var circle = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(20),
                Background = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            if (icon != null) circle.Child = new Image { Source = icon, Width = 22, Height = 22 };
            else
            {
                var g = Glyph(glyph, 17);
                g.HorizontalAlignment = HorizontalAlignment.Center;
                g.VerticalAlignment = VerticalAlignment.Center;
                circle.Child = g;
            }
            var t = Text(label, 10.5, FontWeights.Normal);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.Margin = new Thickness(0, 5, 0, 0);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.MaxWidth = 58;
            stack.Children.Add(circle);
            stack.Children.Add(t);
            var cell = new Border { Width = 62, Child = stack, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var scale = new ScaleTransform(1, 1, 31, 30);
            cell.RenderTransform = scale;
            cell.MouseEnter += (a, b) => circle.Background = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
            cell.MouseLeave += (a, b) => { circle.Background = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)); scale.ScaleX = scale.ScaleY = 1; };
            cell.MouseLeftButtonDown += (a, b) => { scale.ScaleX = scale.ScaleY = 0.9; b.Handled = true; };
            cell.MouseLeftButtonUp += (a, b) =>
            {
                b.Handled = true;
                scale.ScaleX = scale.ScaleY = 1;
                Collapse();
                run();
            };
            return cell;
        }

        // ================== Rendu ==================

        /// <summary>Anime tant qu'un ressort bouge (a chaque image de l'ecran).</summary>
        private void Kick()
        {
            if (_animating) return;
            _animating = true;
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object sender, EventArgs e)
        {
            bool moving = _w.Tick() | _h.Tick() | _alpha.Tick() | _tint.Tick() | _level.Tick();
            Render();
            if (!moving && !_w.Moving && !_h.Moving && !_alpha.Moving && !_tint.Moving && !_level.Moving)
            {
                CompositionTarget.Rendering -= OnFrame;
                _animating = false;
            }
        }

        private double IslandCenterX(double w)
        {
            switch (Settings.Current.Position)
            {
                case "left": return Margin + w / 2;
                case "right": return WinW - Margin - w / 2;
                default: return WinW / 2;
            }
        }

        private void Render()
        {
            var s = Settings.Current;
            double w = Math.Max(_w.Value, 10), h = Math.Max(_h.Value, 10);
            double cx = IslandCenterX(w), top = s.Top;
            double radius = Math.Min(h / 2, 30);
            double k = _scale;

            _fx.Geo = new Point4D(cx * k, (top + h / 2) * k, w / 2 * k, h / 2 * k);
            _fx.Sizes = new Point4D(WinW * k, Height * k, radius * k, Math.Min(h * 0.42, 18) * k);
            bool dark = s.Theme != "light";
            _fx.Opt = new Point4D(Math.Min(h * 0.4, 14) * k, 0.14, Math.Max(0, Math.Min(1, _tint.Value)), dark ? 1 : 0);
            var l = _fx.Light;
            _fx.Light = new Point4D(l.X, l.Y, 1, 0.95);

            if (!(s.Refraction && _excluded) && _glass.Source != FlatSource()) _glass.Source = FlatSource();

            // Contenu : meme forme que le verre
            _box.Margin = new Thickness(cx - w / 2, top, 0, 0);
            _box.Width = w;
            _box.Height = h;
            _clip.Rect = new Rect(0, 0, w, h);
            _clip.RadiusX = _clip.RadiusY = radius;
            foreach (var p in new[] { _volPanel, _notePanel, (FrameworkElement)_menuPanel })
                p.Opacity = p == _panel ? Math.Max(0, Math.Min(1, _alpha.Value)) : 0;
            if (_panel == _volPanel)
            {
                double tw = Math.Max(0, _volTrack.ActualWidth);
                _volFill.Width = Math.Max(6, tw * Math.Max(0, Math.Min(1, _level.Value)));
            }
            _box.IsHitTestVisible = _mode != Mode.Collapsed;
        }

        /// <summary>Fond uni utilise quand le verre ne peut pas voir derriere lui.</summary>
        private BitmapSource FlatSource()
        {
            if (_flat != null) return _flat;
            bool light = Settings.Current.Theme == "light";
            var c = light ? Color.FromRgb(236, 238, 242) : Color.FromRgb(28, 31, 36);
            var px = new byte[] { c.B, c.G, c.R, 255 };
            _flat = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, px, 4);
            _flat.Freeze();
            return _flat;
        }

        /// <summary>Capture le fond derriere l'ilot (si l'image a change) et oriente les reflets vers la souris.</summary>
        private void CaptureFrame()
        {
            if (_hidden || _hwnd == IntPtr.Zero) return;
            if (!Native.GetWindowRect(_hwnd, out var r)) return;

            // Reflets : la lumiere vient du cote de la souris
            if (Native.GetCursorPos(out var cur))
            {
                double w = _w.Value;
                double cx = r.Left + IslandCenterX(w) * _scale, cy = r.Top + (Settings.Current.Top + _h.Value / 2) * _scale;
                double dx = cur.X - cx, dy = cur.Y - cy, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1) { dx = -0.5; dy = -1; len = Math.Sqrt(1.25); }
                // melange avec une lumiere venant d'en haut a gauche pour rester naturel
                double lx = dx / len * 0.6 - 0.45, ly = dy / len * 0.6 - 0.8, ll = Math.Sqrt(lx * lx + ly * ly);
                var old = _fx.Light;
                double nx = old.X + (lx / ll - old.X) * 0.25, ny = old.Y + (ly / ll - old.Y) * 0.25;
                double nl = Math.Sqrt(nx * nx + ny * ny);
                if (nl > 0.0001 && (Math.Abs(nx / nl - old.X) > 0.002 || Math.Abs(ny / nl - old.Y) > 0.002))
                    _fx.Light = new Point4D(nx / nl, ny / nl, old.Z, old.W);
            }

            if (!Settings.Current.Refraction || !_excluded) return;
            try
            {
                if (_cap.Grab(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) && _glass.Source != _cap.Bitmap)
                    _glass.Source = _cap.Bitmap;
            }
            catch (Exception e) { Log.I("Capture : " + e.Message); }
        }

        /// <summary>Plein ecran (jeu, film, presentation) : l'ilot se cache. Et il reste au-dessus de tout.</summary>
        private void Watch()
        {
            bool full = false;
            if (Settings.Current.HideInFullscreen && Native.SHQueryUserNotificationState(out int st) == 0)
                full = st == Native.QUNS_BUSY || st == Native.QUNS_RUNNING_D3D_FULL_SCREEN || st == Native.QUNS_PRESENTATION_MODE;
            if (full != _hidden)
            {
                _hidden = full;
                _root.Opacity = full ? 0 : 1;
                _root.IsHitTestVisible = !full;
                if (full) Collapse();
                Log.I(full ? "Plein ecran : ilot cache" : "Fin du plein ecran : ilot visible");
            }
            if (!_hidden && _hwnd != IntPtr.Zero)
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        // ================== Outils ==================

        private static TextBlock Text(string s, double size, FontWeight weight) => new TextBlock
        {
            Text = s, FontSize = size, FontWeight = weight, FontFamily = Ui, Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };

        private static TextBlock Glyph(string g, double size) => new TextBlock
        {
            Text = g, FontSize = size, FontFamily = Icons, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center
        };

        private static Color BadgeColor(string app)
        {
            var palette = new[]
            {
                Color.FromRgb(79, 93, 240), Color.FromRgb(52, 168, 120), Color.FromRgb(232, 98, 82),
                Color.FromRgb(236, 160, 50), Color.FromRgb(150, 92, 230), Color.FromRgb(40, 150, 210)
            };
            int h = 0;
            foreach (char c in app ?? "") h = h * 31 + c;
            return palette[Math.Abs(h) % palette.Length];
        }

        private static ImageSource LoadIcon(string src)
        {
            try
            {
                if (string.IsNullOrEmpty(src)) return null;
                string path = src.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) ? new Uri(src).LocalPath : src;
                if (!Path.IsPathRooted(path) || !File.Exists(path)) return null;
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.UriSource = new Uri(path);
                b.DecodePixelWidth = 88;
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch { return null; }
        }

        // ================== Captures pour les tests automatiques ==================

        /// <summary>Enregistre une image de l'ilot sur son fond (ce que voit l'utilisateur).</summary>
        public void Snap(string name, string folder)
        {
            try
            {
                Render();
                UpdateLayout();
                Native.GetWindowRect(_hwnd, out var wr);
                int ww = wr.Right - wr.Left, wh = wr.Bottom - wr.Top;
                int pad = (int)(150 * _scale);
                int bx = wr.Left - pad, by = wr.Top, bw = ww + 2 * pad, bh = wh + (int)(30 * _scale);
                var bg = new ScreenCapture();
                bg.Grab(bx, by, bw, bh);
                var fg = new RenderTargetBitmap(ww, wh, 96 * _scale, 96 * _scale, PixelFormats.Pbgra32);
                fg.Render(_root);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawImage(bg.Bitmap, new Rect(0, 0, bw, bh));
                    dc.DrawImage(fg, new Rect(pad, 0, ww, wh));
                }
                var outBmp = new RenderTargetBitmap(bw, bh, 96, 96, PixelFormats.Pbgra32);
                outBmp.Render(dv);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(outBmp));
                Directory.CreateDirectory(folder);
                using var f = File.Create(Path.Combine(folder, name + ".png"));
                enc.Save(f);
                bg.Dispose();
                Log.I("Capture enregistree : " + name);
            }
            catch (Exception e) { Log.I("Capture impossible : " + e); }
        }

        protected override void OnClosed(EventArgs e)
        {
            _captureTimer.Stop();
            _watchTimer.Stop();
            _cap.Dispose();
            base.OnClosed(e);
        }
    }
}
