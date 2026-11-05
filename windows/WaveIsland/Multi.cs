using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WaveIsland
{
    /// <summary>
    /// Lot 3 : multitache du mode tablette (facon iPad).
    /// - Ecran partage : glisser une icone du dock vers le bord gauche ou droit de l'ecran.
    ///   Les deux applis se partagent l'ecran ; la barre du milieu se fait glisser pour changer les tailles
    ///   (jusqu'au bord : l'autre appli reprend tout l'ecran).
    /// - Slide Over : lacher l'icone au milieu de l'ecran. L'appli flotte a droite, par-dessus les autres ;
    ///   glisser sa poignee vers la droite la range sur le cote, glisser depuis le bord droit la fait revenir.
    /// </summary>
    public static class TabletMulti
    {
        private static IntPtr _left, _right, _slide;
        private static double _ratio = 0.5;
        private static Divider _divider;
        private static SlideHandle _handle;
        private static RightEdge _edge;
        private static bool _slideHidden;
        private static DispatcherTimer _watch;

        /// <summary>Fenetre geree par l'ecran partage ou Slide Over (le mode tablette ne doit pas l'agrandir).</summary>
        public static bool Owns(IntPtr h) => h != IntPtr.Zero && (h == _left || h == _right || h == _slide);

        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Native.RECT r, int size);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Native.POINT p);

        private static System.Drawing.Rectangle Screen => System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        private static double K => App.Island != null ? VisualTreeHelper.GetDpi(App.Island).DpiScaleX : 1;

        /// <summary>Place la partie visible de la fenetre exactement sur ce rectangle (pixels ecran).</summary>
        private static void Place(IntPtr h, int x, int y, int w, int hh, bool topmost = false)
        {
            if (h == IntPtr.Zero) return;
            if (IsZoomed(h) || Native.IsIconic(h)) Native.ShowWindow(h, 9 /* SW_RESTORE */);
            int l = 7, t = 0, r = 7, b = 7;
            if (Native.GetWindowRect(h, out var wr) && DwmGetWindowAttribute(h, 9, out var v, Marshal.SizeOf<Native.RECT>()) == 0)
            { l = v.Left - wr.Left; t = v.Top - wr.Top; r = wr.Right - v.Right; b = wr.Bottom - v.Bottom; }
            Native.SetWindowPos(h, topmost ? Native.HWND_TOPMOST : IntPtr.Zero, x - l, y - t, w + l + r, hh + t + b,
                (uint)((topmost ? 0 : 0x4 /* NOZORDER */) | 0x10 /* NOACTIVATE */));
        }

        // ================== Depuis le dock ==================

        /// <summary>
        /// Icone du dock lachee sur l'ecran. hwnd : fenetre de l'appli (zero = pas ouverte, launch la lance).
        /// </summary>
        public static async void Dropped(Point screenPx, IntPtr hwnd, Action launch)
        {
            var sb = Screen;
            double fx = (screenPx.X - sb.Left) / sb.Width;
            string where = fx < 0.22 ? "left" : fx > 0.78 ? "right" : "slide";
            var front = TabletShell.AppInFront();
            if (hwnd == IntPtr.Zero)
            {
                var before = TaskbarWindow.AppWindows().Select(w => w.h).ToHashSet();
                launch();
                for (int i = 0; i < 40 && hwnd == IntPtr.Zero; i++)
                {
                    await Task.Delay(150);
                    hwnd = TaskbarWindow.AppWindows().Select(w => w.h).FirstOrDefault(h => !before.Contains(h));
                }
                if (hwnd == IntPtr.Zero) return;
            }
            if (where == "slide") { SlideOver(hwnd); return; }
            if (front == IntPtr.Zero || front == hwnd)
            {
                // Rien d'autre a l'ecran : simplement ouvrir l'appli
                if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
                Native.SetForegroundWindow(hwnd);
                return;
            }
            Split(where == "left" ? hwnd : front, where == "left" ? front : hwnd);
        }

        // ================== Ecran partage ==================

        public static void Split(IntPtr left, IntPtr right)
        {
            if (_slide == left || _slide == right) EndSlide(keep: true);
            _left = left; _right = right; _ratio = 0.5;
            Log.I("Mode tablette : ecran partage");
            WaveSound.Play(WaveSound.Kind.Open);
            Layout();
            Native.SetForegroundWindow(right);
            Native.SetForegroundWindow(left);
            _divider ??= new Divider();
            _divider.Show();
            _divider.Place(_ratio);
            Watch();
        }

        private const int Gap = 6;

        private static void Layout()
        {
            var sb = Screen;
            int mid = sb.Left + (int)(sb.Width * _ratio);
            Place(_left, sb.Left, sb.Top, mid - Gap / 2 - sb.Left, sb.Height);
            Place(_right, mid + Gap / 2, sb.Top, sb.Right - mid - Gap / 2, sb.Height);
        }

        /// <summary>La barre du milieu bouge : les deux applis suivent. Au bord : fin de l'ecran partage.</summary>
        internal static void DividerMoved(double ratio, bool released)
        {
            if (!released) { _ratio = Math.Max(0.12, Math.Min(0.88, ratio)); Layout(); return; }
            if (ratio < 0.2) { EndSplit(keep: _right); return; }
            if (ratio > 0.8) { EndSplit(keep: _left); return; }
            // Comme sur iPad : un tiers, la moitie ou deux tiers
            _ratio = ratio < 0.42 ? 1 / 3.0 : ratio > 0.58 ? 2 / 3.0 : 0.5;
            Layout();
            _divider?.Place(_ratio);
        }

        private static void EndSplit(IntPtr keep)
        {
            var other = keep == _left ? _right : _left;
            Log.I("Mode tablette : fin de l'ecran partage");
            _left = _right = IntPtr.Zero;
            _divider?.Hide();
            if (other != IntPtr.Zero && IsWindow(other)) Native.ShowWindow(other, 7 /* SW_SHOWMINNOACTIVE */);
            if (keep != IntPtr.Zero && IsWindow(keep)) { Native.ShowWindow(keep, 3 /* SW_MAXIMIZE */); Native.SetForegroundWindow(keep); }
        }

        // ================== Slide Over ==================

        public static void SlideOver(IntPtr h)
        {
            if (_slide != IntPtr.Zero && _slide != h) EndSlide(keep: false);
            if (h == _left || h == _right) EndSplit(keep: h == _left ? _right : _left);
            _slide = h;
            _slideHidden = false;
            Log.I("Mode tablette : Slide Over");
            WaveSound.Play(WaveSound.Kind.Open);
            PlaceSlide(0);
            Native.SetForegroundWindow(h);
            _handle ??= new SlideHandle();
            _handle.Show();
            _handle.Follow(SlideRect(0));
            _edge ??= new RightEdge();
            _edge.Start();
            Watch();
        }

        private static System.Drawing.Rectangle SlideRect(double hideFrac)
        {
            var sb = Screen;
            int w = Math.Max((int)(380 * K), (int)(sb.Width * 0.3));
            int x = sb.Right - w - (int)(14 * K) + (int)((w + 30 * K) * hideFrac);
            return new System.Drawing.Rectangle(x, sb.Top + (int)(48 * K), w, sb.Height - (int)(130 * K));
        }

        private static void PlaceSlide(double hideFrac)
        {
            var r = SlideRect(hideFrac);
            Place(_slide, r.X, r.Y, r.Width, r.Height, topmost: true);
        }

        /// <summary>Glisser la poignee : la fenetre suit ; lachee vers la droite = rangee sur le cote.</summary>
        internal static void HandleMoved(double dx, bool released)
        {
            if (_slide == IntPtr.Zero) return;
            var r = SlideRect(0);
            double frac = Math.Max(0, dx / (r.Width + 30 * K));
            if (!released) { PlaceSlide(frac); _handle?.Follow(SlideRect(frac)); return; }
            AnimateSlide(frac, frac > 0.3 ? 1 : 0);
        }

        /// <summary>Glisser depuis le bord droit : l'appli rangee revient.</summary>
        internal static void EdgeSwiped()
        {
            if (_slide == IntPtr.Zero || !_slideHidden) return;
            AnimateSlide(1, 0);
        }

        private static async void AnimateSlide(double from, double to)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            for (int i = 1; i <= 14; i++)
            {
                double f = from + (to - from) * ease.Ease(i / 14.0);
                PlaceSlide(f);
                _handle?.Follow(SlideRect(f));
                await Task.Delay(16);
            }
            _slideHidden = to >= 1;
            if (_slideHidden) { _handle?.Hide(); Log.I("Mode tablette : Slide Over range sur le cote"); }
            else { _handle?.Show(); _handle?.Follow(SlideRect(0)); Native.SetForegroundWindow(_slide); }
        }

        private static void EndSlide(bool keep)
        {
            var h = _slide;
            _slide = IntPtr.Zero;
            _handle?.Hide();
            _edge?.Hide();
            if (h == IntPtr.Zero || !IsWindow(h)) return;
            Native.SetWindowPos(h, new IntPtr(-2) /* HWND_NOTOPMOST */, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            if (keep) Native.ShowWindow(h, 3 /* SW_MAXIMIZE */);
        }

        // ================== Surveillance (appli fermee / reduite) ==================

        private static void Watch()
        {
            if (_watch != null) return;
            _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _watch.Tick += (a, b) =>
            {
                bool Gone(IntPtr h) => h != IntPtr.Zero && (!IsWindow(h) || !IsWindowVisible(h) || Native.IsIconic(h));
                if (_left != IntPtr.Zero || _right != IntPtr.Zero)
                {
                    if (Gone(_left)) EndSplit(keep: _right);
                    else if (Gone(_right)) EndSplit(keep: _left);
                    else if (!TabletShell.Active) EndSplit(keep: _left);
                }
                if (_slide != IntPtr.Zero && (!IsWindow(_slide) || !IsWindowVisible(_slide) || !TabletShell.Active)) EndSlide(keep: false);
                if (_left == IntPtr.Zero && _right == IntPtr.Zero && _slide == IntPtr.Zero) { _watch.Stop(); _watch = null; }
            };
            _watch.Start();
        }

        // ================== Petites fenetres (barre du milieu, poignee, bord droit) ==================

        private static void NoActivate(Window w)
        {
            w.SourceInitialized += (a, b) =>
            {
                var h = new WindowInteropHelper(w).Handle;
                Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            };
        }

        private static Border Pill(double w, double h) => new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(Math.Min(w, h) / 2),
            Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };

        /// <summary>La barre du milieu de l'ecran partage.</summary>
        private sealed class Divider : Window
        {
            private double _downX = double.NaN, _startRatio;

            public Divider()
            {
                WindowStyle = WindowStyle.None; AllowsTransparency = true; ResizeMode = ResizeMode.NoResize;
                ShowInTaskbar = false; Topmost = true; ShowActivated = false;
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
                Width = 28; Height = 120;
                Content = Pill(6, 64);
                Cursor = Cursors.SizeWE;
                NoActivate(this);
                MouseLeftButtonDown += (a, e) => { CaptureMouse(); _downX = PointToScreen(e.GetPosition(this)).X; _startRatio = _ratio; };
                MouseMove += (a, e) =>
                {
                    if (!IsMouseCaptured || double.IsNaN(_downX)) return;
                    double x = PointToScreen(e.GetPosition(this)).X;
                    double r = _startRatio + (x - _downX) / Screen.Width;
                    DividerMoved(r, false);
                    Place(Math.Max(0.12, Math.Min(0.88, r)));
                };
                MouseLeftButtonUp += (a, e) =>
                {
                    if (!IsMouseCaptured) return;
                    ReleaseMouseCapture();
                    double x = PointToScreen(e.GetPosition(this)).X;
                    DividerMoved(_startRatio + (x - _downX) / Screen.Width, true);
                    _downX = double.NaN;
                };
            }

            public void Place(double ratio)
            {
                var sb = Screen;
                double k = K;
                Left = (sb.Left + sb.Width * ratio) / k - Width / 2;
                Top = (sb.Top + sb.Height / 2.0) / k - Height / 2;
            }
        }

        /// <summary>La poignee au-dessus de l'appli Slide Over.</summary>
        private sealed class SlideHandle : Window
        {
            private double _downX = double.NaN;

            public SlideHandle()
            {
                WindowStyle = WindowStyle.None; AllowsTransparency = true; ResizeMode = ResizeMode.NoResize;
                ShowInTaskbar = false; Topmost = true; ShowActivated = false;
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
                Width = 140; Height = 26;
                Content = Pill(70, 6);
                Cursor = Cursors.SizeAll;
                NoActivate(this);
                MouseLeftButtonDown += (a, e) => { CaptureMouse(); _downX = PointToScreen(e.GetPosition(this)).X; };
                MouseMove += (a, e) => { if (IsMouseCaptured && !double.IsNaN(_downX)) HandleMoved(PointToScreen(e.GetPosition(this)).X - _downX, false); };
                MouseLeftButtonUp += (a, e) =>
                {
                    if (!IsMouseCaptured) return;
                    ReleaseMouseCapture();
                    double dx = PointToScreen(e.GetPosition(this)).X - _downX;
                    _downX = double.NaN;
                    HandleMoved(dx, true);
                };
            }

            public void Follow(System.Drawing.Rectangle r)
            {
                double k = K;
                Left = (r.X + r.Width / 2.0) / k - Width / 2;
                Top = r.Y / k - Height - 2;
                Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }

        /// <summary>Bande invisible sur le bord droit : glisser vers la gauche fait revenir l'appli Slide Over.</summary>
        private sealed class RightEdge : Window
        {
            private double _downX = double.NaN;

            public RightEdge()
            {
                WindowStyle = WindowStyle.None; AllowsTransparency = true; ResizeMode = ResizeMode.NoResize;
                ShowInTaskbar = false; Topmost = true; ShowActivated = false;
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
                Width = 12;
                NoActivate(this);
                MouseLeftButtonDown += (a, e) => { CaptureMouse(); _downX = PointToScreen(e.GetPosition(this)).X; };
                MouseMove += (a, e) =>
                {
                    if (!IsMouseCaptured || double.IsNaN(_downX)) return;
                    if (_downX - PointToScreen(e.GetPosition(this)).X > 20 * K) { _downX = double.NaN; ReleaseMouseCapture(); EdgeSwiped(); }
                };
                MouseLeftButtonUp += (a, e) => { if (IsMouseCaptured) ReleaseMouseCapture(); _downX = double.NaN; };
            }

            public void Start()
            {
                var sb = Screen;
                Show();
                double k = K;
                Left = sb.Right / k - Width;
                Top = sb.Top / k + 80;
                Height = sb.Height / k - 160;
            }
        }
    }
}
