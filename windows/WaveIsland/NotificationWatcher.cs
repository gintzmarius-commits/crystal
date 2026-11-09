using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using System.Xml.Linq;

namespace WaveIsland
{
    public class IslandNote
    {
        public string App, Title, Text, Aumid, Icon;
        /// <summary>Action au clic (sinon : ouvre l'appli de la notification).</summary>
        public Action OnClick;
    }

    /// <summary>
    /// Lit les notifications de Windows dans sa base locale (wpndatabase.db), sans rien modifier.
    /// Les nouvelles notifications s'affichent dans l'ilot.
    /// </summary>
    public sealed class NotificationWatcher : IDisposable
    {
        public event Action<IslandNote> Arrived;

        private readonly Dispatcher _ui;
        private readonly string _folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications");
        private string Db => Path.Combine(_folder, "wpndatabase.db");
        private FileSystemWatcher _watcher;
        private DispatcherTimer _debounce, _poll;
        private long _lastId = -1;
        private bool _copyMode;

        public NotificationWatcher(Dispatcher ui) { _ui = ui; }

        public void Start()
        {
            if (!File.Exists(Db)) { Log.I("Base des notifications introuvable : " + Db); return; }
            _lastId = MaxId();
            Log.I($"Notifications : surveillance active (dernier n° {_lastId}, copie={_copyMode})");
            _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (s, e) => { _debounce.Stop(); Check(); }, _ui);
            _debounce.Stop();
            // Le dossier est surveille (Windows previent a chaque ecriture) : la relecture reguliere n'est qu'un filet de securite
            // (avant : la base etait ouverte et relue toutes les 4 s)
            _poll = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (s, e) => { Perf.Hit("notifs-sondage"); Check(); }, _ui);
            Power.Register(_poll);
            try
            {
                _watcher = new FileSystemWatcher(_folder, "wpndatabase.db*")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                _watcher.Changed += (s, e) => _ui.BeginInvoke(new Action(() => { _debounce.Stop(); _debounce.Start(); }));
            }
            catch (Exception e) { Log.I("Surveillance du dossier impossible : " + e.Message); _poll.Interval = TimeSpan.FromSeconds(4); }
        }

        private void Check()
        {
            if (!Settings.Current.Notifications || _lastId < 0) return;
            try
            {
                foreach (var n in ReadNew()) Arrived?.Invoke(n);
            }
            catch (Exception e) { Log.I("Lecture des notifications : " + e.Message); }
        }

        // ---------- SQLite (winsqlite3.dll, fourni avec Windows) ----------

        [DllImport("winsqlite3.dll")] private static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll")] private static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll")] private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_column_bytes(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll")] private static extern IntPtr sqlite3_errmsg(IntPtr db);
        private const int SQLITE_OPEN_READONLY = 1, SQLITE_ROW = 100;

        private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

        private IntPtr Open()
        {
            string path = Db;
            if (_copyMode)
            {
                // Copie de la base (et de son journal) dans un dossier temporaire, lue ensuite tranquillement
                string tmp = Path.Combine(Path.GetTempPath(), "WaveIsland-notifs");
                Directory.CreateDirectory(tmp);
                foreach (var ext in new[] { "", "-wal", "-shm" })
                {
                    string src = Db + ext, dst = Path.Combine(tmp, "n.db" + ext);
                    if (!File.Exists(src)) { if (File.Exists(dst)) File.Delete(dst); continue; }
                    using var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var o = new FileStream(dst, FileMode.Create, FileAccess.Write);
                    i.CopyTo(o);
                }
                path = Path.Combine(tmp, "n.db");
            }
            int rc = sqlite3_open_v2(Utf8(path), out IntPtr db, _copyMode ? 2 : SQLITE_OPEN_READONLY, IntPtr.Zero);
            if (rc != 0) { sqlite3_close(db); return IntPtr.Zero; }
            sqlite3_busy_timeout(db, 500);
            return db;
        }

        private long MaxId()
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                IntPtr db = Open();
                if (db != IntPtr.Zero)
                {
                    try
                    {
                        if (sqlite3_prepare_v2(db, Utf8("SELECT MAX(Id) FROM Notification"), -1, out IntPtr st, IntPtr.Zero) == 0)
                        {
                            try { if (sqlite3_step(st) == SQLITE_ROW) return sqlite3_column_int64(st, 0); }
                            finally { sqlite3_finalize(st); }
                        }
                        Log.I("Base des notifications : " + Marshal.PtrToStringUTF8(sqlite3_errmsg(db)));
                    }
                    finally { sqlite3_close(db); }
                }
                _copyMode = true; // lecture directe impossible : on passe par une copie
            }
            return -1;
        }

        private IslandNote[] ReadNew()
        {
            IntPtr db = Open();
            if (db == IntPtr.Zero) return Array.Empty<IslandNote>();
            var list = new System.Collections.Generic.List<IslandNote>();
            try
            {
                string sql = "SELECT n.Id, n.Payload, h.PrimaryId FROM Notification n " +
                             "LEFT JOIN NotificationHandler h ON n.HandlerId = h.RecordId " +
                             $"WHERE n.Type = 'toast' AND n.Id > {_lastId} ORDER BY n.Id";
                if (sqlite3_prepare_v2(db, Utf8(sql), -1, out IntPtr st, IntPtr.Zero) != 0)
                {
                    Log.I("Requete des notifications : " + Marshal.PtrToStringUTF8(sqlite3_errmsg(db)));
                    return Array.Empty<IslandNote>();
                }
                try
                {
                    while (sqlite3_step(st) == SQLITE_ROW)
                    {
                        long id = sqlite3_column_int64(st, 0);
                        _lastId = Math.Max(_lastId, id);
                        string payload = Blob(st, 1);
                        string aumid = Blob(st, 2);
                        var note = Parse(payload, aumid);
                        if (note != null)
                        {
                            Log.I($"Notification lue : {note.App} / {note.Title}");
                            list.Add(note);
                        }
                    }
                }
                finally { sqlite3_finalize(st); }
            }
            finally { sqlite3_close(db); }
            return list.ToArray();
        }

        private static string Blob(IntPtr st, int col)
        {
            int n = sqlite3_column_bytes(st, col);
            if (n <= 0) return "";
            var bytes = new byte[n];
            Marshal.Copy(sqlite3_column_blob(st, col), bytes, 0, n);
            return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }

        private static IslandNote Parse(string xml, string aumid)
        {
            try
            {
                var doc = XDocument.Parse(xml);
                var texts = doc.Descendants().Where(e => e.Name.LocalName == "text").Select(e => e.Value.Trim())
                    .Where(t => t.Length > 0).ToList();
                if (texts.Count == 0) return null;
                string icon = doc.Descendants().Where(e => e.Name.LocalName == "image")
                    .Select(e => (string)e.Attribute("src")).FirstOrDefault(s => !string.IsNullOrEmpty(s));
                return new IslandNote
                {
                    App = AppName(aumid),
                    Title = texts[0],
                    Text = string.Join(" ", texts.Skip(1)),
                    Aumid = aumid,
                    Icon = icon
                };
            }
            catch { return null; }
        }

        /// <summary>Nom lisible de l'appli a partir de son identifiant Windows.</summary>
        public static string AppName(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return "Notification";
            string s = aumid;
            int bang = s.IndexOf('!');
            if (bang > 0) s = s.Substring(0, bang);
            s = s.Split('\\', '/').Last();
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            int us = s.IndexOf('_');
            if (us > 0) s = s.Substring(0, us);
            var parts = s.Split('.');
            s = parts.Length > 1 ? parts.Last() : s;
            return s.Length == 0 ? "Notification" : char.ToUpper(s[0]) + s.Substring(1);
        }

        public void Dispose()
        {
            _watcher?.Dispose();
            _poll?.Stop();
            _debounce?.Stop();
        }
    }
}
