using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WaveIsland
{
    /// <summary>
    /// Copie la zone de l'ecran situee derriere une fenetre dans une image WPF.
    /// La fenetre est exclue de la capture (SetWindowDisplayAffinity), elle ne se voit donc pas elle-meme.
    /// Optimisations : on ne copie que la partie utile (autour de l'ilot), et l'image n'est envoyee
    /// a la carte graphique que si le fond a vraiment change.
    /// </summary>
    public sealed class ScreenCapture : IDisposable
    {
        private IntPtr _memDc, _dib, _old, _bits;
        private int _w, _h;
        public WriteableBitmap Bitmap { get; private set; }
        private ulong _lastHash;
        private Int32Rect _lastSub;
        /// <summary>Luminosite moyenne de la derniere capture (0 = noir, 1 = blanc).</summary>
        public double Luma { get; private set; } = 0.3;

        /// <summary>Capture le rectangle (pixels physiques). Renvoie true si l'image a change.</summary>
        public bool Grab(int x, int y, int w, int h) => Grab(x, y, w, h, new Int32Rect(0, 0, w, h));

        /// <summary>Capture seulement la partie « sub » (relative au rectangle) de la zone x, y, w, h.</summary>
        public unsafe bool Grab(int x, int y, int w, int h, Int32Rect sub)
        {
            if (w <= 0 || h <= 0) return false;
            if (w != _w || h != _h) Allocate(w, h);
            int sx = Math.Max(0, Math.Min(w, sub.X)), sy = Math.Max(0, Math.Min(h, sub.Y));
            int sw = Math.Max(0, Math.Min(w - sx, sub.Width)), sh = Math.Max(0, Math.Min(h - sy, sub.Height));
            if (sw == 0 || sh == 0) return false;

            IntPtr screen = Native.GetDC(IntPtr.Zero);
            try { Native.BitBlt(_memDc, sx, sy, sw, sh, screen, x + sx, y + sy, Native.SRCCOPY); }
            finally { Native.ReleaseDC(IntPtr.Zero, screen); }

            // Empreinte rapide (1 pixel sur 7) : si rien n'a change, on ne renvoie pas l'image
            uint* p = (uint*)_bits;
            ulong hash = 1469598103934665603UL ^ (ulong)(sx * 73856093 ^ sy * 19349663 ^ sw * 83492791 ^ sh);
            for (int row = sy; row < sy + sh; row += 2)
            {
                uint* line = p + row * w;
                for (int i = sx + (row % 7); i < sx + sw; i += 7) { hash ^= line[i]; hash *= 1099511628211UL; }
            }
            if (hash == _lastHash && sub.Equals(_lastSub)) return false;
            _lastHash = hash;
            _lastSub = sub;

            // Luminosite moyenne (pour choisir un texte clair ou fonce, comme sur iPhone)
            double sum = 0; int count = 0;
            for (int row = sy; row < sy + sh; row += 4)
            {
                uint* line = p + row * w;
                for (int i = sx; i < sx + sw; i += 9)
                {
                    uint c = line[i];
                    sum += 0.114 * (c & 0xFF) + 0.587 * ((c >> 8) & 0xFF) + 0.299 * ((c >> 16) & 0xFF);
                    count++;
                }
            }
            if (count > 0) Luma = sum / count / 255.0;

            // Les pixels captures sont opaques : on force l'alpha a 255
            for (int row = sy; row < sy + sh; row++)
            {
                uint* line = p + row * w;
                for (int i = sx; i < sx + sw; i++) line[i] |= 0xFF000000;
            }
            int stride = w * 4;
            IntPtr start = _bits + (sy * w + sx) * 4;
            Bitmap.WritePixels(new Int32Rect(sx, sy, sw, sh), start, (sh - 1) * stride + sw * 4, stride);
            return true;
        }

        private void Allocate(int w, int h)
        {
            Free();
            _w = w; _h = h;
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            _memDc = Native.CreateCompatibleDC(screen);
            Native.ReleaseDC(IntPtr.Zero, screen);
            var bmi = new Native.BITMAPINFOHEADER
            {
                biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0
            };
            _dib = Native.CreateDIBSection(_memDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
            _old = Native.SelectObject(_memDc, _dib);
            Bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            _lastHash = 0;
        }

        private void Free()
        {
            if (_memDc != IntPtr.Zero)
            {
                Native.SelectObject(_memDc, _old);
                Native.DeleteObject(_dib);
                Native.DeleteDC(_memDc);
            }
            _memDc = _dib = _old = _bits = IntPtr.Zero;
        }

        public void Dispose() => Free();
    }
}
