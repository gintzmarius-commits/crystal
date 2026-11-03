using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace WaveIsland
{
    /// <summary>
    /// Mises a jour : la derniere version est publiee sur GitHub (release « windows »).
    /// Verification au lancement puis toutes les 6 heures ; l'ilot propose d'installer.
    /// </summary>
    public static class Updater
    {
        private const string Base = "https://github.com/gintzmarius-commits/crystal/releases/download/windows/";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        private static DispatcherTimer _timer;
        private static bool _busy;

        public static int CurrentBuild
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? 0 : Math.Max(0, v.Build);
            }
        }

        public static string CurrentVersion => "1.0." + CurrentBuild;

        static Updater()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("WaveIsland-Windows");
        }

        public static void Start(IslandWindow island)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            _timer.Tick += (a, b) => CheckNow(island, false);
            _timer.Start();
            // Petit delai : on laisse Windows finir de demarrer
            var once = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            once.Tick += (a, b) => { once.Stop(); CheckNow(island, false); };
            once.Start();
        }

        public static async void CheckNow(IslandWindow island, bool manual)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                string s = await Http.GetStringAsync(Base + "version.txt?t=" + DateTime.Now.Ticks);
                int latest = int.Parse(s.Trim());
                Log.I($"Mise a jour : version en ligne 1.0.{latest}, installee {CurrentVersion}");
                if (latest > CurrentBuild)
                {
                    island.ShowNote(new IslandNote
                    {
                        App = "Wave Island",
                        Title = "Mise à jour disponible",
                        Text = $"Version 1.0.{latest} — clique pour l'installer",
                        OnClick = () => Install(island)
                    });
                }
                else if (manual)
                {
                    island.ShowNote(new IslandNote { App = "Wave Island", Title = "Tu as la dernière version", Text = CurrentVersion, OnClick = () => { } });
                }
            }
            catch (Exception e)
            {
                Log.I("Mise a jour : " + e.Message);
                if (manual) island.ShowNote(new IslandNote { App = "Wave Island", Title = "Vérification impossible", Text = "Pas de connexion ?", OnClick = () => { } });
            }
            finally { _busy = false; }
        }

        public static async void Install(IslandWindow island)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                island.ShowNote(new IslandNote { App = "Wave Island", Title = "Téléchargement…", Text = "La Wave Island redémarre toute seule", OnClick = () => { } });
                string exe = Environment.ProcessPath;
                string tmp = Path.Combine(Path.GetTempPath(), "WaveIsland-maj");
                Directory.CreateDirectory(tmp);
                string fresh = Path.Combine(tmp, "WaveIsland.exe");
                using (var resp = await Http.GetAsync(Base + "WaveIsland.exe", HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    using var o = File.Create(fresh);
                    await resp.Content.CopyToAsync(o);
                }
                if (new FileInfo(fresh).Length < 1_000_000) throw new Exception("fichier telecharge incomplet");

                // Remplace l'exe une fois la Wave Island fermee, puis la relance
                string script = Path.Combine(tmp, "maj.cmd");
                File.WriteAllText(script,
                    "@echo off\r\n" +
                    "ping 127.0.0.1 -n 3 > nul\r\n" +
                    $"move /y \"{fresh}\" \"{exe}\" > nul\r\n" +
                    $"start \"\" \"{exe}\"\r\n");
                Log.I("Mise a jour : installation");
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false });
                App.Quit();
            }
            catch (Exception e)
            {
                Log.I("Mise a jour impossible : " + e.Message);
                island.ShowNote(new IslandNote { App = "Wave Island", Title = "Mise à jour impossible", Text = e.Message, OnClick = () => { } });
            }
            finally { _busy = false; }
        }
    }
}
