using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace WaveIsland
{
    /// <summary>
    /// Installation comme une vraie appli, sans droits administrateur :
    /// %LOCALAPPDATA%\Programs\Wave Island, menu Demarrer, bureau, « Applications installees » (avec desinstallation).
    /// </summary>
    public static class Installer
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Wave Island");
        public static readonly string Exe = Path.Combine(Dir, "WaveIsland.exe");
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WaveIsland";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Wave Island.lnk");
        private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Wave Island.lnk");

        /// <summary>true si l'appli tourne depuis son dossier d'installation.</summary>
        public static bool IsInstalledCopy =>
            string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase);

        public static bool IsInstalled => File.Exists(Exe);

        /// <summary>Copie l'appli, cree les raccourcis et l'entree « Applications installees ».</summary>
        public static void Install(bool desktopShortcut)
        {
            Directory.CreateDirectory(Dir);
            StopOthers();
            string src = Environment.ProcessPath;
            // Quelques essais : l'ancienne version peut mettre un instant a se fermer
            for (int i = 0; ; i++)
            {
                try { File.Copy(src, Exe, true); break; }
                catch (IOException) when (i < 20) { System.Threading.Thread.Sleep(300); }
            }
            Shortcut(StartMenuLink, Exe, "");
            if (desktopShortcut) Shortcut(DesktopLink, Exe, "");
            Register();
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                if (Settings.Current.StartWithWindows) k.SetValue("WaveIsland", $"\"{Exe}\" --tray");
            Log.I("Installee dans " + Dir);
        }

        /// <summary>Entree dans Parametres > Applications installees (avec « Desinstaller »).</summary>
        public static void Register()
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
                k.SetValue("DisplayName", "Wave Island");
                k.SetValue("DisplayVersion", Updater.CurrentVersion);
                k.SetValue("Publisher", "Wave Island");
                k.SetValue("DisplayIcon", Exe + ",0");
                k.SetValue("InstallLocation", Dir);
                k.SetValue("UninstallString", $"\"{Exe}\" --uninstall");
                k.SetValue("QuietUninstallString", $"\"{Exe}\" --uninstall --quiet");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long kb = File.Exists(Exe) ? new FileInfo(Exe).Length / 1024 : 0;
                k.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
            catch (Exception e) { Log.I("Enregistrement de l'appli : " + e.Message); }
        }

        public static void Uninstall(bool quiet)
        {
            StopOthers();
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { using var k = Registry.CurrentUser.CreateSubKey(RunKey); k.DeleteValue("WaveIsland", false); } catch { }
            foreach (var l in new[] { StartMenuLink, DesktopLink }) try { if (File.Exists(l)) File.Delete(l); } catch { }
            // Le dossier est supprime juste apres la fermeture de l'appli
            try
            {
                string script = Path.Combine(Path.GetTempPath(), "WaveIsland-desinstall.cmd");
                File.WriteAllText(script,
                    "@echo off\r\nping 127.0.0.1 -n 3 > nul\r\n" +
                    $"rmdir /s /q \"{Dir}\"\r\ndel \"%~f0\"\r\n");
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
            Log.I("Desinstallee");
            if (!quiet) MessageBox.Show("Wave Island a été désinstallée.", "Wave Island", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>Ferme les autres Wave Island ouvertes (ancienne version pendant une installation).</summary>
        public static void StopOthers()
        {
            int me = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("WaveIsland"))
            {
                try
                {
                    if (p.Id == me) continue;
                    p.Kill();
                    p.WaitForExit(4000);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        private static void Shortcut(string link, string target, string args)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(link));
                var type = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(type);
                dynamic sc = shell.CreateShortcut(link);
                sc.TargetPath = target;
                sc.Arguments = args;
                sc.WorkingDirectory = Path.GetDirectoryName(target);
                sc.IconLocation = target + ",0";
                sc.Description = "Wave Island : l'îlot dynamique en verre liquide";
                sc.Save();
            }
            catch (Exception e) { Log.I("Raccourci " + link + " : " + e.Message); }
        }
    }

    /// <summary>Fenetre d'installation (quand on lance le fichier telecharge).</summary>
    public sealed class InstallWindow : Window
    {
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(176, 240, 210));
        private static readonly FontFamily Ui = new FontFamily("Segoe UI Variable Text, Segoe UI");

        public InstallWindow()
        {
            Title = "Installer Wave Island";
            Width = 480;
            Height = 400;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(16, 19, 21));
            FontFamily = Ui;
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/WaveIsland;component/wave.ico")); } catch { }
            SourceInitialized += (a, b) => AppWindow.DarkTitleBar(this);

            var col = new StackPanel { Margin = new Thickness(36, 30, 36, 24) };
            var logo = new Image { Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Left };
            try { logo.Source = new BitmapImage(new Uri("pack://application:,,,/WaveIsland;component/wave.png")); } catch { }
            col.Children.Add(logo);
            col.Children.Add(new TextBlock { Text = "Wave Island", FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 14, 0, 0) });
            col.Children.Add(new TextBlock
            {
                Text = (Installer.IsInstalled ? "Une version est déjà installée : elle sera remplacée par la version " : "Version ") + Updater.CurrentVersion +
                       ". L'îlot dynamique en verre liquide, en haut de ton écran.",
                FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 18)
            });
            var desk = new CheckBox { Content = "Créer un raccourci sur le bureau", IsChecked = true, Foreground = Brushes.White, FontSize = 14 };
            var auto = new CheckBox { Content = "Lancer avec Windows", IsChecked = Settings.Current.StartWithWindows, Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(0, 8, 0, 0) };
            col.Children.Add(desk);
            col.Children.Add(auto);
            var status = new TextBlock { FontSize = 13, Foreground = Accent, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap };
            var btnText = new TextBlock { Text = Installer.IsInstalled ? "Mettre à jour" : "Installer", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(12, 22, 18)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var btn = new Border { Height = 46, CornerRadius = new CornerRadius(23), Background = Accent, Child = btnText, Cursor = Cursors.Hand, Margin = new Thickness(0, 18, 0, 0) };
            col.Children.Add(btn);
            col.Children.Add(status);
            Content = col;

            bool busy = false;
            btn.MouseLeftButtonUp += async (a, b) =>
            {
                if (busy) return;
                busy = true;
                btn.Opacity = 0.6;
                btnText.Text = "Installation…";
                Settings.Current.StartWithWindows = auto.IsChecked == true;
                Settings.Current.Save();
                bool d = desk.IsChecked == true;
                try
                {
                    await Task.Run(() => Installer.Install(d));
                    status.Text = "Installée ! Wave Island démarre…";
                    Process.Start(new ProcessStartInfo(Installer.Exe) { UseShellExecute = true, WorkingDirectory = Installer.Dir });
                    await Task.Delay(900);
                    Application.Current.Shutdown();
                }
                catch (Exception e)
                {
                    Log.I("Installation impossible : " + e);
                    status.Foreground = new SolidColorBrush(Color.FromRgb(255, 140, 130));
                    status.Text = "Installation impossible : " + e.Message;
                    btnText.Text = "Réessayer";
                    btn.Opacity = 1;
                    busy = false;
                }
            };
        }
    }
}
