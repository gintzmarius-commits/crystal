using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace WaveIsland
{
    public static class Program
    {
        /// <summary>Lance avec --demo : enchaine volume, notification, menu et reglages (tests automatiques).</summary>
        public static bool Demo { get; private set; }
        /// <summary>Tests automatiques : l'ilot reste visible dans les captures d'ecran.</summary>
        public static bool Ci => Environment.GetEnvironmentVariable("WAVE_CI") == "1";

        /// <summary>Lancee au demarrage de Windows : pas de fenetre, juste l'ilot.</summary>
        public static bool Tray { get; private set; }
        /// <summary>Relancee apres une mise a jour.</summary>
        public static bool Updated { get; private set; }

        [STAThread]
        public static void Main(string[] args)
        {
            Demo = args.Contains("--demo");
            Tray = args.Contains("--tray");
            Updated = args.Contains("--updated");
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Crash(e.ExceptionObject as Exception);

            if (args.Contains("--uninstall"))
            {
                Installer.Uninstall(args.Contains("--quiet"));
                return;
            }
            // Tests automatiques : installation silencieuse
            if (args.Contains("--install-test"))
            {
                Installer.Install(true);
                return;
            }
            // Lancee depuis le fichier telecharge : on propose de l'installer comme une vraie appli
            if (!Demo && !Installer.IsInstalledCopy && !args.Contains("--portable"))
            {
                var setup = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                setup.Run(new InstallWindow());
                return;
            }

            // Une seule Wave Island a la fois : relancer l'appli ouvre simplement sa fenetre
            using var mutex = new Mutex(true, "WaveIsland.SingleInstance", out bool first);
            if (!first && !Demo)
            {
                if (!Tray && EventWaitHandle.TryOpenExisting("WaveIsland.Open", out var ev)) { ev.Set(); ev.Dispose(); }
                return;
            }

            var app = new App();
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Crash(e.Exception);
                e.Handled = true; // on garde l'ilot en vie
            };
            app.Run();
        }
    }
}
