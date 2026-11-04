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
    public sealed class TaskbarPanel : Window
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

        private void BuildQuick()
        {
            var grid = new UniformGrid3();
            grid.Add(Toggle("", "Wi-Fi", () => _wifi?.State == Windows.Devices.Radios.RadioState.On, () => FlipRadio(_wifi), () => Shortcuts.Start("ms-availablenetworks:")));
            grid.Add(Toggle("", "Bluetooth", () => _bt?.State == Windows.Devices.Radios.RadioState.On, () => FlipRadio(_bt), () => Shortcuts.Start("ms-settings:bluetooth")));
            grid.Add(Toggle("", "Mode avion", () => _wifi != null && _wifi.State != Windows.Devices.Radios.RadioState.On && (_bt == null || _bt.State != Windows.Devices.Radios.RadioState.On),
                Airplane, () => Shortcuts.Start("ms-settings:network-airplanemode")));
            grid.Add(Toggle("", "Wave Drop", () => Settings.Current.DropEnabled, () =>
            {
                var s = Settings.Current; s.DropEnabled = !s.DropEnabled; s.Save();
                if (s.DropEnabled) App.Drop?.Start(); else App.Drop?.Stop();
            }, () => App.OpenSettings()));
            grid.Add(Toggle("", "Économie", () => false, () => Shortcuts.Start("ms-settings:batterysaver"), null));
            grid.Add(Toggle("", "Projeter", () => false, () => { CloseAnimated(); WinKey(0x4B); }, null));
            _col.Children.Add(grid.Panel);

            // Luminosite (ecran de portable) et volume
            var vol = App.Volume?.Read() ?? (0.5f, false);
            var volSlider = new GlassBar("", vol.level, v => App.Volume?.SetLevel((float)v));
            volSlider.Margin = new Thickness(0, 14, 0, 0);
            var briSlider = new GlassBar("", 0.5, v => Brightness.Set(v)) { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
            _col.Children.Add(briSlider);
            _col.Children.Add(volSlider);
            Task.Run(() => Brightness.Get()).ContinueWith(t =>
            {
                if (t.Result >= 0) Dispatcher.Invoke(() => { briSlider.Value = t.Result; briSlider.Visibility = Visibility.Visible; });
            });

            // Batterie + raccourcis
            var bottom = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            bool hasBat = (ps.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryLifePercent <= 1.0f;
            if (hasBat)
            {
                int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
                bool charging = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                bottom.Children.Add(PlateBox(new TextBlock { Text = $"{(char)((charging ? 0xE85A : 0xE850) + Math.Min(10, pct / 10))}  {pct} %", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe UI"), FontSize = 13 }, 15, new Thickness(12, 6, 14, 7)));
            }
            var set = Pill("", "Paramètres", () => { CloseAnimated(); Shortcuts.Start("ms-settings:"); });
            DockPanel.SetDock(set, Dock.Right);
            bottom.Children.Add(set);
            _col.Children.Add(bottom);

            LoadRadios();
        }

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

        private Border Toggle(string glyph, string label, Func<bool> state, Action click, Action longClick)
        {
            var icon = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center };
            var text = new TextBlock { Text = label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            st.Children.Add(icon);
            st.Children.Add(text);
            var box = new Border
            {
                Height = 74, CornerRadius = new CornerRadius(18), Background = Plate, BorderBrush = PlateLine, BorderThickness = new Thickness(1),
                Child = st, Cursor = Cursors.Hand, Margin = new Thickness(4), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1)
            };
            box.MouseLeftButtonDown += (a, b) => Bounce(box, 0.9);
            box.MouseLeftButtonUp += (a, b) => { Bounce(box, 1); click(); Dispatcher.BeginInvoke(new Action(RefreshToggles), System.Windows.Threading.DispatcherPriority.Background); };
            if (longClick != null) box.MouseRightButtonUp += (a, b) => { CloseAnimated(); longClick(); };
            box.ToolTip = longClick != null ? "Clic droit : plus de réglages" : null;
            _toggles.Add((box, icon, text, state));
            return box;
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
            if (list.Count > 0)
            {
                var clear = Pill(null, "Tout effacer", () => { App.NoteHistory.Clear(); CloseAnimated(); });
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
            var cal = new StackPanel();
            cal.Children.Add(T(fr.TextInfo.ToTitleCase(now.ToString("dddd d MMMM", fr)), 15, FontWeights.SemiBold));
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var d in new[] { "lu", "ma", "me", "je", "ve", "sa", "di" })
                grid.Children.Add(new TextBlock { Text = d, FontSize = 11.5, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
            var first = new DateTime(now.Year, now.Month, 1);
            int lead = ((int)first.DayOfWeek + 6) % 7;
            for (int i = 0; i < lead; i++) grid.Children.Add(new TextBlock());
            for (int day = 1; day <= DateTime.DaysInMonth(now.Year, now.Month); day++)
            {
                bool today = day == now.Day;
                var tb = new TextBlock { Text = day.ToString(), FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = today ? OnInk : Brushes.White, FontWeight = today ? FontWeights.SemiBold : FontWeights.Normal };
                grid.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = today ? On : Brushes.Transparent, Child = tb, Margin = new Thickness(1) });
            }
            cal.Children.Add(grid);
            _col.Children.Add(PlateBox(cal, 20, new Thickness(14, 12, 14, 12)).With(b => b.Margin = new Thickness(0, 14, 0, 0)));
        }

        private FrameworkElement NoteCard(IslandNote n, DateTime when)
        {
            var st = new StackPanel();
            var top = new DockPanel { LastChildFill = true };
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
            card.MouseLeftButtonUp += (a, b) =>
            {
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
