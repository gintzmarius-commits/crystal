using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WaveIsland
{
    /// <summary>Reglages, enregistres dans %APPDATA%\WaveIsland\reglages.json.</summary>
    public class Settings
    {
        /// <summary>"left", "center" ou "right"</summary>
        public string Position { get; set; } = "center";
        public double Width { get; set; } = 200;
        public double Height { get; set; } = 34;
        public double Top { get; set; } = 8;
        /// <summary>"clear" (transparent), "dark" ou "light"</summary>
        public string Theme { get; set; } = "clear";
        /// <summary>Effet goutte d'eau (le fond est vu a travers le verre)</summary>
        public bool Refraction { get; set; } = true;
        public bool VolumeInIsland { get; set; } = true;
        public bool Notifications { get; set; } = true;
        public bool HideInFullscreen { get; set; } = true;
        public bool StartWithWindows { get; set; } = true;
        /// <summary>Musique / videos en cours dans l'ilot (avec les commandes)</summary>
        public bool LiveMedia { get; set; } = true;
        /// <summary>Telechargements en cours dans l'ilot (avec leur barre)</summary>
        public bool LiveDownloads { get; set; } = true;
        /// <summary>Touche Windows : le menu des applis coule depuis l'ilot (a la place du menu Demarrer)</summary>
        public bool WinKeyMenu { get; set; } = true;
        /// <summary>Nombre d'ouvertures de chaque appli depuis l'ilot (pour « les plus utilisees »)</summary>
        public Dictionary<string, int> LaunchCounts { get; set; } = new Dictionary<string, int>();
        /// <summary>Applis epinglees en haut du menu (comme le menu Demarrer)</summary>
        public List<string> Pinned { get; set; } = new List<string>();
        public bool PinnedInit { get; set; }
        /// <summary>Affichage de « Toutes les applis » : "categories", "grid" ou "list"</summary>
        public string StartView { get; set; } = "categories";
        public List<string> Shortcuts { get; set; } = new List<string>
        {
            "action:explorer", "action:settings", "action:calc", "action:snip", "action:playpause"
        };

        private static string FilePath => Path.Combine(Log.Folder, "reglages.json");
        private static Settings _current;
        public static Settings Current => _current ??= Load();

        public static event Action Changed;

        /// <summary>Applique tout de suite sans enregistrer (pendant qu'on glisse un curseur).</summary>
        public static void Notify() => Changed?.Invoke();

        private static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch (Exception e) { Log.I("Reglages illisibles : " + e.Message); }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Log.Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) { Log.I("Reglages non enregistres : " + e.Message); }
            Changed?.Invoke();
        }
    }
}
