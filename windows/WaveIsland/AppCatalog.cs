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
        public string Name, Id, Category;
        public ImageSource Icon;
        internal void SetIcon(ImageSource i) { Icon = i; AppCatalog.RaiseIcon(this); }
    }

    /// <summary>
    /// Liste des applis du menu Demarrer (dossier « shell:AppsFolder » : programmes classiques et applis du Store),
    /// avec leurs icones, chargees en arriere-plan.
    /// </summary>
    public static class AppCatalog
    {
        public static List<AppEntry> Apps { get; private set; } = new List<AppEntry>();
        public static event Action Loaded;
        /// <summary>L'icone d'une appli vient d'etre chargee.</summary>
        public static event Action<AppEntry> IconLoaded;
        internal static void RaiseIcon(AppEntry a) => IconLoaded?.Invoke(a);

        /// <summary>Categories, comme le menu Demarrer de Windows 11.</summary>
        public static readonly string[] Categories =
            { "Productivité", "Créativité", "Réseaux sociaux", "Divertissement", "Jeux", "Utilitaires et outils", "Autres" };

        private static readonly (string cat, string[] words)[] Rules =
        {
            ("Jeux", new[] { "steam", "epic games", "game", "jeu", "xbox", "minecraft", "roblox", "battle.net", "riot", "valorant", "league of", "ubisoft", "ea app", "gog", "fortnite", "solitaire", "itch", "playnite", "rockstar", "mojang", "curseforge", "lunar client", "modrinth", "nvidia app", "geforce" }),
            ("Réseaux sociaux", new[] { "discord", "whatsapp", "telegram", "teams", "messenger", "instagram", "facebook", "tiktok", "twitter", "skype", "zoom", "slack", "signal", "snapchat", "linkedin", "mail", "outlook", "courrier", "thunderbird", "phone link", "lien avec" }),
            ("Créativité", new[] { "photoshop", "illustrator", "premiere", "after effects", "lightroom", "blender", "gimp", "krita", "paint", "clipchamp", "davinci", "obs studio", "audacity", "fl studio", "capcut", "canva", "figma", "unity", "unreal", "godot", "mcreator", "inkscape", "affinity", "photos", "camera", "caméra", "sound recorder", "magnétophone", "designer" }),
            ("Divertissement", new[] { "spotify", "netflix", "prime video", "disney", "vlc", "media player", "lecteur multim", "deezer", "youtube", "twitch", "music", "musique", "films", "movies", "tv", "plex", "apple tv", "crunchyroll" }),
            ("Productivité", new[] { "word", "excel", "powerpoint", "onenote", "office", "notion", "onedrive", "calendar", "calendrier", "to do", "notepad", "bloc-notes", "acrobat", "pdf", "libreoffice", "obsidian", "edge", "chrome", "firefox", "opera", "brave", "vivaldi", "navigateur", "copilot", "chatgpt", "claude", "google drive", "dropbox", "visual studio", "code", "android studio", "intellij", "pycharm", "eclipse" }),
            ("Utilitaires et outils", new[] { "settings", "paramètres", "calculat", "explor", "snipping", "capture", "control panel", "panneau de config", "task manager", "gestionnaire", "7-zip", "winrar", "store", "defender", "sécurité", "security", "clock", "horloge", "météo", "weather", "maps", "cartes", "terminal", "powershell", "command prompt", "invite de commandes", "cmd", "python", "git", "node", "docker", "anaconda", "cmake", "java", "wsl", "ubuntu", "registry", "registre", "disk", "disque", "device manager", "périphériques", "remote desktop", "bureau à distance", "magnifier", "loupe", "narrator", "narrateur", "keyboard", "clavier", "character map", "table des caract", "services", "event viewer", "observateur", "performance", "system", "système", "backup", "sauvegarde", "update", "help", "aide", "tips", "astuces", "feedback", "commentaires", "get started", "prise en main", "windows tools", "outils windows", "sticky", "pense-bêtes", "recorder", "voice", "voix", "quick assist", "assistance rapide", "nvidia", "amd", "intel", "realtek", "lenovo", "msi", "asus", "dell", "hp ", "logitech", "razer", "corsair", "wave island" }),
        };

        public static string CategoryOf(AppEntry a)
        {
            string n = (a.Name + " " + a.Id).ToLowerInvariant();
            foreach (var (cat, words) in Rules)
                foreach (var w in words)
                    if (n.Contains(w)) return cat;
            return "Autres";
        }

        /// <summary>Applis epinglees (choisies par l'utilisateur, sinon quelques applis courantes).</summary>
        public static List<AppEntry> Pinned()
        {
            var s = Settings.Current;
            if (!s.PinnedInit && Apps.Count > 0)
            {
                string[] defaults =
                {
                    "Microsoft Edge", "Google Chrome", "Firefox", "Explorateur de fichiers", "File Explorer", "Paramètres", "Settings",
                    "Microsoft Store", "Calculatrice", "Calculator", "Photos", "Spotify", "Discord", "WhatsApp", "Steam", "Word", "Excel",
                    "Bloc-notes", "Notepad", "Outil Capture", "Snipping Tool", "Terminal", "Visual Studio Code", "Paint", "Horloge", "Clock"
                };
                foreach (var d in defaults)
                {
                    var a = Apps.FirstOrDefault(x => string.Equals(x.Name, d, StringComparison.OrdinalIgnoreCase));
                    if (a != null && !s.Pinned.Contains(a.Id) && s.Pinned.Count < 14) s.Pinned.Add(a.Id);
                }
                s.PinnedInit = true;
                s.Save();
            }
            var byId = Apps.ToDictionary(a => a.Id, a => a, StringComparer.OrdinalIgnoreCase);
            return s.Pinned.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }

        public static void TogglePin(AppEntry a)
        {
            var p = Settings.Current.Pinned;
            if (p.Contains(a.Id)) p.Remove(a.Id); else p.Add(a.Id);
            Settings.Current.Save();
        }
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
                    var e = new AppEntry { Name = name, Id = id };
                    e.Category = CategoryOf(e);
                    list.Add(e);
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

        public static ImageSource LoadIcon(string id, int size) => LoadShellIcon("shell:AppsFolder\\" + id, size);

        /// <summary>Icone de n'importe quel element (fichier, raccourci, appli), comme dans l'Explorateur.</summary>
        public static ImageSource LoadShellIcon(string parsingName, int size)
        {
            IShellItemImageFactory f = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f) != 0 || f == null) return null;
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
