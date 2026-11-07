using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WaveIsland
{
    /// <summary>
    /// Reglages rapides : copie en verre liquide de ceux de Windows 11.
    /// Tuiles en deux parties (le bouton allume / eteint, la fleche ouvre la liste sans ouvrir les Parametres) :
    /// Wi-Fi (reseaux et barres de signal en direct, connexion avec mot de passe), Bluetooth (appareils),
    /// Mode avion, Economie d'energie, Eclairage nocturne, Accessibilite, Diffuser (ecrans sans fil), Point d'acces, Wave Drop ;
    /// luminosite, volume (fleche : choix de la sortie audio), batterie en %.
    /// </summary>
    public sealed partial class TaskbarPanel
    {
        private static readonly FontFamily Txt = new FontFamily("Segoe UI Variable Text, Segoe UI");
        private Windows.Devices.WiFi.WiFiAdapter _wifiAdapter;

        // ================== Page principale ==================

        private void BuildQuick()
        {
            _col.Children.Clear();
            _toggles.Clear();
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
            grid.Children.Add(Tile("", "Wi-Fi", () => _wifi?.State == Windows.Devices.Radios.RadioState.On, () => FlipRadio(_wifi), () => Page("Wi-Fi", BuildWifiPage), WifiName));
            grid.Children.Add(Tile("", "Bluetooth", () => _bt?.State == Windows.Devices.Radios.RadioState.On, () => FlipRadio(_bt), () => Page("Bluetooth", BuildBtPage)));
            grid.Children.Add(Tile("", "Mode avion", () => _wifi != null && _wifi.State != Windows.Devices.Radios.RadioState.On && (_bt == null || _bt.State != Windows.Devices.Radios.RadioState.On), Airplane, null));
            grid.Children.Add(Tile("", "Économie d'énergie", EnergySaverOn, () => Open("ms-settings:batterysaver"), null));
            grid.Children.Add(Tile("", "Éclairage nocturne", () => false, () => Open("ms-settings:nightlight"), null));
            grid.Children.Add(Tile("", "Accessibilité", () => false, () => Page("Accessibilité", BuildA11yPage), () => Page("Accessibilité", BuildA11yPage)));
            grid.Children.Add(Tile("", "Diffuser", () => false, () => Page("Diffuser", BuildCastPage), () => Page("Diffuser", BuildCastPage)));
            grid.Children.Add(Tile("", "Point d'accès", () => false, () => Open("ms-settings:network-mobilehotspot"), null));
            grid.Children.Add(Tile("", "Wave Drop", () => Settings.Current.DropEnabled, () =>
            {
                var s = Settings.Current; s.DropEnabled = !s.DropEnabled; s.Save();
                if (s.DropEnabled) App.Drop?.Start(); else App.Drop?.Stop();
            }, null));
            _col.Children.Add(grid);

            // Luminosite et volume (fleche : sortie audio)
            var briSlider = new GlassBar("", 0.5, v => Brightness.Set(v)) { Visibility = Visibility.Collapsed, Margin = new Thickness(4, 14, 4, 0) };
            _col.Children.Add(briSlider);
            Task.Run(() => Brightness.Get()).ContinueWith(t =>
            {
                if (t.Result >= 0) Dispatcher.Invoke(() => { briSlider.Value = t.Result; briSlider.Visibility = Visibility.Visible; });
            });
            var vol = App.Volume?.Read() ?? (0.5f, false);
            var volRow = new DockPanel { Margin = new Thickness(4, 12, 4, 0) };
            var outBtn = RoundButton("", () => Page("Sortie audio", BuildAudioPage));
            outBtn.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(outBtn, Dock.Right);
            volRow.Children.Add(outBtn);
            volRow.Children.Add(new GlassBar(vol.muted ? "" : "", vol.level, v => App.Volume?.SetLevel((float)v)));
            _col.Children.Add(volRow);

            // Telephone lie
            if (WaveLink.Battery >= 0 && (DateTime.Now - WaveLink.BatteryAt).TotalHours < 3)
                _col.Children.Add(Row("", (WaveLink.PhoneName ?? "Téléphone") + " : " + WaveLink.Battery + " %" + (WaveLink.Charging ? " (en charge)" : ""), null).With(r => r.Margin = new Thickness(4, 12, 4, 0)));

            // Bas : batterie (icone + pourcentage) et engrenage
            var bottom = new DockPanel { Margin = new Thickness(4, 14, 4, 0), LastChildFill = false };
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            bool hasBat = (ps.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryLifePercent <= 1.0f;
            if (hasBat)
            {
                int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
                bool charging = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                var bat = new StackPanel { Orientation = Orientation.Horizontal };
                // Icone et chiffres dans deux polices differentes (avant, les chiffres s'affichaient en carres)
                bat.Children.Add(new TextBlock { Text = ((char)((charging ? 0xE85A : 0xE850) + Math.Min(10, pct / 10))).ToString(), FontFamily = Icons, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
                bat.Children.Add(new TextBlock { Text = pct + " %" + (charging ? " · en charge" : ""), FontFamily = Txt, FontSize = 13, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                var bb = PlateBox(bat, 15, new Thickness(12, 6, 14, 7));
                bb.Cursor = Cursors.Hand;
                bb.MouseLeftButtonUp += (a, b) => Open("ms-settings:batterysaver");
                bottom.Children.Add(bb);
            }
            var set = RoundButton("", () => Open("ms-settings:"));
            DockPanel.SetDock(set, Dock.Right);
            bottom.Children.Add(set);
            _col.Children.Add(bottom);

            LoadRadios();
        }

        private void Open(string uri) { CloseAnimated(); Shortcuts.Start(uri); }

        private static bool EnergySaverOn()
        {
            try { return Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On; } catch { return false; }
        }

        private static string WifiName()
        {
            try
            {
                var p = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                if (p != null && p.IsWlanConnectionProfile) return p.WlanConnectionProfileDetails.GetConnectedSsid();
            }
            catch { }
            return null;
        }

        /// <summary>Tuile en deux parties comme Windows : a gauche le bouton (allume / eteint), a droite la fleche vers la liste.</summary>
        private Border Tile(string glyph, string label, Func<bool> state, Action click, Action more, Func<string> sub = null)
        {
            var icon = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var left = new Border { Child = icon, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var g = new Grid { Height = 50 };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            if (more != null) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.Children.Add(left);
            TextBlock chevron = null;
            if (more != null)
            {
                chevron = new TextBlock { Text = "", FontFamily = Icons, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var right = new Border { Child = chevron, Background = Brushes.Transparent, Cursor = Cursors.Hand, BorderBrush = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)), BorderThickness = new Thickness(1, 0, 0, 0) };
                Grid.SetColumn(right, 1);
                g.Children.Add(right);
                right.MouseLeftButtonUp += (a, b) => { b.Handled = true; more(); };
            }
            var box = new Border
            {
                CornerRadius = new CornerRadius(14), Background = Plate, BorderBrush = PlateLine, BorderThickness = new Thickness(1), Child = g,
                Margin = new Thickness(4), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1)
            };
            var text = new TextBlock { Text = label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 2, 6), MaxWidth = 100 };
            var cell = new StackPanel();
            cell.Children.Add(box);
            cell.Children.Add(text);
            left.MouseLeftButtonDown += (a, b) => Bounce(box, 0.92);
            left.MouseLeftButtonUp += (a, b) => { Bounce(box, 1); click(); Dispatcher.BeginInvoke(new Action(RefreshToggles), System.Windows.Threading.DispatcherPriority.Background); };
            _toggles.Add((box, icon, chevron ?? new TextBlock(), () =>
            {
                if (sub != null) { var n = sub(); text.Text = string.IsNullOrEmpty(n) ? label : n; }
                return state();
            }));
            return new Border { Child = cell };
        }

        private static Border RoundButton(string glyph, Action act)
        {
            var b = new Border
            {
                Width = 42, Height = 42, CornerRadius = new CornerRadius(21), Background = Plate, BorderBrush = PlateLine, BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                Child = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            b.MouseEnter += (x, y) => b.Background = PlateHover;
            b.MouseLeave += (x, y) => b.Background = Plate;
            b.MouseLeftButtonUp += (x, y) => act();
            return b;
        }

        /// <summary>Ligne de liste (reseau, appareil, sortie audio...).</summary>
        private static Border Row(string glyph, string title, string sub, bool selected = false, Action act = null)
        {
            var dp = new DockPanel();
            var ic = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 16, Width = 26, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(ic, Dock.Left);
            dp.Children.Add(ic);
            var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            st.Children.Add(new TextBlock { Text = title, FontFamily = Txt, FontSize = 13.5, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(sub)) st.Children.Add(new TextBlock { Text = sub, FontFamily = Txt, FontSize = 11.5, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
            dp.Children.Add(st);
            var b = new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 4),
                Background = selected ? new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) : Brushes.Transparent, Child = dp
            };
            if (act != null)
            {
                b.Cursor = Cursors.Hand;
                b.MouseEnter += (x, y) => { if (!selected) b.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)); };
                b.MouseLeave += (x, y) => { if (!selected) b.Background = Brushes.Transparent; };
                b.MouseLeftButtonUp += (x, y) => act();
            }
            return b;
        }

        // ================== Sous-pages (glissent depuis la droite, fleche retour) ==================

        private void Page(string title, Action<StackPanel> build)
        {
            var content = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var back = RoundButton("", () => Back());
            back.Width = back.Height = 34;
            back.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(back, Dock.Left);
            head.Children.Add(back);
            head.Children.Add(new TextBlock { Text = title, FontFamily = Txt, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(head);
            var body = new StackPanel();
            content.Children.Add(new ScrollViewer { Content = body, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
            build(body);
            Slide(content, fromRight: true);
        }

        private void Back()
        {
            BuildQuick();
            var main = new StackPanel();
            foreach (UIElement c in _col.Children.Cast<UIElement>().ToList()) { _col.Children.Remove(c); main.Children.Add(c); }
            Slide(main, fromRight: false);
        }

        private void Slide(UIElement content, bool fromRight)
        {
            _col.Children.Clear();
            var tt = new TranslateTransform(fromRight ? 60 : -60, 0);
            content.RenderTransform = tt;
            content.Opacity = 0;
            _col.Children.Add(content);
            var ease = new SpringEase();
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        }

        private static void Wait(StackPanel body, string text)
        {
            body.Children.Clear();
            body.Children.Add(new TextBlock { Text = text, FontFamily = Txt, FontSize = 13, Opacity = 0.8, Margin = new Thickness(6, 8, 0, 8) });
        }

        // ---- Wi-Fi ----

        private static string Bars(int bars) => bars <= 1 ? "" : bars == 2 ? "" : bars == 3 ? "" : "";

        private async void BuildWifiPage(StackPanel body)
        {
            Wait(body, "Recherche des réseaux…");
            try
            {
                var access = await Windows.Devices.WiFi.WiFiAdapter.RequestAccessAsync();
                if (access != Windows.Devices.WiFi.WiFiAccessStatus.Allowed)
                {
                    Wait(body, "Windows ne donne pas la liste des réseaux à Wave Island. Autorise la localisation pour les applis de bureau (Paramètres > Confidentialité > Localisation).");
                    body.Children.Add(Row("", "Ouvrir les réseaux de Windows", null, false, () => Open("ms-availablenetworks:")));
                    return;
                }
                var adapters = await Windows.Devices.WiFi.WiFiAdapter.FindAllAdaptersAsync();
                _wifiAdapter = adapters.FirstOrDefault();
                if (_wifiAdapter == null) { Wait(body, "Pas de Wi-Fi sur ce PC."); return; }
                await _wifiAdapter.ScanAsync();
                string connected = null;
                try { var prof = await _wifiAdapter.NetworkAdapter.GetConnectedProfileAsync(); connected = prof?.ProfileName; } catch { }
                body.Children.Clear();
                var nets = _wifiAdapter.NetworkReport.AvailableNetworks
                    .Where(n => !string.IsNullOrEmpty(n.Ssid)).GroupBy(n => n.Ssid).Select(g => g.OrderByDescending(n => n.SignalBars).First())
                    .OrderByDescending(n => n.Ssid == connected).ThenByDescending(n => n.SignalBars).Take(14).ToList();
                foreach (var n in nets)
                {
                    bool on = n.Ssid == connected;
                    bool secure = n.SecuritySettings.NetworkAuthenticationType != Windows.Networking.Connectivity.NetworkAuthenticationType.Open80211;
                    var net = n;
                    var holder = new StackPanel();
                    holder.Children.Add(Row(Bars(n.SignalBars), n.Ssid, on ? "Connecté" + (secure ? ", sécurisé" : "") : secure ? "Sécurisé" : "Ouvert", on,
                        () => WifiExpand(holder, net, on, secure)));
                    body.Children.Add(holder);
                }
                if (nets.Count == 0) Wait(body, "Aucun réseau trouvé.");
                body.Children.Add(Row("", "Plus de paramètres Wi-Fi", null, false, () => Open("ms-settings:network-wifi")));
            }
            catch (Exception e)
            {
                Log.I("Reglages rapides : Wi-Fi : " + e.Message);
                Wait(body, "Liste des réseaux indisponible.");
                body.Children.Add(Row("", "Ouvrir les réseaux de Windows", null, false, () => Open("ms-availablenetworks:")));
            }
        }

        /// <summary>Un reseau touche : boutons Se connecter / Deconnecter (et mot de passe si besoin), sans quitter le panneau.</summary>
        private void WifiExpand(StackPanel holder, Windows.Devices.WiFi.WiFiAvailableNetwork n, bool connected, bool secure)
        {
            while (holder.Children.Count > 1) holder.Children.RemoveAt(1);
            var box = new StackPanel { Margin = new Thickness(36, 2, 6, 10) };
            var status = new TextBlock { FontFamily = Txt, FontSize = 12, Opacity = 0.85, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
            if (connected)
            {
                box.Children.Add(Pill(null, "Déconnecter", () => { _wifiAdapter?.Disconnect(); Page("Wi-Fi", BuildWifiPage); }));
            }
            else
            {
                PasswordBox pass = null;
                if (secure)
                {
                    pass = new PasswordBox { FontSize = 13, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 8), Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), Foreground = Brushes.White, BorderBrush = PlateLine };
                    box.Children.Add(new TextBlock { Text = "Mot de passe (vide si déjà enregistré)", FontFamily = Txt, FontSize = 11.5, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 4) });
                    box.Children.Add(pass);
                }
                box.Children.Add(Pill(null, "Se connecter", async () =>
                {
                    status.Text = "Connexion…";
                    try
                    {
                        Windows.Devices.WiFi.WiFiConnectionResult r;
                        if (pass != null && pass.Password.Length > 0)
                            r = await _wifiAdapter.ConnectAsync(n, Windows.Devices.WiFi.WiFiReconnectionKind.Automatic, new Windows.Security.Credentials.PasswordCredential { Password = pass.Password });
                        else r = await _wifiAdapter.ConnectAsync(n, Windows.Devices.WiFi.WiFiReconnectionKind.Automatic);
                        status.Text = r.ConnectionStatus == Windows.Devices.WiFi.WiFiConnectionStatus.Success ? "Connecté !" :
                            r.ConnectionStatus == Windows.Devices.WiFi.WiFiConnectionStatus.InvalidCredential ? "Mot de passe incorrect." : "Connexion impossible (" + r.ConnectionStatus + ").";
                        if (r.ConnectionStatus == Windows.Devices.WiFi.WiFiConnectionStatus.Success) { await Task.Delay(700); Page("Wi-Fi", BuildWifiPage); }
                    }
                    catch (Exception e) { status.Text = "Connexion impossible : " + e.Message; }
                }));
            }
            box.Children.Add(status);
            holder.Children.Add(box);
        }

        // ---- Bluetooth ----

        private async void BuildBtPage(StackPanel body)
        {
            Wait(body, "Recherche des appareils…");
            try
            {
                var list = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true));
                body.Children.Clear();
                foreach (var d in list.OrderBy(x => x.Name).Take(14))
                {
                    bool connected = false;
                    try { var dev = await Windows.Devices.Bluetooth.BluetoothDevice.FromIdAsync(d.Id); connected = dev?.ConnectionStatus == Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected; } catch { }
                    body.Children.Add(Row("", d.Name, connected ? "Connecté" : "Associé", connected, () => Open("ms-settings:bluetooth")));
                }
                if (list.Count == 0) Wait(body, "Aucun appareil associé.");
                body.Children.Add(Row("", "Ajouter un appareil", null, false, () => Open("ms-settings:connecteddevices")));
            }
            catch (Exception e)
            {
                Log.I("Reglages rapides : Bluetooth : " + e.Message);
                Wait(body, "Liste indisponible.");
                body.Children.Add(Row("", "Paramètres Bluetooth", null, false, () => Open("ms-settings:bluetooth")));
            }
        }

        // ---- Sortie audio ----

        private void BuildAudioPage(StackPanel body)
        {
            try
            {
                var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                string cur = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia).ID;
                foreach (var d in en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
                {
                    string id = d.ID;
                    body.Children.Add(Row(d.FriendlyName.Contains("Casque") || d.FriendlyName.Contains("Headphone") || d.FriendlyName.Contains("Head") ? "" : "",
                        d.FriendlyName, id == cur ? "Sortie actuelle" : null, id == cur, () => { AudioOut.SetDefault(id); Page("Sortie audio", BuildAudioPage); }));
                }
            }
            catch (Exception e) { Log.I("Reglages rapides : audio : " + e.Message); Wait(body, "Sorties audio indisponibles."); }
            body.Children.Add(Row("", "Plus de paramètres audio", null, false, () => Open("ms-settings:sound")));
        }

        // ---- Diffuser ----

        private async void BuildCastPage(StackPanel body)
        {
            Wait(body, "Recherche des écrans sans fil…");
            try
            {
                var sel = Windows.Media.Casting.CastingDevice.GetDeviceSelector(Windows.Media.Casting.CastingPlaybackTypes.Video);
                var list = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(sel);
                body.Children.Clear();
                foreach (var d in list.Take(10))
                    body.Children.Add(Row("", d.Name, "Écran sans fil", false, () => { CloseAnimated(); WinKey(0x4B); }));
                if (list.Count == 0) Wait(body, "Aucun écran trouvé.");
            }
            catch (Exception e) { Log.I("Reglages rapides : diffuser : " + e.Message); Wait(body, "Recherche impossible."); }
            body.Children.Add(Row("", "Connecter un écran (Windows)", null, false, () => { CloseAnimated(); WinKey(0x4B); }));
            body.Children.Add(Row("", "Projeter : dupliquer, étendre…", null, false, () => { CloseAnimated(); WinKey(0x50); }));
        }

        // ---- Accessibilite ----

        private void BuildA11yPage(StackPanel body)
        {
            body.Children.Add(Row("", "Loupe", "Agrandir l'écran", false, () => { CloseAnimated(); Shortcuts.Start("magnify.exe"); }));
            body.Children.Add(Row("", "Narrateur", "Lecture de l'écran", false, () => { CloseAnimated(); Shortcuts.Start("narrator.exe"); }));
            body.Children.Add(Row("", "Contraste élevé", null, false, () => Open("ms-settings:easeofaccess-highcontrast")));
            body.Children.Add(Row("", "Sous-titres en direct", null, false, () => { CloseAnimated(); Native.keybd_event(0x11, 0, 0, UIntPtr.Zero); WinKey(0x4C); Native.keybd_event(0x11, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero); }));
            body.Children.Add(Row("", "Plus de paramètres", null, false, () => Open("ms-settings:easeofaccess")));
        }
    }

    /// <summary>Changer la sortie audio par defaut (interface de Windows utilisee par le menu du son).</summary>
    public static class AudioOut
    {
        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] private class PolicyConfigClient { }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            int GetMixFormat(); int GetDeviceFormat(); int ResetDeviceFormat(); int SetDeviceFormat(); int GetProcessingPeriod();
            int SetProcessingPeriod(); int GetShareMode(); int SetShareMode(); int GetPropertyValue(); int SetPropertyValue();
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            int SetEndpointVisibility();
        }

        public static void SetDefault(string id)
        {
            try
            {
                var p = (IPolicyConfig)new PolicyConfigClient();
                for (int role = 0; role < 3; role++) p.SetDefaultEndpoint(id, role);
                Log.I("Sortie audio changee");
            }
            catch (Exception e) { Log.I("Sortie audio : " + e.Message); }
        }
    }
}
