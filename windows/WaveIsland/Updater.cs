using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;

namespace WaveIsland
{
    /// <summary>
    /// Mises a jour depuis l'ilot : la derniere version est publiee sur GitHub (release « windows »).
    /// Verification au lancement, toutes les 30 minutes, au deverrouillage et a la sortie de veille
    /// (un petit fichier texte : quasi gratuit). Une version deja proposee revient au plus une fois par heure. L'ilot propose la mise a jour ; un clic la telecharge
    /// (barre de progression dans l'ilot), remplace l'appli et la relance.
    /// </summary>
    public static class Updater
    {
        private const string Base = "https://github.com/gintzmarius-commits/crystal/releases/download/windows/";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        private static DispatcherTimer _timer;
        private static bool _busy;
        private static int _offered;
        private static DateTime _offeredAt, _checkedAt;

        /// <summary>Derniere version vue en ligne (0 = inconnue).</summary>
        public static int Latest { get; private set; }
        public static bool Available => Latest > CurrentBuild;

        public static int CurrentBuild
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? 0 : Math.Max(0, v.Build);
            }
        }

        public static string CurrentVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "2.0" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        static Updater()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("WaveIsland-Windows");
        }

        public static void Start(IslandWindow island)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            _timer.Tick += (a, b) => CheckNow(island, false);
            _timer.Start();
            // Deverrouillage de la session / sortie de veille : on reverifie (le reseau revient en quelques secondes)
            void Soon()
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                t.Tick += (a, b) =>
                {
                    t.Stop();
                    if (DateTime.Now - _checkedAt > TimeSpan.FromMinutes(10)) CheckNow(island, false);
                    else if (Available) Offer(island, false);
                };
                t.Start();
            }
            Microsoft.Win32.SystemEvents.SessionSwitch += (a, e) =>
            {
                if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock) island.Dispatcher.BeginInvoke(new Action(Soon));
            };
            Microsoft.Win32.SystemEvents.PowerModeChanged += (a, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Resume) island.Dispatcher.BeginInvoke(new Action(Soon));
            };
            var once = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            once.Tick += (a, b) => { once.Stop(); CheckNow(island, false); };
            once.Start();
        }

        /// <summary>Verifie ; manual = message meme si tout est a jour ; install = installe directement.</summary>
        public static async void CheckNow(IslandWindow island, bool manual, bool install = false)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                _checkedAt = DateTime.Now;
                string s = await Http.GetStringAsync(Base + "version.txt?t=" + DateTime.UtcNow.Ticks);
                Latest = int.Parse(s.Trim());
                Log.I($"Mise a jour : version en ligne 3.0.{Latest}, installee {CurrentVersion}");
                island.RefreshMenu();
                if (Available)
                {
                    if (install) { _busy = false; Install(island); return; }
                    Offer(island, manual);
                }
                else if (manual)
                    island.ShowNote(new IslandNote { App = "Wave Island", Title = "Tu as la dernière version", Text = CurrentVersion, OnClick = () => { } });
            }
            catch (Exception e)
            {
                Log.I("Mise a jour : " + e.Message);
                if (manual) island.ShowNote(new IslandNote { App = "Wave Island", Title = "Vérification impossible", Text = "Pas de connexion ?", OnClick = () => { } });
            }
            finally { _busy = false; }
        }

        /// <summary>Propose la mise a jour dans l'ilot (au plus une fois par heure pour la meme version).</summary>
        private static void Offer(IslandWindow island, bool force)
        {
            if (!force && _offered == Latest && DateTime.Now - _offeredAt < TimeSpan.FromHours(1)) return;
            _offered = Latest;
            _offeredAt = DateTime.Now;
            island.ShowNote(new IslandNote
            {
                App = "Wave Island",
                Title = "Mise à jour disponible",
                Text = $"Wave Island 3.0.{Latest} — clique pour l'installer",
                OnClick = () => Install(island)
            });
        }

        public static async void Install(IslandWindow island)
        {
            if (_busy) return;
            _busy = true;
            var dl = new DownloadInfo { Path = "wave-update", Name = "Mise à jour de Wave Island" };
            try
            {
                // L'appli installee (ou le fichier lance s'il n'est pas installe)
                string exe = Installer.IsInstalledCopy || !Installer.IsInstalled ? Environment.ProcessPath : Installer.Exe;
                string tmp = Path.Combine(Path.GetTempPath(), "WaveIsland-maj");
                Directory.CreateDirectory(tmp);
                string fresh = Path.Combine(tmp, "WaveIsland.exe");
                Log.I("Mise a jour : telechargement vers " + fresh + " pour remplacer " + exe);

                island.SetUpdate(dl);
                island.ShowLive();
                using (var resp = await Http.GetAsync(Base + "WaveIsland.exe", HttpCompletionOption.ResponseHeadersRead))
                {
                    resp.EnsureSuccessStatusCode();
                    dl.Total = resp.Content.Headers.ContentLength ?? 0;
                    using var input = await resp.Content.ReadAsStreamAsync();
                    using var o = File.Create(fresh);
                    var buf = new byte[256 * 1024];
                    var clock = Stopwatch.StartNew();
                    long lastBytes = 0;
                    double lastT = 0;
                    int n;
                    while ((n = await input.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await o.WriteAsync(buf, 0, n);
                        dl.Bytes += n;
                        double t = clock.Elapsed.TotalSeconds;
                        if (t - lastT > 0.25)
                        {
                            dl.Speed = (dl.Bytes - lastBytes) / (t - lastT);
                            dl.LastGrowth = DateTime.Now;
                            lastBytes = dl.Bytes;
                            lastT = t;
                            island.SetUpdate(dl);
                        }
                    }
                }
                if (new FileInfo(fresh).Length < 1_000_000 || (dl.Total > 0 && dl.Bytes != dl.Total))
                    throw new Exception("fichier téléchargé incomplet");

                // Remplace l'appli une fois fermee (plusieurs essais), puis la relance
                string script = Path.Combine(tmp, "maj.cmd");
                string log = Path.Combine(Log.Folder, "maj.txt");
                File.WriteAllText(script,
                    "@echo off\r\n" +
                    "set n=0\r\n" +
                    ":retry\r\n" +
                    "ping 127.0.0.1 -n 2 > nul\r\n" +
                    $"move /y \"{fresh}\" \"{exe}\" > nul 2>&1\r\n" +
                    "if not errorlevel 1 goto ok\r\n" +
                    "set /a n=n+1\r\n" +
                    "if %n% LSS 40 goto retry\r\n" +
                    $"echo %date% %time% echec du remplacement >> \"{log}\"\r\n" +
                    ":ok\r\n" +
                    $"echo %date% %time% remplacement termine (essais : %n%) >> \"{log}\"\r\n" +
                    $"start \"\" \"{exe}\" --updated\r\n");
                Log.I("Mise a jour : installation, la Wave Island redemarre");
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, UseShellExecute = false });
                // Fermeture immediate et certaine : avant, l'arret « propre » pouvait rester bloque (barre figee a la fin)
                // et l'ancien fichier restait verrouille, donc la mise a jour ne s'installait jamais.
                App.PrepareForUpdate();
                Log.I("Mise a jour : fermeture pour remplacement");
                Process.GetCurrentProcess().Kill();
            }
            catch (Exception e)
            {
                Log.I("Mise a jour impossible : " + e);
                island.SetUpdate(null);
                island.ShowNote(new IslandNote { App = "Wave Island", Title = "Mise à jour impossible", Text = e.Message, OnClick = () => { } });
            }
            finally { _busy = false; }
        }
    }
}
