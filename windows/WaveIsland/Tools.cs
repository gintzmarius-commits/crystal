using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WaveIsland
{
    // ======================================================================================
    // Lot 2 : recherche universelle (facon Spotlight) — Alt + Espace
    // ======================================================================================

    /// <summary>
    /// Une barre de recherche en verre au milieu de l'ecran : applis, calculs, fichiers recents,
    /// pages des Parametres, recherche web, question a l'assistant. Fleches + Entree, Echap pour fermer.
    /// </summary>
    public sealed class SearchWindow : Window
    {
        public static SearchWindow Current { get; private set; }

        private readonly TextBox _box;
        private readonly StackPanel _list = new StackPanel();
        private readonly List<(string glyph, ImageSource icon, string title, string sub, Action act)> _items = new List<(string, ImageSource, string, string, Action)>();
        private int _sel;
        private readonly ScaleTransform _scale = new ScaleTransform(0.92, 0.92);
        private static readonly FontFamily Ui = new FontFamily("Segoe UI Variable Display, Segoe UI");
        private static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        public static void Toggle()
        {
            if (Current != null) { Current.Fade(); return; }
            Current = new SearchWindow();
            Current.Show();
            Current.Activate();
            Current._box.Focus();
        }

        private SearchWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Width = 640;
            SizeToContent = SizeToContent.Height;
            FontFamily = Ui;

            var root = new Grid();
            var glass = new GlassBackdrop(this, 26, 0.12);
            var holder = new Canvas { IsHitTestVisible = false };
            holder.Children.Add(glass.Image);
            root.Children.Add(holder);
            root.SizeChanged += (a, b) => { glass.Image.Width = root.ActualWidth; glass.Image.Height = root.ActualHeight; };
            root.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(26), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), Background = new SolidColorBrush(Color.FromArgb(70, 20, 22, 30))
            });
            var col = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
            var top = new DockPanel();
            var glyph = new TextBlock { Text = "", FontFamily = Icons, FontSize = 20, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) };
            DockPanel.SetDock(glyph, Dock.Left);
            top.Children.Add(glyph);
            _box = new TextBox
            {
                FontSize = 22, Foreground = Brushes.White, CaretBrush = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 6, 0, 6)
            };
            top.Children.Add(_box);
            col.Children.Add(top);
            col.Children.Add(_list);
            root.Children.Add(col);
            root.RenderTransformOrigin = new Point(0.5, 0.2);
            root.RenderTransform = _scale;
            Content = root;

            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            double k = App.Island != null ? VisualTreeHelper.GetDpi(App.Island).DpiScaleX : 1;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = sb.Left / k + (sb.Width / k - Width) / 2;
            Top = sb.Top / k + sb.Height / k * 0.22;

            _box.TextChanged += (a, b) => Search(_box.Text);
            PreviewKeyDown += (a, e) =>
            {
                if (e.Key == Key.Escape) { Fade(); e.Handled = true; }
                else if (e.Key == Key.Down) { Select(_sel + 1); e.Handled = true; }
                else if (e.Key == Key.Up) { Select(_sel - 1); e.Handled = true; }
                else if (e.Key == Key.Enter) { Run(_sel); e.Handled = true; }
            };
            Deactivated += (a, b) => Fade();
            Loaded += (a, b) =>
            {
                WaveSound.Play(WaveSound.Kind.Open);
                var spring = new ElasticEase { Oscillations = 1, Springiness = 6 };
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(380)) { EasingFunction = spring });
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(380)) { EasingFunction = spring });
            };
            Search("");
        }

        private bool _fading;
        private void Fade()
        {
            if (_fading) return;
            _fading = true;
            if (Current == this) Current = null;
            var t = TimeSpan.FromMilliseconds(140);
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, t));
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, t));
            var a = new DoubleAnimation(0, t);
            a.Completed += (s, e) => { try { Close(); } catch { } };
            BeginAnimation(OpacityProperty, a);
        }

        private static string Fold(string s) =>
            new string((s ?? "").Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant().Trim();

        private static readonly (string name, string uri)[] SettingsPages =
        {
            ("Wi-Fi", "ms-settings:network-wifi"), ("Bluetooth", "ms-settings:bluetooth"), ("Affichage", "ms-settings:display"),
            ("Son", "ms-settings:sound"), ("Batterie", "ms-settings:batterysaver"), ("Fond d'écran", "ms-settings:personalization-background"),
            ("Mises à jour", "ms-settings:windowsupdate"), ("Applications", "ms-settings:appsfeatures"), ("Imprimantes", "ms-settings:printers"),
            ("Souris", "ms-settings:mousetouchpad"), ("Clavier", "ms-settings:typing"), ("Comptes", "ms-settings:yourinfo"),
            ("Confidentialité", "ms-settings:privacy"), ("Stockage", "ms-settings:storagesense"), ("Notifications", "ms-settings:notifications"),
        };

        private void Search(string q)
        {
            _items.Clear();
            string f = Fold(q);
            if (f.Length > 0)
            {
                // Calcul (« 12*8 », « (3+4)/2 »...)
                var calc = Calc(q);
                if (calc != null) _items.Add(("", null, "= " + calc, "Calcul (Entrée pour copier)", () => Clipboard.SetText(calc)));
                // Applis
                foreach (var a in AppCatalog.Apps.Select(a => (a, n: Fold(a.Name)))
                             .Where(x => x.n.Contains(f) || Initials(x.n).StartsWith(f))
                             .OrderBy(x => x.n.StartsWith(f) ? 0 : 1).ThenBy(x => x.a.Name.Length).Take(6))
                {
                    var app = a.a;
                    _items.Add((null, app.Icon, app.Name, "Application", () => AppCatalog.Launch(app)));
                }
                // Pages des Parametres
                foreach (var p in SettingsPages.Where(p => Fold(p.name).Contains(f)).Take(2))
                {
                    var uri = p.uri;
                    _items.Add(("", null, p.name, "Paramètres de Windows", () => Shortcuts.Start(uri)));
                }
                // Fichiers recents
                foreach (var file in RecentFiles(f).Take(4))
                {
                    var path = file;
                    _items.Add(("", null, System.IO.Path.GetFileNameWithoutExtension(path), "Fichier récent", () => Shortcuts.Start(path)));
                }
                // Web et assistant
                string query = q.Trim();
                _items.Add(("", null, "Rechercher « " + query + " » sur le web", "Navigateur", () => Shortcuts.Start("https://www.google.com/search?q=" + Uri.EscapeDataString(query))));
                if (!string.IsNullOrWhiteSpace(Settings.Current.AiKey))
                    _items.Add(("", null, "Demander à l'assistant", "Gemini répond dans l'îlot", () => Assistant.AskText(query)));
            }
            else
            {
                foreach (var app in AppCatalog.Pinned().Take(6))
                {
                    var a = app;
                    _items.Add((null, a.Icon, a.Name, "Application épinglée", () => AppCatalog.Launch(a)));
                }
            }
            Build();
            Select(0);
        }

        private static string Initials(string s) => new string(s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]).ToArray());

        private static string Calc(string q)
        {
            string e = q.Replace("×", "*").Replace("x", "*").Replace("÷", "/").Replace(",", ".").Replace(" ", "");
            if (e.Length < 3 || !e.Any(char.IsDigit) || !e.Any(c => "+-*/%".Contains(c))) return null;
            if (e.Any(c => !"0123456789.+-*/%()".Contains(c))) return null;
            try
            {
                var r = new DataTable().Compute(e, null);
                double d = Convert.ToDouble(r, CultureInfo.InvariantCulture);
                if (double.IsInfinity(d) || double.IsNaN(d)) return null;
                return d.ToString("0.##########", CultureInfo.GetCultureInfo("fr-FR"));
            }
            catch { return null; }
        }

        private static IEnumerable<string> RecentFiles(string f)
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            IEnumerable<FileInfo> links;
            try { links = new DirectoryInfo(dir).GetFiles("*.lnk").OrderByDescending(x => x.LastWriteTime).Take(150).ToList(); }
            catch { yield break; }
            foreach (var l in links)
            {
                string name = Fold(System.IO.Path.GetFileNameWithoutExtension(l.Name));
                if (!name.Contains(f)) continue;
                string target = null;
                try { target = TaskbarWindow.LinkTarget(l.FullName); } catch { }
                if (!string.IsNullOrEmpty(target) && File.Exists(target)) yield return target;
            }
        }

        private void Build()
        {
            _list.Children.Clear();
            if (_items.Count > 0) _list.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 10, 0, 6), Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) });
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                int idx = i;
                var row = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 7, 10, 7), Background = Brushes.Transparent, Cursor = Cursors.Hand };
                var dp = new DockPanel();
                FrameworkElement ic = it.icon != null
                    ? new Image { Source = it.icon, Width = 26, Height = 26 }
                    : new TextBlock { Text = it.glyph ?? "", FontFamily = Icons, FontSize = 18, Foreground = Brushes.White, Width = 26, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                ic.Margin = new Thickness(0, 0, 12, 0);
                DockPanel.SetDock(ic, Dock.Left);
                dp.Children.Add(ic);
                var sub = new TextBlock { Text = it.sub, FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(sub, Dock.Right);
                dp.Children.Add(sub);
                dp.Children.Add(new TextBlock { Text = it.title, FontSize = 15, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                row.Child = dp;
                row.MouseEnter += (a, b) => Select(idx);
                row.MouseLeftButtonUp += (a, b) => Run(idx);
                _list.Children.Add(row);
            }
        }

        private void Select(int i)
        {
            if (_items.Count == 0) return;
            _sel = (i + _items.Count) % _items.Count;
            int off = _list.Children.Count - _items.Count;
            for (int j = 0; j < _items.Count; j++)
                ((Border)_list.Children[j + off]).Background = j == _sel ? new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)) : Brushes.Transparent;
        }

        private void Run(int i)
        {
            if (i < 0 || i >= _items.Count) return;
            var act = _items[i].act;
            Fade();
            try { act(); } catch (Exception e) { Log.I("Recherche : " + e.Message); }
        }
    }

    // ======================================================================================
    // Lot 2 : capture d'ecran Wave — Impr. ecran
    // ======================================================================================

    /// <summary>
    /// Capture tout l'ecran : petit flash, la capture tombe en goutte dans l'ilot. Elle est deja copiee
    /// et enregistree (Images > Captures Wave). Toucher l'ilot : l'annoter (Paint) ; menu de l'ilot : l'envoyer au telephone.
    /// </summary>
    public static class WaveShot
    {
        public static string Last { get; private set; }
        public static string Folder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Captures Wave");

        public static void Take()
        {
            try
            {
                var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                using var bmp = new System.Drawing.Bitmap(sb.Width, sb.Height);
                using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(sb.Left, sb.Top, 0, 0, bmp.Size);
                Directory.CreateDirectory(Folder);
                string path = System.IO.Path.Combine(Folder, "Capture " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".png");
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                Last = path;
                // Copiee tout de suite
                var src = Imaging.CreateBitmapSourceFromHBitmap(bmp.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                try { Clipboard.SetImage(src); } catch { }
                Flash(sb);
                Log.I("Capture Wave : " + path);
                WaveSound.Play(WaveSound.Kind.Land);
                App.Island?.ShowNote(new IslandNote
                {
                    App = "Capture", Title = "Capture copiée", Text = "Touche pour l'annoter · glisse-la sur l'îlot pour l'envoyer", Icon = path,
                    OnClick = () => { try { Process.Start(new ProcessStartInfo("mspaint.exe", "\"" + path + "\"") { UseShellExecute = true }); } catch { Shortcuts.Start(path); } }
                });
            }
            catch (Exception e) { Log.I("Capture Wave impossible : " + e.Message); }
        }

        /// <summary>Petit flash blanc sur tout l'ecran.</summary>
        private static void Flash(System.Drawing.Rectangle sb)
        {
            double k = App.Island != null ? VisualTreeHelper.GetDpi(App.Island).DpiScaleX : 1;
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.White, Topmost = true, ShowInTaskbar = false,
                ShowActivated = false, Left = sb.Left / k, Top = sb.Top / k, Width = sb.Width / k, Height = sb.Height / k, Opacity = 0.55, IsHitTestVisible = false
            };
            w.Show();
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
            a.Completed += (s, e) => w.Close();
            w.BeginAnimation(UIElement.OpacityProperty, a);
        }
    }

    // ======================================================================================
    // Lot 2 : ecran de veille goutte
    // ======================================================================================

    /// <summary>
    /// PC inactif quelques minutes (et pas de video / jeu / presentation) : grande horloge en verre liquide,
    /// gouttes qui tombent et ondes. Un geste, une touche ou la souris : il disparait.
    /// </summary>
    public sealed class DropSaver : Window
    {
        private static DropSaver _cur;
        private static DispatcherTimer _watch;

        [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize, dwTime; }
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO i);
        [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

        private static double IdleSeconds()
        {
            var i = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            return GetLastInputInfo(ref i) ? (Environment.TickCount - (int)i.dwTime) / 1000.0 : 0;
        }

        public static void Start()
        {
            _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _watch.Tick += (a, b) =>
            {
                int min = Settings.Current.SaverMinutes;
                if (min <= 0 || _cur != null) return;
                if (IdleSeconds() < min * 60) return;
                // Pas pendant une video, un jeu, une presentation, ou de la musique
                if (SHQueryUserNotificationState(out int st) == 0 && st != 5 /* QUNS_ACCEPTS_NOTIFICATIONS */ && st != 1) return;
                if (App.Media?.Current?.Playing == true) return;
                Show2();
            };
            _watch.Start();
        }

        public static void Show2()
        {
            if (_cur != null) return;
            Log.I("Ecran de veille goutte");
            _cur = new DropSaver();
            _cur.Show();
        }

        private readonly Canvas _c = new Canvas();
        private readonly TextBlock _time, _date;
        private readonly Random _rnd = new Random();
        private readonly DispatcherTimer _t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        private readonly List<(Ellipse e, double x, double y, double v, double r)> _drops = new List<(Ellipse, double, double, double, double)>();
        private readonly List<(Ellipse e, double age, double x, double y)> _waves = new List<(Ellipse, double, double, double)>();
        private double _w, _h, _spawn;
        private readonly DateTime _start = DateTime.Now;
        private Point _mouse0 = new Point(double.NaN, 0);

        private DropSaver()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Black;
            Cursor = Cursors.None;
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            double k = App.Island != null ? VisualTreeHelper.GetDpi(App.Island).DpiScaleX : 1;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = sb.Left / k; Top = sb.Top / k; Width = _w = sb.Width / k; Height = _h = sb.Height / k;
            _c.Background = new LinearGradientBrush(Color.FromRgb(8, 14, 32), Color.FromRgb(20, 40, 72), 90);
            Content = _c;
            // L'horloge en verre : texte clair, presque transparent, avec un reflet et une ombre douce
            var glassText = new LinearGradientBrush(Color.FromArgb(235, 255, 255, 255), Color.FromArgb(120, 190, 225, 255), 90);
            _time = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.Light, FontSize = Math.Min(_w, _h) * 0.26,
                Foreground = glassText, Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 0, Color = Color.FromRgb(120, 190, 255), Opacity = 0.55 }
            };
            _date = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontSize = Math.Min(_w, _h) * 0.04,
                Foreground = new SolidColorBrush(Color.FromArgb(200, 220, 235, 255))
            };
            _c.Children.Add(_time);
            _c.Children.Add(_date);
            Opacity = 0;
            Loaded += (a, b) => BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(900)));
            _t.Tick += (a, b) => Frame();
            _t.Start();
            KeyDown += (a, b) => Leave();
            MouseDown += (a, b) => Leave();
            TouchDown += (a, b) => Leave();
            MouseMove += (a, e) =>
            {
                var p = e.GetPosition(this);
                if (double.IsNaN(_mouse0.X)) { _mouse0 = p; return; }
                if ((p - _mouse0).Length > 12 && (DateTime.Now - _start).TotalSeconds > 1) Leave();
            };
        }

        private bool _leaving;
        private void Leave()
        {
            if (_leaving) return;
            _leaving = true;
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
            a.Completed += (s, e) => { _t.Stop(); Close(); _cur = null; };
            BeginAnimation(OpacityProperty, a);
        }

        private void Frame()
        {
            var now = DateTime.Now;
            _time.Text = now.ToString("HH:mm");
            _date.Text = now.ToString("dddd d MMMM", new CultureInfo("fr-FR"));
            _time.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _date.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // L'horloge flotte doucement
            double bob = Math.Sin((now - _start).TotalSeconds * 0.6) * 6;
            Canvas.SetLeft(_time, (_w - _time.DesiredSize.Width) / 2); Canvas.SetTop(_time, _h * 0.3 + bob);
            Canvas.SetLeft(_date, (_w - _date.DesiredSize.Width) / 2); Canvas.SetTop(_date, _h * 0.3 + _time.DesiredSize.Height + bob);
            double floor = _h * 0.86;
            // Nouvelles gouttes
            _spawn -= 0.016;
            if (_spawn <= 0)
            {
                _spawn = 0.25 + _rnd.NextDouble() * 0.55;
                double r = 3 + _rnd.NextDouble() * 4;
                var e = new Ellipse
                {
                    Width = r * 2, Height = r * 2.6,
                    Fill = new RadialGradientBrush(Color.FromArgb(230, 255, 255, 255), Color.FromArgb(60, 140, 200, 255)) { GradientOrigin = new Point(0.35, 0.3) }
                };
                _c.Children.Add(e);
                _drops.Add((e, _rnd.NextDouble() * _w, -20, 0, r));
            }
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                var d = _drops[i];
                d.v += 0.35; d.y += d.v;
                if (d.y >= floor)
                {
                    _c.Children.Remove(d.e);
                    _drops.RemoveAt(i);
                    var w = new Ellipse { Stroke = new SolidColorBrush(Color.FromArgb(180, 170, 215, 255)), StrokeThickness = 1.5 };
                    _c.Children.Add(w);
                    _waves.Add((w, 0, d.x, floor));
                    continue;
                }
                Canvas.SetLeft(d.e, d.x - d.r); Canvas.SetTop(d.e, d.y);
                _drops[i] = d;
            }
            // Ondes a l'impact
            for (int i = _waves.Count - 1; i >= 0; i--)
            {
                var w = _waves[i];
                w.age += 0.016;
                double rad = 6 + w.age * 90;
                w.e.Width = rad * 2; w.e.Height = rad * 0.5;
                Canvas.SetLeft(w.e, w.x - rad); Canvas.SetTop(w.e, w.y - rad * 0.25);
                w.e.Opacity = Math.Max(0, 1 - w.age / 1.2);
                if (w.age > 1.2) { _c.Children.Remove(w.e); _waves.RemoveAt(i); continue; }
                _waves[i] = w;
            }
        }
    }
}
