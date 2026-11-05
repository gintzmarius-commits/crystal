using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace WaveIsland
{
    /// <summary>Lot 1 : teinte du verre (ilot, dock, panneaux).</summary>
    public static class WaveColor
    {
        /// <summary>Nom affiche, cle, couleur (0..1).</summary>
        public static readonly (string name, string key, double r, double g, double b)[] All =
        {
            ("Transparent", "", 0, 0, 0),
            ("Océan", "ocean", 0.20, 0.55, 0.95),
            ("Rosé", "rose", 0.98, 0.45, 0.68),
            ("Menthe", "menthe", 0.25, 0.85, 0.65),
            ("Soleil", "soleil", 1.00, 0.72, 0.25),
            ("Lavande", "lavande", 0.62, 0.50, 0.98),
            ("Nuit", "nuit", 0.06, 0.07, 0.12),
        };

        public static event Action Changed;

        public static string Key => Settings.Current.GlassColor ?? "";

        public static void Set(string key)
        {
            Settings.Current.GlassColor = key;
            Settings.Current.Save();
            Changed?.Invoke();
        }

        /// <summary>Teinte du verre : la couleur choisie (legere), sinon celle d'origine.</summary>
        public static Point4D Tint(Point4D original)
        {
            foreach (var c in All)
                if (c.key == Key && c.key != "")
                    return new Point4D(c.r, c.g, c.b, Math.Max(original.W, c.key == "nuit" ? 0.42 : 0.16));
            return original;
        }
    }

    /// <summary>
    /// Lot 1 : sons Wave. Petits sons de goutte d'eau fabriques par le programme (pas de fichiers),
    /// joues sans attendre. Desactivables dans les reglages.
    /// </summary>
    public static class WaveSound
    {
        public enum Kind { Open, Close, Note, Land, Listen }
        private static readonly Dictionary<Kind, byte[]> Cache = new Dictionary<Kind, byte[]>();
        private static DateTime _last = DateTime.MinValue;

        public static void Play(Kind k)
        {
            if (!Settings.Current.Sounds) return;
            // Pas de pluie de sons (plusieurs icones qui arrivent ensemble)
            if ((DateTime.Now - _last).TotalMilliseconds < 70) return;
            _last = DateTime.Now;
            Task.Run(() =>
            {
                try
                {
                    byte[] wav;
                    lock (Cache) { if (!Cache.TryGetValue(k, out wav)) Cache[k] = wav = Make(k); }
                    using var ms = new MemoryStream(wav);
                    using var p = new SoundPlayer(ms);
                    p.PlaySync();
                }
                catch { }
            });
        }

        /// <summary>Une goutte : un « bloup » dont la note glisse (vers le haut a l'ouverture, vers le bas a la fermeture).</summary>
        private static byte[] Make(Kind k)
        {
            const int rate = 44100;
            var (f0, f1, ms, vol, second) = k switch
            {
                Kind.Open => (520.0, 980.0, 110, 0.22, 0.0),
                Kind.Close => (900.0, 420.0, 110, 0.18, 0.0),
                Kind.Note => (760.0, 1180.0, 90, 0.20, 1320.0),
                Kind.Land => (300.0, 620.0, 85, 0.20, 0.0),
                _ => (640.0, 1280.0, 140, 0.20, 0.0),
            };
            int n1 = rate * ms / 1000, gap = second > 0 ? rate * 50 / 1000 : 0, n2 = second > 0 ? rate * 90 / 1000 : 0;
            int n = n1 + gap + n2;
            var samples = new short[n];
            double phase = 0;
            for (int i = 0; i < n1; i++)
            {
                double t = (double)i / n1;
                double f = f0 + (f1 - f0) * (1 - Math.Pow(1 - t, 2.2));
                phase += 2 * Math.PI * f / rate;
                double env = Math.Min(1, i / (rate * 0.004)) * Math.Pow(1 - t, 2.4);
                samples[i] = (short)(Math.Sin(phase) * env * vol * short.MaxValue);
            }
            phase = 0;
            for (int i = 0; i < n2; i++)
            {
                double t = (double)i / n2;
                double f = second * (1 + 0.15 * t);
                phase += 2 * Math.PI * f / rate;
                double env = Math.Min(1, i / (rate * 0.004)) * Math.Pow(1 - t, 2.6);
                samples[n1 + gap + i] = (short)(Math.Sin(phase) * env * vol * 0.8 * short.MaxValue);
            }
            using var ms2 = new MemoryStream();
            using var w = new BinaryWriter(ms2);
            int bytes = n * 2;
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + bytes); w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(bytes);
            foreach (var s in samples) w.Write(s);
            return ms2.ToArray();
        }
    }

    /// <summary>
    /// Lot 1 : mode concentration. Les notifications ne s'affichent plus (elles sont gardees dans le centre
    /// de notifications) et un resume apparait a la fin. Manuel ou par horaires.
    /// </summary>
    public static class FocusMode
    {
        public static event Action Changed;
        private static int _missed;
        private static bool _was;

        public static bool On
        {
            get
            {
                var s = Settings.Current;
                if (s.FocusOn) return true;
                if (!s.FocusScheduled) return false;
                int h = DateTime.Now.Hour;
                return s.FocusFrom <= s.FocusTo ? h >= s.FocusFrom && h < s.FocusTo : h >= s.FocusFrom || h < s.FocusTo;
            }
        }

        public static void Toggle()
        {
            Settings.Current.FocusOn = !Settings.Current.FocusOn;
            Settings.Current.Save();
            Check();
        }

        /// <summary>Notification pendant la concentration : comptee, pas affichee.</summary>
        public static bool Swallow()
        {
            if (!On) return false;
            _missed++;
            return true;
        }

        /// <summary>Debut / fin (aussi par horaire) : petit message dans l'ilot.</summary>
        public static void Check()
        {
            bool on = On;
            if (on == _was) return;
            _was = on;
            Log.I("Concentration : " + (on ? "activee" : "terminee"));
            if (on)
            {
                _missed = 0;
                App.Island?.ShowNote(new IslandNote { App = "Concentration", Title = "Concentration activée", Text = "Les notifications attendront la fin." });
            }
            else
            {
                App.Island?.ShowNote(new IslandNote
                {
                    App = "Concentration", Title = "Concentration terminée",
                    Text = _missed == 0 ? "Aucune notification manquée." : _missed + (_missed > 1 ? " notifications t'attendent" : " notification t'attend") + " dans le centre de notifications."
                });
                _missed = 0;
            }
            Changed?.Invoke();
        }
    }
}
