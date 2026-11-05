using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace WaveIsland
{
    /// <summary>
    /// Version finale : l'assistant vocal de la Wave Island (Gemini, gratuit).
    /// Touche Copilot / Microsoft 365 (ou bouton micro du menu de l'ilot) : l'ilot s'ouvre et ecoute,
    /// la question part a Claude, qui peut agir (sites, recherches, applis, son, luminosite, Wi-Fi, musique,
    /// minuteur) ; la reponse s'affiche dans l'ilot et est lue a voix haute.
    /// </summary>
    public static class Assistant
    {
        public enum State { Idle, Listening, Thinking, Speaking, Done }
        public static State Now { get; private set; } = State.Idle;

        private static Dispatcher _ui;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        private static CancellationTokenSource _cts;
        private static readonly List<JsonObject> _history = new List<JsonObject>();
        private static DateTime _lastTurn = DateTime.MinValue;
        private static bool _noWebSearch;
        private static string _heard = "";

        public static void Init(Dispatcher ui) => _ui = ui;

        // ================== Touche / clic ==================

        /// <summary>
        /// Touche de l'assistant : au repos = ecoute ; pendant l'ecoute = envoie ce qui a ete dit ;
        /// pendant la reponse = coupe la voix et ecoute la question suivante.
        /// </summary>
        public static void Trigger()
        {
            Log.I("Assistant : touche (" + Now + ")");
            switch (Now)
            {
                case State.Listening: Voice.StopListening(); break;
                case State.Thinking: break;
                case State.Speaking: Voice.StopSpeaking(); Listen(); break;
                default: Listen(); break;
            }
        }

        /// <summary>Question tapee (recherche universelle) : meme reponse, sans micro.</summary>
        public static async void AskText(string q)
        {
            if (string.IsNullOrWhiteSpace(q) || Now == State.Thinking) return;
            Voice.StopListening();
            Voice.StopSpeaking();
            if ((DateTime.Now - _lastTurn).TotalMinutes > 5) _history.Clear();
            _heard = q.Trim();
            await Ask(_heard);
        }

        /// <summary>Ilot ferme (clic droit) : on arrete tout.</summary>
        public static void Close()
        {
            _cts?.Cancel();
            Voice.StopListening();
            Voice.StopSpeaking();
            Now = State.Idle;
        }

        private static void Show(string status, string answer = null, bool done = false)
            => App.Island?.ShowAssist(status, _heard, answer, Now, done);

        // ================== Ecoute ==================

        private static async void Listen()
        {
            if (string.IsNullOrWhiteSpace(Settings.Current.AiKey))
            {
                Now = State.Done;
                _heard = "";
                Show("Assistant à configurer", "Colle ta clé Gemini gratuite (aistudio.google.com/apikey) dans les réglages de Wave Island, section « Assistant IA ».", done: true);
                return;
            }
            // Conversation oubliee apres 5 minutes de silence
            if ((DateTime.Now - _lastTurn).TotalMinutes > 5) _history.Clear();
            Now = State.Listening;
            _heard = "";
            Show("Je t'écoute…");
            WaveSound.Play(WaveSound.Kind.Listen);
            string text;
            try
            {
                text = await Voice.Listen(partial => _ui.BeginInvoke(new Action(() => { if (Now == State.Listening) { _heard = partial; Show("Je t'écoute…"); } })));
            }
            catch (Exception e)
            {
                Now = State.Done;
                Log.I("Assistant : micro impossible : " + e.Message);
                Show("Micro indisponible", Voice.Explain(e), done: true);
                return;
            }
            if (Now != State.Listening) return;
            if (string.IsNullOrWhiteSpace(text))
            {
                Now = State.Done;
                Show("Je n'ai rien entendu", null, done: true);
                return;
            }
            _heard = text.Trim();
            Log.I("Assistant : « " + _heard + " »");
            await Ask(_heard);
        }

        // ================== Claude ==================

        private static async Task Ask(string question)
        {
            Now = State.Thinking;
            Show("Je réfléchis…");
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            string answer;
            try { answer = await Run(question, ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                Log.I("Assistant : erreur : " + e.Message);
                Now = State.Done;
                Show("Oups", e.Message, done: true);
                return;
            }
            if (ct.IsCancellationRequested) return;
            answer = Clean(answer);
            _lastTurn = DateTime.Now;
            _history.Add(new JsonObject { ["role"] = "user", ["content"] = question });
            _history.Add(new JsonObject { ["role"] = "assistant", ["content"] = string.IsNullOrWhiteSpace(answer) ? "C'est fait." : answer });
            while (_history.Count > 16) _history.RemoveAt(0);
            if (string.IsNullOrWhiteSpace(answer)) answer = "C'est fait.";
            Log.I("Assistant : reponse : " + answer);
            if (Settings.Current.AiVoice)
            {
                Now = State.Speaking;
                Show("", answer);
                await Voice.Speak(answer);
                if (Now != State.Speaking) return;   // coupe par la touche
            }
            // Question en retour (« Je l'envoie ? ») : on ecoute la reponse tout de suite
            if (answer.TrimEnd().EndsWith("?") && Settings.Current.AiVoice) { Listen(); return; }
            Now = State.Done;
            Show("", answer, done: true);
        }

        private static string Model => string.IsNullOrWhiteSpace(Settings.Current.AiModel) || !Settings.Current.AiModel.StartsWith("gemini") ? "gemini-flash-latest" : Settings.Current.AiModel;

        private static string SystemPrompt()
        {
            var now = DateTime.Now;
            return "Tu es l'assistant vocal intégré à Wave Island sur le PC Windows de l'utilisateur (un îlot en verre liquide en haut de l'écran). " +
                   "Tu réponds en français, à l'oral : une à trois phrases courtes et naturelles, sans markdown, sans listes, sans emojis, sans liens. " +
                   "Quand l'utilisateur demande d'agir (ouvrir un site, chercher quelque chose, lancer une appli, régler le son, la luminosité, le Wi-Fi, le Bluetooth, la musique, un minuteur), " +
                   "utilise directement l'outil adapté sans demander de confirmation, puis confirme en une courte phrase. " +
                   "Pour une question d'actualité, de météo, de prix ou tout fait récent, utilise la recherche web si elle est disponible. " +
                   "Si on te demande de chercher quelque chose « sur » un navigateur ou un site (Firefox, Chrome, YouTube, Google...), ouvre la recherche dans le navigateur avec l'outil. " +
                   $"Nous sommes le {now.ToString("dddd d MMMM yyyy", new CultureInfo("fr-FR"))}, il est {now:HH:mm}.";
        }

        private static JsonArray Tools()
        {
            var t = new JsonArray();
            t.Add(Tool("open_url", "Ouvre une adresse web (ou une adresse ms-settings: pour une page des Paramètres de Windows) dans le navigateur.",
                new JsonObject { ["url"] = Str("Adresse complète"), ["browser"] = Browser() }, "url"));
            t.Add(Tool("search_in_browser", "Lance une recherche dans le navigateur de l'utilisateur (Google par défaut, ou sur YouTube, Wikipédia, Amazon, Google Maps, Google Images).",
                new JsonObject
                {
                    ["query"] = Str("Ce qu'il faut chercher"),
                    ["site"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("google", "youtube", "wikipedia", "amazon", "maps", "images") },
                    ["browser"] = Browser()
                }, "query"));
            t.Add(Tool("open_app", "Ouvre une application installée sur le PC (par son nom, ex. Spotify, Paint, Word, Steam, Explorateur).",
                new JsonObject { ["name"] = Str("Nom de l'appli") }, "name"));
            t.Add(Tool("set_volume", "Règle le volume du PC (0 à 100) ou coupe / remet le son.",
                new JsonObject { ["level"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 100 }, ["mute"] = new JsonObject { ["type"] = "boolean" } }));
            t.Add(Tool("media", "Commande la musique ou la vidéo en cours.",
                new JsonObject { ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("play_pause", "next", "previous") } }, "action"));
            t.Add(Tool("set_brightness", "Règle la luminosité de l'écran (0 à 100).",
                new JsonObject { ["level"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 100 } }, "level"));
            t.Add(Tool("set_radio", "Active ou coupe le Wi-Fi ou le Bluetooth.",
                new JsonObject { ["radio"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("wifi", "bluetooth") }, ["on"] = new JsonObject { ["type"] = "boolean" } }, "radio", "on"));
            t.Add(Tool("set_reminder", "Crée un rappel : dans N minutes, à une heure précise (HH:MM, aujourd'hui ou demain), ou à la prochaine ouverture du PC. Le rappel arrive aussi sur le téléphone lié.",
                new JsonObject
                {
                    ["text"] = Str("Ce qu'il faut rappeler"),
                    ["minutes"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                    ["time"] = Str("Heure HH:MM (facultatif)"),
                    ["when_pc_opens"] = new JsonObject { ["type"] = "boolean" }
                }, "text"));
            t.Add(Tool("ring_phone", "Fait sonner le téléphone lié (pour le retrouver), même en silencieux.", new JsonObject()));
            t.Add(Tool("set_timer", "Lance un minuteur : l'îlot préviendra à la fin.",
                new JsonObject { ["seconds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 }, ["label"] = Str("Nom du minuteur (facultatif)") }, "seconds"));
            return t;
        }

        private static JsonObject Str(string d) => new JsonObject { ["type"] = "string", ["description"] = d };
        private static JsonObject Browser() => new JsonObject
        {
            ["type"] = "string", ["description"] = "Navigateur demandé (sinon celui par défaut)",
            ["enum"] = new JsonArray("default", "firefox", "chrome", "edge", "opera", "brave")
        };

        private static JsonObject Tool(string name, string desc, JsonObject props, params string[] required)
        {
            var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
            if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
            return new JsonObject { ["name"] = name, ["description"] = desc, ["input_schema"] = schema };
        }

        /// <summary>Conversation avec Gemini, outils compris (jusqu'a 6 allers-retours).</summary>
        private static async Task<string> Run(string question, CancellationToken ct)
        {
            var contents = new JsonArray();
            foreach (var h in _history)
                contents.Add(new JsonObject
                {
                    ["role"] = h["role"]?.GetValue<string>() == "assistant" ? "model" : "user",
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = h["content"]?.GetValue<string>() ?? "" })
                });
            contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = question }) });
            var text = new StringBuilder();
            for (int round = 0; round < 7; round++)
            {
                var decls = new JsonArray();
                foreach (var t in Tools())
                {
                    if (t?["input_schema"] == null) continue;
                    decls.Add(new JsonObject { ["name"] = t["name"]!.GetValue<string>(), ["description"] = t["description"]!.GetValue<string>(), ["parameters"] = t["input_schema"]!.DeepClone() });
                }
                var tools = new JsonArray(new JsonObject { ["function_declarations"] = decls });
                if (!_noWebSearch) tools.Add(new JsonObject { ["google_search"] = new JsonObject() });
                var body = new JsonObject
                {
                    ["system_instruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = SystemPrompt() }) },
                    ["contents"] = contents.DeepClone(),
                    ["tools"] = tools,
                    ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = 900 }
                };
                var res = await Post(body, ct);
                if (res == null) continue;   // reessai sans la recherche Google
                var content = res["candidates"]?[0]?["content"] as JsonObject;
                var parts = content?["parts"] as JsonArray ?? new JsonArray();
                var replies = new JsonArray();
                text.Clear();
                foreach (var part in parts)
                {
                    if (part?["text"] != null && part["thought"]?.GetValue<bool>() != true) text.Append(part["text"]!.GetValue<string>());
                    var call = part?["functionCall"] as JsonObject;
                    if (call == null) continue;
                    string name = call["name"]?.GetValue<string>();
                    var input = call["args"] as JsonObject ?? new JsonObject();
                    string outcome;
                    try { outcome = await RunTool(name, input); }
                    catch (Exception e) { outcome = "Erreur : " + e.Message; }
                    Log.I($"Assistant : outil {name} {input.ToJsonString()} -> {outcome}");
                    replies.Add(new JsonObject { ["functionResponse"] = new JsonObject { ["name"] = name, ["response"] = new JsonObject { ["result"] = outcome } } });
                }
                if (replies.Count > 0)
                {
                    contents.Add(new JsonObject { ["role"] = "model", ["parts"] = parts.DeepClone() });
                    contents.Add(new JsonObject { ["role"] = "user", ["parts"] = replies });
                    continue;
                }
                break;
            }
            return text.ToString();
        }

        private static async Task<JsonObject> Post(JsonObject body, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent");
            req.Headers.Add("x-goog-api-key", Settings.Current.AiKey.Trim());
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, ct);
            string json = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return JsonNode.Parse(json) as JsonObject;
            string msg = "";
            try { msg = JsonNode.Parse(json)?["error"]?["message"]?.GetValue<string>() ?? ""; } catch { }
            Log.I($"Assistant : API {(int)resp.StatusCode} {msg}");
            int code = (int)resp.StatusCode;
            if (code == 400 && !_noWebSearch && (msg.IndexOf("search", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("tool", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                _noWebSearch = true;   // recherche Google + outils ensemble refuses : on continue sans
                return null;
            }
            bool badKey = msg.IndexOf("API key", StringComparison.OrdinalIgnoreCase) >= 0;
            throw new Exception(code switch
            {
                _ when badKey => "La clé Gemini n'est pas valide. Vérifie-la dans les réglages de Wave Island.",
                403 => "La clé Gemini n'a pas accès à ce modèle.",
                404 => "Modèle Gemini introuvable : choisis-en un autre dans les réglages.",
                429 => "Limite gratuite de Gemini atteinte pour le moment, réessaie un peu plus tard.",
                500 or 503 => "Gemini est surchargé, réessaie dans un instant.",
                _ => "Gemini ne répond pas (" + code + ")."
            });
        }

        /// <summary>Texte pour l'oral : sans markdown ni liens.</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");
            s = Regex.Replace(s, @"https?://\S+", "");
            s = s.Replace("**", "").Replace("__", "").Replace("`", "").Replace("#", "");
            s = Regex.Replace(s, @"^\s*[-*•]\s+", "", RegexOptions.Multiline);
            s = Regex.Replace(s, @"\s+", " ");
            return s.Trim();
        }

        // ================== Outils ==================

        private static Task<string> RunTool(string name, JsonObject a)
        {
            string S(string k) => a[k]?.ToString();
            switch (name)
            {
                case "open_url":
                {
                    string url = S("url") ?? "";
                    if (!url.Contains(":")) url = "https://" + url;
                    return Task.FromResult(OpenInBrowser(url, S("browser")));
                }
                case "search_in_browser":
                {
                    string q = Uri.EscapeDataString(S("query") ?? "");
                    string url = (S("site") ?? "google") switch
                    {
                        "youtube" => "https://www.youtube.com/results?search_query=" + q,
                        "wikipedia" => "https://fr.wikipedia.org/w/index.php?search=" + q,
                        "amazon" => "https://www.amazon.fr/s?k=" + q,
                        "maps" => "https://www.google.com/maps/search/" + q,
                        "images" => "https://www.google.com/search?tbm=isch&q=" + q,
                        _ => "https://www.google.com/search?q=" + q
                    };
                    return Task.FromResult(OpenInBrowser(url, S("browser")));
                }
                case "open_app":
                {
                    var app = FindApp(S("name") ?? "");
                    if (app == null) return Task.FromResult("Aucune appli de ce nom n'est installée.");
                    _ui.Invoke(() => AppCatalog.Launch(app));
                    return Task.FromResult("Appli ouverte : " + app.Name);
                }
                case "set_volume":
                {
                    var v = App.Volume;
                    if (v == null) return Task.FromResult("Volume indisponible.");
                    _ui.Invoke(() =>
                    {
                        var (lvl, muted) = v.Read();
                        if (a["mute"] != null) { bool m = a["mute"].GetValue<bool>(); if (m != muted) v.ToggleMute(); }
                        if (a["level"] != null) v.SetLevel(a["level"].GetValue<int>() / 100f);
                    });
                    return Task.FromResult("Volume réglé.");
                }
                case "media":
                {
                    var m = App.Media;
                    if (m == null) return Task.FromResult("Aucune musique.");
                    _ui.Invoke(() =>
                    {
                        switch (S("action")) { case "next": m.Next(); break; case "previous": m.Previous(); break; default: m.PlayPause(); break; }
                    });
                    return Task.FromResult(m.Current == null ? "Commande envoyée (rien ne semblait jouer)." : "Fait.");
                }
                case "set_brightness":
                    Brightness.Set((a["level"]?.GetValue<int>() ?? 50) / 100.0);
                    return Task.FromResult(Brightness.Get() < 0 ? "Cet écran ne permet pas de régler la luminosité." : "Luminosité réglée.");
                case "set_radio":
                    return SetRadio(S("radio"), a["on"]?.GetValue<bool>() ?? true);
                case "set_reminder":
                {
                    string txt = S("text") ?? "";
                    if (a["when_pc_opens"]?.GetValue<bool>() == true) { _ui.Invoke(() => Reminders.Add(txt, DateTime.Now, false, "pc")); return Task.FromResult("Rappel à la prochaine ouverture du PC."); }
                    DateTime at;
                    if (a["minutes"] != null) at = DateTime.Now.AddMinutes(a["minutes"].GetValue<int>());
                    else if (TimeSpan.TryParse(S("time") ?? "", out var hm)) { at = DateTime.Today + hm; if (at < DateTime.Now) at = at.AddDays(1); }
                    else at = DateTime.Now.AddHours(1);
                    _ui.Invoke(() => Reminders.Add(txt, at));
                    return Task.FromResult("Rappel programmé pour " + at.ToString("dddd HH:mm", new CultureInfo("fr-FR")) + ".");
                }
                case "ring_phone":
                    _ui.Invoke(WaveLink.RingPhone);
                    return Task.FromResult("Le téléphone sonne (s'il est joignable).");
                case "set_timer":
                {
                    int sec = a["seconds"]?.GetValue<int>() ?? 60;
                    string label = S("label");
                    var end = DateTime.Now.AddSeconds(sec);
                    _ = Task.Delay(TimeSpan.FromSeconds(sec)).ContinueWith(_ => _ui.BeginInvoke(new Action(() =>
                    {
                        App.Island?.ShowNote(new IslandNote { App = "Minuteur", Title = string.IsNullOrWhiteSpace(label) ? "Minuteur terminé" : label, Text = "Terminé à " + end.ToString("HH:mm") });
                        System.Media.SystemSounds.Exclamation.Play();
                        if (Settings.Current.AiVoice && Now != State.Speaking && Now != State.Listening) _ = Voice.Speak(string.IsNullOrWhiteSpace(label) ? "Le minuteur est terminé." : label + " : c'est terminé.");
                    })));
                    return Task.FromResult("Minuteur lancé jusqu'à " + end.ToString("HH:mm:ss"));
                }
            }
            return Task.FromResult("Outil inconnu.");
        }

        private static async Task<string> SetRadio(string kind, bool on)
        {
            try
            {
                await Windows.Devices.Radios.Radio.RequestAccessAsync();
                var radios = await Windows.Devices.Radios.Radio.GetRadiosAsync();
                var r = radios.FirstOrDefault(x => x.Kind == (kind == "bluetooth" ? Windows.Devices.Radios.RadioKind.Bluetooth : Windows.Devices.Radios.RadioKind.WiFi));
                if (r == null) return "Pas de " + kind + " sur ce PC.";
                await r.SetStateAsync(on ? Windows.Devices.Radios.RadioState.On : Windows.Devices.Radios.RadioState.Off);
                return (kind == "bluetooth" ? "Bluetooth" : "Wi-Fi") + (on ? " activé." : " coupé.");
            }
            catch (Exception e) { return "Impossible : " + e.Message; }
        }

        private static readonly Dictionary<string, string[]> BrowserExe = new Dictionary<string, string[]>
        {
            ["firefox"] = new[] { @"Mozilla Firefox\firefox.exe" },
            ["chrome"] = new[] { @"Google\Chrome\Application\chrome.exe" },
            ["edge"] = new[] { @"Microsoft\Edge\Application\msedge.exe" },
            ["opera"] = new[] { @"Opera\opera.exe", @"Programs\Opera\opera.exe", @"Programs\Opera GX\opera.exe" },
            ["brave"] = new[] { @"BraveSoftware\Brave-Browser\Application\brave.exe" },
        };

        private static string OpenInBrowser(string url, string browser)
        {
            try
            {
                if (!string.IsNullOrEmpty(browser) && browser != "default" && BrowserExe.TryGetValue(browser, out var rel) && !url.StartsWith("ms-"))
                {
                    var roots = new[]
                    {
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                    };
                    foreach (var root in roots)
                        foreach (var r in rel)
                        {
                            var exe = Path.Combine(root, r);
                            if (File.Exists(exe))
                            {
                                Process.Start(new ProcessStartInfo(exe, "\"" + url + "\"") { UseShellExecute = true });
                                return "Ouvert dans " + browser + ".";
                            }
                        }
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    return browser + " n'est pas installé : ouvert dans le navigateur par défaut.";
                }
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return "Ouvert.";
            }
            catch (Exception e) { return "Impossible d'ouvrir : " + e.Message; }
        }

        private static string Fold(string s) =>
            new string((s ?? "").Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant().Trim();

        private static AppEntry FindApp(string name)
        {
            string q = Fold(name);
            if (q.Length == 0) return null;
            var apps = AppCatalog.Apps.ToList();
            return apps.FirstOrDefault(x => Fold(x.Name) == q)
                ?? apps.Where(x => Fold(x.Name).StartsWith(q)).OrderBy(x => x.Name.Length).FirstOrDefault()
                ?? apps.Where(x => Fold(x.Name).Contains(q)).OrderBy(x => x.Name.Length).FirstOrDefault()
                ?? apps.Where(x => q.Contains(Fold(x.Name)) && x.Name.Length > 2).OrderByDescending(x => x.Name.Length).FirstOrDefault();
        }

        // ================== Tests automatiques ==================

        /// <summary>Pour les captures : l'ilot dans chaque etat de l'assistant, sans micro ni reseau.</summary>
        internal static async Task Demo(Action<string> shot)
        {
            _heard = "";
            Now = State.Listening;
            Show("Je t'écoute…");
            await Task.Delay(700);
            shot("32-assistant-ecoute");
            _heard = "Cherche la météo de Paris sur Firefox";
            Now = State.Thinking;
            Show("Je réfléchis…");
            await Task.Delay(700);
            shot("33-assistant-reflechit");
            Now = State.Done;
            Show("", "J'ai ouvert la recherche « météo Paris » dans Firefox. Il fait 12 degrés avec quelques nuages cet après-midi.", done: true);
            await Task.Delay(800);
            shot("34-assistant-reponse");
            App.Island?.Collapse();
            Now = State.Idle;
        }
    }

    /// <summary>Micro (reconnaissance vocale de Windows) et voix (synthese vocale francaise).</summary>
    public static class Voice
    {
        private static Windows.Media.SpeechRecognition.SpeechRecognizer _rec;
        private static System.Speech.Recognition.SpeechRecognitionEngine _offline;
        private static Windows.Media.Playback.MediaPlayer _player;
        private static TaskCompletionSource<bool> _speakDone;

        /// <summary>Ecoute une phrase (fin automatique apres un court silence). partial = texte en cours.</summary>
        public static async Task<string> Listen(Action<string> partial)
        {
            try { return await ListenOnline(partial); }
            catch (Exception e) when (IsPrivacy(e))
            {
                Log.I("Assistant : reconnaissance en ligne refusee, essai hors ligne");
                return await ListenOffline(partial);
            }
        }

        private static bool IsPrivacy(Exception e) => (uint)e.HResult == 0x80045509;

        private static async Task<string> ListenOnline(Action<string> partial)
        {
            StopListening();
            var lang = new Windows.Globalization.Language("fr-FR");
            var rec = new Windows.Media.SpeechRecognition.SpeechRecognizer(lang);
            _rec = rec;
            rec.Constraints.Add(new Windows.Media.SpeechRecognition.SpeechRecognitionTopicConstraint(
                Windows.Media.SpeechRecognition.SpeechRecognitionScenario.Dictation, "dictee"));
            rec.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(6);
            rec.Timeouts.EndSilenceTimeout = TimeSpan.FromSeconds(1.1);
            rec.HypothesisGenerated += (s, e) => partial?.Invoke(e.Hypothesis.Text);
            var compiled = await rec.CompileConstraintsAsync();
            if (compiled.Status != Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.Success)
                throw new InvalidOperationException("Reconnaissance vocale indisponible (" + compiled.Status + ")");
            try
            {
                var r = await rec.RecognizeAsync();
                if (r.Status == Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.Success) return r.Text;
                if (r.Status == Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.UserCanceled) return "";
                Log.I("Assistant : ecoute " + r.Status);
                return r.Text ?? "";
            }
            finally
            {
                if (_rec == rec) _rec = null;
                try { rec.Dispose(); } catch { }
            }
        }

        private static Task<string> ListenOffline(Action<string> partial)
        {
            return Task.Run(() =>
            {
                var eng = new System.Speech.Recognition.SpeechRecognitionEngine(new CultureInfo("fr-FR"));
                _offline = eng;
                try
                {
                    eng.LoadGrammar(new System.Speech.Recognition.DictationGrammar());
                    eng.SetInputToDefaultAudioDevice();
                    eng.InitialSilenceTimeout = TimeSpan.FromSeconds(6);
                    eng.EndSilenceTimeout = TimeSpan.FromSeconds(1.1);
                    eng.SpeechHypothesized += (s, e) => partial?.Invoke(e.Result.Text);
                    var r = eng.Recognize(TimeSpan.FromSeconds(20));
                    return r?.Text ?? "";
                }
                finally { _offline = null; eng.Dispose(); }
            });
        }

        public static void StopListening()
        {
            try { _ = _rec?.StopRecognitionAsync(); } catch { }
            try { _offline?.RecognizeAsyncCancel(); } catch { }
        }

        /// <summary>Message clair quand le micro ne marche pas.</summary>
        public static string Explain(Exception e)
        {
            if (e is ArgumentException || e.Message.Contains("fr-FR") || e is PlatformNotSupportedException)
                return "La reconnaissance vocale française n'est pas installée. Paramètres > Heure et langue > Voix : ajoute le français, puis réessaie.";
            if ((uint)e.HResult == 0x80045509 || (uint)e.HResult == 0x80070005)
                return "Autorise le micro : Paramètres > Confidentialité > Voix (« Reconnaissance vocale en ligne ») et Microphone (« Autoriser les applis de bureau »).";
            return "Le micro ne répond pas : vérifie qu'il est branché et autorisé (Paramètres > Confidentialité > Microphone).";
        }

        /// <summary>Lit le texte avec une voix francaise ; se termine quand la voix a fini (ou est coupee).</summary>
        public static async Task Speak(string text)
        {
            StopSpeaking();
            try
            {
                using var synth = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
                var voice = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith("fr", StringComparison.OrdinalIgnoreCase));
                if (voice != null) synth.Voice = voice;
                synth.Options.SpeakingRate = 1.08;
                var stream = await synth.SynthesizeTextToStreamAsync(text);
                var player = new Windows.Media.Playback.MediaPlayer();
                _player = player;
                var done = new TaskCompletionSource<bool>();
                _speakDone = done;
                player.MediaEnded += (s, e) => done.TrySetResult(true);
                player.MediaFailed += (s, e) => done.TrySetResult(false);
                player.Source = Windows.Media.Core.MediaSource.CreateFromStream(stream, stream.ContentType);
                player.Play();
                await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10 + text.Length / 8.0)));
                if (_player == player) { _player = null; player.Dispose(); }
            }
            catch (Exception e) { Log.I("Assistant : voix impossible : " + e.Message); }
        }

        public static void StopSpeaking()
        {
            var p = _player;
            _player = null;
            try { p?.Pause(); p?.Dispose(); } catch { }
            _speakDone?.TrySetResult(false);
        }
    }
}
