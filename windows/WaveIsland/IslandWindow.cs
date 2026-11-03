using System;
using System.Collections.Generic;
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
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace WaveIsland
{
    /// <summary>
    /// La Wave Island : une fenetre transparente toujours au-dessus, en haut de l'ecran.
    /// La fenetre garde une taille fixe ; seule la forme dessinee par le verre change (pas de saut).
    /// Les pixels transparents laissent passer la souris.
    /// </summary>
    public sealed class IslandWindow : Window
    {
        private enum Mode { Collapsed, Volume, Note, Menu, Live }

        /// <summary>Marge autour de l'ilot (pour que le verre puisse aller chercher le fond autour).</summary>
        private const double Pad = 26;
        private const double WinW = 520;
        private const double LiveW = 400;
        private const double MaxH = 320;
        /// <summary>Largeur ajoutee a l'ilot au repos quand une activite tourne (pochette a gauche, barres a droite).</summary>
        private const double CompactExtra = 96;

        private readonly VolumeService _volume;
        private readonly MediaService _media;
        private readonly Grid _root = new Grid();
        private readonly Image _glass = new Image { Stretch = Stretch.Fill, IsHitTestVisible = true, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly GlassEffect _fx = new GlassEffect();
        private readonly Grid _box = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly RectangleGeometry _clip = new RectangleGeometry();
        private readonly ScreenCapture _cap = new ScreenCapture();

        private readonly SpringValue _w, _h, _alpha = new SpringValue(0), _tint = new SpringValue(0), _level = new SpringValue(0);
        private readonly DispatcherTimer _captureTimer, _hideTimer, _watchTimer, _hoverTimer, _animTimer;
        private Mode _mode = Mode.Collapsed;
        private FrameworkElement _panel;
        private IntPtr _hwnd;
        private double _scale = 1;
        private bool _excluded, _hidden, _hover, _animating;
        private BitmapSource _flat;
        private IslandNote _note;

        // Activites en arriere-plan
        private MediaInfo _mediaInfo;
        private List<DownloadInfo> _downloads = new List<DownloadInfo>();
        private bool HasLive => _mediaInfo != null || _downloads.Count > 0;
        private int _tick;

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
        private readonly Grid _compact;
        private readonly Border _compactArt;
        private readonly Image _compactArtImg;
        private readonly TextBlock _compactGlyph;
        private readonly StackPanel _eq;
        private readonly Canvas _ring;
        private readonly Path _ringArc;
        private readonly RotateTransform _ringSpin = new RotateTransform(0, 11, 11);
        private readonly StackPanel _livePanel;

        // Elements du lecteur (mis a jour sans tout reconstruire)
        private TextBlock _mTime, _mDur, _mPlay;
        private Border _mFill, _mTrack;
        private readonly List<(DownloadInfo d, Border fill, Border track, TextBlock info, TextBlock pct)> _dlRows =
            new List<(DownloadInfo, Border, Border, TextBlock, TextBlock)>();
        private string _liveSig = "";

        private static readonly FontFamily Ui = new FontFamily("Segoe UI Variable Text, Segoe UI");
        private static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        private static readonly Random Rnd = new Random();

        public IslandWindow(VolumeService volume, MediaService media)
        {
            _volume = volume;
            _media = media;
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
            _volTrack = new Border { Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
            _volFill = new Border { Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
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
            _noteLetter.Foreground = Brushes.White;
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
            _noteApp.Opacity = 0.7;
            _noteTitle = Text("", 14.5, FontWeights.SemiBold);
            _noteTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            _noteText = Text("", 13, FontWeights.Normal);
            _noteText.Opacity = 0.9;
            _noteText.TextTrimming = TextTrimming.CharacterEllipsis;
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

            // --- Ilot au repos avec une activite (pochette a gauche, barres qui dansent ou anneau a droite) ---
            _compactArtImg = new Image { Stretch = Stretch.UniformToFill };
            _compactGlyph = Glyph("", 13);
            _compactGlyph.HorizontalAlignment = HorizontalAlignment.Center;
            var artGrid = new Grid();
            artGrid.Children.Add(_compactGlyph);
            artGrid.Children.Add(_compactArtImg);
            _compactArt = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = artGrid,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0)
            };
            _eq = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 13, 0), Height = 16 };
            for (int i = 0; i < 4; i++)
                _eq.Children.Add(new Rectangle { Width = 3, Height = 6, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(1.2, 0, 1.2, 0), VerticalAlignment = VerticalAlignment.Center });
            _ring = new Canvas { Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };
            _ring.Children.Add(new Ellipse { Width = 22, Height = 22, StrokeThickness = 3, Stroke = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) });
            _ringArc = new Path { StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransform = _ringSpin };
            _ring.Children.Add(_ringArc);
            _compact = new Grid();
            _compact.Children.Add(_compactArt);
            _compact.Children.Add(_eq);
            _compact.Children.Add(_ring);

            // --- Activites en grand (au survol) ---
            _livePanel = new StackPanel { Width = LiveW, VerticalAlignment = VerticalAlignment.Top };

            foreach (var p in new FrameworkElement[] { _volPanel, _notePanel, _menuPanel, _compact, _livePanel })
            {
                p.Opacity = 0;
                if (p != _compact) p.HorizontalAlignment = HorizontalAlignment.Center;
                if (p != _volPanel && p != _livePanel) p.VerticalAlignment = VerticalAlignment.Center;
                _box.Children.Add(p);
            }

            // --- Souris ---
            MouseEnter += (a, b) =>
            {
                _hover = true;
                _hideTimer.Stop();
                if (_mode == Mode.Collapsed)
                {
                    Resize();
                    if (HasLive) { _hoverTimer.Stop(); _hoverTimer.Start(); }
                }
            };
            MouseLeave += (a, b) =>
            {
                _hover = false;
                _hoverTimer.Stop();
                if (_mode == Mode.Collapsed) Resize();
                else RestartHide(_mode == Mode.Menu || _mode == Mode.Live ? 0.5 : 2.5);
            };
            MouseLeftButtonUp += (a, b) =>
            {
                _hoverTimer.Stop();
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
            _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _hoverTimer.Tick += (a, b) => { _hoverTimer.Stop(); if (_hover && _mode == Mode.Collapsed && HasLive) ShowLive(); };
            _animTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(90) };
            _animTimer.Tick += (a, b) => AnimateLive();

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
        }

        /// <summary>Place la fenetre en haut de l'ecran principal (a gauche, au centre ou a droite).</summary>
        private void Place()
        {
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds; // pixels physiques
            double sl = sb.Left / _scale, st = sb.Top / _scale, sw = sb.Width / _scale;
            double h = Settings.Current.Top + MaxH + Pad;
            Width = WinW;
            Height = h;
            switch (Settings.Current.Position)
            {
                case "left": Left = sl + 14 - Pad; break;
                case "right": Left = sl + sw - 14 + Pad - WinW; break;
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
            _flat = null;
            BuildMenu();
            RebuildLive(true);
            Restyle();
            if (_mode == Mode.Collapsed) SettleCollapsed();
            Resize();
            Render();
        }

        private bool LightTheme => Settings.Current.Theme == "light";
        private Brush Ink => LightTheme ? new SolidColorBrush(Color.FromRgb(20, 22, 26)) : Brushes.White;

        /// <summary>Couleurs du texte selon le theme ; en verre transparent, une ombre douce rend le texte lisible sans assombrir l'ilot.</summary>
        private void Restyle()
        {
            var ink = Ink;
            bool light = LightTheme;
            var soft = new SolidColorBrush(light ? Color.FromArgb(50, 0, 0, 0) : Color.FromArgb(80, 255, 255, 255));
            foreach (var panel in new FrameworkElement[] { _volPanel, _notePanel, _menuPanel, _compact, _livePanel })
            {
                foreach (var t in Texts(panel)) if (t != _noteLetter) t.Foreground = ink;
                panel.Effect = Settings.Current.Theme == "clear"
                    ? new DropShadowEffect { ShadowDepth = 0, BlurRadius = 7, Opacity = 0.75, Color = Colors.Black, RenderingBias = RenderingBias.Performance }
                    : null;
            }
            _volFill.Background = ink;
            _volTrack.Background = soft;
            foreach (Rectangle r in _eq.Children) r.Fill = ink;
            _ringArc.Stroke = ink;
            ((Ellipse)_ring.Children[0]).Stroke = soft;
            if (_mFill != null) { _mFill.Background = ink; _mTrack.Background = soft; }
            foreach (var row in _dlRows) { row.fill.Background = ink; row.track.Background = soft; }
        }

        private static IEnumerable<TextBlock> Texts(DependencyObject e)
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
            Restyle();
            Switch(Mode.Menu, _menuPanel);
            RestartHide(6);
        }

        /// <summary>Activites en grand : lecteur de musique et telechargements.</summary>
        public void ShowLive()
        {
            if (_hidden || !HasLive) return;
            RebuildLive(false);
            Switch(Mode.Live, _livePanel);
            if (!_hover) RestartHide(5);
        }

        public void Collapse()
        {
            _hideTimer.Stop();
            if (_mode == Mode.Collapsed) return;
            _mode = Mode.Collapsed;
            SettleCollapsed();
            Resize();
        }

        /// <summary>Au repos : ilot vide, ou ilot elargi avec l'activite en cours.</summary>
        private void SettleCollapsed()
        {
            if (HasLive && !_hidden)
            {
                UpdateCompact();
                if (_panel != _compact)
                {
                    _panel = _compact;
                    _alpha.Set(0);
                    _alpha.AnimateTo(1, Spring.Soft);
                }
                else if (_alpha.Target < 1) _alpha.AnimateTo(1, Spring.Soft);
            }
            else if (_panel == _compact || _alpha.Target > 0) _alpha.AnimateTo(0, Spring.Close);
            UpdateAnimTimer();
            Kick();
        }

        private void Switch(Mode m, FrameworkElement panel)
        {
            bool fromCollapsed = _mode == Mode.Collapsed;
            if (_panel != panel || fromCollapsed)
            {
                _panel = panel;
                _alpha.Set(fromCollapsed && panel != _compact ? 0 : 0.2);
                _alpha.AnimateTo(1, Spring.Soft);
            }
            _mode = m;
            UpdateAnimTimer();
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
                case Mode.Live:
                    w = LiveW;
                    _livePanel.Measure(new Size(LiveW, double.PositiveInfinity));
                    h = Math.Min(MaxH, Math.Max(60, _livePanel.DesiredSize.Height));
                    sw = Spring.OpenW; sh = Spring.Open;
                    break;
                default:
                    w = s.Width + (HasLive && !_hidden ? CompactExtra : 0) + (_hover ? 10 : 0);
                    h = s.Height + (_hover ? 3 : 0);
                    sw = _hover ? Spring.Pop : Spring.Close; sh = sw;
                    break;
            }
            w = Math.Min(w, WinW - 2 * Pad);
            if (Math.Abs(_w.Target - w) > 0.1) _w.AnimateTo(w, sw);
            if (Math.Abs(_h.Target - h) > 0.1) _h.AnimateTo(h, sh);
            double tint = TintFor(_mode != Mode.Collapsed);
            if (Math.Abs(_tint.Target - tint) > 0.001) _tint.AnimateTo(tint, Spring.Soft);
            Kick();
        }

        /// <summary>Voile du verre : jamais d'assombrissement en mode transparent (le texte a une ombre douce).</summary>
        private double TintFor(bool expanded)
        {
            var s = Settings.Current;
            bool see = s.Refraction && _excluded;
            switch (s.Theme)
            {
                case "dark": return see ? (expanded ? 0.86 : 0.8) : 0.93;
                case "light": return see ? (expanded ? 0.8 : 0.7) : 0.9;
                default: return see ? 0.0 : 0.55;
            }
        }

        private void RestartHide(double seconds)
        {
            _hideTimer.Stop();
            _hideTimer.Interval = TimeSpan.FromSeconds(seconds);
            _hideTimer.Start();
        }

        /// <summary>Comme un clic sur la notification affichee (tests).</summary>
        public void ClickNote() => OpenNote();

        private void OpenNote()
        {
            var n = _note;
            Collapse();
            if (n == null) return;
            if (n.OnClick != null) n.OnClick();
            else Shortcuts.OpenApp(n.Aumid, n.App);
        }

        // ================== Activites en arriere-plan ==================

        public void SetMedia(MediaInfo m)
        {
            bool had = HasLive;
            _mediaInfo = m;
            LiveChanged(had);
        }

        public void SetDownloads(IReadOnlyList<DownloadInfo> list)
        {
            bool had = HasLive;
            _downloads = list.ToList();
            LiveChanged(had);
        }

        private void LiveChanged(bool had)
        {
            RebuildLive(false);
            if (_mode == Mode.Collapsed) { SettleCollapsed(); Resize(); }
            else if (_mode == Mode.Live)
            {
                if (!HasLive) Collapse();
                else Resize();
            }
            else UpdateAnimTimer();
            if (had != HasLive) Log.I(HasLive ? "Activite en cours affichee" : "Plus d'activite en cours");
        }

        /// <summary>Pochette et barres (musique) ou anneau de progression (telechargement).</summary>
        private void UpdateCompact()
        {
            var m = _mediaInfo;
            var dl = _downloads.FirstOrDefault();
            _compactArtImg.Source = m?.Art;
            _compactGlyph.Text = m != null ? "" : "";
            _compactArt.Background = m != null && m.Art == null ? new SolidColorBrush(BadgeColor(m.App)) :
                                     m == null ? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) : Brushes.Transparent;
            _eq.Visibility = dl == null && m != null ? Visibility.Visible : Visibility.Collapsed;
            _ring.Visibility = dl != null ? Visibility.Visible : Visibility.Collapsed;
            if (dl != null) DrawRing(dl.Progress);
        }

        private void DrawRing(double progress)
        {
            double sweep = progress < 0 ? 0.28 : Math.Max(0.02, Math.Min(0.999, progress));
            double a = sweep * 2 * Math.PI, r = 9.5, c = 11;
            var start = new Point(c, c - r);
            var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
            var fig = new PathFigure { StartPoint = start, IsClosed = false };
            fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, sweep > 0.5, SweepDirection.Clockwise, true));
            _ringArc.Data = new PathGeometry(new[] { fig });
            if (progress >= 0) _ringSpin.Angle = 0;
        }

        /// <summary>Petites animations : barres de la musique, anneau, temps qui avance.</summary>
        private void UpdateAnimTimer()
        {
            bool need = !_hidden && HasLive && (_mode == Mode.Collapsed || _mode == Mode.Live);
            if (need && !_animTimer.IsEnabled) _animTimer.Start();
            else if (!need && _animTimer.IsEnabled) _animTimer.Stop();
        }

        private void AnimateLive()
        {
            _tick++;
            if (_mode == Mode.Collapsed)
            {
                if (_eq.Visibility == Visibility.Visible)
                {
                    bool playing = _mediaInfo?.Playing == true;
                    for (int i = 0; i < _eq.Children.Count; i++)
                    {
                        var r = (Rectangle)_eq.Children[i];
                        double target = playing ? 4 + 12 * Math.Abs(Math.Sin(_tick * (0.35 + i * 0.13) + i * 1.7)) * (0.6 + 0.4 * Rnd.NextDouble()) : 4;
                        r.Height = r.Height + (target - r.Height) * 0.6;
                    }
                }
                if (_ring.Visibility == Visibility.Visible && _downloads.FirstOrDefault()?.Progress < 0)
                    _ringSpin.Angle = (_ringSpin.Angle + 24) % 360;
            }
            else if (_mode == Mode.Live && _tick % 3 == 0) UpdateLiveProgress();
        }

        private static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

        private static string FormatSize(double bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024 * 1024)).ToString("0.0") + " Go";
            if (bytes >= 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0") + " Mo";
            return Math.Max(0, bytes / 1024.0).ToString("0") + " Ko";
        }

        /// <summary>Construit le panneau des activites (seulement si sa structure a change).</summary>
        private void RebuildLive(bool force)
        {
            var m = _mediaInfo;
            string sig = (m == null ? "-" : m.Title + "|" + m.Artist + "|" + m.App + "|" + (m.Art != null)) + "#" +
                         string.Join(",", _downloads.Select(d => d.Path)) + "#" + Settings.Current.Theme;
            if (sig == _liveSig && !force) { UpdateLiveProgress(); return; }
            _liveSig = sig;
            _livePanel.Children.Clear();
            _dlRows.Clear();
            _mFill = _mTrack = null;
            var top = new Thickness(16, 14, 16, 0);

            if (m != null)
            {
                var head = new Grid { Margin = top };
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.ColumnDefinitions.Add(new ColumnDefinition());
                var art = new Border { Width = 58, Height = 58, CornerRadius = new CornerRadius(14), ClipToBounds = true, Margin = new Thickness(0, 0, 12, 0) };
                if (m.Art != null) art.Child = new Image { Source = m.Art, Stretch = Stretch.UniformToFill };
                else
                {
                    art.Background = new SolidColorBrush(BadgeColor(m.App));
                    var g = Glyph("", 22);
                    g.HorizontalAlignment = HorizontalAlignment.Center;
                    g.Foreground = Brushes.White;
                    art.Child = g;
                }
                var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var title = Text(m.Title, 15, FontWeights.SemiBold);
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                var artist = Text(m.Artist, 12.5, FontWeights.Normal);
                artist.Opacity = 0.8;
                artist.TextTrimming = TextTrimming.CharacterEllipsis;
                var app = Text(m.App, 11, FontWeights.Normal);
                app.Opacity = 0.6;
                texts.Children.Add(title);
                if (!string.IsNullOrWhiteSpace(m.Artist)) texts.Children.Add(artist);
                texts.Children.Add(app);
                Grid.SetColumn(texts, 1);
                head.Children.Add(art);
                head.Children.Add(texts);
                _livePanel.Children.Add(head);

                // Progression (clic sur la barre = aller a cet endroit)
                var prog = new Grid { Margin = new Thickness(16, 10, 16, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
                prog.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
                prog.ColumnDefinitions.Add(new ColumnDefinition());
                prog.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
                _mTime = Text("0:00", 11, FontWeights.Normal);
                _mDur = Text("", 11, FontWeights.Normal);
                _mDur.TextAlignment = TextAlignment.Right;
                _mTrack = new Border { Height = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
                _mFill = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
                var barArea = new Grid { Height = 18, Background = Brushes.Transparent };
                barArea.Children.Add(_mTrack);
                barArea.Children.Add(_mFill);
                barArea.MouseLeftButtonUp += (a, b) =>
                {
                    b.Handled = true;
                    var mi = _mediaInfo;
                    if (mi == null || mi.Duration <= TimeSpan.Zero) return;
                    double f = Math.Max(0, Math.Min(1, (b.GetPosition(_mTrack).X) / Math.Max(1, _mTrack.ActualWidth)));
                    _media.Seek(TimeSpan.FromTicks((long)(mi.Duration.Ticks * f)));
                };
                barArea.MouseLeftButtonDown += (a, b) => b.Handled = true;
                Grid.SetColumn(barArea, 1);
                Grid.SetColumn(_mDur, 2);
                prog.Children.Add(_mTime);
                prog.Children.Add(barArea);
                prog.Children.Add(_mDur);
                prog.Visibility = m.Duration > TimeSpan.FromSeconds(1) ? Visibility.Visible : Visibility.Collapsed;
                _livePanel.Children.Add(prog);

                // Commandes
                var ctl = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
                ctl.Children.Add(MediaButton("", 18, () => _media.Previous(), m.CanPrev));
                var play = MediaButton(m.Playing ? "" : "", 24, () => { _media.PlayPause(); }, true);
                _mPlay = (TextBlock)((Border)play).Child;
                ctl.Children.Add(play);
                ctl.Children.Add(MediaButton("", 18, () => _media.Next(), m.CanNext));
                _livePanel.Children.Add(ctl);
            }

            int shown = 0;
            foreach (var d in _downloads)
            {
                if (shown++ == 3) break;
                var row = new Grid { Margin = new Thickness(16, m != null && shown == 1 ? 4 : 14, 16, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var icon = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Margin = new Thickness(0, 0, 10, 0), Background = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)) };
                var ig = Glyph("", 15);
                ig.HorizontalAlignment = HorizontalAlignment.Center;
                icon.Child = ig;
                var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = Text(d.Name, 13.5, FontWeights.SemiBold);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                var info = Text("", 11.5, FontWeights.Normal);
                info.Opacity = 0.8;
                texts.Children.Add(name);
                texts.Children.Add(info);
                var pct = Text("", 13, FontWeights.SemiBold);
                pct.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(texts, 1);
                Grid.SetColumn(pct, 2);
                row.Children.Add(icon);
                row.Children.Add(texts);
                row.Children.Add(pct);
                var bar = new Grid { Margin = new Thickness(60, 6, 16, 0), Height = 4 };
                var track = new Border { CornerRadius = new CornerRadius(2) };
                var fill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
                bar.Children.Add(track);
                bar.Children.Add(fill);
                _livePanel.Children.Add(row);
                _livePanel.Children.Add(bar);
                _dlRows.Add((d, fill, track, info, pct));
            }
            _livePanel.Children.Add(new Border { Height = 14 });
            Restyle();
            UpdateLiveProgress();
            UpdateCompact();
        }

        private FrameworkElement MediaButton(string glyph, double size, Action act, bool enabled)
        {
            var g = Glyph(glyph, size);
            g.HorizontalAlignment = HorizontalAlignment.Center;
            var b = new Border
            {
                Width = 56, Height = 42, CornerRadius = new CornerRadius(21), Child = g, Background = Brushes.Transparent,
                Cursor = enabled ? Cursors.Hand : Cursors.Arrow, Opacity = enabled ? 1 : 0.35
            };
            var hover = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            b.MouseEnter += (x, y) => { if (enabled) b.Background = hover; };
            b.MouseLeave += (x, y) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonDown += (x, y) => y.Handled = true;
            b.MouseLeftButtonUp += (x, y) =>
            {
                y.Handled = true;
                if (!enabled) return;
                if (glyph == "" || glyph == "")
                {
                    // retour immediat, sans attendre Windows
                    if (_mediaInfo != null) _mediaInfo.Playing = !_mediaInfo.Playing;
                    if (_mPlay != null) _mPlay.Text = _mediaInfo?.Playing == true ? "" : "";
                }
                act();
            };
            return b;
        }

        private void UpdateLiveProgress()
        {
            var m = _mediaInfo;
            if (m != null && _mFill != null)
            {
                var now = m.Now;
                _mTime.Text = Clock(now);
                _mDur.Text = m.Duration > TimeSpan.Zero ? "-" + Clock(m.Duration - now) : "";
                double f = m.Duration > TimeSpan.Zero ? now.TotalSeconds / m.Duration.TotalSeconds : 0;
                _mFill.Width = Math.Max(4, Math.Max(0, _mTrack.ActualWidth) * Math.Max(0, Math.Min(1, f)));
                if (_mPlay != null) _mPlay.Text = m.Playing ? "" : "";
            }
            foreach (var (d, fill, track, info, pct) in _dlRows)
            {
                double p = d.Progress;
                string speed = d.Stalled ? "en pause" : FormatSize(d.Speed) + "/s";
                info.Text = (d.Total > 0 ? FormatSize(d.Bytes) + " sur " + FormatSize(d.Total) : FormatSize(d.Bytes)) + " • " + speed;
                pct.Text = p >= 0 ? Math.Round(p * 100) + " %" : "";
                double tw = Math.Max(0, track.ActualWidth);
                if (p >= 0) { fill.Margin = new Thickness(0); fill.Width = Math.Max(4, tw * p); }
                else
                {
                    // Taille totale inconnue : un segment qui glisse
                    double seg = tw * 0.3, x = (_tick * 9) % (tw + seg) - seg;
                    fill.Width = Math.Max(4, Math.Min(seg, Math.Min(tw - Math.Max(0, x), seg + Math.Min(0, x))));
                    fill.Margin = new Thickness(Math.Max(0, x), 0, 0, 0);
                }
            }
        }

        // ================== Menu ==================

        private void BuildMenu()
        {
            _menuRow.Children.Clear();
            var items = Settings.Current.Shortcuts.Select(Shortcuts.Resolve).Where(i => i != null).Take(6).ToList();
            foreach (var it in items) _menuRow.Children.Add(MenuItem(it.Glyph, it.Icon, it.Label, () => Shortcuts.Run(it.Id, _volume)));
            _menuRow.Children.Add(MenuItem("", null, "Réglages", App.OpenSettings));
        }

        private FrameworkElement MenuItem(string glyph, ImageSource icon, string label, Action run)
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var normal = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            var hover = new SolidColorBrush(Color.FromArgb(85, 255, 255, 255));
            var circle = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = normal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            if (icon != null) circle.Child = new Image { Source = icon, Width = 22, Height = 22 };
            else
            {
                var g = Glyph(glyph, 17);
                g.HorizontalAlignment = HorizontalAlignment.Center;
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
            cell.MouseEnter += (a, b) => circle.Background = hover;
            cell.MouseLeave += (a, b) => { circle.Background = normal; scale.ScaleX = scale.ScaleY = 1; };
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
            _w.Tick(); _h.Tick(); _alpha.Tick(); _tint.Tick(); _level.Tick();
            Render();
            if (!_w.Moving && !_h.Moving && !_alpha.Moving && !_tint.Moving && !_level.Moving)
            {
                CompositionTarget.Rendering -= OnFrame;
                _animating = false;
            }
        }

        private double IslandCenterX(double w)
        {
            switch (Settings.Current.Position)
            {
                case "left": return Pad + w / 2;
                case "right": return WinW - Pad - w / 2;
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
            if (double.IsNaN(Height)) return;

            double aw = WinW * k, ah = Height * k;
            double mag = 0.95, strength = Math.Min(h * 0.4, 14) * k;
            _fx.Geo = new Point4D(cx * k, (top + h / 2) * k, (w / 2 - radius) * k, (h / 2 - radius) * k);
            _fx.Sizes = new Point4D(aw, ah, radius * k, 1.0 / (Math.Min(h * 0.42, 18) * k));
            // Loupe autour du centre de l'ilot : uv' = uv * mag + centre * (1 - mag)
            _fx.Bend = new Point4D(strength / aw, strength / ah, cx * k / aw * (1 - mag), (top + h / 2) * k / ah * (1 - mag));
            double tint = Math.Max(0, Math.Min(1, _tint.Value));
            _fx.Tint = !LightTheme ? new Point4D(0.05, 0.06, 0.08, tint) : new Point4D(0.97, 0.98, 1.0, tint);
            _fx.Disp = new Point4D(1.14, 0.86, 0, 0);
            var l = _fx.Light;
            _fx.Light = new Point4D(l.X, l.Y, 1, mag);

            if (!(s.Refraction && _excluded) && _glass.Source != FlatSource()) _glass.Source = FlatSource();

            // Contenu : meme forme que le verre
            _box.Margin = new Thickness(cx - w / 2, top, 0, 0);
            _box.Width = w;
            _box.Height = h;
            _clip.Rect = new Rect(0, 0, w, h);
            _clip.RadiusX = _clip.RadiusY = radius;
            foreach (var p in new[] { _volPanel, _notePanel, (FrameworkElement)_menuPanel, _compact, _livePanel })
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
            var c = LightTheme ? Color.FromRgb(236, 238, 242) : Color.FromRgb(28, 31, 36);
            var px = new byte[] { c.B, c.G, c.R, 255 };
            _flat = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, px, 4);
            _flat.Freeze();
            return _flat;
        }

        /// <summary>Capture le fond derriere l'ilot (seulement autour de lui, et si l'image a change) et oriente les reflets vers la souris.</summary>
        private void CaptureFrame()
        {
            if (_hidden || _hwnd == IntPtr.Zero) return;
            if (!Native.GetWindowRect(_hwnd, out var r)) return;
            double k = _scale;
            double w = Math.Max(_w.Value, _w.Target), h = Math.Max(_h.Value, _h.Target);
            double cx = IslandCenterX(_w.Value);

            // Reflets : la lumiere vient du cote de la souris
            if (Native.GetCursorPos(out var cur))
            {
                double icx = r.Left + cx * k, icy = r.Top + (Settings.Current.Top + _h.Value / 2) * k;
                double dx = cur.X - icx, dy = cur.Y - icy, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1) { dx = -0.5; dy = -1; len = Math.Sqrt(1.25); }
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
                int ww = r.Right - r.Left, wh = r.Bottom - r.Top;
                // Seulement la zone utile : l'ilot (a sa plus grande taille pendant l'animation) + la marge du verre
                double bw = Math.Max(w, _w.Value) + 2 * Pad, bh = h + Settings.Current.Top + Pad;
                int sx = (int)Math.Floor((cx - bw / 2) * k) & ~15, sw = (int)Math.Ceiling(bw * k / 16 + 2) * 16;
                var sub = new Int32Rect(Math.Max(0, sx), 0, Math.Min(ww - Math.Max(0, sx), sw), Math.Min(wh, (int)Math.Ceiling(bh * k / 16 + 1) * 16));
                if (_cap.Grab(r.Left, r.Top, ww, wh, sub) && _glass.Source != _cap.Bitmap)
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
                else SettleCollapsed();
                UpdateAnimTimer();
                Log.I(full ? "Plein ecran : ilot cache" : "Fin du plein ecran : ilot visible");
            }
            if (!_hidden && _hwnd != IntPtr.Zero)
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        // ================== Outils ==================

        private static TextBlock Text(string s, double size, FontWeight weight) => new TextBlock
        {
            Text = s ?? "", FontSize = size, FontWeight = weight, FontFamily = Ui, Foreground = Brushes.White,
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
            return palette[Math.Abs(h % palette.Length)];
        }

        private static ImageSource LoadIcon(string src)
        {
            try
            {
                if (string.IsNullOrEmpty(src)) return null;
                string path = src.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) ? new Uri(src).LocalPath : src;
                if (!System.IO.Path.IsPathRooted(path) || !File.Exists(path)) return null;
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
                int pad = (int)(120 * _scale);
                int bx = wr.Left - pad, by = wr.Top, bw = ww + 2 * pad, bh = wh;
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
                using var f = File.Create(System.IO.Path.Combine(folder, name + ".png"));
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
            _animTimer.Stop();
            _cap.Dispose();
            base.OnClosed(e);
        }
    }
}
