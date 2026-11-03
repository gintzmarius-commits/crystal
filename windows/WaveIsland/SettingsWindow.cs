using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace WaveIsland
{
    /// <summary>Reglages, dans le meme style que l'appli Android (sombre, verre, accent menthe).</summary>
    public sealed class SettingsWindow : Window
    {
        private static readonly Color Accent = Color.FromRgb(176, 240, 210);
        private static readonly Brush AccentBrush = new SolidColorBrush(Accent);
        private static readonly Brush Ink = Brushes.White;
        private static readonly Brush InkSoft = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255));
        private static readonly Brush Card = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));
        private static readonly Brush CardLine = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));
        private static readonly FontFamily Ui = new FontFamily("Segoe UI Variable Text, Segoe UI");
        private static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        private readonly StackPanel _col = new StackPanel { Margin = new Thickness(22, 0, 22, 22) };
        private StackPanel _shortcutList;
        private WrapPanel _shortcutAdd;

        private Settings S => Settings.Current;

        public SettingsWindow()
        {
            Title = "Wave Island — Réglages";
            Width = 460;
            Height = 720;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Ui;
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/WaveIsland;component/wave.ico")); } catch { }

            var frame = new Border
            {
                CornerRadius = new CornerRadius(24),
                Background = new LinearGradientBrush(Color.FromRgb(22, 27, 30), Color.FromRgb(12, 14, 16), 90),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.5 }
            };
            var dock = new DockPanel();
            dock.Children.Add(Header());
            DockPanel.SetDock(dock.Children[0], Dock.Top);
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _col };
            dock.Children.Add(scroll);
            frame.Child = dock;
            Content = frame;

            Build();
            KeyDown += (a, b) => { if (b.Key == Key.Escape) Close(); };
        }

        // ================== Structure ==================

        private FrameworkElement Header()
        {
            var g = new Grid { Margin = new Thickness(22, 18, 14, 10), Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var logo = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0) };
            try { logo.Source = new BitmapImage(new Uri("pack://application:,,,/WaveIsland;component/wave.png")); } catch { }
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(T("Wave Island", 21, FontWeights.SemiBold, Ink));
            titles.Children.Add(T("Version " + Updater.CurrentVersion + " pour Windows", 12, FontWeights.Normal, InkSoft));
            var close = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Card, Cursor = Cursors.Hand,
                Child = Center(G("", 12))
            };
            close.MouseLeftButtonUp += (a, b) => Close();
            Grid.SetColumn(titles, 1);
            Grid.SetColumn(close, 2);
            g.Children.Add(logo);
            g.Children.Add(titles);
            g.Children.Add(close);
            g.MouseLeftButtonDown += (a, b) => { if (b.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
            return g;
        }

        private void Build()
        {
            // --- Position ---
            Section("Position de l'îlot");
            var pos = CardPanel();
            pos.Children.Add(Segmented(new[] { "Gauche", "Centre", "Droite" }, new[] { "left", "center", "right" },
                () => S.Position, v => { S.Position = v; S.Save(); }));
            _col.Children.Add(pos.Parent as FrameworkElement);

            // --- Taille ---
            Section("Taille");
            var size = CardPanel();
            size.Children.Add(Slider("Largeur", 120, 400, () => S.Width, v => S.Width = v));
            size.Children.Add(Slider("Hauteur", 24, 48, () => S.Height, v => S.Height = v));
            size.Children.Add(Slider("Distance du haut de l'écran", 0, 40, () => S.Top, v => S.Top = v));
            _col.Children.Add(size.Parent as FrameworkElement);

            // --- Apparence ---
            Section("Apparence");
            var look = CardPanel();
            look.Children.Add(Segmented(new[] { "Transparent", "Sombre", "Clair" }, new[] { "clear", "dark", "light" },
                () => S.Theme, v => { S.Theme = v; S.Save(); }));
            look.Children.Add(Toggle("Effet goutte d'eau", "Le fond est vu à travers le verre, déformé sur les bords",
                () => S.Refraction, v => { S.Refraction = v; S.Save(); }));
            _col.Children.Add(look.Parent as FrameworkElement);

            // --- Fonctions ---
            Section("Fonctions");
            var fx = CardPanel();
            fx.Children.Add(Toggle("Volume dans l'îlot", "Remplace l'indicateur de Windows en bas de l'écran",
                () => S.VolumeInIsland, v => { S.VolumeInIsland = v; S.Save(); }));
            fx.Children.Add(Toggle("Notifications dans l'îlot", "Clique dessus pour ouvrir l'appli",
                () => S.Notifications, v => { S.Notifications = v; S.Save(); }));
            fx.Children.Add(Toggle("Cacher en plein écran", "Jeux, films, présentations",
                () => S.HideInFullscreen, v => { S.HideInFullscreen = v; S.Save(); }));
            fx.Children.Add(Toggle("Lancer avec Windows", "La Wave Island démarre toute seule",
                () => S.StartWithWindows, v => { S.StartWithWindows = v; S.Save(); }));
            var tip = T("Pour ne plus voir les bannières de Windows en bas à droite, active « Ne pas déranger » : " +
                        "les notifications arrivent quand même dans l'îlot.", 12, FontWeights.Normal, InkSoft);
            tip.Margin = new Thickness(2, 10, 2, 8);
            fx.Children.Add(tip);
            fx.Children.Add(Button("Ouvrir les réglages de notifications", () => Shortcuts.Start("ms-settings:notifications")));
            _col.Children.Add(fx.Parent as FrameworkElement);

            // --- Raccourcis ---
            Section("Raccourcis (clic sur l'îlot)");
            var sc = CardPanel();
            _shortcutList = new StackPanel();
            _shortcutAdd = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            sc.Children.Add(_shortcutList);
            sc.Children.Add(T("Ajouter", 12, FontWeights.SemiBold, InkSoft).With(t => t.Margin = new Thickness(2, 10, 0, 0)));
            sc.Children.Add(_shortcutAdd);
            _col.Children.Add(sc.Parent as FrameworkElement);
            RefreshShortcuts();

            // --- Tester ---
            Section("Tester");
            var test = CardPanel();
            var row = new UniformGrid { Columns = 3 };
            row.Children.Add(Button("Volume", () => App.Island.ShowVolume(0.6f, false), compact: true));
            row.Children.Add(Button("Notification", () => App.Island.ShowNote(new IslandNote
            {
                App = "Wave Island", Title = "Ça marche !", Text = "Les notifications s'affichent ici", OnClick = () => { }
            }), compact: true));
            row.Children.Add(Button("Menu", () => App.Island.ShowMenu(), compact: true));
            test.Children.Add(row);
            _col.Children.Add(test.Parent as FrameworkElement);

            // --- Aide ---
            Section("Aide");
            var help = CardPanel();
            help.Children.Add(Button("Rechercher une mise à jour", () => Updater.CheckNow(App.Island, true)));
            help.Children.Add(Button("Copier le rapport de bug", App.CopyReport));
            help.Children.Add(Button("Ouvrir le dossier du journal", App.OpenLogFolder));
            help.Children.Add(Button("Quitter la Wave Island", App.Quit, danger: true));
            var hint = T("Clic sur l'îlot : raccourcis • molette : volume • clic droit : réglages", 12, FontWeights.Normal, InkSoft);
            hint.Margin = new Thickness(2, 10, 2, 2);
            help.Children.Add(hint);
            _col.Children.Add(help.Parent as FrameworkElement);
        }

        private void RefreshShortcuts()
        {
            _shortcutList.Children.Clear();
            var list = S.Shortcuts.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                string id = list[i];
                var it = Shortcuts.Resolve(id);
                if (it == null) continue;
                var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var icon = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = Card, Margin = new Thickness(0, 0, 12, 0) };
                icon.Child = it.Icon != null ? (UIElement)new Image { Source = it.Icon, Width = 18, Height = 18 } : Center(G(it.Glyph, 14));
                var name = T(it.Label, 14, FontWeights.Normal, Ink);
                name.VerticalAlignment = VerticalAlignment.Center;
                int index = i;
                var up = SmallIcon("", () =>
                {
                    if (index == 0) return;
                    var l = S.Shortcuts; (l[index - 1], l[index]) = (l[index], l[index - 1]);
                    S.Save(); RefreshShortcuts();
                });
                up.Opacity = index == 0 ? 0.25 : 1;
                var del = SmallIcon("", () => { S.Shortcuts.Remove(id); S.Save(); RefreshShortcuts(); });
                Grid.SetColumn(name, 1); Grid.SetColumn(up, 2); Grid.SetColumn(del, 3);
                g.Children.Add(icon); g.Children.Add(name); g.Children.Add(up); g.Children.Add(del);
                _shortcutList.Children.Add(g);
            }
            if (_shortcutList.Children.Count == 0)
                _shortcutList.Children.Add(T("Aucun raccourci", 13, FontWeights.Normal, InkSoft));

            _shortcutAdd.Children.Clear();
            bool full = S.Shortcuts.Count >= 6;
            foreach (var a in Shortcuts.Actions)
            {
                if (S.Shortcuts.Contains(a.id)) continue;
                string aid = a.id;
                _shortcutAdd.Children.Add(Chip(a.glyph, a.label, full ? null : (Action)(() => { S.Shortcuts.Add(aid); S.Save(); RefreshShortcuts(); })));
            }
            _shortcutAdd.Children.Add(Chip("", "Une appli…", full ? null : (Action)PickApp));
            if (full) _shortcutAdd.Children.Add(T("6 raccourcis au maximum", 12, FontWeights.Normal, InkSoft).With(t => t.Margin = new Thickness(4, 6, 0, 0)));
        }

        private void PickApp()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choisis une appli",
                Filter = "Applications et raccourcis|*.exe;*.lnk|Tous les fichiers|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + "\\Programs",
                DereferenceLinks = false
            };
            if (dlg.ShowDialog(this) == true)
            {
                S.Shortcuts.Add("app:" + dlg.FileName);
                S.Save();
                RefreshShortcuts();
            }
        }

        // ================== Petits composants ==================

        private void Section(string title)
        {
            var t = T(title.ToUpper(), 11.5, FontWeights.SemiBold, InkSoft);
            t.Margin = new Thickness(6, 18, 0, 8);
            _col.Children.Add(t);
        }

        private StackPanel CardPanel()
        {
            var sp = new StackPanel();
            var b = new Border
            {
                CornerRadius = new CornerRadius(18), Background = Card, BorderBrush = CardLine, BorderThickness = new Thickness(1),
                Padding = new Thickness(14), Child = sp
            };
            return sp;
        }

        private static TextBlock T(string s, double size, FontWeight w, Brush c) => new TextBlock
        {
            Text = s, FontSize = size, FontWeight = w, Foreground = c, FontFamily = Ui, TextWrapping = TextWrapping.Wrap
        };

        private static TextBlock G(string g, double size) => new TextBlock { Text = g, FontSize = size, FontFamily = Icons, Foreground = Ink };

        private static UIElement Center(FrameworkElement e)
        {
            e.HorizontalAlignment = HorizontalAlignment.Center;
            e.VerticalAlignment = VerticalAlignment.Center;
            return e;
        }

        private FrameworkElement Segmented(string[] labels, string[] keys, Func<string> get, Action<string> set)
        {
            var host = new Border { CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), Padding = new Thickness(3), Margin = new Thickness(0, 2, 0, 2) };
            var grid = new UniformGrid { Columns = labels.Length };
            host.Child = grid;
            var cells = new Border[labels.Length];
            void Refresh()
            {
                for (int i = 0; i < cells.Length; i++)
                {
                    bool on = keys[i] == get();
                    cells[i].Background = on ? AccentBrush : Brushes.Transparent;
                    ((TextBlock)cells[i].Child).Foreground = on ? new SolidColorBrush(Color.FromRgb(12, 22, 18)) : Ink;
                }
            }
            for (int i = 0; i < labels.Length; i++)
            {
                int k = i;
                var t = T(labels[i], 13.5, FontWeights.SemiBold, Ink);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.VerticalAlignment = VerticalAlignment.Center;
                cells[i] = new Border { CornerRadius = new CornerRadius(11), Height = 34, Child = t, Cursor = Cursors.Hand, Background = Brushes.Transparent };
                cells[i].MouseLeftButtonUp += (a, b) => { set(keys[k]); Refresh(); };
                grid.Children.Add(cells[i]);
            }
            Refresh();
            return host;
        }

        private FrameworkElement Toggle(string label, string sub, Func<bool> get, Action<bool> set)
        {
            var g = new Grid { Margin = new Thickness(0, 6, 0, 6), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var texts = new StackPanel { Margin = new Thickness(2, 0, 12, 0) };
            texts.Children.Add(T(label, 14.5, FontWeights.Normal, Ink));
            if (sub != null) texts.Children.Add(T(sub, 12, FontWeights.Normal, InkSoft));
            var track = new Border { Width = 46, Height = 28, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Center };
            var knobMove = new TranslateTransform();
            var knob = new Ellipse { Width = 22, Height = 22, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(3, 0, 0, 0), RenderTransform = knobMove };
            track.Child = knob;
            void Refresh()
            {
                bool on = get();
                track.Background = on ? AccentBrush : new SolidColorBrush(Color.FromArgb(55, 255, 255, 255));
                knob.Fill = on ? new SolidColorBrush(Color.FromRgb(12, 22, 18)) : Brushes.White;
                knobMove.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(on ? 18 : 0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new System.Windows.Media.Animation.BackEase { Amplitude = 0.35, EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                });
            }
            Grid.SetColumn(track, 1);
            g.Children.Add(texts);
            g.Children.Add(track);
            g.MouseLeftButtonUp += (a, b) => { set(!get()); Refresh(); };
            Refresh();
            return g;
        }

        private FrameworkElement Slider(string label, double min, double max, Func<double> get, Action<double> set)
        {
            var root = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
            var head = new Grid();
            var name = T(label, 14, FontWeights.Normal, Ink);
            var val = T("", 13, FontWeights.SemiBold, AccentBrush);
            val.HorizontalAlignment = HorizontalAlignment.Right;
            head.Children.Add(name);
            head.Children.Add(val);
            var area = new Grid { Height = 26, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 4, 0, 0) };
            var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };
            var fill = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = AccentBrush, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            var thumb = new Ellipse { Width = 20, Height = 20, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            area.Children.Add(track);
            area.Children.Add(fill);
            area.Children.Add(thumb);
            root.Children.Add(head);
            root.Children.Add(area);

            void Refresh()
            {
                double v = get();
                val.Text = Math.Round(v) + " px";
                double w = Math.Max(0, area.ActualWidth - 20);
                double x = w * (v - min) / (max - min);
                thumb.Margin = new Thickness(x, 0, 0, 0);
                fill.Width = Math.Max(0, x + 10);
            }
            void FromMouse(MouseEventArgs e)
            {
                double w = Math.Max(1, area.ActualWidth - 20);
                double x = Math.Max(0, Math.Min(w, e.GetPosition(area).X - 10));
                double v = Math.Round(min + (max - min) * x / w);
                if (Math.Abs(v - get()) < 0.5) return;
                set(v);
                Settings.Notify();
                Refresh();
            }
            area.MouseLeftButtonDown += (a, b) => { area.CaptureMouse(); FromMouse(b); };
            area.MouseMove += (a, b) => { if (area.IsMouseCaptured) FromMouse(b); };
            area.MouseLeftButtonUp += (a, b) => { area.ReleaseMouseCapture(); S.Save(); };
            area.SizeChanged += (a, b) => Refresh();
            Refresh();
            return root;
        }

        private FrameworkElement Button(string text, Action click, bool compact = false, bool danger = false)
        {
            var t = T(text, 13.5, FontWeights.SemiBold, danger ? new SolidColorBrush(Color.FromRgb(255, 140, 130)) : Ink);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.TextWrapping = TextWrapping.NoWrap;
            var normal = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
            var hover = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
            var b = new Border
            {
                CornerRadius = new CornerRadius(14), Height = 40, Background = normal, Child = t, Cursor = Cursors.Hand,
                Margin = compact ? new Thickness(3) : new Thickness(0, 4, 0, 4)
            };
            b.MouseEnter += (x, y) => b.Background = hover;
            b.MouseLeave += (x, y) => b.Background = normal;
            b.MouseLeftButtonUp += (x, y) => click();
            return b;
        }

        private FrameworkElement Chip(string glyph, string label, Action click)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(G(glyph, 12).With(g => { g.VerticalAlignment = VerticalAlignment.Center; g.Margin = new Thickness(0, 0, 6, 0); }));
            sp.Children.Add(T(label, 12.5, FontWeights.Normal, Ink));
            var b = new Border
            {
                CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                Padding = new Thickness(11, 6, 12, 6), Margin = new Thickness(0, 6, 6, 0), Child = sp,
                Cursor = click == null ? Cursors.Arrow : Cursors.Hand, Opacity = click == null ? 0.35 : 1
            };
            if (click != null) b.MouseLeftButtonUp += (x, y) => click();
            return b;
        }

        private FrameworkElement SmallIcon(string glyph, Action click)
        {
            var b = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = Center(G(glyph, 11)) };
            b.MouseLeftButtonUp += (x, y) => click();
            return b;
        }
    }

    internal static class UiExt
    {
        public static T With<T>(this T e, Action<T> a) { a(e); return e; }
    }
}
