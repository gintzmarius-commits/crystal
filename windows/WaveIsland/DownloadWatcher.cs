using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace WaveIsland
{
    public class DownloadInfo
    {
        public string Path, Name;
        public long Bytes, Total;
        public double Speed; // octets par seconde
        public DateTime LastGrowth = DateTime.Now;
        public bool Stalled => (DateTime.Now - LastGrowth).TotalSeconds > 8;
        public double Progress => Total > 0 ? Math.Min(1, (double)Bytes / Total) : -1;
    }

    /// <summary>
    /// Telechargements en cours dans le dossier Telechargements (Chrome, Edge, Brave, Opera, Firefox...).
    /// La taille totale est lue dans l'historique de Chrome / Edge / Brave quand c'est possible.
    /// </summary>
    public sealed class DownloadWatcher
    {
        public event Action<IReadOnlyList<DownloadInfo>> Changed;
        public event Action<string> Finished;

        private static readonly string[] TempExt = { ".crdownload", ".part", ".partial", ".download", ".opdownload", ".tmp.part" };
        private readonly Dispatcher _ui;
        private readonly Dictionary<string, DownloadInfo> _active = new Dictionary<string, DownloadInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _lookups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastLookup = DateTime.MinValue;
        private string _folder;
        private DispatcherTimer _timer;
        private string _signature = "";

        public DownloadWatcher(Dispatcher ui) { _ui = ui; }

        public string Folder => _folder;

        public void Start()
        {
            _folder = Environment.GetEnvironmentVariable("WAVE_DOWNLOADS") ?? Native.KnownFolder(Native.FOLDERID_Downloads)
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Log.I("Telechargements surveilles : " + _folder);
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (a, b) => { Perf.Hit("telechargements"); Tick(); }, _ui);
            Power.Register(_timer);
            // Le dossier n'est relu chaque seconde que pendant un telechargement : Windows previent quand un fichier
            // y apparait ou change (avant : relu chaque seconde en permanence)
            try
            {
                if (Directory.Exists(_folder))
                {
                    _fsw = new FileSystemWatcher(_folder) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite, IncludeSubdirectories = false };
                    FileSystemEventHandler wake = (a, e) => { if (IsTemp(e.FullPath) || (e is RenamedEventArgs r && IsTemp(r.OldFullPath))) Wake(); };
                    _fsw.Created += wake;
                    _fsw.Changed += wake;
                    _fsw.Deleted += wake;
                    _fsw.Renamed += (a, e) => wake(a, e);
                    _fsw.Error += (a, e) => Wake();
                    _fsw.EnableRaisingEvents = true;
                    _eventDriven = true;
                }
            }
            catch (Exception e) { Log.I("Telechargements : surveillance par evenements impossible (" + e.Message + ")"); }
        }

        private FileSystemWatcher _fsw;
        private bool _eventDriven;

        /// <summary>Un fichier temporaire de telechargement a bouge : la surveillance seconde par seconde reprend.</summary>
        private void Wake() => _ui.BeginInvoke(new Action(() => { if (!_timer.IsEnabled && !Power.Asleep) { _timer.Start(); Tick(); } }));

        public static bool IsTemp(string path) => TempExt.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        private static string FinalName(string path)
        {
            foreach (var e in TempExt.OrderByDescending(x => x.Length))
                if (path.EndsWith(e, StringComparison.OrdinalIgnoreCase)) return path.Substring(0, path.Length - e.Length);
            return path;
        }

        private void Tick()
        {
            if (!Settings.Current.LiveDownloads || _folder == null || !Directory.Exists(_folder))
            {
                if (_active.Count > 0) { _active.Clear(); Publish(); }
                if (_eventDriven) _timer.Stop();
                return;
            }
            var now = DateTime.Now;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Directory.EnumerateFiles(_folder))
                {
                    if (!IsTemp(f)) continue;
                    var fi = new FileInfo(f);
                    // Un vieux fichier abandonne n'est pas un telechargement en cours
                    if ((now - fi.LastWriteTime).TotalMinutes > 3 && !_active.ContainsKey(f)) continue;
                    seen.Add(f);
                    if (!_active.TryGetValue(f, out var d))
                    {
                        d = new DownloadInfo { Path = f, Bytes = fi.Length };
                        _active[f] = d;
                        Log.I("Telechargement en cours : " + Path.GetFileName(f));
                    }
                    string name = Path.GetFileName(FinalName(f));
                    d.Name = name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) ? "Téléchargement" : name;
                    long grown = fi.Length - d.Bytes;
                    double inst = Math.Max(0, grown); // par seconde (le minuteur tourne chaque seconde)
                    d.Speed = d.Speed <= 0 ? inst : d.Speed * 0.6 + inst * 0.4;
                    if (grown > 0) d.LastGrowth = now;
                    d.Bytes = fi.Length;
                }
            }
            catch (Exception e) { Log.I("Telechargements : " + e.Message); }

            // Termines (le fichier temporaire a disparu)
            foreach (var gone in _active.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                var d = _active[gone];
                _active.Remove(gone);
                _lookups.Remove(gone);
                string final = FinalName(gone);
                if (!File.Exists(final))
                {
                    // Nom definitif different (« Unconfirmed ... ») : le fichier le plus recent
                    final = Directory.EnumerateFiles(_folder).Where(x => !IsTemp(x))
                        .Select(x => new FileInfo(x)).Where(x => (now - x.LastWriteTime).TotalSeconds < 10)
                        .OrderByDescending(x => x.LastWriteTime).FirstOrDefault()?.FullName;
                }
                if (final != null)
                {
                    Log.I("Telechargement termine : " + Path.GetFileName(final));
                    Finished?.Invoke(final);
                }
            }

            // Taille totale depuis l'historique du navigateur (quelques essais, pas trop souvent)
            var unknown = _active.Values.FirstOrDefault(d => d.Total <= 0 && (!_lookups.TryGetValue(d.Path, out int n) || n < 5));
            if (unknown != null && (now - _lastLookup).TotalSeconds > 4)
            {
                _lastLookup = now;
                _lookups[unknown.Path] = (_lookups.TryGetValue(unknown.Path, out int n) ? n : 0) + 1;
                unknown.Total = BrowserTotal(unknown.Path);
                if (unknown.Total > 0) Log.I($"Taille totale trouvee : {unknown.Total} octets");
            }
            Publish();
            // Plus rien en cours : on attend le prochain evenement du dossier
            if (_eventDriven && _active.Count == 0) _timer.Stop();
        }

        private void Publish()
        {
            var list = _active.Values.OrderBy(d => d.Path).ToList();
            string sig = string.Join("|", list.Select(d => d.Path + ":" + d.Bytes + ":" + d.Total));
            if (sig == _signature) return;
            _signature = sig;
            Changed?.Invoke(list);
        }

        /// <summary>Cherche la taille totale d'un telechargement dans l'historique des navigateurs bases sur Chromium.</summary>
        private static long BrowserTotal(string tempPath)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var roots = new[]
            {
                Path.Combine(local, "Google", "Chrome", "User Data"),
                Path.Combine(local, "Microsoft", "Edge", "User Data"),
                Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
                Path.Combine(local, "Vivaldi", "User Data"),
                Path.Combine(roaming, "Opera Software", "Opera Stable"),
                Path.Combine(roaming, "Opera Software", "Opera GX Stable"),
            };
            string final = FinalName(tempPath);
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                var histories = new List<string>();
                if (File.Exists(Path.Combine(root, "History"))) histories.Add(Path.Combine(root, "History"));
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        string n = Path.GetFileName(dir);
                        if (n == "Default" || n.StartsWith("Profile ")) histories.Add(Path.Combine(dir, "History"));
                    }
                }
                catch { }
                foreach (var h in histories.Where(File.Exists))
                {
                    try
                    {
                        // On evite de recopier une enorme base pour rien
                        if (new FileInfo(h).Length > 300L * 1024 * 1024) continue;
                        using var db = Sqlite.Open(h, true);
                        if (db == null) continue;
                        long total = 0;
                        db.Query("SELECT total_bytes FROM downloads WHERE current_path = " + Sqlite.Quote(tempPath) +
                                 " OR target_path = " + Sqlite.Quote(final) + " ORDER BY id DESC LIMIT 1",
                            r => total = r.Long(0));
                        if (total > 0) return total;
                    }
                    catch { }
                }
            }
            return 0;
        }
    }
}
