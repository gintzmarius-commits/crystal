using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WaveIsland
{
    /// <summary>Une appli installee (la meme liste que le menu Demarrer de Windows).</summary>
    public class AppEntry
    {
        public string Name, Id;
        public ImageSource Icon;
        public event Action IconReady;
        internal void SetIcon(ImageSource i) { Icon = i; IconReady?.Invoke(); }
    }

    /// <summary>
    /// Liste des applis du menu Demarrer (dossier « shell:AppsFolder » : programmes classiques et applis du Store),
    /// avec leurs icones, chargees en arriere-plan.
    /// </summary>
    public static class AppCatalog
    {
        public static List<AppEntry> Apps { get; private set; } = new List<AppEntry>();
        public static event Action Loaded;
        private static DateTime _loadedAt = DateTime.MinValue;
        private static bool _loading;
        private static Dispatcher _ui;
        private static readonly Dictionary<string, ImageSource> IconCache = new Dictionary<string, ImageSource>();

        public static void Refresh(Dispatcher ui, bool force = false)
        {
            _ui = ui;
            if (_loading || (!force && (DateTime.Now - _loadedAt).TotalMinutes < 5 && Apps.Count > 0)) return;
            _loading = true;
            var t = new Thread(Load) { IsBackground = true, Name = "applis" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        private static void Load()
        {
            var list = new List<AppEntry>();
            var sw = Stopwatch.StartNew();
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (dynamic item in folder.Items())
                {
                    string name = item.Name, id = item.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                    string low = name.ToLowerInvariant();
                    if (low.Contains("uninstall") || low.Contains("désinstall") || low.Contains("desinstall")) continue;
                    if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(name)) continue;
                    list.Add(new AppEntry { Name = name, Id = id });
                }
                Marshal.FinalReleaseComObject(folder);
                Marshal.FinalReleaseComObject(shell);
            }
            catch (Exception e) { Log.I("Liste des applis : " + e.Message); }
            list.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase));
            Log.I($"Liste des applis : {list.Count} applis ({sw.ElapsedMilliseconds} ms)");

            // Les icones deja connues tout de suite
            lock (IconCache)
                foreach (var a in list) if (IconCache.TryGetValue(a.Id, out var ic)) a.Icon = ic;

            _ui.BeginInvoke(new Action(() =>
            {
                Apps = list;
                _loadedAt = DateTime.Now;
                _loading = false;
                Loaded?.Invoke();
            }));

            // Puis les autres icones, une par une
            foreach (var a in list)
            {
                if (a.Icon != null) continue;
                var icon = LoadIcon(a.Id, 64);
                if (icon == null) continue;
                lock (IconCache) IconCache[a.Id] = icon;
                var entry = a;
                _ui.BeginInvoke(new Action(() => entry.SetIcon(icon)), DispatcherPriority.Background);
            }
        }

        public static void Launch(AppEntry a)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + a.Id) { UseShellExecute = true });
                var counts = Settings.Current.LaunchCounts;
                counts[a.Id] = (counts.TryGetValue(a.Id, out int n) ? n : 0) + 1;
                Settings.Current.Save();
                Log.I("Appli lancee : " + a.Name);
            }
            catch (Exception e) { Log.I("Lancement impossible : " + e.Message); }
        }

        /// <summary>Les applis les plus ouvertes depuis l'ilot.</summary>
        public static List<AppEntry> Favorites(int n)
        {
            var counts = Settings.Current.LaunchCounts;
            return Apps.Where(a => counts.ContainsKey(a.Id)).OrderByDescending(a => counts[a.Id]).Take(n).ToList();
        }

        public static bool Matches(AppEntry a, string query) =>
            string.IsNullOrWhiteSpace(query) ||
            CultureInfo.CurrentCulture.CompareInfo.IndexOf(a.Name, query.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

        // ---------- Icones (IShellItemImageFactory) ----------

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref Native.BITMAPINFOHEADER bmi, uint usage);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }

        [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bmp);

        public static ImageSource LoadIcon(string id, int size)
        {
            IShellItemImageFactory f = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                if (SHCreateItemFromParsingName("shell:AppsFolder\\" + id, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f) != 0 || f == null) return null;
                // 0x4 = icone seule (pas de miniature), 0x1 = un peu plus grand accepte
                if (f.GetImage(new SIZE { cx = size, cy = size }, 0x4 | 0x1, out hbm) != 0 || hbm == IntPtr.Zero) return null;
                GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bm);
                int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
                if (w <= 0 || h <= 0) return null;
                var bmi = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                var px = new byte[w * h * 4];
                IntPtr dc = Native.CreateCompatibleDC(IntPtr.Zero);
                try { GetDIBits(dc, hbm, 0, (uint)h, px, ref bmi, 0); }
                finally { Native.DeleteDC(dc); }
                bool anyAlpha = false;
                for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
                if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
                var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally
            {
                if (hbm != IntPtr.Zero) Native.DeleteObject(hbm);
                if (f != null) Marshal.ReleaseComObject(f);
            }
        }
    }
}
