using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WaveIsland.Drop;

namespace WaveIsland
{
    /// <summary>
    /// Lot 4 : Wave Link, le lien permanent entre le PC et le telephone (par Wave Drop, sur le meme Wi-Fi).
    /// Une fois lies (une seule fois, chaque cote accepte) : batterie du telephone sur le PC,
    /// copier sur l'un / coller sur l'autre, faire sonner l'autre appareil, continuite, rappels.
    /// Messages chiffres comme les fichiers Wave Drop ; un appareil non lie est ignore.
    /// </summary>
    public static class WaveLink
    {
        public static event Action Changed;
        private static Dispatcher _ui;

        // ---- Appareils lies : "id|nom|ip|port" ----

        public sealed class Device { public int Id; public string Name, Ip; public int Port; }

        public static List<Device> Linked()
        {
            var list = new List<Device>();
            foreach (var s in Settings.Current.Linked ?? new List<string>())
            {
                var p = s.Split('|');
                if (p.Length < 4 || !int.TryParse(p[0], out int id)) continue;
                list.Add(new Device { Id = id, Name = p[1], Ip = p[2], Port = int.TryParse(p[3], out int port) ? port : 0 });
            }
            return list;
        }

        public static bool IsLinked(int id) => Linked().Any(d => d.Id == id);

        private static void Save(int id, string name, string ip, int port)
        {
            var s = Settings.Current;
            s.Linked ??= new List<string>();
            var old = Linked().FirstOrDefault(d => d.Id == id);
            s.Linked.RemoveAll(x => x.StartsWith(id + "|"));
            s.Linked.Add($"{id}|{(name ?? old?.Name ?? "Téléphone").Replace("|", " ")}|{ip ?? old?.Ip ?? ""}|{(port > 0 ? port : old?.Port ?? 0)}");
            s.Save();
            _ui?.BeginInvoke(new Action(() => Changed?.Invoke()));
        }

        public static void Unlink(int id)
        {
            Settings.Current.Linked?.RemoveAll(x => x.StartsWith(id + "|"));
            Settings.Current.Save();
            Changed?.Invoke();
        }

        // ---- Etat du telephone ----

        public static int Battery { get; private set; } = -1;
        public static bool Charging { get; private set; }
        public static DateTime BatteryAt { get; private set; }
        public static string PhoneName { get; private set; }
        private static DateTime _lowNoteAt = DateTime.MinValue;

        public static void Start(Dispatcher ui)
        {
            _ui = ui;
            // Presse-papiers du PC -> telephone (texte), si active
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _clipSeq = GetClipboardSequenceNumber();
            t.Tick += (a, b) => WatchClipboard();
            t.Start();
        }

        // ================== Reception (appelee par Wave Drop, hors du fil de l'interface) ==================

        public static Dictionary<string, string> Receive(Dictionary<string, string> m, int fromId, string fromName, string ip)
        {
            string t = m.TryGetValue("t", out var tt) ? tt : "";
            int port = m.TryGetValue("port", out var ps) && int.TryParse(ps, out int pp) ? pp : 0;
            Log.I($"Wave Link : « {t} » de {fromName}");
            if (t == "pair")
            {
                if (IsLinked(fromId)) { Save(fromId, fromName, ip, port); return Ok(); }
                _ui.BeginInvoke(new Action(() => App.Island?.ShowNote(new IslandNote
                {
                    App = "Wave Link", Title = "Lier « " + fromName + " » ?", Text = "Touche pour accepter : batterie, presse-papiers, sonnerie, continuité",
                    OnClick = () => { Save(fromId, fromName, ip, port); _ = Send(fromId, Msg("pair-ok")); Note("Lié à " + fromName, "Les deux appareils sont maintenant liés."); }
                })));
                return new Dictionary<string, string> { ["ok"] = "pending" };
            }
            if (t == "pair-ok")
            {
                if (_pairing.Contains(fromId)) { _pairing.Remove(fromId); Save(fromId, fromName, ip, port); Note("Lié à " + fromName, "Les deux appareils sont maintenant liés."); }
                return Ok();
            }
            if (!IsLinked(fromId)) return new Dictionary<string, string> { ["ok"] = "0", ["err"] = "pas lie" };
            // Adresse a jour (le telephone change d'adresse Wi-Fi de temps en temps)
            if (!string.IsNullOrEmpty(ip) && port > 0) { var d = Linked().First(x => x.Id == fromId); if (d.Ip != ip || d.Port != port) Save(fromId, fromName, ip, port); }
            PhoneName = fromName;
            switch (t)
            {
                case "battery":
                    Battery = int.TryParse(m.GetValueOrDefault("level"), out int lv) ? lv : -1;
                    Charging = m.GetValueOrDefault("charging") == "1";
                    BatteryAt = DateTime.Now;
                    _ui.BeginInvoke(new Action(() =>
                    {
                        Changed?.Invoke();
                        if (Battery >= 0 && Battery <= 15 && !Charging && (DateTime.Now - _lowNoteAt).TotalMinutes > 30)
                        {
                            _lowNoteAt = DateTime.Now;
                            Note(fromName + " : batterie faible", Battery + " % restants");
                        }
                    }));
                    break;
                case "ring":
                    _ui.BeginInvoke(new Action(() => Ring.Start(fromName)));
                    break;
                case "clip":
                    string text = m.GetValueOrDefault("text") ?? "";
                    _ui.BeginInvoke(new Action(() =>
                    {
                        try { Clipboard.SetText(text); _clipSeq = GetClipboardSequenceNumber(); } catch { }
                        Note("Copié depuis " + fromName, text.Length > 80 ? text.Substring(0, 80) + "…" : text);
                    }));
                    break;
                case "handoff":
                    _ui.BeginInvoke(new Action(() => Continuity.FromPhone(fromName, m.GetValueOrDefault("url"), m.GetValueOrDefault("title"))));
                    break;
                case "remind":
                    _ui.BeginInvoke(new Action(() => Reminders.Add(m.GetValueOrDefault("text"), long.TryParse(m.GetValueOrDefault("at"), out long at) ? DateTimeOffset.FromUnixTimeMilliseconds(at).LocalDateTime : DateTime.Now, false)));
                    break;
            }
            return Ok();
        }

        private static Dictionary<string, string> Ok() => new Dictionary<string, string> { ["ok"] = "1" };

        public static void Note(string title, string text) => App.Island?.ShowNote(new IslandNote { App = "Wave Link", Title = title, Text = text });

        // ================== Envoi ==================

        public static Dictionary<string, string> Msg(string t, params (string k, string v)[] kv)
        {
            var m = new Dictionary<string, string> { ["t"] = t };
            foreach (var (k, v) in kv) m[k] = v ?? "";
            return m;
        }

        private static readonly HashSet<int> _pairing = new HashSet<int>();

        /// <summary>Demande de lien a un appareil proche (il doit accepter de son cote).</summary>
        public static async void Pair(WaveDropService.Nearby n)
        {
            _pairing.Add(n.Beacon.Id);
            var r = await SendTo(n.Beacon.IpString, n.Beacon.Port, Msg("pair"));
            if (r == null) Note("Lien impossible", "Mets les deux appareils sur le même Wi-Fi, avec Wave Drop allumé.");
            else if (r.GetValueOrDefault("ok") == "1") { _pairing.Remove(n.Beacon.Id); Save(n.Beacon.Id, n.Label, n.Beacon.IpString, n.Beacon.Port); Note("Lié à " + n.Label, "Les deux appareils sont liés."); }
            else Note("Demande envoyée", "Accepte sur " + n.Label + " (touche l'îlot).");
        }

        /// <summary>Message a un appareil lie (adresse vue a l'instant, sinon la derniere connue).</summary>
        public static async Task<Dictionary<string, string>> Send(int id, Dictionary<string, string> msg)
        {
            var near = App.Drop?.NearbyList().FirstOrDefault(n => n.Beacon.Id == id);
            string ip = near?.Beacon.IpString;
            int port = near?.Beacon.Port ?? 0;
            if (ip == null || port == 0) { var d = Linked().FirstOrDefault(x => x.Id == id); ip = d?.Ip; port = d?.Port ?? 0; }
            if (string.IsNullOrEmpty(ip) || port == 0) return null;
            return await SendTo(ip, port, msg);
        }

        public static async Task<int> SendAll(Dictionary<string, string> msg)
        {
            int ok = 0;
            foreach (var d in Linked()) if (await Send(d.Id, msg) != null) ok++;
            return ok;
        }

        private static async Task<Dictionary<string, string>> SendTo(string ip, int port, Dictionary<string, string> msg)
        {
            if (App.Drop == null || string.IsNullOrEmpty(ip) || port == 0) return null;
            try { return await App.Drop.SendLink(ip, port, msg); }
            catch (Exception e) { Log.I("Wave Link : envoi impossible (" + e.Message + ")"); return null; }
        }

        // ================== Presse-papiers ==================

        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        private static uint _clipSeq;

        private static async void WatchClipboard()
        {
            uint seq = GetClipboardSequenceNumber();
            if (seq == _clipSeq) return;
            _clipSeq = seq;
            if (!Settings.Current.ClipSync || Linked().Count == 0) return;
            string text = null;
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
            if (string.IsNullOrWhiteSpace(text) || text.Length > 20000) return;
            int n = await SendAll(Msg("clip", ("text", text)));
            if (n > 0) Log.I("Wave Link : presse-papiers envoye au telephone");
        }

        /// <summary>Faire sonner le telephone (bouton de l'ilot / des reglages).</summary>
        public static async void RingPhone()
        {
            if (Linked().Count == 0) { Note("Aucun téléphone lié", "Lie ton téléphone dans les réglages (Wave Drop > Wave Link)."); return; }
            int n = await SendAll(Msg("ring", ("from", WaveDropService.MyName)));
            Note(n > 0 ? "Ton téléphone sonne" : "Téléphone injoignable", n > 0 ? "Même en silencieux. Touche-le pour l'arrêter." : "Il doit être allumé, sur le même Wi-Fi, avec Wave Island ouvert en arrière-plan.");
        }
    }

    /// <summary>« Telephone introuvable » dans l'autre sens : le PC sonne jusqu'a ce qu'on touche l'ilot (30 s max).</summary>
    public static class Ring
    {
        private static DispatcherTimer _t;
        private static DateTime _until;

        public static void Start(string from)
        {
            Stop();
            _until = DateTime.Now.AddSeconds(30);
            _t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _t.Tick += (a, b) => { if (DateTime.Now > _until) Stop(); else System.Media.SystemSounds.Exclamation.Play(); };
            _t.Start();
            App.Island?.ShowNote(new IslandNote { App = "Wave Link", Title = from + " cherche ce PC", Text = "Touche pour arrêter la sonnerie", OnClick = Stop });
        }

        public static void Stop() { _t?.Stop(); _t = null; }
    }
}
