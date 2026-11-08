using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;

namespace WaveIsland
{
    /// <summary>
    /// Mesures de performance (tests automatiques seulement : rien n'est compte tant que [On] est faux).
    /// - au repos : processeur, operations du fil de l'interface (reveils), copies du fond, allocations, ramasse-miettes, memoire ;
    /// - pendant une animation : intervalle entre images (moyenne, 1 % les plus lentes) et temps de calcul de nos images.
    /// Resultats ajoutes a perf.txt dans le dossier des captures.
    /// </summary>
    public static class Perf
    {
        /// <summary>Vrai seulement pendant une mesure (le reste du temps : aucun cout).</summary>
        public static bool On;
        private static readonly Dictionary<string, int> _hits = new Dictionary<string, int>();
        private static long _frameTicks;
        private static int _frames;

        /// <summary>Compte un evenement (minuteur, copie du fond, image...).</summary>
        public static void Hit(string key)
        {
            if (!On) return;
            lock (_hits) _hits[key] = _hits.TryGetValue(key, out var v) ? v + 1 : 1;
        }

        /// <summary>Debut du calcul d'une image (renvoie l'horodatage a passer a [FrameEnd]).</summary>
        public static long FrameStart() => On ? Stopwatch.GetTimestamp() : 0;

        public static void FrameEnd(long start)
        {
            if (!On || start == 0) return;
            _frameTicks += Stopwatch.GetTimestamp() - start;
            _frames++;
        }

        public static string Folder;
        /// <summary>Temps entre le lancement du processus et l'interface au repos.</summary>
        public static double StartupMs;
        public static readonly DateTime ProcessStart = SafeStart();
        private static DateTime SafeStart() { try { return Process.GetCurrentProcess().StartTime; } catch { return DateTime.Now; } }

        public static void Write(string line)
        {
            Log.I("PERF " + line);
            try { if (Folder != null) { Directory.CreateDirectory(Folder); File.AppendAllText(Path.Combine(Folder, "perf.txt"), line + Environment.NewLine); } } catch { }
        }

        /// <summary>Temps de demarrage (appele une fois l'ilot affiche).</summary>
        public static void Startup(string what)
        {
            double ms = (DateTime.Now - ProcessStart).TotalMilliseconds;
            Write($"demarrage [{what}] : {ms:0} ms depuis le lancement du processus");
        }

        /// <summary>Mesure au repos pendant [seconds] : rien ne doit bouger a l'ecran.</summary>
        public static async Task Idle(string label, double seconds)
        {
            var p = Process.GetCurrentProcess();
            int ops = 0;
            DispatcherHookEventHandler count = (a, b) => ops++;
            var hooks = Dispatcher.CurrentDispatcher.Hooks;
            lock (_hits) _hits.Clear();
            _frameTicks = 0; _frames = 0;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            p.Refresh();
            var cpu0 = p.TotalProcessorTime;
            long alloc0 = GC.GetTotalAllocatedBytes(false);
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            var sw = Stopwatch.StartNew();
            On = true;
            hooks.OperationCompleted += count;
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            hooks.OperationCompleted -= count;
            On = false;
            double s = sw.Elapsed.TotalSeconds;
            p.Refresh();
            double cpuMs = (p.TotalProcessorTime - cpu0).TotalMilliseconds;
            long alloc = GC.GetTotalAllocatedBytes(false) - alloc0;
            var sb = new StringBuilder();
            sb.Append($"repos [{label}] {s:0.0} s : processeur {cpuMs / s / 10:0.00} % d'un coeur ({cpuMs / s / 10 / Environment.ProcessorCount:0.000} % du PC)");
            sb.Append($" | operations UI {ops / s:0.0}/s | allocations {alloc / s / 1024:0.0} Ko/s");
            sb.Append($" | GC g0={GC.CollectionCount(0) - g0} g1={GC.CollectionCount(1) - g1} g2={GC.CollectionCount(2) - g2}");
            sb.Append($" | memoire : privee {p.PrivateMemorySize64 / 1048576.0:0.0} Mo, ensemble de travail {p.WorkingSet64 / 1048576.0:0.0} Mo, tas .NET {GC.GetTotalMemory(false) / 1048576.0:0.0} Mo | fils {p.Threads.Count}");
            lock (_hits)
                if (_hits.Count > 0) sb.Append(" | par seconde : " + string.Join(", ", _hits.OrderByDescending(x => x.Value).Select(x => $"{x.Key}={x.Value / s:0.0}")));
            Write(sb.ToString());
        }

        /// <summary>Mesure pendant une animation lancee par [start] : intervalle entre les images et temps de calcul.</summary>
        public static async Task Animation(string label, Action start, double seconds)
        {
            var p = Process.GetCurrentProcess();
            var times = new List<double>(512);
            TimeSpan last = TimeSpan.Zero;
            EventHandler tick = (a, b) =>
            {
                if (b is RenderingEventArgs r)
                {
                    if (last != TimeSpan.Zero && r.RenderingTime != last) times.Add((r.RenderingTime - last).TotalMilliseconds);
                    last = r.RenderingTime;
                }
            };
            lock (_hits) _hits.Clear();
            _frameTicks = 0; _frames = 0;
            p.Refresh();
            var cpu0 = p.TotalProcessorTime;
            long alloc0 = GC.GetTotalAllocatedBytes(false);
            int g0 = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            On = true;
            CompositionTarget.Rendering += tick;
            start();
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            CompositionTarget.Rendering -= tick;
            On = false;
            double s = sw.Elapsed.TotalSeconds;
            p.Refresh();
            double cpuMs = (p.TotalProcessorTime - cpu0).TotalMilliseconds;
            long alloc = GC.GetTotalAllocatedBytes(false) - alloc0;
            var sb = new StringBuilder($"animation [{label}] {s:0.0} s : ");
            if (times.Count > 2)
            {
                times.Sort();
                double avg = times.Average(), p50 = times[times.Count / 2], p99 = times[Math.Min(times.Count - 1, (int)(times.Count * 0.99))];
                double low1 = times.Skip((int)(times.Count * 0.99)).DefaultIfEmpty(times.Last()).Average();
                sb.Append($"{times.Count + 1} images, {1000 / avg:0.0} i/s en moyenne (intervalle median {p50:0.0} ms, 99e centile {p99:0.0} ms, 1 % les plus lentes {1000 / low1:0.0} i/s)");
            }
            else sb.Append("pas assez d'images");
            if (_frames > 0) sb.Append($" | nos calculs : {_frameTicks * 1000.0 / Stopwatch.Frequency / _frames:0.000} ms par image ({_frames} images)");
            sb.Append($" | processeur {cpuMs / s / 10:0.0} % d'un coeur | allocations {alloc / s / 1024:0.0} Ko/s | GC g0={GC.CollectionCount(0) - g0}");
            lock (_hits)
                if (_hits.Count > 0) sb.Append(" | par seconde : " + string.Join(", ", _hits.OrderByDescending(x => x.Value).Select(x => $"{x.Key}={x.Value / s:0.0}")));
            Write(sb.ToString());
        }
    }
}
