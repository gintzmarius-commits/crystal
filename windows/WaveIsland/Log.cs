using System;
using System.IO;

namespace WaveIsland
{
    /// <summary>Petit journal dans %APPDATA%\WaveIsland\journal.txt (pour les rapports de bug).</summary>
    public static class Log
    {
        public static readonly string Folder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaveIsland");
        public static string File => Path.Combine(Folder, "journal.txt");
        public static string CrashFile => Path.Combine(Folder, "dernier-plantage.txt");
        private static readonly object Gate = new object();

        public static void I(string msg)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Folder);
                    var fi = new FileInfo(File);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    System.IO.File.AppendAllText(File, $"{DateTime.Now:dd/MM HH:mm:ss.fff} {msg}{Environment.NewLine}");
                }
            }
            catch { }
        }

        public static void Crash(Exception e)
        {
            if (e == null) return;
            I("PLANTAGE : " + e);
            try { System.IO.File.WriteAllText(CrashFile, $"Plantage le {DateTime.Now:dd/MM HH:mm:ss}{Environment.NewLine}{e}"); } catch { }
        }
    }
}
