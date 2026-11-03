using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace WaveIsland
{
    /// <summary>
    /// Fond « verre liquide » pour une fenetre entiere (les reglages) : ce qu'il y a derriere la fenetre
    /// est vu a travers le verre, deforme sur les bords comme une goutte d'eau. Pas de flou.
    /// </summary>
    public sealed class GlassBackdrop
    {
        public readonly Image Image = new Image { Stretch = Stretch.Fill };
        private readonly GlassEffect _fx = new GlassEffect();
        private readonly ScreenCapture _cap = new ScreenCapture();
        private readonly Window _w;
        private readonly double _radius, _tint;
        private DispatcherTimer _timer;
        private IntPtr _hwnd;
        private bool _excluded;

        public GlassBackdrop(Window w, double radius, double tint)
        {
            _w = w;
            _radius = radius;
            _tint = tint;
            Image.Effect = _fx;
            w.SourceInitialized += (a, b) => Setup();
            w.SizeChanged += (a, b) => Update();
            Image.SizeChanged += (a, b) => Update();
            w.Closed += (a, b) => { _timer?.Stop(); _cap.Dispose(); };
        }

        private void Setup()
        {
            _hwnd = new WindowInteropHelper(_w).Handle;
            _excluded = Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
            if (!_excluded || !Settings.Current.Refraction)
            {
                var px = new byte[] { 24, 20, 18, 255 };
                var flat = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, px, 4);
                flat.Freeze();
                Image.Source = flat;
            }
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (a, b) => Grab();
            _timer.Start();
            Update();
        }

        private void Grab()
        {
            if (!_excluded || !Settings.Current.Refraction || _w.WindowState == WindowState.Minimized) return;
            // Zone interieure de la fenetre (sans la barre de titre ni les bords)
            if (!Native.GetClientRect(_hwnd, out var c)) return;
            var o = new Native.POINT();
            if (!Native.ClientToScreen(_hwnd, ref o)) return;
            try
            {
                if (_cap.Grab(o.X, o.Y, c.Right - c.Left, c.Bottom - c.Top) && Image.Source != _cap.Bitmap)
                    Image.Source = _cap.Bitmap;
            }
            catch { }
        }

        private void Update()
        {
            double k = VisualTreeHelper.GetDpi(_w).DpiScaleX;
            double W = Image.ActualWidth * k, H = Image.ActualHeight * k;
            if (W < 2 || H < 2) return;
            double rad = _radius * k, mag = 0.985, strength = 20 * k;
            _fx.Geo = new Point4D(W / 2, H / 2, W / 2 - rad, H / 2 - rad);
            _fx.Sizes = new Point4D(W, H, rad, 1.0 / (30 * k));
            _fx.Bend = new Point4D(strength / W, strength / H, 0.5 * (1 - mag), 0.5 * (1 - mag));
            _fx.Tint = new Point4D(0.05, 0.06, 0.07, _tint);
            _fx.Disp = new Point4D(1.12, 0.88, 0, 0);
            _fx.Light = new Point4D(-0.5, -0.866, 1, mag);
        }
    }
}
