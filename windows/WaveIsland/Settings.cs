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
        /// <summary>Assistant IA (Gemini) : cle gratuite (aistudio.google.com/apikey).</summary>
        public string AiKey { get; set; } = "";
        /// <summary>Modele Claude de l'assistant.</summary>
        public string AiModel { get; set; } = "gemini-flash-latest";
        /// <summary>La reponse est lue a voix haute.</summary>
        public bool AiVoice { get; set; } = true;
        /// <summary>Touche Copilot / Microsoft 365 = assistant.</summary>
        public bool AiKeyEnabled { get; set; } = true;
        /// <summary>Lot 1 : teinte du verre ("" = transparent).</summary>
        public string GlassColor { get; set; } = "";
        /// <summary>Lot 1 : petits sons de goutte.</summary>
        public bool Sounds { get; set; } = true;
        /// <summary>Lot 1 : l'ilot pulse avec la musique.</summary>
        public bool MusicPulse { get; set; } = true;
        /// <summary>Lot 1 : mode concentration (manuel / par horaires).</summary>
        public bool FocusOn { get; set; }
        public bool FocusScheduled { get; set; }
        public int FocusFrom { get; set; } = 22;
        public int FocusTo { get; set; } = 7;
        /// <summary>Lot 2 : ecran de veille goutte apres N minutes (0 = jamais).</summary>
        public int SaverMinutes { get; set; } = 5;
        /// <summary>Lot 2 : Alt + Espace = recherche, Impr. ecran = capture Wave.</summary>
        public bool SearchKey { get; set; } = true;
        public bool ShotKey { get; set; } = true;
        /// <summary>Nombre d'ouvertures de chaque appli depuis l'ilot (pour « les plus utilisees »)</summary>
        public Dictionary<string, int> LaunchCounts { get; set; } = new Dictionary<string, int>();
        /// <summary>Applis epinglees en haut du menu (comme le menu Demarrer)</summary>
        public List<string> Pinned { get; set; } = new List<string>();
        public bool PinnedInit { get; set; }
        /// <summary>Affichage de « Toutes les applis » : "categories", "grid" ou "list"</summary>
        public string StartView { get; set; } = "categories";
        /// <summary>Images par seconde des animations et du verre (30 a 120)</summary>
        public int Fps { get; set; } = 60;
        /// <summary>Wave Theme : barre des taches en verre liquide a la place de celle de Windows.</summary>
        public bool WaveTaskbar { get; set; } = true;
        /// <summary>Etat d'origine de la barre de Windows (-1 = non modifiee), pour la rendre telle quelle.</summary>
        public int TaskbarPrevState { get; set; } = -1;
        /// <summary>La barre disparait vers le bas quand une appli est en grand ecran (elle revient en bas de l'ecran).</summary>
        public bool TaskbarAutoHide { get; set; }
        /// <summary>Applis epinglees a la barre : « lnk|chemin », « exe|chemin » ou « app|identifiant ».</summary>
        public List<string> TaskbarPins { get; set; } = new List<string>();
        public bool TaskbarPinsInit { get; set; }
        /// <summary>Mode tablette (4.0) : « auto » (ecran retourne), « on » ou « off ».</summary>
        public string TabletMode { get; set; } = "auto";
        /// <summary>Fermeture d'une appli en mode tablette : « ipad » ou « drop » (goutte).</summary>
        public string TabletCloseAnim2 { get; set; } = "drop";
        public bool EdgeSwipeAsked { get; set; }
        /// <summary>Glissements des bords coupes ET Explorateur relance (correctif 4.0).</summary>
        public bool EdgeSwipeDone { get; set; }
        /// <summary>Contour des fenetres d'applis (coins arrondis) : « black », « white » ou « none ».</summary>
        public string WindowBorder { get; set; } = "black";
        /// <summary>Visible dans les captures et enregistrements d'ecran (le verre ne deforme alors plus le fond)</summary>
        public bool ShowInCaptures { get; set; }
        /// <summary>Wave Drop : visible des appareils proches, envoi / reception de fichiers</summary>
        public bool DropEnabled { get; set; }
        public string DropName { get; set; } = "";
        public int DropId { get; set; }
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
