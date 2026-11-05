using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WaveIsland
{
    /// <summary>
    /// Mode tablette : glisser depuis le bord gauche = retour en arriere (facon iPad).
    /// La page actuelle suit le doigt vers la droite ; en dessous, la page apparait peu a peu (assombrie, un peu en retrait).
    /// Lache assez loin (ou d'un geste rapide) : la page part a droite et l'appli revient a la page precedente
    /// (touche « Precedent » de Windows : navigateurs, Explorateur, Parametres...). Sinon elle revient en place.
    /// </summary>
    public sealed class BackEdge : Window
    {
        private const double EW = 12;
        private IntPtr _hwnd;
        private BackMotion _m;
        private Point _down;
        private bool _active;

        public BackEdge()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));   // presque invisible mais touchable
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            SourceInitialized += (a, b) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            };
            TouchDown += (a, e) => { CaptureTouch(e.TouchDevice); Down(Screen(e.GetTouchPoint(this).Position)); e.Handled = true; };
            TouchMove += (a, e) => { Move(Screen(e.GetTouchPoint(this).Position)); e.Handled = true; };
            TouchUp += (a, e) => { ReleaseTouchCapture(e.TouchDevice); Up(Screen(e.GetTouchPoint(this).Position)); e.Handled = true; };
            MouseLeftButtonDown += (a, e) => { if (e.StylusDevice != null) return; CaptureMouse(); Down(Screen(e.GetPosition(this))); };
            MouseMove += (a, e) => { if (IsMouseCaptured) Move(Screen(e.GetPosition(this))); };
            MouseLeftButtonUp += (a, e) => { if (!IsMouseCaptured) return; ReleaseMouseCapture(); Up(Screen(e.GetPosition(this))); };
        }

        private Point Screen(Point p) => new Point(Left + p.X, Top + p.Y);

        public void Start()
        {
            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            Show();
            double k = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Width = EW;
            Left = sb.Left / k;
            // Pas tout en bas (la petite barre) ni tout en haut (l'ilot)
            Top = sb.Top / k + 60;
            Height = sb.Height / k - 60 - 40;
            if (_hwnd != IntPtr.Zero)
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void Stop() => Hide();

        private void Down(Point p)
        {
            _down = p;
            _active = false;
            _m = null;
        }

        private void Move(Point p)
        {
            double dx = p.X - _down.X;
            if (!_active)
            {
                if (dx < 14 || Math.Abs(p.Y - _down.Y) > dx) return;
                var app = TabletShell.AppInFront();
                if (app == IntPtr.Zero || AppMotion.Current != null) return;
                _active = true;
                _m = new BackMotion(app);
                Log.I("Mode tablette : geste retour");
            }
            _m?.Follow(dx);
        }

        private void Up(Point p)
        {
            if (!_active || _m == null) return;
            _active = false;
            _m.Release(p.X - _down.X);
            _m = null;
        }
    }

    /// <summary>L'animation du retour (fenetre plein ecran le temps du geste).</summary>
    public sealed class BackMotion : Window
    {
        private readonly IntPtr _app;
        private readonly Border _page, _under, _arrow;
        private readonly Border _veil;
        private readonly SpringValue _x = new SpringValue(0);
        private double _sw, _sh;
        private DateTime _lastT = DateTime.Now;
        private double _lastX, _vel;
        private bool _ticking, _done;
        private readonly FrameGate _gate = new FrameGate();

        public BackMotion(IntPtr app)
        {
            _app = app;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Background = Brushes.Black;
            var shot = AppMotion.Fresh(app);
            var brush = shot != null ? (Brush)new ImageBrush(shot) { Stretch = Stretch.Fill } : new SolidColorBrush(Color.FromRgb(40, 44, 54));
            var canvas = new Canvas { ClipToBounds = true };
            // En dessous : la page qui « revient » (un peu en retrait et assombrie)
            _under = new Border { Background = brush, IsHitTestVisible = false };
            _veil = new Border { Background = Brushes.Black, IsHitTestVisible = false };
            // Dessus : la page actuelle, avec une ombre sur son bord gauche
            _page = new Border
            {
                Background = brush, IsHitTestVisible = false, CornerRadius = new CornerRadius(14),
                Effect = Frames.LowEnd ? null : new DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Opacity = 0.45 }
            };
            // La fleche de retour, en verre, qui grossit avec le geste
            _arrow = new Border
            {
                Width = 46, Height = 46, CornerRadius = new CornerRadius(23), IsHitTestVisible = false,
                Background = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20,
                    Foreground = new SolidColorBrush(Color.FromRgb(20, 22, 28)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            canvas.Children.Add(_under);
            canvas.Children.Add(_veil);
            canvas.Children.Add(_page);
            canvas.Children.Add(_arrow);
            Content = canvas;

            var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            double k = App.Island != null ? VisualTreeHelper.GetDpi(App.Island).DpiScaleX : 1;
            _sw = sb.Width / k; _sh = sb.Height / k;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = sb.Left / k; Top = sb.Top / k; Width = _sw; Height = _sh;
            SourceInitialized += (a, b) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            };
            Render();
            Show();
            App.Island?.BringToTop();
        }

        public void Follow(double dx)
        {
            var now = DateTime.Now;
            double dt = Math.Max(0.001, (now - _lastT).TotalSeconds);
            double x = Math.Max(0, dx);
            _vel = _vel * 0.6 + (x - _lastX) / dt * 0.4;
            _lastX = x; _lastT = now;
            _x.Set(x);
            Render();
        }

        public void Release(double dx)
        {
            bool go = dx > _sw * 0.3 || _vel > 900;
            if (go)
            {
                Log.I("Mode tablette : retour a la page precedente");
                _done = true;
                _x.AnimateTo(_sw + 40, new Spring(0.85, 0.3));
                // L'appli recoit « Precedent » pendant que la page part
                Native.SetForegroundWindow(_app);
                Native.keybd_event(0xA6 /* VK_BROWSER_BACK */, 0, 0, UIntPtr.Zero);
                Native.keybd_event(0xA6, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            else _x.AnimateTo(0, new Spring(0.7, 0.42));
            Kick();
        }

        private void Kick()
        {
            if (_ticking) return;
            _ticking = true;
            CompositionTarget.Rendering += OnFrame;
        }

        private void OnFrame(object s, EventArgs e)
        {
            if (!_gate.Ready()) return;
            _x.Tick();
            Render();
            if (_x.Moving) return;
            CompositionTarget.Rendering -= OnFrame;
            _ticking = false;
            if (_done) FadeOut(); else Close();
        }

        /// <summary>La page precedente (la vraie appli, deja revenue en arriere) apparait en fondu.</summary>
        private void FadeOut()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            double o = 1;
            var start = DateTime.Now;
            t.Tick += (a, b) =>
            {
                double p = (DateTime.Now - start).TotalMilliseconds / 200;
                o = 1 - Math.Min(1, p);
                Opacity = o;
                if (p >= 1) { t.Stop(); Close(); }
            };
            // Petite attente : l'appli a le temps d'afficher la page precedente
            var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            wait.Tick += (a, b) => { wait.Stop(); t.Start(); };
            wait.Start();
        }

        private void Render()
        {
            double x = _x.Value;
            double p = Math.Max(0, Math.Min(1, x / _sw));
            // Page du dessous : part de 30 % a gauche et revient a sa place (parallaxe), de moins en moins sombre
            double ux = -_sw * 0.3 * (1 - p);
            Canvas.SetLeft(_under, ux); Canvas.SetTop(_under, 0);
            _under.Width = _sw; _under.Height = _sh;
            _veil.Width = _sw; _veil.Height = _sh;
            _veil.Opacity = 0.45 * (1 - p);
            // Page actuelle : suit le doigt
            Canvas.SetLeft(_page, x); Canvas.SetTop(_page, 0);
            _page.Width = _sw; _page.Height = _sh;
            _page.CornerRadius = new CornerRadius(Math.Min(14, x * 0.2));
            // Fleche : sort du bord gauche et grossit
            double a = Math.Min(1, x / (_sw * 0.3));
            Canvas.SetLeft(_arrow, Math.Min(x, _sw * 0.3) * 0.5 - 23);
            Canvas.SetTop(_arrow, _sh / 2 - 23);
            _arrow.Opacity = a;
            _arrow.RenderTransformOrigin = new Point(0.5, 0.5);
            _arrow.RenderTransform = new ScaleTransform(0.6 + 0.4 * a, 0.6 + 0.4 * a);
        }
    }
}
