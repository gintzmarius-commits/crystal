using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace WaveIsland
{
    public class App : Application
    {
        public static IslandWindow Island { get; private set; }
        public static VolumeService Volume { get; private set; }
        public static MediaService Media { get; private set; }
        public static DownloadWatcher Downloads { get; private set; }
        private NotificationWatcher _notes;
        private System.Windows.Forms.NotifyIcon _tray;
        private static SettingsWindow _settings;
        private static bool _closeHintShown;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Log.I($"===== Wave Island {Updater.CurrentVersion} ({Environment.OSVersion.VersionString}) =====");
            bool firstRun = !File.Exists(Path.Combine(Log.Folder, "reglages.json"));

            if (Program.Demo) new Backdrop().Show();

            Volume = new VolumeService(Dispatcher);
            Media = new MediaService(Dispatcher);
            Island = new IslandWindow(Volume, Media);
            Island.Show();
            Volume.Changed += (v, m) => Island.ShowVolume(v, m);
            Volume.Start();

            _notes = new NotificationWatcher(Dispatcher);
            _notes.Arrived += n => { Island.ShowNote(n); Demo.NoteArrived(n); };
            _notes.Start();

            // Activites en arriere-plan : musique et telechargements
            Media.Changed += m => Island.SetMedia(m);
            Media.Start();
            Downloads = new DownloadWatcher(Dispatcher);
            Downloads.Changed += l => Island.SetDownloads(l);
            Downloads.Finished += path => Island.ShowNote(new IslandNote
            {
                App = "Téléchargements", Title = "Téléchargement terminé", Text = System.IO.Path.GetFileName(path),
                OnClick = () => { try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { } }
            });
            Downloads.Start();

            CreateTray();

            if (Program.Demo)
            {
                Demo.Run(Island, Volume, Media, Downloads);
                return;
            }

            ApplyStartWithWindows();
            Settings.Changed += ApplyStartWithWindows;

            // Plantage la derniere fois ?
            if (File.Exists(Log.CrashFile))
            {
                try { File.Move(Log.CrashFile, Log.CrashFile + ".vu", true); } catch { }
                Island.ShowNote(new IslandNote
                {
                    App = "Wave Island", Title = "Wave Island s'est arrêtée", Text = "Clique pour copier le rapport et me l'envoyer",
                    OnClick = () => { CopyReport(); }
                });
            }
            if (firstRun)
            {
                Settings.Current.Save();
                Dispatcher.BeginInvoke(new Action(OpenSettings), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            Updater.Start(Island);
        }

        private void CreateTray()
        {
            try
            {
                var stream = GetResourceStream(new Uri("pack://application:,,,/WaveIsland;component/wave.ico")).Stream;
                _tray = new System.Windows.Forms.NotifyIcon
                {
                    Icon = new System.Drawing.Icon(stream),
                    Text = "Wave Island",
                    Visible = true
                };
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Réglages", null, (a, b) => OpenSettings());
                menu.Items.Add("Rechercher une mise à jour", null, (a, b) => Updater.CheckNow(Island, true));
                menu.Items.Add("Copier le rapport de bug", null, (a, b) => CopyReport());
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                menu.Items.Add("Quitter Wave Island", null, (a, b) => Quit());
                _tray.ContextMenuStrip = menu;
                _tray.MouseClick += (a, b) => { if (b.Button == System.Windows.Forms.MouseButtons.Left) OpenSettings(); };
            }
            catch (Exception ex) { Log.I("Icone de la barre des taches : " + ex.Message); }
        }

        public static void OpenSettings()
        {
            if (_settings == null)
            {
                _settings = new SettingsWindow();
                _settings.Closed += (a, b) =>
                {
                    _settings = null;
                    if (!_closeHintShown && !Program.Demo)
                    {
                        _closeHintShown = true;
                        Island?.ShowNote(new IslandNote
                        {
                            App = "Wave Island", Title = "Réglages fermés, l'îlot reste actif",
                            Text = "Pour quitter : icône Wave Island près de l'horloge", OnClick = () => { }
                        });
                    }
                };
                _settings.Show();
            }
            _settings.Activate();
        }

        public static SettingsWindow SettingsWin => _settings;

        /// <summary>Copie le journal (et le dernier plantage) pour l'envoyer facilement.</summary>
        public static void CopyReport()
        {
            try
            {
                string txt = $"Wave Island {Updater.CurrentVersion} - {Environment.OSVersion.VersionString}\r\n";
                foreach (var f in new[] { Log.CrashFile, Log.CrashFile + ".vu" })
                    if (File.Exists(f)) txt += "\r\n--- " + Path.GetFileName(f) + " ---\r\n" + File.ReadAllText(f);
                if (File.Exists(Log.File))
                {
                    var lines = File.ReadAllLines(Log.File);
                    int from = Math.Max(0, lines.Length - 250);
                    txt += "\r\n--- journal ---\r\n" + string.Join("\r\n", lines, from, lines.Length - from);
                }
                Clipboard.SetText(txt);
                Island?.ShowNote(new IslandNote { App = "Wave Island", Title = "Rapport copié", Text = "Colle-le dans la conversation avec Claude" });
            }
            catch (Exception e) { Log.I("Rapport : " + e.Message); }
        }

        public static void OpenLogFolder()
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Log.File}\"") { UseShellExecute = true }); }
            catch { }
        }

        private static void ApplyStartWithWindows()
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (Settings.Current.StartWithWindows) k.SetValue("WaveIsland", $"\"{Environment.ProcessPath}\"");
                else k.DeleteValue("WaveIsland", false);
            }
            catch (Exception e) { Log.I("Demarrage avec Windows : " + e.Message); }
        }

        public static void Quit()
        {
            Log.I("Fermeture demandee");
            ((App)Current).Cleanup();
            Current.Shutdown();
        }

        private void Cleanup()
        {
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } } catch { }
            try { Volume?.Dispose(); } catch { }
            try { _notes?.Dispose(); } catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Cleanup();
            base.OnExit(e);
        }
    }
}
