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

        /// <summary>Ouvre l'appli d'une notification a partir de son identifiant Windows.</summary>
        public static void OpenApp(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + aumid) { UseShellExecute = true });
                Log.I("Ouverture : " + aumid);
            }
            catch (Exception e) { Log.I("Ouverture impossible : " + e.Message); }
        }
    }
}
