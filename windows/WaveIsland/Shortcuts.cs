using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WaveIsland
{
    /// <summary>Raccourcis du menu de l'ilot : actions integrees ou applis (« app:chemin »).</summary>
    public static class Shortcuts
    {
        public class Item
        {
            public string Id, Label, Glyph;
            public ImageSource Icon;
        }

        public static readonly (string id, string label, string glyph)[] Actions =
        {
            ("action:explorer", "Fichiers", ""),
            ("action:settings", "Paramètres", ""),
            ("action:calc", "Calculatrice", ""),
            ("action:snip", "Capture", ""),
            ("action:playpause", "Lecture", ""),
            ("action:mute", "Muet", ""),
            ("action:taskmgr", "Tâches", ""),
            ("action:lock", "Verrouiller", ""),
        };

        public static Item Resolve(string id)
        {
            foreach (var a in Actions)
                if (a.id == id) return new Item { Id = id, Label = a.label, Glyph = a.glyph };
            if (id.StartsWith("app:"))
            {
                string path = id.Substring(4);
                if (!File.Exists(path)) return null;
                return new Item { Id = id, Label = Path.GetFileNameWithoutExtension(path), Icon = IconOf(path) };
            }
            return null;
        }

        public static ImageSource IconOf(string path)
        {
            try
            {
                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (ico == null) return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
        }

        public static void Run(string id, VolumeService volume)
        {
            Log.I("Raccourci : " + id);
            try
            {
                switch (id)
                {
                    case "action:explorer": Start("explorer.exe"); break;
                    case "action:settings": Start("ms-settings:"); break;
                    case "action:calc": Start("calc.exe"); break;
                    case "action:snip": Start("ms-screenclip:"); break;
                    case "action:taskmgr": Start("taskmgr.exe"); break;
                    case "action:lock": Native.LockWorkStation(); break;
                    case "action:mute": volume?.ToggleMute(); break;
                    case "action:playpause":
                        Native.keybd_event(Native.VK_MEDIA_PLAY_PAUSE, 0, 0, UIntPtr.Zero);
                        Native.keybd_event(Native.VK_MEDIA_PLAY_PAUSE, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                        break;
                    default:
                        if (id.StartsWith("app:")) Start(id.Substring(4));
                        break;
                }
            }
            catch (Exception e) { Log.I("Raccourci impossible : " + e.Message); }
        }

        public static void Start(string target) =>
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

        /// <summary>
        /// Ouvre l'appli d'une notification : on ramene d'abord sa fenetre si elle est deja ouverte,
        /// sinon on la lance (programme classique ou appli du Store).
        /// </summary>
        public static void OpenApp(string aumid, string appName)
        {
            if (string.IsNullOrEmpty(aumid) && string.IsNullOrEmpty(appName)) return;
            try
            {
                if (BringToFront(aumid, appName)) return;

                // Programme classique : « {dossier connu}\chemin\appli.exe »
                string exe = ResolveExe(aumid);
                if (exe != null)
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                    Log.I("Ouverture (programme) : " + exe);
                    return;
                }
                if (!string.IsNullOrEmpty(aumid))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + aumid) { UseShellExecute = true });
                    Log.I("Ouverture (appli) : " + aumid);
                }
            }
            catch (Exception e) { Log.I("Ouverture impossible : " + e.Message); }
        }

        private static readonly System.Collections.Generic.Dictionary<string, string> Known =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["chrome"] = "chrome", ["msedge"] = "msedge", ["edge"] = "msedge", ["brave"] = "brave", ["firefox"] = "firefox",
                ["opera"] = "opera", ["discord"] = "Discord", ["whatsapp"] = "WhatsApp", ["telegram"] = "Telegram",
                ["outlook"] = "OUTLOOK", ["teams"] = "ms-teams", ["spotify"] = "Spotify", ["slack"] = "slack",
                ["steam"] = "steam", ["messenger"] = "Messenger", ["immersivecontrolpanel"] = "SystemSettings",
            };

        private static bool BringToFront(string aumid, string appName)
        {
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in ((aumid ?? "") + "." + (appName ?? "")).Split('.', '!', '_', '\\', '/', ' ', '{', '}'))
            {
                string p = part.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? part[..^4] : part;
                if (p.Length < 3) continue;
                names.Add(p);
                if (Known.TryGetValue(p, out var k)) names.Add(k);
            }
            foreach (var generic in new[] { "com", "microsoft", "windows", "exe", "app", "squirrel", "notification", "powershell", "WindowsPowerShell" })
                names.Remove(generic);
            if (names.Count == 0) return false;
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.MainWindowHandle == IntPtr.Zero || !names.Contains(proc.ProcessName)) continue;
                    var h = proc.MainWindowHandle;
                    if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
                    Native.AllowSetForegroundWindow(proc.Id);
                    Native.SetForegroundWindow(h);
                    Log.I("Ouverture (fenetre existante) : " + proc.ProcessName);
                    return true;
                }
                catch { }
                finally { proc.Dispose(); }
            }
            return false;
        }

        private static string ResolveExe(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return null;
            try
            {
                if (aumid.StartsWith("{") && aumid.Contains("}"))
                {
                    int end = aumid.IndexOf('}');
                    string folder = Native.KnownFolder(new Guid(aumid.Substring(1, end - 1)));
                    if (folder == null) return null;
                    string path = Path.Combine(folder, aumid.Substring(end + 1).TrimStart('\\'));
                    return File.Exists(path) ? path : null;
                }
                if (Path.IsPathRooted(aumid) && File.Exists(aumid)) return aumid;
            }
            catch { }
            return null;
        }
    }
}
