using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WaveIsland
{
    /// <summary>
    /// Tests automatiques (WaveIsland.exe --demo) : volume, notification, menu, positions, themes, reglages.
    /// Chaque etape est enregistree en image pour verifier le rendu.
    /// </summary>
    public static class Demo
    {
        private static string Folder => Environment.GetEnvironmentVariable("WAVE_SHOTS") ?? Path.Combine(Log.Folder, "captures");
        private static IslandNote _real;

        public static void NoteArrived(IslandNote n)
        {
            if (Program.Demo) _real = n;
        }

        private static Task Wait(double s) => Task.Delay(TimeSpan.FromSeconds(s));

        public static async void Run(IslandWindow island, VolumeService volume, MediaService media, DownloadWatcher downloads)
        {
            try
            {
                var s = Settings.Current;
                await Wait(2.5);
                island.Snap("01-repos", Folder);

                island.ShowVolume(0.30f, false);
                await Wait(0.12);
                island.Snap("02-volume-ouverture", Folder);
                await Wait(0.5);
                volume.Step(Native.VK_VOLUME_UP); // vraie sortie audio s'il y en a une
                island.ShowVolume(0.62f, false);
                await Wait(0.9);
                island.Snap("03-volume", Folder);
                island.ShowVolume(0f, true);
                await Wait(0.8);
                island.Snap("04-volume-muet", Folder);
                island.Collapse();
                await Wait(1.0);

                island.ShowNote(new IslandNote { App = "Messages", Title = "Léa", Text = "On se retrouve à 20h devant le ciné ?" });
                await Wait(1.0);
                island.Snap("05-notification", Folder);
                island.Collapse();
                await Wait(0.9);

                island.ShowMenu();
                await Wait(1.0);
                island.Snap("06-menu", Folder);
                island.Collapse();
                await Wait(0.8);

                // Vraie notification Windows envoyee par le test
                File.WriteAllText(Path.Combine(Folder, "pret-pour-toast.txt"), "ok");
                for (int i = 0; i < 40 && _real == null; i++) await Wait(0.5);
                Log.I(_real != null ? $"DEMO : vraie notification recue ({_real.App} / {_real.Title})" : "DEMO : aucune vraie notification recue");
                if (_real != null) { await Wait(0.8); island.Snap("07-vraie-notification", Folder); }
                island.Collapse();
                await Wait(0.8);

                foreach (var pos in new[] { "left", "right" })
                {
                    s.Position = pos;
                    Settings.Notify();
                    await Wait(0.6);
                    island.ShowVolume(0.5f, false);
                    await Wait(0.9);
                    island.Snap("08-position-" + pos, Folder);
                    island.Collapse();
                    await Wait(0.6);
                }
                s.Position = "center";
                Settings.Notify();

                foreach (var theme in new[] { "dark", "light" })
                {
                    s.Theme = theme;
                    Settings.Notify();
                    island.ShowNote(new IslandNote { App = "Mail", Title = "Thème " + theme, Text = "Aperçu du thème" });
                    await Wait(1.0);
                    island.Snap("09-theme-" + theme, Folder);
                    island.Collapse();
                    await Wait(0.6);
                }
                s.Theme = "clear";
                Settings.Notify();

                // Notification de test : un clic ouvre l'appli concernee (Parametres)
                island.ShowNote(new IslandNote
                {
                    App = "Paramètres", Title = "Notification de test", Text = "Clique dessus : ça ouvre l'appli Paramètres",
                    Aumid = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"
                });
                await Wait(1.0);
                island.ClickNote();
                await Wait(1.5);

                // Musique en arriere-plan
                media.Fake = new MediaInfo
                {
                    Title = "Vagues de nuit", Artist = "Les Reflets", App = "Spotify", Playing = true,
                    Duration = TimeSpan.FromSeconds(214), Position = TimeSpan.FromSeconds(71), PositionAt = DateTime.Now,
                    Art = FakeArt()
                };
                await Wait(2.0);
                island.Snap("11-musique-repos", Folder);
                island.ShowLive();
                await Wait(1.2);
                island.Snap("12-musique-survol", Folder);
                island.Collapse();
                await Wait(1.0);

                // Telechargement en cours (un fichier qui grossit dans le dossier surveille)
                string dl = Path.Combine(downloads.Folder, "video-vacances.mp4.crdownload");
                using (var f = new FileStream(dl, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    var chunk = new byte[350 * 1024];
                    for (int i = 0; i < 14; i++)
                    {
                        f.Write(chunk, 0, chunk.Length);
                        f.Flush();
                        await Wait(0.3);
                        if (i == 8) island.Snap("13-telechargement-repos", Folder);
                    }
                    island.ShowLive();
                    for (int i = 0; i < 5; i++) { f.Write(chunk, 0, chunk.Length); f.Flush(); await Wait(0.3); }
                    island.Snap("14-activites", Folder);
                }
                island.Collapse();
                await Wait(0.5);
                File.Move(dl, Path.Combine(downloads.Folder, "video-vacances.mp4"), true);
                await Wait(1.8);
                island.Snap("15-telechargement-termine", Folder);
                media.Fake = null;
                await Wait(1.5);
                island.Collapse();
                await Wait(1.0);
                island.Snap("16-fin", Folder);

                // Mise a jour telechargee depuis l'ilot (barre de progression)
                var upd = new DownloadInfo { Path = "wave-update", Name = "Mise à jour Wave Island 1.0.99", Total = 78_000_000, Bytes = 47_000_000, Speed = 9_400_000 };
                island.SetUpdate(upd);
                island.ShowLive();
                await Wait(1.2);
                island.Snap("17-mise-a-jour", Folder);
                island.SetUpdate(null);
                island.Collapse();
                await Wait(0.8);

                // Wave Theme : la barre des taches en verre liquide
                if (App.Taskbar != null)
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = true }); } catch { }
                    await Wait(2.5);
                    App.Taskbar.Snap("24-barre-des-taches", Folder);
                    // Disparition en goutte (option « Faire disparaitre la barre ») puis retour
                    App.Taskbar.Drop();
                    await Wait(0.16);
                    App.Taskbar.Snap("24b-barre-goutte-se-forme", Folder);
                    await Wait(0.18);
                    App.Taskbar.Snap("24c-barre-goutte-tombe", Folder);
                    await Wait(1.0);
                    App.Taskbar.Rise();
                    await Wait(0.2);
                    App.Taskbar.Snap("24d-barre-remonte", Folder);
                    await Wait(1.0);
                    Log.I("DEMO : barre des taches Wave Theme " + (App.Taskbar.Active ? "active" : "inactive"));
                    App.NoteHistory.Insert(0, (new IslandNote { App = "Messages", Title = "Léa", Text = "On se retrouve à 20h devant le ciné ?" }, DateTime.Now.AddMinutes(-3)));
                    foreach (var (kind, shot) in new[] { ("quick", "25-reglages-rapides"), ("notes", "26-notifications") })
                    {
                        App.Taskbar.OpenPanel(kind);
                        await Wait(1.4);
                        SnapWindow(TaskbarPanel.Current, shot);
                        TaskbarPanel.Current?.CloseAnimated();
                        await Wait(0.6);
                    }
                }

                // Version 4.0 : mode tablette facon iPad (sur la machine de test, force)
                if (App.Taskbar != null)
                {
                    Settings.Current.TabletMode = "on";
                    TabletShell.Apply();
                    await Wait(1.5);
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = true }); } catch { }
                    await Wait(3);
                    ScreenShot("27-tablette-appli");
                    await AppMotion.Demo(ScreenShot);
                    Log.I("DEMO : mode tablette " + (TabletShell.Active ? "actif" : "inactif"));
                    Settings.Current.TabletMode = "auto";
                    TabletShell.Apply();
                    await Wait(1);
                }

                // Touche Windows : le menu des applis coule depuis l'ilot (vraie touche simulee)
                Native.keybd_event((byte)Native.VK_LWIN, 0, 0x1, UIntPtr.Zero);
                Native.keybd_event((byte)Native.VK_LWIN, 0, Native.KEYEVENTF_KEYUP | 0x1, UIntPtr.Zero);
                var launcher = App.Launcher;
                for (int i = 0; i < 20 && !launcher.IsOpen; i++) await Wait(0.05);
                Log.I(launcher.IsOpen ? "DEMO : touche Windows -> menu de l'ilot" : "DEMO : la touche Windows n'a pas ouvert le menu");
                if (!launcher.IsOpen) launcher.Open();
                foreach (var (t, n) in new[] { (0.09, "18a-menu-etire"), (0.12, "18b-menu-detache"), (0.12, "18c-menu-descend"), (0.25, "18d-menu-ouvre"), (1.6, "18e-menu-applis") })
                {
                    await Wait(t);
                    launcher.Snap(n, Folder);
                }
                launcher.Close2();
                foreach (var (t, n) in new[] { (0.16, "19a-menu-remonte"), (0.2, "19b-menu-fusion"), (0.9, "19c-menu-ferme") })
                {
                    await Wait(t);
                    launcher.Snap(n, Folder);
                }
                await Wait(0.5);

                // Wave Drop : panneau, fichier choisi, demande recue
                Settings.Current.DropEnabled = true;
                string sample = Path.Combine(Folder, "photo-vacances.jpg");
                File.WriteAllBytes(sample, new byte[2_400_000]);
                App.Launcher.OpenDrop(null);
                await Wait(1.4);
                App.Launcher.Snap("20-wavedrop-choix", Folder);
                App.Launcher.OpenDrop(sample);
                await Wait(1.0);
                App.Launcher.Snap("21-wavedrop-fichier", Folder);
                App.Launcher.OpenDrop(null, new WaveDropService.Request { From = "Xiaomi de Marius", Offer = new Drop.WdProtocol.Offer { Name = "video-plage.mp4", Size = 48_000_000, Mime = "video/mp4" } });
                await Wait(1.0);
                App.Launcher.Snap("22-wavedrop-demande", Folder);
                App.Launcher.Close2();
                await Wait(1.5);
                island.SetTransfer(new DownloadInfo { Path = "wave-drop", Name = "Envoi à Xiaomi de Marius", Bytes = 31_000_000, Total = 48_000_000, Speed = 6_200_000, LastGrowth = DateTime.Now });
                island.ShowLive();
                await Wait(1.0);
                island.Snap("23-wavedrop-ilot", Folder);
                island.SetTransfer(null);
                island.Collapse();
                await Wait(0.8);

                App.OpenSettings();
                await Wait(1.5);
                SnapWindow(App.SettingsWin, "10-appli");
                App.SettingsWin?.Close();

                Log.I("DEMO OK");
            }
            catch (Exception e) { Log.I("DEMO ERREUR : " + e); }
            await Wait(0.5);
            App.Quit();
        }

        /// <summary>Fausse pochette (degrade) pour le test de la musique.</summary>
        private static ImageSource FakeArt()
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(255, 94, 98), Color.FromRgb(80, 60, 220), 45), null, new Rect(0, 0, 120, 120));
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(150, 255, 230, 120)), null, new Point(80, 40), 26, 26);
            }
            var bmp = new RenderTargetBitmap(120, 120, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>Capture de tout l'ecran (vignettes en direct comprises).</summary>
        private static void ScreenShot(string name)
        {
            try
            {
                var sb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                var cap = new ScreenCapture();
                cap.Grab(sb.Left, sb.Top, sb.Width, sb.Height);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(cap.Bitmap));
                Directory.CreateDirectory(Folder);
                using var f = File.Create(Path.Combine(Folder, name + ".png"));
                enc.Save(f);
            }
            catch (Exception e) { Log.I("Capture ecran : " + e.Message); }
        }

        private static void SnapWindow(Window w, string name)
        {
            if (w == null) return;
            try
            {
                var el = (FrameworkElement)w.Content;
                double k = VisualTreeHelper.GetDpi(w).DpiScaleX;
                var rtb = new RenderTargetBitmap((int)(el.ActualWidth * k), (int)(el.ActualHeight * k), 96 * k, 96 * k, PixelFormats.Pbgra32);
                rtb.Render(el);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                Directory.CreateDirectory(Folder);
                using var f = File.Create(Path.Combine(Folder, name + ".png"));
                enc.Save(f);
            }
            catch (Exception e) { Log.I("Capture des reglages : " + e.Message); }
        }
    }

    /// <summary>Fond colore pour les tests (on voit la deformation du verre).</summary>
    public class Backdrop : Window
    {
        public Backdrop()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            ShowInTaskbar = false;
            var c = new Canvas
            {
                Background = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromRgb(255, 140, 90), 0),
                        new GradientStop(Color.FromRgb(240, 80, 150), 0.35),
                        new GradientStop(Color.FromRgb(90, 110, 250), 0.7),
                        new GradientStop(Color.FromRgb(40, 200, 190), 1)
                    }, 0)
            };
            // Rayures et texte pour voir la courbure des bords
            for (int i = 0; i < 80; i++)
            {
                var r = new System.Windows.Shapes.Rectangle
                {
                    Width = 6, Height = 220, Fill = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255))
                };
                Canvas.SetLeft(r, i * 28);
                Canvas.SetTop(r, 0);
                c.Children.Add(r);
            }
            var t = new TextBlock
            {
                Text = "Wave Island  •  verre liquide  •  goutte d'eau",
                FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Brushes.Black
            };
            Canvas.SetLeft(t, 40);
            Canvas.SetTop(t, 14);
            c.Children.Add(t);
            var t2 = new TextBlock { Text = "0123456789 ABCDEFGHIJ", FontSize = 22, Foreground = Brushes.White };
            Canvas.SetLeft(t2, 300);
            Canvas.SetTop(t2, 52);
            c.Children.Add(t2);
            Content = c;
        }
    }
}
