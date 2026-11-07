using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WaveIsland
{
    /// <summary>
    /// Panneaux de la barre Wave Theme, en verre liquide (le fond est vu a travers, deforme sur les bords) :
    /// reglages rapides (Wi-Fi, Bluetooth, son, luminosite...) et notifications + calendrier.
    /// Ils montent de leur bulle comme une goutte.
    /// </summary>
    public sealed partial class TaskbarPanel : Window
    {
        private readonly Grid _root = new Grid();
        private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        private readonly TranslateTransform _move = new TranslateTransform();
        private readonly StackPanel _col = new StackPanel { Margin = new Thickness(18) };
        private bool _closing;
        private IntPtr _hwnd;
        private readonly string _kind;
        private DateTime _openedAt = DateTime.Now;
        private bool _wasActive, _wasDown = true;
        private readonly System.Windows.Threading.DispatcherTimer _outside;
        private static string _lastClosedKind;
        private static DateTime _lastClosedAt;
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

        /// <summary>Bouton de la souris (ou doigt) appuye en dehors du panneau : on le ferme.</summary>
        private void CheckOutside()
        {
            bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0;
            bool pressed = down && !_wasDown;
            _wasDown = down;
            if (!pressed || _closing || _hwnd == IntPtr.Zero) return;
            if ((DateTime.Now - _openedAt).TotalMilliseconds < 350) return;
            Native.GetCursorPos(out var p);
            Native.GetWindowRect(_hwnd, out var r);
            if (p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom) return;
            CloseAnimated();
        }

        private static readonly FontFamily Ui = new FontFamily("Segoe UI Variable Text, Segoe UI");
        private static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        private static readonly SolidColorBrush Plate = new SolidColorBrush(Color.FromArgb(110, 20, 22, 26));
        private static readonly SolidColorBrush PlateHover = new SolidColorBrush(Color.FromArgb(160, 20, 22, 26));
        private static readonly SolidColorBrush PlateLine = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
        private static readonly SolidColorBrush On = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
        private static readonly SolidColorBrush OnInk = new SolidColorBrush(Color.FromRgb(24, 28, 36));

        public static TaskbarPanel Current { get; private set; }

        /// <summary>Ouvre (ou referme s'il est deja ouvert) le panneau « quick » ou « notes » au-dessus de sa bulle.</summary>
        /// <summary>Petit menu en verre liquide (clic droit / appui long sur une icone du dock).</summary>
        public static void Menu(Rect anchor, string title, (string glyph, string label, Action act)[] items)
        {
            Current?.CloseAnimated();
            var p = new TaskbarPanel("menu");
            p._col.Margin = new Thickness(10);
            p._col.Children.Add(T(title, 13, FontWeights.SemiBold, 0.85).With(t => t.Margin = new Thickness(8, 2, 8, 8)));
            foreach (var (glyph, label, act) in items)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 14, Width = 26, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = label, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
                var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 9, 14, 9), Child = row, Cursor = Cursors.Hand, Background = Plate, Margin = new Thickness(0, 0, 0, 6) };
                b.MouseEnter += (x, y) => b.Background = PlateHover;
                b.MouseLeave += (x, y) => b.Background = Plate;
                b.MouseLeftButtonUp += (x, y) => { p.CloseAnimated(); act(); };
                b.TouchUp += (x, y) => { y.Handled = true; p.CloseAnimated(); act(); };
                p._col.Children.Add(b);
            }
            Current = p;
            p.Open(anchor);
        }

        public static void Toggle(string kind, Rect bubble)
        {
            if (Current != null && !Current._closing)
            {
                bool same = Current._kind == kind;
                Current.CloseAnimated();
                if (same) return;
            }
            // Le toucher sur la bulle vient de fermer ce meme panneau (toucher « a cote ») : on ne le rouvre pas
            if (_lastClosedKind == kind && (DateTime.Now - _lastClosedAt).TotalMilliseconds < 450) return;
            var p = new TaskbarPanel(kind);
            Current = p;
            p.Open(bubble);
        }

        private TaskbarPanel(string kind)
        {
            _kind = kind;
            Title = kind == "quick" ? "Réglages rapides" : "Notifications";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            FontFamily = Ui;
            Foreground = Brushes.White;
            UseLayoutRounding = true;
            Width = kind == "menu" ? 290 : 380;
            SizeToContent = SizeToContent.Height;

            var glass = new GlassBackdrop(this, 26, 0.0);   // verre transparent : ni flou ni voile
            // L'image du fond ne doit pas decider de la taille du panneau (sinon il prenait toute la hauteur
            // de l'ecran et son contenu sortait de l'ecran) : elle est posee dans un Canvas et suit le panneau.
            var holder = new Canvas { IsHitTestVisible = false };
            holder.Children.Add(glass.Image);
            _root.Children.Add(holder);
            _root.SizeChanged += (a, b) => { glass.Image.Width = _root.ActualWidth; glass.Image.Height = _root.ActualHeight; };
            _root.Children.Add(_col);
            _root.RenderTransformOrigin = new Point(0.5, 1);
            _root.RenderTransform = new TransformGroup { Children = { _scale, _move } };
            Content = _root;

            SourceInitialized += (a, b) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW);
            };
            // Juste apres l'ouverture, Windows peut encore donner la main a l'ancienne fenetre : on ne ferme pas pour ca
            // Fermeture : seulement si le panneau a vraiment eu la main (en tactile, Windows ne la donne pas toujours ;
            // avant, il se refermait des l'ouverture). Un toucher / clic a cote le ferme aussi.
            Activated += (a, b) => _wasActive = true;
            Deactivated += (a, b) => { if (_wasActive && (DateTime.Now - _openedAt).TotalMilliseconds > 500) CloseAnimated(); };
            _outside = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _outside.Tick += (a, b) => CheckOutside();
            PreviewKeyDown += (a, b) => { if (b.Key == Key.Escape) CloseAnimated(); };

            if (kind == "quick") BuildQuick();
            else if (kind == "notes") BuildNotes();
        }

        private void Open(Rect bubble)
        {
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            double k = 1;
            try { k = VisualTreeHelper.GetDpi(App.Island).DpiScaleX; } catch { }
            double right = sb.Right / k - 10;
            Left = Math.Min(right - Width, bubble.Right - Width + 6);
            if (Left < sb.Left / k + 10) Left = sb.Left / k + 10;
            // Le bas du panneau juste au-dessus de la bulle
            Measure(new Size(Width, double.PositiveInfinity));
            double h = Math.Min(DesiredSize.Height, sb.Height / k - 120);
            MaxHeight = sb.Height / k - 120;
            // Bulle en haut de l'ecran (mode tablette) : le panneau descend en dessous ; sinon il monte au-dessus
            bool below = bubble.Top < sb.Height / k / 2;
            Top = below ? bubble.Bottom + 10 : bubble.Top - 12 - h;
            // La vraie hauteur n'est connue qu'apres l'affichage : on recale le panneau au-dessus de sa bulle
            if (!below) SizeChanged += (a, b) => { if (!_closing) Top = bubble.Top - 12 - ActualHeight; };
            // Depart : une goutte qui sort de la bulle
            double ox = (bubble.Left + bubble.Width / 2) - Left;
            _root.RenderTransformOrigin = new Point(Math.Max(0, Math.Min(1, ox / Width)), below ? 0 : 1);
            _scale.ScaleX = 0.25; _scale.ScaleY = 0.08;
            _move.Y = below ? -30 : 30;
            _root.Opacity = 0;
            _openedAt = DateTime.Now;
            _wasDown = true;
            _outside.Start();
            Show();
            Activate();
            ForceForeground();
            var ease = new SpringEase();
            var t = TimeSpan.FromMilliseconds(560);
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, t) { EasingFunction = ease });
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(620)) { EasingFunction = ease });
            _move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, t) { EasingFunction = ease });
            _root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
            // Les elements arrivent un par un avec un rebond
            int i = 0;
            foreach (UIElement c in _col.Children)
            {
                c.RenderTransformOrigin = new Point(0.5, 0.5);
                var st = new ScaleTransform(0.9, 0.9);
                c.RenderTransform = st;
                c.Opacity = 0;
                var bt = TimeSpan.FromMilliseconds(90 + 35 * i++);
                var a = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(500)) { BeginTime = bt, EasingFunction = ease };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
                c.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = bt });
            }
        }

        public void CloseAnimated()
        {
            if (_closing) return;
            _closing = true;
            _outside.Stop();
            _lastClosedKind = _kind;
            _lastClosedAt = DateTime.Now;
            if (Current == this) Current = null;
            var t = TimeSpan.FromMilliseconds(200);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.3, t) { EasingFunction = ease });
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.1, t) { EasingFunction = ease });
            _move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_root.RenderTransformOrigin.Y < 0.5 ? -30 : 30, t) { EasingFunction = ease });
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)) { BeginTime = TimeSpan.FromMilliseconds(40) };
            fade.Completed += (a, b) => { try { Close(); } catch { } };
            _root.BeginAnimation(OpacityProperty, fade);
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);

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
        }

        // ================== Elements communs ==================

        private static TextBlock T(string s, double size, FontWeight w, double opacity = 1) => new TextBlock
        {
            Text = s, FontSize = size, FontWeight = w, Opacity = opacity, TextTrimming = TextTrimming.CharacterEllipsis,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.35 }
        };

        private static Border PlateBox(UIElement child, double radius = 16, Thickness? pad = null) => new Border
        {
            Background = Plate, BorderBrush = PlateLine, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius), Padding = pad ?? new Thickness(12, 10, 12, 10), Child = child
        };

        private static Border Pill(string glyph, string label, Action act)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (glyph != null) row.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var b = PlateBox(row, 15, new Thickness(12, 6, 14, 7));
            b.Cursor = Cursors.Hand;
            b.MouseEnter += (x, y) => b.Background = PlateHover;
            b.MouseLeave += (x, y) => b.Background = Plate;
            b.MouseLeftButtonUp += (x, y) => act();
            return b;
        }

        // ================== Reglages rapides ==================

        private readonly List<(Border box, TextBlock icon, TextBlock label, Func<bool> state)> _toggles = new List<(Border, TextBlock, TextBlock, Func<bool>)>();
        private Windows.Devices.Radios.Radio _wifi, _bt;


        private async void LoadRadios()
        {
            try
            {
                await Windows.Devices.Radios.Radio.RequestAccessAsync();
                var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
                _wifi = radios.FirstOrDefault(r => r.Kind == Windows.Devices.Radios.RadioKind.WiFi);
                _bt = radios.FirstOrDefault(r => r.Kind == Windows.Devices.Radios.RadioKind.Bluetooth);
            }
            catch (Exception e) { Log.I("Reglages rapides : radios : " + e.Message); }
            RefreshToggles();
        }

        private async void FlipRadio(Windows.Devices.Radios.Radio r)
        {
            if (r == null) return;
            try
            {
                await r.SetStateAsync(r.State == Windows.Devices.Radios.RadioState.On ? Windows.Devices.Radios.RadioState.Off : Windows.Devices.Radios.RadioState.On);
            }
            catch (Exception e) { Log.I("Reglages rapides : " + e.Message); }
            RefreshToggles();
        }

        private async void Airplane()
        {
            bool allOff = _wifi != null && _wifi.State != Windows.Devices.Radios.RadioState.On && (_bt == null || _bt.State != Windows.Devices.Radios.RadioState.On);
            var want = allOff ? Windows.Devices.Radios.RadioState.On : Windows.Devices.Radios.RadioState.Off;
            try
            {
                if (_wifi != null) await _wifi.SetStateAsync(want);
                if (_bt != null) await _bt.SetStateAsync(want);
            }
            catch (Exception e) { Log.I("Reglages rapides : mode avion : " + e.Message); }
            RefreshToggles();
        }

        private static void WinKey(byte vk)
        {
            Native.keybd_event((byte)Native.VK_LWIN, 0, 0, UIntPtr.Zero);
            Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
            Native.keybd_event(vk, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Native.keybd_event((byte)Native.VK_LWIN, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }


        private static void Bounce(FrameworkElement e, double to)
        {
            if (!(e.RenderTransform is ScaleTransform st)) return;
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(380)) { EasingFunction = new SpringEase() };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        private void RefreshToggles()
        {
            foreach (var (box, icon, label, state) in _toggles)
            {
                bool on = false;
                try { on = state(); } catch { }
                box.Background = on ? On : Plate;
                icon.Foreground = label.Foreground = on ? OnInk : Brushes.White;
            }
        }

        /// <summary>Grille de 3 colonnes.</summary>
        private sealed class UniformGrid3
        {
            public readonly System.Windows.Controls.Primitives.UniformGrid Panel = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
            public void Add(UIElement e) => Panel.Children.Add(e);
        }

        // ================== Notifications + calendrier ==================

        private void BuildNotes()
        {
            var now = DateTime.Now;
            var fr = new CultureInfo("fr-FR");

            // Notifications recues
            var head = new DockPanel { LastChildFill = false, Margin = new Thickness(2, 0, 0, 8) };
            head.Children.Add(T("Notifications", 15, FontWeights.SemiBold));
            var list = App.NoteHistory.ToList();
            // Ne pas deranger (comme la cloche de Windows)
            Border dnd = null;
            dnd = Pill(FocusMode.On ? "\uE7ED" : "\uEA8F", FocusMode.On ? "Silence" : "Silence", () =>
            {
                FocusMode.Toggle();
                dnd.Background = FocusMode.On ? On : Plate;
                if (dnd.Child is StackPanel sp)
                {
                    ((TextBlock)sp.Children[0]).Text = FocusMode.On ? "\uE7ED" : "\uEA8F";
                    ((TextBlock)sp.Children[1]).Text = FocusMode.On ? "Silence" : "Silence";
                    foreach (TextBlock t in sp.Children) t.Foreground = FocusMode.On ? OnInk : Brushes.White;
                }
            });
            if (FocusMode.On) { dnd.Background = On; foreach (TextBlock t in ((StackPanel)dnd.Child).Children) t.Foreground = OnInk; }
            DockPanel.SetDock(dnd, Dock.Right);
            head.Children.Add(dnd);
            if (list.Count > 0)
            {
                var clear = Pill(null, "Tout effacer", () => { App.NoteHistory.Clear(); CloseAnimated(); });
                clear.Margin = new Thickness(0, 0, 6, 0);
                DockPanel.SetDock(clear, Dock.Right);
                head.Children.Add(clear);
            }
            _col.Children.Add(head);
            if (list.Count == 0)
            {
                _col.Children.Add(PlateBox(new TextBlock { Text = "Aucune nouvelle notification", FontSize = 13.5, Opacity = 0.85, HorizontalAlignment = HorizontalAlignment.Center }, 18, new Thickness(14, 18, 14, 18)));
            }
            else
            {
                var stack = new StackPanel();
                foreach (var (n, when) in list.Take(30)) stack.Children.Add(NoteCard(n, when));
                _col.Children.Add(new ScrollViewer { Content = stack, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
            }

            // Calendrier du mois
            // Calendrier (fleches pour changer de mois, comme Windows)
            var cal = new StackPanel();
            var calHead = new DockPanel { LastChildFill = true };
            var monthGrid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7, Margin = new Thickness(0, 8, 0, 0) };
            var shown = new DateTime(now.Year, now.Month, 1);
            var title = T("", 15, FontWeights.SemiBold);
            void Fill()
            {
                title.Text = shown.Month == now.Month && shown.Year == now.Year ? fr.TextInfo.ToTitleCase(now.ToString("dddd d MMMM", fr)) : fr.TextInfo.ToTitleCase(shown.ToString("MMMM yyyy", fr));
                FillMonth(monthGrid, shown, now);
            }
            Border Arrow(string g, int step)
            {
                var b = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = new TextBlock { Text = g, FontFamily = Icons, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                b.MouseEnter += (x, y) => b.Background = PlateHover;
                b.MouseLeave += (x, y) => b.Background = Brushes.Transparent;
                b.MouseLeftButtonUp += (x, y) => { shown = shown.AddMonths(step); Fill(); };
                DockPanel.SetDock(b, Dock.Right);
                return b;
            }
            calHead.Children.Add(Arrow("\uE70D", 1));
            calHead.Children.Add(Arrow("\uE70E", -1));
            calHead.Children.Add(title);
            cal.Children.Add(calHead);
            cal.Children.Add(monthGrid);
            Fill();
            _col.Children.Add(PlateBox(cal, 20, new Thickness(14, 12, 14, 12)).With(b => b.Margin = new Thickness(0, 14, 0, 0)));
        }

        private static void FillMonth(System.Windows.Controls.Primitives.UniformGrid grid, DateTime first, DateTime now)
        {
            grid.Children.Clear();
            foreach (var d in new[] { "lu", "ma", "me", "je", "ve", "sa", "di" })
                grid.Children.Add(new TextBlock { Text = d, FontSize = 11.5, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
            int lead = ((int)first.DayOfWeek + 6) % 7;
            for (int i = 0; i < lead; i++) grid.Children.Add(new TextBlock());
            for (int day = 1; day <= DateTime.DaysInMonth(first.Year, first.Month); day++)
            {
                bool today = day == now.Day && first.Month == now.Month && first.Year == now.Year;
                var tb = new TextBlock { Text = day.ToString(), FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = today ? OnInk : Brushes.White, FontWeight = today ? FontWeights.SemiBold : FontWeights.Normal };
                grid.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = today ? On : Brushes.Transparent, Child = tb, Margin = new Thickness(1) });
            }
        }

        private FrameworkElement NoteCard(IslandNote n, DateTime when)
        {
            var st = new StackPanel();
            var top = new DockPanel { LastChildFill = true };
            var x = new TextBlock { Text = "\uE711", FontFamily = Icons, FontSize = 10, Margin = new Thickness(10, 1, 0, 0), Opacity = 0.8, Cursor = Cursors.Hand, Background = Brushes.Transparent };
            DockPanel.SetDock(x, Dock.Right);
            top.Children.Add(x);
            var ago = new TextBlock { Text = Ago(when), FontSize = 11.5, Opacity = 0.75 };
            DockPanel.SetDock(ago, Dock.Right);
            top.Children.Add(ago);
            top.Children.Add(new TextBlock { Text = (n.App ?? "").ToUpperInvariant(), FontSize = 11, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis });
            st.Children.Add(top);
            if (!string.IsNullOrWhiteSpace(n.Title)) st.Children.Add(new TextBlock { Text = n.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrWhiteSpace(n.Text)) st.Children.Add(new TextBlock { Text = n.Text, FontSize = 13, Opacity = 0.9, TextWrapping = TextWrapping.Wrap, MaxHeight = 54, TextTrimming = TextTrimming.CharacterEllipsis });
            var card = PlateBox(st, 18);
            card.Margin = new Thickness(0, 0, 0, 8);
            card.Cursor = Cursors.Hand;
            card.MouseEnter += (a, b) => card.Background = PlateHover;
            card.MouseLeave += (a, b) => card.Background = Plate;
            x.MouseLeftButtonUp += (a, b) =>
            {
                b.Handled = true;
                App.NoteHistory.RemoveAll(y => y.note == n);
                // La carte part vers la droite puis la liste se resserre
                var tt = new TranslateTransform();
                card.RenderTransform = tt;
                var go = new DoubleAnimation(420, TimeSpan.FromMilliseconds(260)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
                go.Completed += (c, d) => { if (card.Parent is Panel p) p.Children.Remove(card); };
                tt.BeginAnimation(TranslateTransform.XProperty, go);
            };
            card.MouseLeftButtonUp += (a, b) =>
            {
                if (b.Handled) return;
                CloseAnimated();
                if (n.OnClick != null) n.OnClick();
                else Shortcuts.OpenApp(n.Aumid, n.App);
                App.NoteHistory.RemoveAll(x => x.note == n);
            };
            return card;
        }

        private static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return "maintenant";
            if (d.TotalMinutes < 60) return $"il y a {(int)d.TotalMinutes} min";
            if (t.Date == DateTime.Today) return t.ToString("HH:mm");
            return t.ToString("d MMM", new CultureInfo("fr-FR"));
        }
    }

    /// <summary>Curseur horizontal en verre (luminosite, volume) : on glisse dessus, la partie remplie suit la souris.</summary>
    public sealed class GlassBar : Grid
    {
        private readonly Border _fill;
        private readonly Action<double> _changed;
        private double _value;

        public double Value
        {
            get => _value;
            set { _value = Math.Max(0, Math.Min(1, value)); Update(); }
        }

        public GlassBar(string glyph, double initial, Action<double> changed)
        {
            _changed = changed;
            Height = 42;
            Cursor = Cursors.Hand;
            var track = new Border { CornerRadius = new CornerRadius(21), Background = new SolidColorBrush(Color.FromArgb(110, 20, 22, 26)), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), BorderThickness = new Thickness(1) };
            _fill = new Border { CornerRadius = new CornerRadius(21), Background = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Left };
            Children.Add(track);
            Children.Add(_fill);
            Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(24, 28, 36)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(15, 0, 0, 0), IsHitTestVisible = false });
            _value = Math.Max(0, Math.Min(1, initial));
            SizeChanged += (a, b) => Update();
            MouseLeftButtonDown += (a, b) => { CaptureMouse(); Drag(b.GetPosition(this).X); };
            MouseMove += (a, b) => { if (IsMouseCaptured) Drag(b.GetPosition(this).X); };
            MouseLeftButtonUp += (a, b) => ReleaseMouseCapture();
            MouseWheel += (a, b) => { Value = _value + (b.Delta > 0 ? 0.05 : -0.05); _changed(_value); };
        }

        private void Drag(double x)
        {
            Value = (x - 8) / Math.Max(1, ActualWidth - 16);
            _changed(_value);
        }

        private void Update()
        {
            double w = ActualWidth;
            if (w <= 0) return;
            _fill.Width = Math.Max(42, 42 + (w - 42) * _value);
        }
    }

    /// <summary>Luminosite de l'ecran d'un portable (WMI) ; -1 si l'ecran ne la gere pas (PC de bureau).</summary>
    public static class Brightness
    {
        public static double Get()
        {
            try
            {
                using var s = new System.Management.ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                foreach (System.Management.ManagementObject o in s.Get()) return Convert.ToDouble(o["CurrentBrightness"]) / 100.0;
            }
            catch { }
            return -1;
        }

        private static DateTime _last;
        public static void Set(double v)
        {
            if ((DateTime.Now - _last).TotalMilliseconds < 60) return;
            _last = DateTime.Now;
            byte b = (byte)Math.Round(Math.Max(0, Math.Min(1, v)) * 100);
            Task.Run(() =>
            {
                try
                {
                    using var s = new System.Management.ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                    foreach (System.Management.ManagementObject o in s.Get()) o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, b });
                }
                catch { }
            });
        }
    }
}
