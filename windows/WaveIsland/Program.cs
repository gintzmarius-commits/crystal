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

        [STAThread]
        public static void Main(string[] args)
        {
            Demo = args.Contains("--demo");
            // Une seule Wave Island a la fois
            using var mutex = new Mutex(true, "WaveIsland.SingleInstance", out bool first);
            if (!first && !Demo) return;

            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Crash(e.ExceptionObject as Exception);
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
