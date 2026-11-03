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

        public static async void Run(IslandWindow island, VolumeService volume)
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

                App.OpenSettings();
                await Wait(1.5);
                SnapWindow(App.SettingsWin, "10-reglages");
                App.SettingsWin?.Close();

                Log.I("DEMO OK");
            }
            catch (Exception e) { Log.I("DEMO ERREUR : " + e); }
            await Wait(0.5);
            App.Quit();
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
