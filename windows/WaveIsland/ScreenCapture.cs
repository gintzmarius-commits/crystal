using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WaveIsland
{
    /// <summary>
    /// Copie la zone de l'ecran situee derriere l'ilot dans une image WPF.
    /// La fenetre de l'ilot est exclue de la capture (SetWindowDisplayAffinity), elle ne se voit donc pas elle-meme.
    /// Optimisation : l'image n'est envoyee a la carte graphique que si le fond a change.
    /// </summary>
    public sealed class ScreenCapture : IDisposable
    {
        private IntPtr _memDc, _dib, _old, _bits;
        private int _w, _h;
        public WriteableBitmap Bitmap { get; private set; }
        private ulong _lastHash;

        /// <summary>Capture le rectangle (pixels physiques). Renvoie true si l'image a change.</summary>
        public unsafe bool Grab(int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0) return false;
            if (w != _w || h != _h) Allocate(w, h);
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            try
            {
                Native.BitBlt(_memDc, 0, 0, w, h, screen, x, y, Native.SRCCOPY);
            }
            finally { Native.ReleaseDC(IntPtr.Zero, screen); }

            // Empreinte rapide (1 pixel sur 7) : si rien n'a change, on ne renvoie pas l'image
            uint* p = (uint*)_bits;
            ulong hash = 1469598103934665603UL;
            int n = w * h;
            for (int i = 0; i < n; i += 7) { hash ^= p[i]; hash *= 1099511628211UL; }
            if (hash == _lastHash) return false;
            _lastHash = hash;

            // Les pixels captures sont opaques : on force l'alpha a 255
            for (int i = 0; i < n; i++) p[i] |= 0xFF000000;
            Bitmap.WritePixels(new Int32Rect(0, 0, w, h), _bits, w * h * 4, w * 4);
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
