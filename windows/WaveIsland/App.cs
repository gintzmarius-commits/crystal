using System;
using System.Collections.Generic;
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
        public static LauncherWindow Launcher { get; private set; }
        /// <summary>Wave Theme : la barre des taches en verre liquide (null si desactivee).</summary>
        public static TaskbarWindow Taskbar { get; private set; }
        /// <summary>Notifications recues (pour le panneau des notifications de la barre).</summary>
        public static readonly List<(IslandNote note, DateTime when)> NoteHistory = new List<(IslandNote, DateTime)>();
        public static WaveDropService Drop { get; private set; }
        private NotificationWatcher _notes;
        private System.Windows.Forms.NotifyIcon _tray;
        private static AppWindow _settings;
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
            // Touche Windows : le menu des applis coule depuis l'ilot
            Launcher = new LauncherWindow(Island);
            // Plein ecran (film, jeu en fenetre sans bordure...) : la touche Windows ouvre quand meme le menu de l'ilot.
            // Seul un jeu en plein ecran exclusif garde le menu Demarrer (rien ne peut s'afficher par-dessus).
            Volume.WinAllowed = () => !Island.ExclusiveFullscreen;
            Volume.WinTapped += () => Launcher.Toggle();
            // Version finale : touche Copilot / Microsoft 365 = assistant IA dans l'ilot
            Assistant.Init(Dispatcher);
            Volume.AssistKey += Assistant.Trigger;
            // Lot 2 : recherche universelle, capture Wave, ecran de veille goutte
            Volume.SearchKey += SearchWindow.Toggle;
            Volume.TaskViewKey += AppMotion.BeginSwitcher;
            Volume.ShotKey += WaveShot.Take;
            DropSaver.Start();
            // Lot 4 : Wave Link, continuite, rappels, resume du matin
            WaveLink.Start(Dispatcher);
            Continuity.Start();
            Reminders.Start();
            System.Threading.Tasks.Task.Delay(25000).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => Morning.Maybe())));
            // Wave Theme : la barre des taches de Windows remplacee par la notre (le menu monte de la bulle Windows)
            // (Pas de nettoyage dans ProcessExit : il tournait hors du fil de l'interface et bloquait la fermeture,
            // donc la mise a jour ne pouvait pas remplacer l'appli.)
            DispatcherUnhandledException += (a, b) =>
            {
                // Erreur interne de WPF quand une fenetre d'une autre appli disparait pendant un deplacement : sans gravite
                if (b.Exception is System.ComponentModel.Win32Exception w && w.NativeErrorCode == 1400) { b.Handled = true; Log.I("Erreur WPF ignoree (fenetre disparue)"); }
            };
            AppDomain.CurrentDomain.UnhandledException += (a, b) => { try { TaskbarWindow.RestoreSystemTaskbar(); } catch { } };
            ApplyTaskbar();
            // Version 4.0 : mode tablette facon iPad (ecran retourne)
            TabletShell.Start();
            AppCatalog.Refresh(Dispatcher);
            Volume.Start();

            _notes = new NotificationWatcher(Dispatcher);
            _notes.Arrived += n =>
            {
                // Lot 1 : concentration = la notification attend dans le centre de notifications
                if (FocusMode.Swallow())
                {
                    NoteHistory.Insert(0, (n, DateTime.Now));
                    if (NoteHistory.Count > 40) NoteHistory.RemoveAt(NoteHistory.Count - 1);
                    return;
                }
                Island.ShowNote(n);
                Demo.NoteArrived(n);
                NoteHistory.Insert(0, (n, DateTime.Now));
                if (NoteHistory.Count > 40) NoteHistory.RemoveAt(NoteHistory.Count - 1);
            };
            _notes.Start();

            // Lot 1 : teinte du verre, concentration par horaires
            WaveColor.Changed += () => { Island?.RefreshTint(); Taskbar?.RefreshTint(); };
            var focusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            focusTimer.Tick += (a, b) => FocusMode.Check();
            focusTimer.Start();
            FocusMode.Check();

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

            // Wave Drop : envoi / reception de fichiers avec les appareils proches
            Drop = new WaveDropService(Dispatcher);
            var clock = Stopwatch.StartNew();
            long lastBytes = 0; double lastT = 0, speed = 0;
            Drop.RequestReceived += r => Island.ShowNote(new IslandNote
            {
                App = "Wave Drop", Title = r.From + " veut t'envoyer un fichier",
                Text = r.Offer.Name + " · " + LauncherWindow.SizeText(r.Offer.Size) + " — clique pour répondre",
                OnClick = () => Launcher.OpenDrop(null, r)
            });
            Drop.Progress += (title, text, done, total) =>
            {
                double t = clock.Elapsed.TotalSeconds;
                if (done < lastBytes) { lastBytes = 0; speed = 0; }
                if (t - lastT > 0.3) { speed = (done - lastBytes) / Math.Max(0.001, t - lastT); lastBytes = done; lastT = t; }
                Island.SetTransfer(new DownloadInfo { Path = "wave-drop", Name = title, Bytes = done, Total = total, Speed = speed, LastGrowth = DateTime.Now });
            };
            Drop.Finished += (title, text, ok, path) =>
            {
                Island.SetTransfer(null);
                lastBytes = 0;
                Island.ShowNote(new IslandNote
                {
                    App = "Wave Drop", Title = title, Text = text + (path != null ? " — clique pour l'afficher" : ""),
                    OnClick = () => { if (path != null) try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { } }
                });
            };
            if (!Program.Demo) Drop.Start();

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
            if (firstRun) Settings.Current.Save();
            if (Installer.IsInstalledCopy) Installer.Register(); // version a jour dans « Applications installees »

            // Ouverte depuis le menu Demarrer / le bureau : la fenetre de l'appli s'affiche
            if (!Program.Tray && !Program.Updated)
                Dispatcher.BeginInvoke(new Action(OpenSettings), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (Program.Updated)
                Island.ShowNote(new IslandNote { App = "Wave Island", Title = "Mise à jour installée", Text = "Version " + Updater.CurrentVersion, OnClick = OpenSettings });

            // Relancer Wave Island alors qu'elle tourne deja : on ouvre sa fenetre
            var open = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, "WaveIsland.Open");
            new System.Threading.Thread(() =>
            {
                while (true)
                {
                    open.WaitOne();
                    Dispatcher.BeginInvoke(new Action(OpenSettings));
                }
            }) { IsBackground = true, Name = "ouvrir" }.Start();

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
                menu.Items.Add("Ouvrir Wave Island", null, (a, b) => OpenSettings());
                menu.Items.Add("Mettre à jour", null, (a, b) => Updater.CheckNow(Island, true, install: true));
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
                _settings = new AppWindow();
                _settings.Closed += (a, b) =>
                {
                    _settings = null;
                    if (!_closeHintShown && !Program.Demo)
                    {
                        _closeHintShown = true;
                        Island?.ShowNote(new IslandNote
                        {
                            App = "Wave Island", Title = "Fenêtre fermée, l'îlot reste actif",
                            Text = "Pour quitter : icône Wave Island près de l'horloge", OnClick = () => { }
                        });
                    }
                };
                _settings.Show();
            }
            if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
            _settings.Activate();
        }

        public static AppWindow SettingsWin => _settings;

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
                if (Settings.Current.StartWithWindows) k.SetValue("WaveIsland", $"\"{Environment.ProcessPath}\" --tray");
                else k.DeleteValue("WaveIsland", false);
            }
            catch (Exception e) { Log.I("Demarrage avec Windows : " + e.Message); }
        }

        /// <summary>Redemarre la Wave Island (nouveau nombre d'images par seconde...).</summary>
        public static void Restart()
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & start \"\" \"{Environment.ProcessPath}\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
            Quit();
        }

        /// <summary>Met en place (ou retire) la barre des taches Wave Theme selon le reglage.</summary>
        public static void ApplyTaskbar()
        {
            try
            {
                if (Settings.Current.WaveTaskbar)
                {
                    if (Taskbar == null) Taskbar = new TaskbarWindow();
                    if (!Taskbar.Active) Taskbar.Start();
                }
                else
                {
                    Taskbar?.Stop();
                    TaskbarWindow.RestoreSystemTaskbar();
                }
            }
            catch (Exception e)
            {
                Log.I("Wave Theme : barre des taches impossible : " + e);
                TaskbarWindow.RestoreSystemTaskbar();
            }
        }

        /// <summary>Avant une mise a jour : on rend la barre de Windows et on retire l'icone, sans rien d'autre qui puisse bloquer.</summary>
        public static void PrepareForUpdate()
        {
            try { TabletShell.Stop(); } catch { }
            try { Taskbar?.Stop(); } catch { }
            try { TaskbarWindow.RestoreSystemTaskbar(); } catch { }
            try { var t = ((App)Current)._tray; if (t != null) { t.Visible = false; t.Dispose(); } } catch { }
        }

        public static void Quit()
        {
            Log.I("Fermeture demandee");
            try { Taskbar?.Stop(); } catch { }
            ((App)Current).Cleanup();
            Current.Shutdown();
        }

        private void Cleanup()
        {
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } } catch { }
            try { Volume?.Dispose(); } catch { }
            try { _notes?.Dispose(); } catch { }
            try { Drop?.Stop(); } catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Cleanup();
            base.OnExit(e);
        }
    }
}
