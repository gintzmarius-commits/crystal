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
    /// <summary>L'appli Wave Island : une vraie fenetre (barre de titre, barre des taches, redimensionnable) en verre liquide, avec un menu a gauche.</summary>
    public sealed class AppWindow : Window
    {
        private static readonly Color Accent = Color.FromRgb(176, 240, 210);
        private static readonly Brush AccentBrush = new SolidColorBrush(Accent);
        private static readonly Brush Ink = Brushes.White;
        private static readonly Brush InkSoft = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255));
        private static readonly Brush Card = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255));
        private static readonly Brush CardLine = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));
        private static readonly FontFamily Ui = new FontFamily("Segoe UI Variable Text, Segoe UI");
        private static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        private readonly StackPanel _col = new StackPanel { Margin = new Thickness(28, 6, 28, 40), MaxWidth = 680 };
        private readonly System.Collections.Generic.List<(string title, FrameworkElement anchor, Border nav)> _nav =
            new System.Collections.Generic.List<(string, FrameworkElement, Border)>();
        private StackPanel _navPanel;
        private TextBlock _updateStatus;

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Barre de titre sombre (Windows 10/11) pour aller avec le verre.</summary>
        public static void DarkTitleBar(Window w)
        {
            try
            {
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                int on = 1;
                if (DwmSetWindowAttribute(h, 20, ref on, 4) != 0) DwmSetWindowAttribute(h, 19, ref on, 4);
                int round = 2; // coins arrondis (Windows 11)
                DwmSetWindowAttribute(h, 33, ref round, 4);
            }
            catch { }
        }
        private StackPanel _shortcutList;
        private WrapPanel _shortcutAdd;

        private Settings S => Settings.Current;
        private ScrollViewer _scroll;
        private double _scrollTarget;
        private bool _scrolling;

        /// <summary>Defilement doux, pixel par pixel (avant : une section entiere par cran de molette).</summary>
        private void SmoothWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            if (!_scrolling) _scrollTarget = _scroll.VerticalOffset;
            _scrollTarget = Math.Max(0, Math.Min(_scroll.ScrollableHeight, _scrollTarget - e.Delta * 0.45));
            if (_scrolling) return;
            _scrolling = true;
            CompositionTarget.Rendering += ScrollFrame;
        }

        private readonly FrameGate _gate = new FrameGate();

        private void ScrollFrame(object sender, EventArgs e)
        {
            if (!_gate.Ready()) return;
            double cur = _scroll.VerticalOffset, next = cur + (_scrollTarget - cur) * 0.2;
            if (Math.Abs(_scrollTarget - next) < 0.5)
            {
                next = _scrollTarget;
                _scrolling = false;
                CompositionTarget.Rendering -= ScrollFrame;
            }
            _scroll.ScrollToVerticalOffset(next);
        }

        public AppWindow()
        {
            Title = "Wave Island";
            Width = 980;
            Height = 700;
            MinWidth = 720;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(14, 17, 19));
            FontFamily = Ui;
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/WaveIsland;component/wave.ico")); } catch { }
            SourceInitialized += (a, b) => DarkTitleBar(this);

            // Fond en verre liquide : le bureau est vu a travers la fenetre (pas de flou),
            // avec un voile sombre pour bien lire
            var glass = new GlassBackdrop(this, 8, 0.74);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(glass.Image);
            Grid.SetColumnSpan(glass.Image, 2);

            // Menu de gauche
            var side = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                BorderThickness = new Thickness(0, 0, 1, 0)
            };
            var sideDock = new DockPanel { Margin = new Thickness(14, 18, 14, 16) };
            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 22) };
            var logo = new Image { Width = 38, Height = 38, Margin = new Thickness(0, 0, 10, 0) };
            try { logo.Source = new BitmapImage(new Uri("pack://application:,,,/WaveIsland;component/wave.png")); } catch { }
            brand.Children.Add(logo);
            brand.Children.Add(T("Wave Island", 19, FontWeights.SemiBold, Ink).With(t => t.VerticalAlignment = VerticalAlignment.Center));
            DockPanel.SetDock(brand, Dock.Top);
            sideDock.Children.Add(brand);
            var ver = T("Version " + Updater.CurrentVersion, 12, FontWeights.Normal, InkSoft);
            ver.Margin = new Thickness(10, 10, 0, 0);
            DockPanel.SetDock(ver, Dock.Bottom);
            sideDock.Children.Add(ver);
            _navPanel = new StackPanel();
            sideDock.Children.Add(_navPanel);
            side.Child = sideDock;
            grid.Children.Add(side);

            _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _col, CanContentScroll = false };
            _scroll.PreviewMouseWheel += SmoothWheel;
            _scroll.ScrollChanged += (a, b) => HighlightNav();
            Grid.SetColumn(_scroll, 1);
            grid.Children.Add(_scroll);
            Content = grid;

            Build();
            KeyDown += (a, b) => { if (b.Key == Key.Escape) Close(); };
        }

        private static readonly (string section, string label, string glyph)[] NavItems =
        {
            ("Position de l'îlot", "Îlot", "\uE7F4"),
            ("Apparence", "Apparence", "\uE790"),
            ("Fonctions", "Fonctions", "\uE9E9"),
            ("Raccourcis (clic sur l'îlot)", "Raccourcis", "\uE8A9"),
            ("Wave Drop", "Wave Drop", "\uE72D"),
            ("Mises à jour", "Mises à jour", "\uE777"),
            ("Tester", "Tester", "\uE768"),
            ("Aide", "Aide", "\uE897"),
        };

        private void AddNav(string section, FrameworkElement anchor)
        {
            foreach (var n in NavItems)
            {
                if (n.section != section) continue;
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(G(n.glyph, 15).With(g => { g.Margin = new Thickness(0, 0, 12, 0); g.VerticalAlignment = VerticalAlignment.Center; }));
                sp.Children.Add(T(n.label, 14, FontWeights.Normal, Ink).With(t => t.VerticalAlignment = VerticalAlignment.Center));
                var b = new Border { Height = 40, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 0, 12, 0), Child = sp, Cursor = Cursors.Hand, Background = Brushes.Transparent, Margin = new Thickness(0, 2, 0, 2) };
                b.MouseLeftButtonUp += (x, y) => ScrollTo(anchor);
                _navPanel.Children.Add(b);
                _nav.Add((section, anchor, b));
            }
        }

        private void ScrollTo(FrameworkElement anchor)
        {
            try
            {
                double y = anchor.TransformToAncestor(_col).Transform(new Point(0, 0)).Y - 4;
                _scrollTarget = Math.Max(0, Math.Min(_scroll.ScrollableHeight, y));
                if (!_scrolling) { _scrolling = true; CompositionTarget.Rendering += ScrollFrame; }
            }
            catch { }
        }

        private void HighlightNav()
        {
            if (_nav.Count == 0) return;
            int current = 0;
            for (int i = 0; i < _nav.Count; i++)
            {
                try
                {
                    double y = _nav[i].anchor.TransformToAncestor(_col).Transform(new Point(0, 0)).Y;
                    if (y - 60 <= _scroll.VerticalOffset) current = i;
                }
                catch { }
            }
            if (_scroll.VerticalOffset >= _scroll.ScrollableHeight - 2) current = _nav.Count - 1;
            for (int i = 0; i < _nav.Count; i++)
                _nav[i].nav.Background = i == current ? new SolidColorBrush(Color.FromArgb(40, 176, 240, 210)) : Brushes.Transparent;
        }

        // ================== Structure ==================

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
            look.Children.Add(T("Images par seconde (animations et verre)", 13, FontWeights.Normal, InkSoft).With(t => t.Margin = new Thickness(2, 12, 0, 6)));
            look.Children.Add(Segmented(new[] { "30", "60", "90", "120" }, new[] { "30", "60", "90", "120" },
                () => S.Fps.ToString(), v =>
                {
                    S.Fps = int.Parse(v);
                    S.Save();
                    App.Restart(); // la cadence de WPF ne se change qu'au demarrage
                }));
            int hz = Frames.ScreenHz();
            look.Children.Add(T((hz > 0 ? $"Ton écran affiche {hz} images par seconde au maximum. " : "") +
                "Plus d'images = plus fluide, mais un peu plus de travail pour le PC. L'appli redémarre pour l'appliquer.",
                11.5, FontWeights.Normal, InkSoft).With(t => t.Margin = new Thickness(2, 6, 2, 8)));
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
            fx.Children.Add(Toggle("Musique et vidéos", "Titre, pochette et commandes quand quelque chose joue (survole l'îlot)",
                () => S.LiveMedia, v => { S.LiveMedia = v; S.Save(); }));
            fx.Children.Add(Toggle("Téléchargements en cours", "Une barre de progression par téléchargement",
                () => S.LiveDownloads, v => { S.LiveDownloads = v; S.Save(); }));
            fx.Children.Add(Toggle("Wave Theme : barre des tâches en verre liquide", "Remplace la barre de Windows : bulle Windows à part, applis épinglées et ouvertes, Wi-Fi / son / batterie et l'heure",
                () => S.WaveTaskbar, v => { S.WaveTaskbar = v; S.Save(); App.ApplyTaskbar(); }));
            fx.Children.Add(Toggle("Faire disparaître la barre des tâches", "Quand une appli est en grand écran, la barre descend en goutte ; amène la souris tout en bas de l'écran pour la faire revenir",
                () => S.TaskbarAutoHide, v => { S.TaskbarAutoHide = v; S.Save(); }));
            fx.Children.Add(Toggle("Touche Windows : menu de l'îlot", "Le menu des applis coule depuis la Wave Island au lieu du menu Démarrer (Win + autre touche marche comme avant)",
                () => S.WinKeyMenu, v => { S.WinKeyMenu = v; S.Save(); }));
            fx.Children.Add(Toggle("Cacher en plein écran", "Jeux, films, présentations",
                () => S.HideInFullscreen, v => { S.HideInFullscreen = v; S.Save(); }));
            fx.Children.Add(Toggle("Lancer avec Windows", "La Wave Island démarre toute seule",
                () => S.StartWithWindows, v => { S.StartWithWindows = v; S.Save(); }));
            var tip = T("Masquer les notifications de Windows en bas à droite : dans Paramètres > Système > Notifications, " +
                        "active « Ne pas déranger » (et décoche les règles automatiques). Les notifications arrivent quand même " +
                        "dans l'îlot, mais plus en bas de l'écran.", 12, FontWeights.Normal, InkSoft);
            tip.Margin = new Thickness(2, 10, 2, 8);
            fx.Children.Add(tip);
            fx.Children.Add(Button("Ouvrir « Ne pas déranger »", () => Shortcuts.Start("ms-settings:notifications")));
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

            // --- Wave Drop ---
            Section("Wave Drop");
            var wd = CardPanel();
            wd.Children.Add(T("Envoie n'importe quel fichier à un appareil proche (téléphone ou PC avec Wave Island), comme AirDrop.", 13, FontWeights.Normal, InkSoft).With(t => t.Margin = new Thickness(2, 0, 2, 8)));
            wd.Children.Add(Toggle("Activer Wave Drop", "Ton PC devient visible des appareils Wave Drop tout proches",
                () => S.DropEnabled, v => { S.DropEnabled = v; S.Save(); if (v) App.Drop.Start(); else App.Drop.Stop(); }));
            wd.Children.Add(T("Nom de ce PC", 13, FontWeights.Normal, InkSoft).With(t => t.Margin = new Thickness(2, 8, 0, 4)));
            var nameBox = new TextBox
            {
                Text = WaveDropService.MyName, FontSize = 14, Foreground = Ink, CaretBrush = Ink, Padding = new Thickness(10, 7, 10, 7),
                Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), BorderBrush = CardLine, BorderThickness = new Thickness(1)
            };
            nameBox.TextChanged += (a, b) => { if (!string.IsNullOrWhiteSpace(nameBox.Text)) { S.DropName = nameBox.Text.Trim(); S.Save(); } };
            wd.Children.Add(nameBox);
            wd.Children.Add(Button("Envoyer un fichier", () => App.Launcher.OpenDrop(null)));
            wd.Children.Add(Button("Ouvrir les fichiers reçus", () =>
            {
                System.IO.Directory.CreateDirectory(WaveDropService.DownloadFolder);
                Shortcuts.Start(WaveDropService.DownloadFolder);
            }));
            wd.Children.Add(T("Pour envoyer : glisse un fichier sur l'îlot, ou clique sur l'îlot › Wave Drop, puis rapproche l'appareil et clique « Envoyer ». " +
                              "L'autre doit accepter. Même Wi-Fi : rapide ; sinon Bluetooth, automatiquement. Tout est chiffré. " +
                              "La première fois, Windows peut demander d'autoriser Wave Island dans le pare-feu : accepte pour les réseaux privés.",
                12, FontWeights.Normal, InkSoft).With(t => t.Margin = new Thickness(2, 10, 2, 2)));
            _col.Children.Add(wd.Parent as FrameworkElement);

            // --- Mises a jour ---
            Section("Mises à jour");
            var upd = CardPanel();
            _updateStatus = T(Updater.Latest > Updater.CurrentBuild
                ? $"La version 1.0.{Updater.Latest} est disponible."
                : "Wave Island vérifie toute seule les mises à jour (au démarrage puis toutes les 6 heures). Quand il y en a une, l'îlot te la propose : clique dessus pour l'installer.",
                13, FontWeights.Normal, InkSoft);
            _updateStatus.Margin = new Thickness(2, 0, 2, 8);
            upd.Children.Add(_updateStatus);
            upd.Children.Add(Button("Rechercher et installer maintenant", () => Updater.CheckNow(App.Island, true, install: true)));
            upd.Children.Add(T(Installer.IsInstalledCopy ? "Installée dans " + Installer.Dir : "Pas encore installée (lancée depuis " + System.IO.Path.GetDirectoryName(Environment.ProcessPath) + ")",
                11.5, FontWeights.Normal, InkSoft).With(t => t.Margin = new Thickness(2, 8, 2, 0)));
            _col.Children.Add(upd.Parent as FrameworkElement);

            // --- Tester ---
            Section("Tester");
            var test = CardPanel();
            var row = new UniformGrid { Columns = 4 };
            row.Children.Add(Button("Volume", () => App.Island.ShowVolume(0.6f, false), compact: true));
            row.Children.Add(Button("Notification", () => App.Island.ShowNote(new IslandNote
            {
                App = "Paramètres", Title = "Notification de test", Text = "Clique dessus : ça ouvre l'appli Paramètres",
                Aumid = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"
            }), compact: true));
            row.Children.Add(Button("Menu", () => App.Island.ShowMenu(), compact: true));
            row.Children.Add(Button("Applis", () => App.Launcher.Open(), compact: true));
            test.Children.Add(row);
            _col.Children.Add(test.Parent as FrameworkElement);

            // --- Aide ---
            Section("Aide");
            var help = CardPanel();
            help.Children.Add(Toggle("Visible dans les enregistrements d'écran", "Pour filmer l'îlot (le verre ne déforme plus le fond tant que c'est activé)",
                () => S.ShowInCaptures, v => { S.ShowInCaptures = v; S.Save(); }));
            help.Children.Add(Button("Copier le rapport de bug", App.CopyReport));
            help.Children.Add(Button("Ouvrir le dossier du journal", App.OpenLogFolder));
            help.Children.Add(Button("Quitter la Wave Island", App.Quit, danger: true));
            if (Installer.IsInstalledCopy)
                help.Children.Add(Button("Désinstaller Wave Island", () =>
                {
                    if (MessageBox.Show(this, "Désinstaller Wave Island de ce PC ?", "Wave Island", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    Installer.Uninstall(true);
                    App.Quit();
                }, danger: true));
            var hint = T("Survol de l'îlot : musique et téléchargements • clic : raccourcis • molette : volume • clic droit : réglages", 12, FontWeights.Normal, InkSoft);
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
            var t = T(title, 20, FontWeights.SemiBold, Ink);
            t.Margin = new Thickness(4, _col.Children.Count == 0 ? 18 : 30, 0, 10);
            _col.Children.Add(t);
            AddNav(title, t);
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
