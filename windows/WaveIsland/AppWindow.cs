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
    /// <summary>L'appli Wave Island : une vraie fenetre (barre de titre, barre des taches, redimensionnable), claire et coloree, avec un menu a gauche.</summary>
    public sealed class AppWindow : Window
    {
        // Nouveau look : fond clair opaque, cartes blanches bien arrondies, accents en degrade bleu > violet > rose
        private static readonly Color Accent = Color.FromRgb(108, 92, 246);
        private static readonly Brush AccentBrush = Grad(Color.FromRgb(79, 124, 255), Color.FromRgb(139, 92, 246), Color.FromRgb(236, 72, 153));
        private static readonly Brush AccentText = new SolidColorBrush(Color.FromRgb(98, 84, 230));
        private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(28, 29, 42));
        private static readonly Brush InkSoft = new SolidColorBrush(Color.FromRgb(108, 112, 134));
        private static readonly Brush PageBg = new SolidColorBrush(Color.FromRgb(242, 243, 250));
        private static readonly Brush Card = Brushes.White;
        private static readonly Brush CardLine = new SolidColorBrush(Color.FromRgb(232, 233, 243));
        private static readonly Brush Soft = new SolidColorBrush(Color.FromRgb(240, 241, 249));
        private static readonly Brush SoftHover = new SolidColorBrush(Color.FromRgb(228, 230, 245));
        private static readonly Brush TrackOff = new SolidColorBrush(Color.FromRgb(222, 224, 237));

        private static LinearGradientBrush Grad(params Color[] c)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            for (int i = 0; i < c.Length; i++) g.GradientStops.Add(new GradientStop(c[i], c.Length == 1 ? 0 : (double)i / (c.Length - 1)));
            g.Freeze();
            return g;
        }

        private static System.Windows.Media.Effects.DropShadowEffect Shadow(double blur = 22, double depth = 4, double opacity = 0.08, Color? c = null) =>
            new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = blur, ShadowDepth = depth, Direction = 270, Opacity = opacity, Color = c ?? Color.FromRgb(40, 40, 90) };

        /// <summary>Une couleur par rubrique (pastille ronde du menu et des titres).</summary>
        private static readonly System.Collections.Generic.Dictionary<string, (string glyph, Color color)> SectionLook = new System.Collections.Generic.Dictionary<string, (string, Color)>
        {
            ["Position de l'îlot"] = ("\uE7F4", Color.FromRgb(79, 124, 255)),
            ["Taille"] = ("\uE740", Color.FromRgb(56, 189, 248)),
            ["Apparence"] = ("\uE790", Color.FromRgb(139, 92, 246)),
            ["Fonctions"] = ("\uE9E9", Color.FromRgb(16, 185, 129)),
            ["Ambiance"] = ("\uE706", Color.FromRgb(245, 158, 11)),
            ["Outils"] = ("\uE90F", Color.FromRgb(20, 184, 166)),
            ["Assistant IA (Gemini)"] = ("\uE99A", Color.FromRgb(168, 85, 247)),
            ["Raccourcis (clic sur l'îlot)"] = ("\uE8A9", Color.FromRgb(236, 72, 153)),
            ["Applis du dock (barre des tâches)"] = ("\uE71D", Color.FromRgb(99, 102, 241)),
            ["Mode tablette"] = ("\uE70A", Color.FromRgb(14, 165, 233)),
            ["Wave Link (PC + téléphone)"] = ("\uE8EA", Color.FromRgb(34, 197, 94)),
            ["Wave Drop"] = ("\uE72D", Color.FromRgb(59, 130, 246)),
            ["Mises à jour"] = ("\uE777", Color.FromRgb(249, 115, 22)),
            ["Tester"] = ("\uE768", Color.FromRgb(234, 88, 12)),
            ["Aide"] = ("\uE897", Color.FromRgb(244, 63, 94)),
        };

        private static Border Bubble(string glyph, Color c, double size)
        {
            var light = Color.FromRgb((byte)Math.Min(255, c.R + 50), (byte)Math.Min(255, c.G + 50), (byte)Math.Min(255, c.B + 50));
            return new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Grad(light, c),
                Child = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = size * 0.45, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                Effect = Shadow(10, 2, 0.25, c)
            };
        }
        private static readonly FontFamily Ui = new FontFamily("Segoe UI Variable Text, Segoe UI");
        private static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        private readonly StackPanel _col = new StackPanel { Margin = new Thickness(28, 6, 28, 40), MaxWidth = 680 };
        private readonly System.Collections.Generic.List<(string title, FrameworkElement anchor, Border nav, TextBlock label, Color color)> _nav =
            new System.Collections.Generic.List<(string, FrameworkElement, Border, TextBlock, Color)>();
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
                int on = w is AppWindow ? 0 : 1; // la fenetre des reglages est claire
                if (DwmSetWindowAttribute(h, 20, ref on, 4) != 0) DwmSetWindowAttribute(h, 19, ref on, 4);
                if (w is AppWindow)
                {
                    int cap = 0x00FAF3F2; // barre de titre de la meme couleur que le fond (COLORREF 0x00BBGGRR)
                    DwmSetWindowAttribute(h, 35, ref cap, 4);
                }
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
            Background = PageBg;
            FontFamily = Ui;
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/WaveIsland;component/wave.ico")); } catch { }
            SourceInitialized += (a, b) => DarkTitleBar(this);

            var grid = new Grid { Background = PageBg };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            // Menu de gauche : une grande carte blanche arrondie
            var side = new Border
            {
                Background = Card, CornerRadius = new CornerRadius(26), Margin = new Thickness(14, 14, 4, 14),
                BorderBrush = CardLine, BorderThickness = new Thickness(1), Effect = Shadow()
            };
            var sideDock = new DockPanel { Margin = new Thickness(14, 18, 14, 16) };
            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 22) };
            var logo = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 0, 10, 0) };
            try { logo.Source = new BitmapImage(new Uri("pack://application:,,,/WaveIsland;component/wave.png")); } catch { }
            brand.Children.Add(logo);
            var brandText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            brandText.Children.Add(T("Wave Island", 18, FontWeights.Bold, Ink));
            brandText.Children.Add(T("Réglages", 12, FontWeights.SemiBold, AccentText));
            brand.Children.Add(brandText);
            DockPanel.SetDock(brand, Dock.Top);
            sideDock.Children.Add(brand);
            var ver = new Border
            {
                CornerRadius = new CornerRadius(14), Background = Soft, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 10, 0, 0),
                Child = T("Version " + Updater.CurrentVersion, 12, FontWeights.SemiBold, InkSoft)
            };
            DockPanel.SetDock(ver, Dock.Bottom);
            sideDock.Children.Add(ver);
            _navPanel = new StackPanel();
            sideDock.Children.Add(new ScrollViewer { Content = _navPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
            side.Child = sideDock;
            grid.Children.Add(side);

            _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _col, CanContentScroll = false };
            _scroll.PreviewMouseWheel += SmoothWheel;
            _scroll.ScrollChanged += (a, b) => HighlightNav();
            Grid.SetColumn(_scroll, 1);
            grid.Children.Add(_scroll);
            Content = grid;

            Build();
            KeyDown += (a, b) => { if (b.Key == Key.Escape) Close(); };
        }

        private static readonly (string section, string label)[] NavItems =
        {
            ("Position de l'îlot", "Îlot"),
            ("Apparence", "Apparence"),
            ("Fonctions", "Fonctions"),
            ("Ambiance", "Ambiance"),
            ("Outils", "Outils"),
            ("Assistant IA (Gemini)", "Assistant IA"),
            ("Raccourcis (clic sur l'îlot)", "Raccourcis"),
            ("Applis du dock (barre des tâches)", "Dock"),
            ("Mode tablette", "Mode tablette"),
            ("Wave Link (PC + téléphone)", "Wave Link"),
            ("Wave Drop", "Wave Drop"),
            ("Mises à jour", "Mises à jour"),
            ("Tester", "Tester"),
            ("Aide", "Aide"),
        };

        private void AddNav(string section, FrameworkElement anchor)
        {
            foreach (var n in NavItems)
            {
                if (n.section != section) continue;
                var look = SectionLook.TryGetValue(section, out var l) ? l : ("\uE713", Accent);
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(Bubble(look.Item1, look.Item2, 28).With(g => { g.Margin = new Thickness(0, 0, 12, 0); g.VerticalAlignment = VerticalAlignment.Center; }));
                var label = T(n.label, 14, FontWeights.SemiBold, Ink).With(t => t.VerticalAlignment = VerticalAlignment.Center);
                sp.Children.Add(label);
                var b = new Border { Height = 44, CornerRadius = new CornerRadius(22), Padding = new Thickness(8, 0, 12, 0), Child = sp, Cursor = Cursors.Hand, Background = Brushes.Transparent, Margin = new Thickness(0, 2, 0, 2) };
                b.MouseEnter += (x, y) => { if (b.Tag == null) b.Background = Soft; };
                b.MouseLeave += (x, y) => { if (b.Tag == null) b.Background = Brushes.Transparent; };
                b.MouseLeftButtonUp += (x, y) => ScrollTo(anchor);
                _navPanel.Children.Add(b);
                _nav.Add((section, anchor, b, label, look.Item2));
            }
        }

        /// <summary>Pour les captures du test automatique.</summary>
        public void DemoScroll(double y) { _scroll.ScrollToVerticalOffset(y); }

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
            {
                bool on = i == current;
                var c = _nav[i].color;
                _nav[i].nav.Tag = on ? "on" : null;
                _nav[i].nav.Background = on ? new SolidColorBrush(Color.FromArgb(34, c.R, c.G, c.B)) : Brushes.Transparent;
                _nav[i].label.Foreground = on ? new SolidColorBrush(Color.FromRgb((byte)(c.R * 0.75), (byte)(c.G * 0.75), (byte)(c.B * 0.75))) : Ink;
            }
        }

        // ================== Structure ==================

        private void Build()
        {
            // --- Bandeau colore en haut ---
            var heroText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            heroText.Children.Add(T("Bonjour !", 26, FontWeights.Bold, Brushes.White));
            heroText.Children.Add(T("Règle ta Wave Island comme tu l'aimes : l'îlot, la barre, le dock, l'assistant et plus encore.", 13.5, FontWeights.Normal,
                new SolidColorBrush(Color.FromArgb(225, 255, 255, 255))).With(t => t.Margin = new Thickness(0, 4, 0, 0)));
            var heroLogo = new Border
            {
                Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                Margin = new Thickness(0, 0, 18, 0), Child = new Image { Width = 40, Height = 40 }.With(im => { try { im.Source = new BitmapImage(new Uri("pack://application:,,,/WaveIsland;component/wave.png")); } catch { } })
            };
            var heroRow = new DockPanel();
            DockPanel.SetDock(heroLogo, Dock.Left);
            heroRow.Children.Add(heroLogo);
            heroRow.Children.Add(heroText);
            _col.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(28), Background = AccentBrush, Padding = new Thickness(24, 22, 24, 22), Margin = new Thickness(0, 14, 0, 0),
                Child = heroRow, Effect = Shadow(26, 6, 0.3, Color.FromRgb(120, 80, 230))
            });

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

            // --- Lot 1 : ambiance ---
            Section("Ambiance");
            var amb = CardPanel();
            amb.Children.Add(T("Teinte du verre (îlot, dock, panneaux)", 12.5, FontWeights.SemiBold, InkSoft).With(t => t.Margin = new Thickness(2, 0, 0, 6)));
            amb.Children.Add(Segmented(WaveColor.All.Select(c => c.name).ToArray(), WaveColor.All.Select(c => c.key).ToArray(),
                () => WaveColor.Key, v => WaveColor.Set(v)));
            amb.Children.Add(Toggle("Sons Wave", "Petits sons de goutte d'eau quand l'îlot s'ouvre, se ferme, pour les notifications et quand une appli rentre dans le dock",
                () => S.Sounds, v => { S.Sounds = v; S.Save(); if (v) WaveSound.Play(WaveSound.Kind.Note); }));
            amb.Children.Add(Toggle("L'îlot vit avec la musique", "Le bord de l'îlot s'illumine et ses barres bougent au rythme du son",
                () => S.MusicPulse, v => { S.MusicPulse = v; S.Save(); }));
            amb.Children.Add(Toggle("Mode concentration", "Les notifications n'apparaissent plus (elles attendent dans le centre de notifications). Aussi dans le menu de l'îlot",
                () => S.FocusOn, v => { S.FocusOn = v; S.Save(); FocusMode.Check(); }));
            amb.Children.Add(Toggle("Concentration automatique la nuit", "Tous les jours de " + S.FocusFrom + " h à " + S.FocusTo + " h",
                () => S.FocusScheduled, v => { S.FocusScheduled = v; S.Save(); FocusMode.Check(); }));
            _col.Children.Add(amb.Parent as FrameworkElement);

            // --- Lot 2 : outils ---
            Section("Outils");
            var tools = CardPanel();
            tools.Children.Add(Toggle("Recherche universelle (Alt + Espace)", "Applis, calculs, fichiers récents, Paramètres, web et assistant, dans une barre en verre",
                () => S.SearchKey, v => { S.SearchKey = v; S.Save(); }));
            tools.Children.Add(Toggle("Vue des applis Wave (Win + Tab, 3 doigts vers le haut)", "Remplace l'affichage des tâches de Windows : aperçus, croix pour fermer, bureaux (glisse un aperçu sur un bureau pour l'y déplacer)",
                () => S.OwnTaskView, v => { S.OwnTaskView = v; S.Save(); }));
            tools.Children.Add(Toggle("Tout ranger dans le dock (Win + D, 3 doigts vers le bas)", "Les applis à l'écran se changent en icônes et vont se poser dans le dock",
                () => S.OwnShowDesktop, v => { S.OwnShowDesktop = v; S.Save(); }));
            tools.Children.Add(Toggle("Réglages rapides et notifications Wave (Win + A, Win + N)", "Les raccourcis de Windows ouvrent les panneaux en verre de Wave Island",
                () => S.OwnPanels, v => { S.OwnPanels = v; S.Save(); }));
            tools.Children.Add(Toggle("Capture Wave (Impr. écran)", "L'écran est copié et enregistré dans Images > Captures Wave ; la capture tombe dans l'îlot (touche-la pour l'annoter)",
                () => S.ShotKey, v => { S.ShotKey = v; S.Save(); }));
            tools.Children.Add(T("Écran de veille goutte", 12.5, FontWeights.SemiBold, InkSoft).With(t => t.Margin = new Thickness(2, 12, 0, 6)));
            tools.Children.Add(Segmented(new[] { "Jamais", "2 min", "5 min", "10 min", "20 min" }, new[] { "0", "2", "5", "10", "20" },
                () => S.SaverMinutes.ToString(), v => { S.SaverMinutes = int.Parse(v); S.Save(); }));
            tools.Children.Add(Button("Voir l'écran de veille", DropSaver.Show2, compact: true));
            _col.Children.Add(tools.Parent as FrameworkElement);

            // --- Assistant IA (version finale) ---
            Section("Assistant IA (Gemini)");
            var ai = CardPanel();
            ai.Children.Add(T("Appuie sur la touche Copilot ou Microsoft 365 de ton clavier (ou sur « Assistant » dans le menu de l'îlot) : " +
                "l'îlot t'écoute, Gemini répond à voix haute et peut agir : chercher sur Firefox, ouvrir une appli ou un site, régler le son, " +
                "la luminosité, le Wi-Fi, la musique, lancer un minuteur… Appuie de nouveau pour envoyer tout de suite, ou pour couper la voix et reposer une question.",
                13, FontWeights.Normal, InkSoft).With(t => t.Margin = new Thickness(2, 0, 2, 8)));
            ai.Children.Add(T("Clé Gemini gratuite (aistudio.google.com > Get API key)", 13, FontWeights.Normal, InkSoft).With(t => t.Margin = new Thickness(2, 4, 0, 4)));
            var keyBox = new PasswordBox
            {
                Password = S.AiKey ?? "", FontSize = 14, Foreground = Ink, CaretBrush = Ink, Padding = new Thickness(6, 6, 6, 6),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0)
            };
            keyBox.PasswordChanged += (a, b) => { S.AiKey = keyBox.Password.Trim(); S.Save(); };
            ai.Children.Add(Field(keyBox));
            ai.Children.Add(Button("Créer une clé Gemini gratuite", () => Shortcuts.Start("https://aistudio.google.com/apikey"), compact: true));
            ai.Children.Add(T("Modèle", 12.5, FontWeights.SemiBold, InkSoft).With(t => t.Margin = new Thickness(2, 12, 0, 6)));
            ai.Children.Add(Segmented(new[] { "Flash (rapide)", "Pro (plus malin, moins de questions gratuites)" }, new[] { "gemini-flash-latest", "gemini-pro-latest" },
                () => (S.AiModel ?? "").StartsWith("gemini") ? S.AiModel : "gemini-flash-latest", v => { S.AiModel = v; S.Save(); }));
            ai.Children.Add(Toggle("Réponse à voix haute", "Une voix française lit la réponse (sinon elle s'affiche seulement dans l'îlot)",
                () => S.AiVoice, v => { S.AiVoice = v; S.Save(); }));
            ai.Children.Add(Toggle("Touche Copilot / Microsoft 365", "La touche lance l'assistant de l'îlot au lieu de Copilot ou de l'appli Microsoft 365",
                () => S.AiKeyEnabled, v => { S.AiKeyEnabled = v; S.Save(); }));
            ai.Children.Add(Button("Essayer maintenant", Assistant.Trigger));
            ai.Children.Add(T("Si le micro ne marche pas : Paramètres > Confidentialité et sécurité > Voix (« Reconnaissance vocale en ligne ») et Microphone (« Autoriser les applis de bureau »).",
                11.5, FontWeights.Normal, InkSoft).With(t => t.Margin = new Thickness(2, 10, 2, 4)));
            ai.Children.Add(Button("Ouvrir les réglages de voix", () => Shortcuts.Start("ms-settings:privacy-speech"), compact: true));
            _col.Children.Add(ai.Parent as FrameworkElement);

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

            // --- Dock de la barre des taches (Wave Theme) : autant d'applis qu'on veut ---
            Section("Applis du dock (barre des tâches)");
            var dk = CardPanel();
            dk.Children.Add(T("Toutes les applis épinglées dans la barre / le dock. Tu peux aussi faire un clic droit (ou un appui long) sur une icône du dock, " +
                "ou sur une appli dans le menu Windows.", 13, FontWeights.Normal, InkSoft).With(t => t.Margin = new Thickness(2, 0, 2, 8)));
            _pinList = new StackPanel();
            dk.Children.Add(_pinList);
            dk.Children.Add(T("Ajouter une appli", 12, FontWeights.SemiBold, InkSoft).With(t => t.Margin = new Thickness(2, 12, 0, 6)));
            _pinSearch = new TextBox { FontSize = 14, Padding = new Thickness(6), Background = Brushes.Transparent, Foreground = Ink, BorderThickness = new Thickness(0), CaretBrush = Ink };
            _pinSearch.TextChanged += (a, b) => RefreshPins();
            dk.Children.Add(Field(_pinSearch, "\uE721"));
            _pinAdd = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            dk.Children.Add(_pinAdd);
            _col.Children.Add(dk.Parent as FrameworkElement);
            RefreshPins();

            // --- Mode tablette (4.0) ---
            Section("Mode tablette");
            var tab = CardPanel();
            tab.Children.Add(T("Quand l'écran est retourné (PC convertible), le PC se comporte comme un iPad : applis en plein écran, " +
                "petite barre en bas. Glisse depuis le bas : 1 fois = le dock, 2 fois = l'appli rentre dans le dock. Garde le doigt en haut : toutes les applis ouvertes.",
                13, FontWeights.Normal, InkSoft).With(t => t.Margin = new Thickness(2, 0, 2, 8)));
            tab.Children.Add(Segmented(new[] { "Automatique", "Toujours", "Jamais" }, new[] { "auto", "on", "off" },
                () => S.TabletMode, v => { S.TabletMode = v; S.Save(); TabletShell.Apply(); }));
            tab.Children.Add(T("Animation quand une appli se ferme", 12.5, FontWeights.SemiBold, InkSoft).With(t => t.Margin = new Thickness(2, 12, 0, 6)));
            tab.Children.Add(Segmented(new[] { "Goutte", "iPad" }, new[] { "drop", "ipad" },
                () => S.TabletCloseAnim2, v => { S.TabletCloseAnim2 = v; S.Save(); }));
            tab.Children.Add(T("Contour des fenêtres d'applis (coins arrondis)", 12.5, FontWeights.SemiBold, InkSoft).With(t => t.Margin = new Thickness(2, 12, 0, 6)));
            tab.Children.Add(Segmented(new[] { "Noir", "Blanc", "Windows" }, new[] { "black", "white", "none" },
                () => S.WindowBorder, v => { S.WindowBorder = v; S.Save(); TabletShell.ReapplyBorders(); }));
            tab.Children.Add(Button("Couper les glissements des bords de Windows", () => EdgeSwipes.Set(false)));
            tab.Children.Add(Button("Remettre les glissements des bords de Windows", () => EdgeSwipes.Set(true)));
            _col.Children.Add(tab.Parent as FrameworkElement);

            // --- Lot 4 : Wave Link ---
            Section("Wave Link (PC + téléphone)");
            var wl = CardPanel();
            wl.Children.Add(T("Lie ton téléphone une seule fois (même Wi-Fi, Wave Drop allumé des deux côtés) : batterie du téléphone ici, " +
                "copier sur l'un et coller sur l'autre, faire sonner l'autre, continuité des pages web et rappels partagés.", 13, FontWeights.Normal, InkSoft).With(t => t.Margin = new Thickness(2, 0, 2, 8)));
            var linkedBox = new StackPanel();
            void RefreshLinked()
            {
                linkedBox.Children.Clear();
                foreach (var d in WaveLink.Linked())
                {
                    var id = d.Id;
                    linkedBox.Children.Add(Button("Délier " + d.Name, () => { WaveLink.Unlink(id); RefreshLinked(); }, compact: true));
                }
                foreach (var n in App.Drop?.NearbyList().Where(x => !x.Beacon.IsPc && !WaveLink.IsLinked(x.Beacon.Id)) ?? Enumerable.Empty<WaveDropService.Nearby>())
                {
                    var nn = n;
                    linkedBox.Children.Add(Button("Lier " + n.Label, () => WaveLink.Pair(nn)));
                }
                if (linkedBox.Children.Count == 0)
                    linkedBox.Children.Add(T("Aucun téléphone proche : ouvre Wave Island sur le téléphone (Wave Drop activé, même Wi-Fi), puis touche « Actualiser ».", 12, FontWeights.Normal, InkSoft));
            }
            RefreshLinked();
            WaveLink.Changed += () => Dispatcher.BeginInvoke(new Action(RefreshLinked));
            wl.Children.Add(linkedBox);
            wl.Children.Add(Button("Actualiser", RefreshLinked, compact: true));
            wl.Children.Add(Button("Faire sonner mon téléphone", WaveLink.RingPhone));
            wl.Children.Add(Toggle("Presse-papiers partagé", "Le texte copié sur le PC arrive dans le presse-papiers du téléphone",
                () => S.ClipSync, v => { S.ClipSync = v; S.Save(); }));
            wl.Children.Add(Toggle("Continuité", "La page web ouverte sur un appareil est proposée sur l'autre",
                () => S.Handoff, v => { S.Handoff = v; S.Save(); }));
            wl.Children.Add(Toggle("Résumé du matin", "Au premier déverrouillage de la journée : météo, batterie du téléphone, rappels, notifications",
                () => S.MorningBrief, v => { S.MorningBrief = v; S.Save(); }));
            wl.Children.Add(Toggle("Lire le résumé à voix haute", "Avec la voix de l'assistant",
                () => S.MorningVoice, v => { S.MorningVoice = v; S.Save(); }));
            wl.Children.Add(Button("Voir le résumé maintenant", () => Morning.Maybe(force: true), compact: true));
            _col.Children.Add(wl.Parent as FrameworkElement);

            // --- Wave Drop ---
            Section("Wave Drop");
            var wd = CardPanel();
            wd.Children.Add(T("Envoie n'importe quel fichier à un appareil proche (téléphone ou PC avec Wave Island), comme AirDrop.", 13, FontWeights.Normal, InkSoft).With(t => t.Margin = new Thickness(2, 0, 2, 8)));
            wd.Children.Add(Toggle("Activer Wave Drop", "Ton PC devient visible des appareils Wave Drop tout proches",
                () => S.DropEnabled, v => { S.DropEnabled = v; S.Save(); if (v) App.Drop.Start(); else App.Drop.Stop(); }));
            wd.Children.Add(T("Nom de ce PC", 13, FontWeights.Normal, InkSoft).With(t => t.Margin = new Thickness(2, 8, 0, 4)));
            var nameBox = new TextBox
            {
                Text = WaveDropService.MyName, FontSize = 14, Foreground = Ink, CaretBrush = Ink, Padding = new Thickness(6),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0)
            };
            nameBox.TextChanged += (a, b) => { if (!string.IsNullOrWhiteSpace(nameBox.Text)) { S.DropName = nameBox.Text.Trim(); S.Save(); } };
            wd.Children.Add(Field(nameBox));
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

        private StackPanel _pinList;
        private TextBox _pinSearch;
        private WrapPanel _pinAdd;

        /// <summary>Liste des applis du dock (retirer) + recherche pour en ajouter autant qu'on veut.</summary>
        private void RefreshPins()
        {
            if (_pinList == null) return;
            _pinList.Children.Clear();
            var tb = App.Taskbar;
            if (tb == null)
            {
                _pinList.Children.Add(T("Active « Wave Theme : barre des tâches en verre liquide » pour avoir le dock.", 13, FontWeights.Normal, InkSoft));
                _pinAdd.Children.Clear();
                return;
            }
            foreach (var (name, pin) in tb.PinnedList())
            {
                var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var n = T(name, 14, FontWeights.Normal, Ink);
                n.VerticalAlignment = VerticalAlignment.Center;
                string p = pin;
                var del = SmallIcon("\uE711", () => { App.Taskbar?.Unpin(p); RefreshPins(); });
                Grid.SetColumn(del, 1);
                g.Children.Add(n); g.Children.Add(del);
                _pinList.Children.Add(g);
            }
            if (_pinList.Children.Count == 0) _pinList.Children.Add(T("Aucune appli épinglée", 13, FontWeights.Normal, InkSoft));
            _pinAdd.Children.Clear();
            string q = _pinSearch?.Text?.Trim() ?? "";
            var apps = AppCatalog.Apps.Where(a => !tb.IsPinned(a) && (q.Length == 0 || AppCatalog.Matches(a, q))).OrderBy(a => a.Name).Take(q.Length == 0 ? 12 : 24);
            foreach (var a in apps)
            {
                var app = a;
                _pinAdd.Children.Add(Chip("\uE718", a.Name, () => { App.Taskbar?.PinApp(app); RefreshPins(); }));
            }
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
                var icon = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Soft, Margin = new Thickness(0, 0, 12, 0) };
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
            var look = SectionLook.TryGetValue(title, out var l) ? l : ("\uE713", Accent);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, _col.Children.Count <= 1 ? 22 : 32, 0, 12) };
            row.Children.Add(Bubble(look.Item1, look.Item2, 34).With(b => { b.Margin = new Thickness(0, 0, 12, 0); b.VerticalAlignment = VerticalAlignment.Center; }));
            row.Children.Add(T(title, 20, FontWeights.Bold, Ink).With(t => t.VerticalAlignment = VerticalAlignment.Center));
            _col.Children.Add(row);
            AddNav(title, row);
        }

        private StackPanel CardPanel()
        {
            var sp = new StackPanel();
            var b = new Border
            {
                CornerRadius = new CornerRadius(24), Background = Card, BorderBrush = CardLine, BorderThickness = new Thickness(1),
                Padding = new Thickness(18, 16, 18, 16), Child = sp, Effect = Shadow()
            };
            return sp;
        }

        /// <summary>Champ de texte arrondi (gris tres clair, contour colore quand on ecrit dedans).</summary>
        private static Border Field(Control box, string glyph = null)
        {
            var dp = new DockPanel();
            if (glyph != null)
            {
                var ic = G(glyph, 13).With(g => { g.Foreground = InkSoft; g.VerticalAlignment = VerticalAlignment.Center; g.Margin = new Thickness(4, 0, 4, 0); });
                DockPanel.SetDock(ic, Dock.Left);
                dp.Children.Add(ic);
            }
            dp.Children.Add(box);
            var b = new Border
            {
                CornerRadius = new CornerRadius(18), Background = Soft, BorderBrush = Soft, BorderThickness = new Thickness(2),
                Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 2, 0, 4), Child = dp
            };
            box.GotKeyboardFocus += (x, y) => { b.BorderBrush = AccentBrush; b.Background = Card; };
            box.LostKeyboardFocus += (x, y) => { b.BorderBrush = Soft; b.Background = Soft; };
            return b;
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
            var host = new Border { CornerRadius = new CornerRadius(20), Background = Soft, Padding = new Thickness(4), Margin = new Thickness(0, 2, 0, 2) };
            var grid = new UniformGrid { Columns = labels.Length };
            host.Child = grid;
            var cells = new Border[labels.Length];
            void Refresh()
            {
                for (int i = 0; i < cells.Length; i++)
                {
                    bool on = keys[i] == get();
                    cells[i].Background = on ? AccentBrush : Brushes.Transparent;
                    cells[i].Effect = on ? Shadow(12, 2, 0.3, Accent) : null;
                    ((TextBlock)cells[i].Child).Foreground = on ? Brushes.White : InkSoft;
                }
            }
            for (int i = 0; i < labels.Length; i++)
            {
                int k = i;
                var t = T(labels[i], 13.5, FontWeights.SemiBold, InkSoft);
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.VerticalAlignment = VerticalAlignment.Center;
                t.TextAlignment = TextAlignment.Center;
                t.Margin = new Thickness(4, 0, 4, 0);
                cells[i] = new Border { CornerRadius = new CornerRadius(16), MinHeight = 36, Child = t, Cursor = Cursors.Hand, Background = Brushes.Transparent };
                cells[i].MouseLeftButtonUp += (a, b) => { set(keys[k]); Refresh(); };
                grid.Children.Add(cells[i]);
            }
            Refresh();
            return host;
        }

        private FrameworkElement Toggle(string label, string sub, Func<bool> get, Action<bool> set)
        {
            var g = new Grid { Margin = new Thickness(0, 7, 0, 7), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var texts = new StackPanel { Margin = new Thickness(2, 0, 14, 0) };
            texts.Children.Add(T(label, 14.5, FontWeights.SemiBold, Ink));
            if (sub != null) texts.Children.Add(T(sub, 12.5, FontWeights.Normal, InkSoft).With(t => t.Margin = new Thickness(0, 2, 0, 0)));
            var track = new Border { Width = 50, Height = 30, CornerRadius = new CornerRadius(15), VerticalAlignment = VerticalAlignment.Center };
            var knobMove = new TranslateTransform();
            var knob = new Ellipse { Width = 24, Height = 24, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(3, 0, 0, 0), RenderTransform = knobMove, Effect = Shadow(6, 1, 0.25) };
            track.Child = knob;
            void Refresh()
            {
                bool on = get();
                track.Background = on ? AccentBrush : TrackOff;
                knobMove.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(on ? 20 : 0, TimeSpan.FromMilliseconds(220))
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
            var name = T(label, 14, FontWeights.SemiBold, Ink);
            var val = T("", 12.5, FontWeights.Bold, AccentText);
            var valBox = new Border { CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Color.FromRgb(238, 236, 255)), Padding = new Thickness(9, 2, 9, 3), Child = val, HorizontalAlignment = HorizontalAlignment.Right };
            head.Children.Add(name);
            head.Children.Add(valBox);
            var area = new Grid { Height = 30, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 4, 0, 0) };
            var track = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = TrackOff, VerticalAlignment = VerticalAlignment.Center };
            var fill = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = AccentBrush, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            var thumb = new Ellipse { Width = 22, Height = 22, Fill = Brushes.White, Stroke = new SolidColorBrush(Accent), StrokeThickness = 3, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Effect = Shadow(8, 1, 0.25) };
            area.Children.Add(track);
            area.Children.Add(fill);
            area.Children.Add(thumb);
            root.Children.Add(head);
            root.Children.Add(area);

            void Refresh()
            {
                double v = get();
                val.Text = Math.Round(v) + " px";
                double w = Math.Max(0, area.ActualWidth - 22);
                double x = w * (v - min) / (max - min);
                thumb.Margin = new Thickness(x, 0, 0, 0);
                fill.Width = Math.Max(0, x + 11);
            }
            void FromMouse(MouseEventArgs e)
            {
                double w = Math.Max(1, area.ActualWidth - 22);
                double x = Math.Max(0, Math.Min(w, e.GetPosition(area).X - 11));
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
            var t = T(text, 13.5, FontWeights.SemiBold, danger ? new SolidColorBrush(Color.FromRgb(225, 45, 70)) : AccentText);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            t.TextWrapping = TextWrapping.NoWrap;
            var normal = danger ? new SolidColorBrush(Color.FromRgb(255, 236, 239)) : new SolidColorBrush(Color.FromRgb(238, 236, 255));
            var hover = danger ? new SolidColorBrush(Color.FromRgb(255, 222, 228)) : new SolidColorBrush(Color.FromRgb(226, 222, 255));
            var b = new Border
            {
                CornerRadius = new CornerRadius(21), Height = 42, Background = normal, Child = t, Cursor = Cursors.Hand,
                Margin = compact ? new Thickness(3) : new Thickness(0, 4, 0, 4), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1)
            };
            b.MouseEnter += (x, y) => b.Background = hover;
            b.MouseLeave += (x, y) => { b.Background = normal; Press(b, 1); };
            b.MouseLeftButtonDown += (x, y) => Press(b, 0.96);
            b.MouseLeftButtonUp += (x, y) => { Press(b, 1); click(); };
            return b;
        }

        private static void Press(Border b, double to)
        {
            var a = new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(140)) { EasingFunction = new System.Windows.Media.Animation.QuadraticEase() };
            b.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            b.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        private FrameworkElement Chip(string glyph, string label, Action click)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(G(glyph, 12).With(g => { g.VerticalAlignment = VerticalAlignment.Center; g.Margin = new Thickness(0, 0, 7, 0); g.Foreground = AccentText; }));
            sp.Children.Add(T(label, 12.5, FontWeights.SemiBold, Ink));
            var b = new Border
            {
                CornerRadius = new CornerRadius(16), Background = Soft,
                Padding = new Thickness(12, 7, 14, 7), Margin = new Thickness(0, 6, 6, 0), Child = sp,
                Cursor = click == null ? Cursors.Arrow : Cursors.Hand, Opacity = click == null ? 0.4 : 1
            };
            if (click != null)
            {
                b.MouseEnter += (x, y) => b.Background = SoftHover;
                b.MouseLeave += (x, y) => b.Background = Soft;
                b.MouseLeftButtonUp += (x, y) => click();
            }
            return b;
        }

        private FrameworkElement SmallIcon(string glyph, Action click)
        {
            var b = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = Soft, Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 0, 0), Child = Center(G(glyph, 11).With(g => g.Foreground = InkSoft)) };
            b.MouseEnter += (x, y) => b.Background = SoftHover;
            b.MouseLeave += (x, y) => b.Background = Soft;
            b.MouseLeftButtonUp += (x, y) => click();
            return b;
        }
    }

    internal static class UiExt
    {
        public static T With<T>(this T e, Action<T> a) { a(e); return e; }
    }
}
