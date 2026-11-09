using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Threading;

namespace WaveIsland
{
    /// <summary>
    /// Lot 4 : continuite (Handoff). La page web ouverte sur un appareil se continue sur l'autre.
    /// Telephone -> PC : quand tu quittes le navigateur du telephone, l'ilot du PC propose la page.
    /// PC -> telephone : quand le PC se verrouille (ou reste inactif) avec une page ouverte, le telephone la propose
    /// au deverrouillage.
    /// </summary>
    public static class Continuity
    {
        private static string _url, _title, _sentUrl;
        private static DateTime _lastNote = DateTime.MinValue;
        private static readonly string[] Browsers = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };

        public static void Start()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (a, b) => { Perf.Hit("continuite-4s"); Sample(); };
            t.Start();
            Power.Register(t);
            Microsoft.Win32.SystemEvents.SessionSwitch += (a, e) =>
            {
                if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock) Hand();
                if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock) App.Current?.Dispatcher.BeginInvoke(new Action(() => { Reminders.PcOpened(); Morning.Maybe(); }));
            };
            Microsoft.Win32.SystemEvents.PowerModeChanged += (a, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Suspend) Hand();
                if (e.Mode == Microsoft.Win32.PowerModes.Resume) App.Current?.Dispatcher.BeginInvoke(new Action(() => { Reminders.PcOpened(); Morning.Maybe(); }));
            };
        }

        /// <summary>Page recue du telephone : proposee dans l'ilot.</summary>
        public static void FromPhone(string from, string url, string title)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if ((DateTime.Now - _lastNote).TotalSeconds < 20) return;
            _lastNote = DateTime.Now;
            WaveSound.Play(WaveSound.Kind.Note);
            App.Island?.ShowNote(new IslandNote
            {
                App = "Continuité", Title = "Continuer sur le PC", Text = (string.IsNullOrWhiteSpace(title) ? url : title) + " · depuis " + from,
                OnClick = () => Shortcuts.Start(url)
            });
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        private static bool _busy;

        /// <summary>Navigateur au premier plan : on note sa page (lecture de la barre d'adresse, hors du fil de l'interface).</summary>
        [DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
        private static IntPtr _lastFg;
        private static string _lastTitle;
        private static readonly System.Text.StringBuilder _titleBuf = new System.Text.StringBuilder(512);

        private static void Sample()
        {
            if (_busy || !Settings.Current.Handoff || WaveLink.Linked().Count == 0) return;
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return;
            // Meme fenetre, meme titre : meme page, on ne relit pas la barre d'adresse (lecture couteuse pour le navigateur)
            _titleBuf.Clear();
            GetWindowText(fg, _titleBuf, _titleBuf.Capacity);
            string title = _titleBuf.ToString();
            if (fg == _lastFg && title == _lastTitle) return;
            _lastFg = fg;
            _lastTitle = title;
            GetWindowThreadProcessId(fg, out uint pid);
            string name;
            try { name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { return; }
            if (!Browsers.Contains(name)) return;
            _busy = true;
            Task.Run(() =>
            {
                try
                {
                    var root = AutomationElement.FromHandle(fg);
                    var edit = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                    if (edit != null && edit.TryGetCurrentPattern(ValuePattern.Pattern, out var p))
                    {
                        string v = ((ValuePattern)p).Current.Value?.Trim();
                        if (!string.IsNullOrEmpty(v) && !v.Contains(' ') && v.Contains('.'))
                        {
                            _url = v.Contains("://") ? v : "https://" + v;
                            _title = root.Current.Name;
                            foreach (var suffix in new[] { " - Google Chrome", " — Mozilla Firefox", " - Mozilla Firefox", " - Microsoft​ Edge", " - Microsoft Edge", " - Brave", " - Opera" })
                                if (_title.EndsWith(suffix)) _title = _title.Substring(0, _title.Length - suffix.Length);
                        }
                    }
                }
                catch { }
                finally { _busy = false; }
            });
        }

        /// <summary>Le PC se verrouille / se met en veille : la derniere page part vers le telephone.</summary>
        private static async void Hand()
        {
            if (!Settings.Current.Handoff || _url == null || _url == _sentUrl) return;
            _sentUrl = _url;
            int n = await WaveLink.SendAll(WaveLink.Msg("handoff", ("url", _url), ("title", _title)));
            if (n > 0) Log.I("Continuite : page envoyee au telephone");
        }
    }

    /// <summary>
    /// Lot 4 : rappels intelligents (« rappelle-moi d'appeler Léo dans 2 h / à 18 h / quand j'ouvre le PC »).
    /// Crees par l'assistant (ou recus du telephone) ; l'ilot les montre au bon moment, avec un son et la voix.
    /// </summary>
    public static class Reminders
    {
        // "ticks|kind|texte" ; kind = time ou pc
        public static void Add(string text, DateTime at, bool share = true, string kind = "time")
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var s = Settings.Current;
            s.Reminders ??= new List<string>();
            s.Reminders.Add($"{at.Ticks}|{kind}|{text.Replace("\n", " ")}");
            s.Save();
            Schedule();   // le minuteur vise le prochain rappel
            Log.I($"Rappel : « {text} » ({(kind == "pc" ? "a l'ouverture du PC" : at.ToString("g"))})");
            if (share && kind == "time")
                _ = WaveLink.SendAll(WaveLink.Msg("remind", ("text", text), ("at", new DateTimeOffset(at).ToUnixTimeMilliseconds().ToString())));
        }

        public static int Today() => (Settings.Current.Reminders ?? new List<string>()).Count(r =>
        {
            var p = r.Split('|');
            return p.Length >= 3 && long.TryParse(p[0], out long t) && new DateTime(t).Date == DateTime.Today && p[1] == "time";
        });

        private static DispatcherTimer _timer;

        /// <summary>Le minuteur ne tourne que s'il y a un rappel a l'heure, et vise ce rappel (avant : toutes les 15 s, toujours).</summary>
        public static void Start()
        {
            _timer = new DispatcherTimer();
            _timer.Tick += (a, b) => { Perf.Hit("rappels"); Check("time"); Schedule(); };
            Schedule();
        }

        private static void Schedule()
        {
            if (_timer == null) return;
            var s = Settings.Current;
            DateTime next = DateTime.MaxValue;
            foreach (var r in s.Reminders ?? new List<string>())
            {
                var p = r.Split('|');
                if (p.Length >= 3 && p[1] == "time" && long.TryParse(p[0], out long t) && t < next.Ticks) next = new DateTime(t);
            }
            if (next == DateTime.MaxValue) { _timer.Stop(); return; }
            var wait = next - DateTime.Now;
            if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
            if (wait > TimeSpan.FromMinutes(10)) wait = TimeSpan.FromMinutes(10);   // horloge du PC changee, veille : on revérifie
            _timer.Interval = wait;
            _timer.Start();
        }

        public static void PcOpened() => Check("pc");

        private static void Check(string kind)
        {
            var s = Settings.Current;
            if (s.Reminders == null || s.Reminders.Count == 0) return;
            foreach (var r in s.Reminders.ToList())
            {
                var p = r.Split('|');
                if (p.Length < 3 || !long.TryParse(p[0], out long ticks)) { s.Reminders.Remove(r); continue; }
                if (p[1] != kind) continue;
                if (kind == "time" && new DateTime(ticks) > DateTime.Now) continue;
                s.Reminders.Remove(r);
                string text = string.Join("|", p.Skip(2));
                WaveSound.Play(WaveSound.Kind.Note);
                App.Island?.ShowNote(new IslandNote { App = "Rappel", Title = "Rappel", Text = text });
                if (Settings.Current.AiVoice) _ = Voice.Speak("Rappel : " + text);
            }
            s.Save();
        }
    }

    /// <summary>
    /// Lot 4 : resume du matin. Au premier deverrouillage de la journee (apres 5 h) : meteo du jour,
    /// batterie du telephone, rappels du jour et notifications de la nuit, dans l'ilot (et lu a voix haute si la voix est activee).
    /// </summary>
    public static class Morning
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        public static async void Maybe(bool force = false)
        {
            var s = Settings.Current;
            if (!force && (!s.MorningBrief || DateTime.Now.Hour < 5 || s.MorningDone == DateTime.Today.ToString("yyyy-MM-dd"))) return;
            s.MorningDone = DateTime.Today.ToString("yyyy-MM-dd");
            s.Save();
            var parts = new List<string>();
            string weather = await Weather();
            if (weather != null) parts.Add(weather);
            if (WaveLink.Battery >= 0 && (DateTime.Now - WaveLink.BatteryAt).TotalHours < 3)
                parts.Add($"téléphone à {WaveLink.Battery} %{(WaveLink.Charging ? " (en charge)" : "")}");
            int r = Reminders.Today();
            if (r > 0) parts.Add(r + (r > 1 ? " rappels aujourd'hui" : " rappel aujourd'hui"));
            int notes = App.NoteHistory.Count(n => n.Item2 > DateTime.Today.AddHours(-6));
            if (notes > 0) parts.Add(notes + (notes > 1 ? " notifications depuis hier soir" : " notification depuis hier soir"));
            string day = DateTime.Now.ToString("dddd d MMMM", new CultureInfo("fr-FR"));
            string text = parts.Count > 0 ? string.Join(" · ", parts) : "Belle journée !";
            Log.I("Resume du matin : " + text);
            WaveSound.Play(WaveSound.Kind.Open);
            App.Island?.ShowNote(new IslandNote { App = "Bonjour", Title = "Bonjour ! On est " + day, Text = text });
            if (s.AiVoice && s.MorningVoice) _ = Voice.Speak("Bonjour ! " + string.Join(". ", parts) + ".");
        }

        /// <summary>Meteo du jour (Open-Meteo, sans cle) a l'endroit approximatif du PC (adresse internet).</summary>
        private static async Task<string> Weather()
        {
            try
            {
                var loc = JsonNode.Parse(await Http.GetStringAsync("https://ipapi.co/json/"));
                double lat = loc["latitude"].GetValue<double>(), lon = loc["longitude"].GetValue<double>();
                string city = loc["city"]?.GetValue<string>() ?? "";
                var w = JsonNode.Parse(await Http.GetStringAsync(string.Format(CultureInfo.InvariantCulture,
                    "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=auto&forecast_days=1", lat, lon)));
                double now = w["current"]["temperature_2m"].GetValue<double>();
                int code = w["current"]["weather_code"].GetValue<int>();
                double max = w["daily"]["temperature_2m_max"][0].GetValue<double>();
                double min = w["daily"]["temperature_2m_min"][0].GetValue<double>();
                int rain = w["daily"]["precipitation_probability_max"][0]?.GetValue<int>() ?? 0;
                string sky = code == 0 ? "ciel dégagé" : code <= 3 ? "quelques nuages" : code <= 48 ? "brouillard" : code <= 67 ? "pluie" : code <= 77 ? "neige" : code <= 82 ? "averses" : "orages";
                return $"{city} {Math.Round(now)}°, {sky} (de {Math.Round(min)}° à {Math.Round(max)}°{(rain >= 40 ? $", pluie {rain} %" : "")})";
            }
            catch (Exception e) { Log.I("Meteo indisponible : " + e.Message); return null; }
        }
    }
}
