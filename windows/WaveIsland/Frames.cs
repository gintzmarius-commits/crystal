using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace WaveIsland
{
    /// <summary>
    /// Nombre d'images par seconde choisi (30 a 120) : applique aux animations de l'ilot, du menu Windows,
    /// des fenetres et au rafraichissement du fond vu a travers le verre.
    /// </summary>
    public static class Frames
    {
        /// <summary>Petit PC (4 coeurs ou moins, ou processeur basse consommation) : on allege le verre.</summary>
        public static readonly bool LowEnd = DetectLowEnd();

        private static bool DetectLowEnd()
        {
            try
            {
                if (Environment.ProcessorCount <= 4) return true;
                string cpu = (Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "") as string ?? "").ToLowerInvariant();
                foreach (var w in new[] { "celeron", "pentium", "atom", " n100", " n200", " n50", " n95", " n97", "i3-n", " n300", "athlon" })
                    if (cpu.Contains(w)) return true;
            }
            catch { }
            return false;
        }

        public static int Fps => Math.Max(30, Math.Min(120, Settings.Current.Fps));
        public static TimeSpan Interval => TimeSpan.FromMilliseconds(1000.0 / Fps);

        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);

        /// <summary>Au demarrage : cadence de WPF et minuteurs precis a la milliseconde.</summary>
        public static void Init()
        {
            try { Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = Fps }); } catch { }
            try { timeBeginPeriod(1); } catch { }
        }

        // ---------- Frequence de l'ecran ----------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);

        /// <summary>Frequence de l'ecran principal en Hz (0 si inconnue).</summary>
        public static int ScreenHz()
        {
            try
            {
                var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                return EnumDisplaySettings(null, -1, ref dm) ? dm.dmDisplayFrequency : 0;
            }
            catch { return 0; }
        }
    }

    /// <summary>Laisse passer au plus « Fps » images par seconde (pour les animations calculees a chaque image).</summary>
    public sealed class FrameGate
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _last = -1;

        public bool Ready()
        {
            double now = _clock.Elapsed.TotalMilliseconds, step = 1000.0 / Frames.Fps;
            if (_last >= 0 && now - _last < step - 1.5) return false;
            _last = now;
            return true;
        }
    }
}
