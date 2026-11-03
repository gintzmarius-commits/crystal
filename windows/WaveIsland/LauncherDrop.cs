using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WaveIsland
{
    /// <summary>Contenu Wave Drop du panneau qui coule depuis l'ilot.</summary>
    public sealed partial class LauncherWindow
    {
        private StackPanel _dropBody, _dropNearby;
        private TextBlock _dropStatus;
        private Border _dropBarFill, _dropBarTrack;
        private DispatcherTimer _dropTimer;

        private void BuildDropContent()
        {
            _iconSlots.Clear();
            _popQueue.Clear();
            var root = new Grid();
            var dock = new DockPanel { Margin = new Thickness(26, 22, 26, 18) };

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            head.Children.Add(PlateText("Wave Drop", 20, new Thickness(0), bold: true));
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head);

            // Etat du transfert en bas
            var foot = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            _dropStatus = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
            var statusPlate = new Border { Background = _plate, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 5, 10, 6), Child = _dropStatus, Visibility = Visibility.Collapsed };
            _dropBarTrack = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = _plateOn, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            _dropBarFill = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb(176, 240, 210)), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            _dropBarTrack.Child = _dropBarFill;
            foot.Children.Add(statusPlate);
            foot.Children.Add(_dropBarTrack);
            DockPanel.SetDock(foot, Dock.Bottom);
            dock.Children.Add(foot);

            _dropBody = new StackPanel();
            dock.Children.Add(new ScrollViewer { Content = _dropBody, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden });
            root.Children.Add(dock);
            _overlayLayer = new Canvas();
            root.Children.Add(_overlayLayer);
            // Glisser un fichier sur le panneau
            root.AllowDrop = true;
            root.Background = Brushes.Transparent;
            root.Drop += (a, b) =>
            {
                if (b.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0 && File.Exists(f[0]))
                {
                    App.Drop.Pending = WaveDropService.Describe(f[0]);
                    _dropRequest = null;
                    FillDrop();
                }
            };
            _content.Child = root;

            if (_dropTimer == null)
            {
                _dropTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                _dropTimer.Tick += (a, b) => { if (_open && _drop) RefreshNearby(); else _dropTimer.Stop(); };
                App.Drop.NearbyChanged += () => { if (_open && _drop) RefreshNearby(); };
                App.Drop.Progress += (t, x, done, total) => { if (_open && _drop) DropProgress(t, done, total); };
                App.Drop.Finished += (t, x, ok, path) => { if (_open && _drop) DropFinished(t, x, ok, path); };
                App.Drop.RequestReceived += r => { if (_open && _drop && _dropRequest == null) { _dropRequest = r; FillDrop(); } };
            }
            _dropTimer.Start();
            FillDrop();
        }

        private void FillDrop()
        {
            _dropBody.Children.Clear();
            _dropNearby = null;
            var made = new List<FrameworkElement>();

            if (!Settings.Current.DropEnabled)
            {
                _dropBody.Children.Add(PlateText("Wave Drop est désactivé. Active-le pour être visible des appareils proches.", 14, new Thickness(0, 0, 0, 10)));
                var on = Chip("Activer Wave Drop", () => { Settings.Current.DropEnabled = true; Settings.Current.Save(); App.Drop.Start(); FillDrop(); });
                on.HorizontalAlignment = HorizontalAlignment.Left;
                _dropBody.Children.Add(on);
                return;
            }

            // Demande recue : accepter ou refuser
            var r = _dropRequest;
            if (r != null)
            {
                _dropBody.Children.Add(PlateText(r.From + " veut t'envoyer", 14, new Thickness(0, 0, 0, 8)));
                _dropBody.Children.Add(FileCard(r.Offer.Name, r.Offer.Size, r.Offer.Mime, null));
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
                var no = BigButton("Refuser", false, () => { r.Reply(false); _dropRequest = null; Close2(); });
                var yes = BigButton("Accepter", true, () => { r.Reply(true); row.Visibility = Visibility.Collapsed; DropProgress("Réception…", 0, -1); });
                row.Children.Add(no);
                row.Children.Add(yes);
                _dropBody.Children.Add(row);
                made.Add(yes); made.Add(no);
                PopIn(made, 60);
                return;
            }

            var p = App.Drop.Pending;
            if (p == null)
            {
                _dropBody.Children.Add(PlateText("Que veux-tu envoyer ? (ou glisse un fichier sur l'îlot)", 14, new Thickness(0, 0, 0, 10)));
                var wrap = new WrapPanel();
                foreach (var (icon, label, filter) in new[]
                {
                    ("", "Fichiers", "Tous les fichiers|*.*"),
                    ("", "Images", "Images|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.heic;*.bmp"),
                    ("", "Vidéos", "Vidéos|*.mp4;*.mov;*.mkv;*.avi;*.webm"),
                    ("", "Musique", "Musique|*.mp3;*.wav;*.flac;*.m4a;*.ogg"),
                    ("", "Documents", "Documents|*.pdf;*.docx;*.doc;*.xlsx;*.pptx;*.txt;*.odt"),
                    ("", "Applis", "Applis|*.apk;*.exe;*.msi;*.zip"),
                })
                {
                    var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    sp.Children.Add(new TextBlock { Text = icon, FontFamily = Icons, FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center });
                    sp.Children.Add(new TextBlock { Text = label, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });
                    string f = filter;
                    var tile = Clickable(sp, 102, 96, label, () => PickFile(f), null);
                    ((Border)tile).Background = _plate;
                    wrap.Children.Add(tile);
                    made.Add(tile);
                }
                _dropBody.Children.Add(wrap);
                PopIn(made, 60);
                return;
            }

            // Fichier choisi : a qui l'envoyer ?
            _dropBody.Children.Add(FileCard(p.Name, p.Size, p.Mime, () => { App.Drop.Pending = null; FillDrop(); }));
            _dropBody.Children.Add(PlateText("Rapproche l'autre appareil (téléphone ou PC avec Wave Island) :", 13, new Thickness(0, 14, 0, 8)));
            _dropNearby = new StackPanel();
            _dropBody.Children.Add(_dropNearby);
            RefreshNearby();
        }

        private void RefreshNearby()
        {
            if (_dropNearby == null) return;
            _dropNearby.Children.Clear();
            var list = App.Drop.NearbyList();
            if (list.Count == 0)
            {
                _dropNearby.Children.Add(PlateText(App.Drop.BluetoothState == "prêt"
                    ? "Recherche des appareils Wave Drop autour…"
                    : "Bluetooth : " + App.Drop.BluetoothState + ". Les appareils sur le même Wi-Fi apparaîtront quand même.", 13, new Thickness(0)));
                return;
            }
            bool first = true;
            foreach (var n in list.Take(5))
            {
                var g = new DockPanel { Margin = new Thickness(4, 0, 4, 0) };
                var send = BigButton("Envoyer", true, () => App.Drop.Send(n));
                send.Margin = new Thickness(10, 0, 0, 0);
                DockPanel.SetDock(send, Dock.Right);
                g.Children.Add(send);
                var ic = new TextBlock { Text = n.Beacon.IsPc ? "" : "", FontFamily = Icons, FontSize = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 12, 0) };
                g.Children.Add(ic);
                var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                texts.Children.Add(new TextBlock { Text = n.Label, FontSize = 14.5, FontWeight = FontWeights.SemiBold });
                texts.Children.Add(new TextBlock
                {
                    Text = (n.Close ? "Tout près" : n.Rssi > -75 ? "Proche" : "Dans les environs") + (n.Beacon.Ip != null ? " · Wi-Fi" : " · Bluetooth"),
                    FontSize = 12, Opacity = 0.8
                });
                g.Children.Add(texts);
                var card = new Border
                {
                    Child = g, CornerRadius = new CornerRadius(16), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8),
                    Background = first && n.Close ? _plateOn : _plate
                };
                _dropNearby.Children.Add(card);
                first = false;
            }
        }

        private FrameworkElement FileCard(string name, long size, string mime, Action change)
        {
            var g = new DockPanel();
            if (change != null)
            {
                var c = Chip("Changer", change);
                DockPanel.SetDock(c, Dock.Right);
                c.VerticalAlignment = VerticalAlignment.Center;
                g.Children.Add(c);
            }
            string glyph = mime.StartsWith("image") ? "" : mime.StartsWith("video") ? "" : mime.StartsWith("audio") ? "" : "";
            g.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 26, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 14, 0) });
            var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            t.Children.Add(new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            t.Children.Add(new TextBlock { Text = SizeText(size) + " · " + mime, FontSize = 12, Opacity = 0.8 });
            g.Children.Add(t);
            return new Border { Child = g, Background = _plateOn, CornerRadius = new CornerRadius(16), Padding = new Thickness(12) };
        }

        private Border BigButton(string label, bool primary, Action click)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(18), Padding = new Thickness(20, 8, 20, 9), Margin = new Thickness(0, 0, 10, 0), Cursor = Cursors.Hand,
                Background = primary ? new SolidColorBrush(Color.FromRgb(176, 240, 210)) : _plateOn,
                Child = primary
                    ? new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(12, 34, 24)) }
                    : new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold },
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1)
            };
            b.MouseLeftButtonDown += (x, y) => y.Handled = true;
            b.MouseLeftButtonUp += (x, y) => { y.Handled = true; click(); };
            return b;
        }

        private void PickFile(string filter)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choisis le fichier à envoyer", Filter = filter + "|Tous les fichiers|*.*" };
            _dialog = true;
            bool? ok;
            try { ok = dlg.ShowDialog(this); }
            finally { _dialog = false; }
            ForceForeground();
            if (ok == true)
            {
                App.Drop.Pending = WaveDropService.Describe(dlg.FileName);
                FillDrop();
            }
        }

        private void DropProgress(string title, long done, long total)
        {
            if (_dropStatus == null) return;
            ((Border)_dropStatus.Parent).Visibility = Visibility.Visible;
            _dropStatus.Text = total > 0 ? $"{title} · {done * 100 / total} %" : title;
            _dropBarTrack.Visibility = Visibility.Visible;
            double w = Math.Max(0, _dropBarTrack.ActualWidth);
            _dropBarFill.Width = total > 0 ? Math.Max(6, w * done / total) : w * 0.3;
        }

        private void DropFinished(string title, string text, bool ok, string path)
        {
            if (_dropStatus == null) return;
            ((Border)_dropStatus.Parent).Visibility = Visibility.Visible;
            _dropStatus.Text = (ok ? "✓ " : "✕ ") + title + (string.IsNullOrEmpty(text) ? "" : " — " + text);
            _dropBarTrack.Visibility = Visibility.Collapsed;
            _dropRequest = null;
            if (ok && path != null)
            {
                var open = Chip("Afficher le fichier", () => { Close2(); try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { } });
                open.HorizontalAlignment = HorizontalAlignment.Left;
                open.Margin = new Thickness(0, 12, 0, 0);
                _dropBody.Children.Add(open);
            }
            else if (ok) Dispatcher.BeginInvoke(new Action(FillDrop), DispatcherPriority.Background);
        }

        public static string SizeText(long b)
        {
            if (b < 0) return "taille inconnue";
            if (b >= 1L << 30) return (b / (1024.0 * 1024 * 1024)).ToString("0.0") + " Go";
            if (b >= 1L << 20) return (b / (1024.0 * 1024)).ToString("0.0") + " Mo";
            return Math.Max(1, b / 1024) + " Ko";
        }
    }
}
