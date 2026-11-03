using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using WaveIsland.Drop;

namespace WaveIsland
{
    /// <summary>
    /// Wave Drop sur PC : visible des appareils proches (balise Bluetooth), recoit par le Wi-Fi local ou le Bluetooth (RFCOMM),
    /// envoie par le Wi-Fi s'il est commun ; sinon le telephone vient chercher le fichier en Bluetooth.
    /// </summary>
    public sealed class WaveDropService
    {
        public const int CloseRssi = -60;

        public sealed class Nearby
        {
            public WdProtocol.Beacon Beacon;
            public double Rssi;
            public DateTime LastSeen;
            public string Name;
            public ulong BtAddress;
            public string Label => Name ?? (Beacon.IsPc ? "PC Windows" : "Téléphone Android");
            public bool Close => Rssi >= CloseRssi;
        }

        public sealed class Outgoing
        {
            public string Path, Name, Mime;
            public long Size;
        }

        public sealed class Request
        {
            public string From;
            public WdProtocol.Offer Offer;
            internal readonly TaskCompletionSource<bool> Answer = new TaskCompletionSource<bool>();
            public void Reply(bool ok) => Answer.TrySetResult(ok);
        }

        public event Action NearbyChanged;
        public event Action<Request> RequestReceived;
        /// <summary>titre, texte, fait, total</summary>
        public event Action<string, string, long, long> Progress;
        /// <summary>titre, texte, ok, fichier recu</summary>
        public event Action<string, string, bool, string> Finished;

        private readonly Dispatcher _ui;
        private readonly ConcurrentDictionary<int, Nearby> _nearby = new ConcurrentDictionary<int, Nearby>();
        private readonly ConcurrentDictionary<int, bool> _naming = new ConcurrentDictionary<int, bool>();
        private TcpListener _tcp;
        private RfcommServiceProvider _rfcomm;
        private StreamSocketListener _rfcommListener;
        private BluetoothLEAdvertisementPublisher _publisher;
        private BluetoothLEAdvertisementWatcher _watcher;
        private byte[] _btAddr;
        private CancellationTokenSource _cts;
        private int _offerFor;
        private int _busy;

        public bool Running { get; private set; }
        public Outgoing Pending { get; set; }
        public Request Waiting { get; private set; }
        public string BluetoothState { get; private set; } = "inconnu";

        public WaveDropService(Dispatcher ui) { _ui = ui; }

        public static string DownloadFolder => Path.Combine(
            Native.KnownFolder(Native.FOLDERID_Downloads) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "Wave Drop");

        private static int MyId
        {
            get
            {
                var s = Settings.Current;
                if (s.DropId == 0) { s.DropId = new Random().Next(1, int.MaxValue); s.Save(); }
                return s.DropId;
            }
        }

        public static string MyName => string.IsNullOrWhiteSpace(Settings.Current.DropName) ? Environment.MachineName : Settings.Current.DropName;

        private Dictionary<string, string> Hello(bool offer) => new Dictionary<string, string>
        {
            ["name"] = MyName, ["platform"] = "windows", ["id"] = MyId.ToString(), ["offer"] = offer ? "1" : "0"
        };

        // ================== Demarrage ==================

        public async void Start()
        {
            if (Running || !Settings.Current.DropEnabled) return;
            Running = true;
            _cts = new CancellationTokenSource();
            Log.I("Wave Drop : demarrage (" + MyName + ")");
            StartTcp();
            try
            {
                var adapter = await BluetoothAdapter.GetDefaultAsync();
                if (adapter == null) { BluetoothState = "absent"; Log.I("Wave Drop : pas de Bluetooth"); }
                else
                {
                    ulong a = adapter.BluetoothAddress;
                    _btAddr = new byte[6];
                    for (int i = 0; i < 6; i++) _btAddr[i] = (byte)(a >> (40 - 8 * i));
                    BluetoothState = adapter.IsLowEnergySupported ? "prêt" : "sans Bluetooth basse énergie";
                    await StartRfcomm();
                    StartWatcher();
                }
            }
            catch (Exception e) { BluetoothState = "indisponible"; Log.I("Wave Drop : Bluetooth indisponible (" + e.Message + ")"); }
            Advertise();
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        }

        public void Stop()
        {
            if (!Running) return;
            Running = false;
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            try { _cts?.Cancel(); } catch { }
            try { _tcp?.Stop(); } catch { }
            try { _publisher?.Stop(); } catch { }
            try { _watcher?.Stop(); } catch { }
            try { _rfcomm?.StopAdvertising(); } catch { }
            try { _rfcommListener?.Dispose(); } catch { }
            _tcp = null; _publisher = null; _watcher = null; _rfcomm = null; _rfcommListener = null;
            _nearby.Clear();
            Log.I("Wave Drop : arret");
        }

        public void Restart() { Stop(); Start(); }

        private void OnNetworkChanged(object s, EventArgs e) => _ui.BeginInvoke(new Action(Advertise));

        private static byte[] LocalIp()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 && ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;
                    var p = ni.GetIPProperties();
                    if (p.GatewayAddresses.Count == 0) continue;
                    foreach (var u in p.UnicastAddresses)
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address))
                            return u.Address.GetAddressBytes();
                }
            }
            catch { }
            return null;
        }

        public WdProtocol.Beacon Beacon() => new WdProtocol.Beacon
        {
            Id = MyId, IsPc = true, Ip = LocalIp(), Port = (_tcp?.LocalEndpoint as IPEndPoint)?.Port ?? 0, BtAddr = _btAddr, OfferFor = _offerFor
        };

        /// <summary>Diffuse la balise (refaite quand l'adresse ou l'offre change).</summary>
        private void Advertise()
        {
            if (!Running || _btAddr == null) return;
            try
            {
                _publisher?.Stop();
                var p = new BluetoothLEAdvertisementPublisher();
                p.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData(WdProtocol.CompanyId, Beacon().Encode().AsBuffer()));
                p.StatusChanged += (a, e) => { if (e.Status == BluetoothLEAdvertisementPublisherStatus.Aborted) Log.I("Wave Drop : diffusion Bluetooth arretee (" + e.Error + ")"); };
                p.Start();
                _publisher = p;
            }
            catch (Exception e) { Log.I("Wave Drop : diffusion Bluetooth impossible (" + e.Message + ")"); }
        }

        // ================== Detection ==================

        private void StartWatcher()
        {
            var w = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
            w.Received += (s, e) =>
            {
                foreach (var md in e.Advertisement.ManufacturerData)
                {
                    if (md.CompanyId != WdProtocol.CompanyId) continue;
                    var b = WdProtocol.Beacon.Decode(md.Data.ToArray());
                    if (b == null || b.Id == MyId) continue;
                    var n = _nearby.GetOrAdd(b.Id, _ => new Nearby { Beacon = b, Rssi = e.RawSignalStrengthInDBm });
                    bool isNew = n.LastSeen == default;
                    n.Beacon = b;
                    n.Rssi = isNew ? e.RawSignalStrengthInDBm : n.Rssi * 0.6 + e.RawSignalStrengthInDBm * 0.4;
                    n.LastSeen = DateTime.Now;
                    n.BtAddress = e.BluetoothAddress;
                    if (isNew) { Log.I($"Wave Drop : appareil detecte ({(b.IsPc ? "PC" : "telephone")}, {e.RawSignalStrengthInDBm} dBm)"); ResolveName(n); }
                    _ui.BeginInvoke(new Action(() => NearbyChanged?.Invoke()));
                }
            };
            w.Stopped += (s, e) => { if (e.Error != BluetoothError.Success) Log.I("Wave Drop : recherche Bluetooth arretee (" + e.Error + ")"); };
            w.Start();
            _watcher = w;
        }

        public List<Nearby> NearbyList()
        {
            var now = DateTime.Now;
            foreach (var kv in _nearby.Where(k => (now - k.Value.LastSeen).TotalSeconds > 12).ToList()) _nearby.TryRemove(kv.Key, out _);
            return _nearby.Values.OrderByDescending(n => n.Rssi).ToList();
        }

        private void ResolveName(Nearby n)
        {
            var ip = n.Beacon.IpString;
            if (ip == null || n.Beacon.Port == 0 || !_naming.TryAdd(n.Beacon.Id, true)) return;
            Task.Run(async () =>
            {
                try
                {
                    using var tcp = new TcpClient();
                    var c = new CancellationTokenSource(4000);
                    await tcp.ConnectAsync(ip, n.Beacon.Port, c.Token);
                    using var s = new WdProtocol.Session(tcp.GetStream(), true);
                    await s.HandshakeAsync(Hello(false), c.Token);
                    n.Name = s.Peer.TryGetValue("name", out var nm) ? nm : null;
                    _ui.BeginInvoke(new Action(() => NearbyChanged?.Invoke()));
                }
                catch (Exception e) { Log.I("Wave Drop : nom non obtenu (" + e.Message + ")"); }
                finally { _naming.TryRemove(n.Beacon.Id, out _); }
            });
        }

        // ================== Reception ==================

        private void StartTcp()
        {
            try
            {
                _tcp = new TcpListener(IPAddress.Any, 0);
                _tcp.Start();
                var l = _tcp;
                Task.Run(async () =>
                {
                    while (Running)
                    {
                        try
                        {
                            var c = await l.AcceptTcpClientAsync(_cts.Token);
                            _ = Task.Run(() => Handle(c.GetStream(), false, "Wi-Fi", c));
                        }
                        catch (Exception e) { if (Running) Log.I("Wave Drop : serveur Wi-Fi (" + e.Message + ")"); break; }
                    }
                });
                Log.I("Wave Drop : en attente sur le Wi-Fi (port " + ((IPEndPoint)_tcp.LocalEndpoint).Port + ")");
            }
            catch (Exception e) { Log.I("Wave Drop : serveur Wi-Fi impossible (" + e.Message + ")"); }
        }

        private async Task StartRfcomm()
        {
            try
            {
                _rfcomm = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(Guid.Parse(WdProtocol.RfcommUuid)));
                _rfcommListener = new StreamSocketListener();
                _rfcommListener.ConnectionReceived += (s, e) =>
                {
                    var sock = e.Socket;
                    var duplex = new DuplexStream(sock.InputStream.AsStreamForRead(), sock.OutputStream.AsStreamForWrite());
                    _ = Task.Run(() => Handle(duplex, false, "Bluetooth", sock));
                };
                await _rfcommListener.BindServiceNameAsync(_rfcomm.ServiceId.AsString(), SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
                _rfcomm.StartAdvertising(_rfcommListener, true);
                Log.I("Wave Drop : en attente en Bluetooth");
            }
            catch (Exception e) { Log.I("Wave Drop : Bluetooth RFCOMM impossible (" + e.Message + ")"); }
        }

        /// <summary>Connexion : presentation, puis reception (l'autre propose) ou envoi (un telephone vient chercher notre fichier).</summary>
        private async Task Handle(Stream stream, bool connector, string via, IDisposable owner)
        {
            try
            {
                var ct = _cts.Token;
                using var s = new WdProtocol.Session(stream, connector);
                bool sending = false;
                await s.HandshakeAsync(peer =>
                {
                    // Un telephone qui vient chercher le fichier qu'on lui destine
                    sending = !connector && peer != null && Pending != null && _offerFor != 0 &&
                              peer.TryGetValue("id", out var pid) && pid == _offerFor.ToString() && !(peer.TryGetValue("offer", out var po) && po == "1");
                    return Hello(sending);
                }, ct);
                string from = s.Peer.TryGetValue("name", out var nm) ? nm : "Appareil";
                if (s.Peer.TryGetValue("offer", out var o) && o == "1") await Receive(s, from, via, ct);
                else if (sending) await SendOver(s, from, via, ct);
            }
            catch (Exception e) { Log.I($"Wave Drop : connexion interrompue ({via}) : {e.Message}"); }
            finally { try { owner?.Dispose(); } catch { } try { stream.Dispose(); } catch { } }
        }

        private async Task Receive(WdProtocol.Session s, string from, string via, CancellationToken ct)
        {
            string path = null, curName = "";
            long curSize = -1;
            WdProtocol.Offer offer = null;
            try
            {
                offer = await WdProtocol.ReceiveFileAsync(s,
                    async off =>
                    {
                        curName = off.Name;
                        curSize = off.Size;
                        var r = new Request { From = from, Offer = off };
                        Waiting = r;
                        Log.I($"Wave Drop : {from} veut envoyer {off.Name} ({off.Size} octets, {via})");
                        _ = _ui.BeginInvoke(new Action(() => RequestReceived?.Invoke(r)));
                        var done = await Task.WhenAny(r.Answer.Task, Task.Delay(90_000, ct));
                        Waiting = null;
                        bool ok = done == r.Answer.Task && r.Answer.Task.Result;
                        Log.I("Wave Drop : demande " + (ok ? "acceptee" : "refusee"));
                        return ok;
                    },
                    off =>
                    {
                        Directory.CreateDirectory(DownloadFolder);
                        path = UniquePath(DownloadFolder, off.Name);
                        return File.Create(path);
                    },
                    got => _ui.BeginInvoke(new Action(() => Progress?.Invoke("Réception de " + curName, "De " + from, got, curSize))), ct);
            }
            catch (Exception e)
            {
                if (path != null) try { File.Delete(path); } catch { }
                _ = _ui.BeginInvoke(new Action(() => Finished?.Invoke("Réception interrompue", e.Message, false, null)));
                return;
            }
            if (offer == null) { _ = _ui.BeginInvoke(new Action(() => Finished?.Invoke("Refusé", "", false, null))); return; }
            Log.I("Wave Drop : recu " + offer.Name);
            _ = _ui.BeginInvoke(new Action(() => Finished?.Invoke("Reçu de " + from, offer.Name, true, path)));
        }

        private static string UniquePath(string dir, string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (string.IsNullOrWhiteSpace(name)) name = "fichier";
            string p = Path.Combine(dir, name);
            for (int k = 1; File.Exists(p); k++)
                p = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + $" ({k})" + Path.GetExtension(name));
            return p;
        }

        // ================== Envoi ==================

        public static Outgoing Describe(string path)
        {
            var fi = new FileInfo(path);
            return new Outgoing { Path = path, Name = fi.Name, Size = fi.Length, Mime = MimeOf(fi.Extension) };
        }

        private static string MimeOf(string ext)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".heic": return "image/heic";
                case ".mp4": return "video/mp4";
                case ".mov": return "video/quicktime";
                case ".mkv": return "video/x-matroska";
                case ".mp3": return "audio/mpeg";
                case ".wav": return "audio/wav";
                case ".flac": return "audio/flac";
                case ".m4a": return "audio/mp4";
                case ".pdf": return "application/pdf";
                case ".txt": return "text/plain";
                case ".zip": return "application/zip";
                case ".apk": return "application/vnd.android.package-archive";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".pptx": return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
                default: return "application/octet-stream";
            }
        }

        /// <summary>Envoie le fichier en attente a cet appareil (Wi-Fi si commun, sinon il vient le chercher en Bluetooth).</summary>
        public void Send(Nearby target)
        {
            var o = Pending;
            if (o == null || Interlocked.Exchange(ref _busy, 1) == 1) return;
            Task.Run(async () =>
            {
                try
                {
                    var ct = _cts.Token;
                    // 1) Wi-Fi local
                    var ip = target.Beacon.IpString;
                    if (ip != null && target.Beacon.Port > 0 && LocalIp() != null)
                    {
                        TcpClient tcp = null;
                        try
                        {
                            tcp = new TcpClient();
                            using var c = new CancellationTokenSource(1500);
                            await tcp.ConnectAsync(ip, target.Beacon.Port, c.Token);
                        }
                        catch { tcp?.Dispose(); tcp = null; Log.I("Wave Drop : pas de Wi-Fi commun, passage au Bluetooth"); }
                        if (tcp != null)
                        {
                            using (tcp)
                            using (var s = new WdProtocol.Session(tcp.GetStream(), true))
                            {
                                await s.HandshakeAsync(Hello(true), ct);
                                await SendOver(s, s.Peer.TryGetValue("name", out var nm) ? nm : target.Label, "Wi-Fi", ct);
                            }
                            return;
                        }
                    }
                    // 2) Bluetooth vers un autre PC
                    if (target.Beacon.IsPc && target.BtAddress != 0)
                    {
                        var dev = await BluetoothDevice.FromBluetoothAddressAsync(target.BtAddress);
                        var services = await dev.GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(Guid.Parse(WdProtocol.RfcommUuid)), BluetoothCacheMode.Uncached);
                        var svc = services.Services.FirstOrDefault() ?? throw new IOException("Wave Drop introuvable sur ce PC");
                        var sock = new StreamSocket();
                        await sock.ConnectAsync(svc.ConnectionHostName, svc.ConnectionServiceName, SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
                        using var duplex = new DuplexStream(sock.InputStream.AsStreamForRead(), sock.OutputStream.AsStreamForWrite());
                        using var s = new WdProtocol.Session(duplex, true);
                        await s.HandshakeAsync(Hello(true), ct);
                        await SendOver(s, s.Peer.TryGetValue("name", out var nm) ? nm : target.Label, "Bluetooth", ct);
                        sock.Dispose();
                        return;
                    }
                    // 3) Telephone sans Wi-Fi commun : on annonce le fichier, il vient le chercher en Bluetooth
                    if (_btAddr == null) throw new IOException("pas de Wi-Fi commun ni de Bluetooth");
                    _offerFor = target.Beacon.Id;
                    _ui.Invoke(Advertise);
                    _ = _ui.BeginInvoke(new Action(() => Progress?.Invoke("En attente de " + target.Label, "Il se connecte en Bluetooth…", 0, o.Size)));
                    Log.I("Wave Drop : fichier annonce en Bluetooth pour " + target.Label);
                    // L'offre reste annoncee 2 minutes
                    await Task.Delay(120_000, ct).ContinueWith(_ => { });
                    if (_offerFor == target.Beacon.Id) { _offerFor = 0; _ui.Invoke(Advertise); }
                }
                catch (Exception e)
                {
                    Log.I("Wave Drop : envoi impossible (" + e.Message + ")");
                    _ = _ui.BeginInvoke(new Action(() => Finished?.Invoke("Envoi impossible", e.Message, false, null)));
                }
                finally { Interlocked.Exchange(ref _busy, 0); }
            });
        }

        private async Task SendOver(WdProtocol.Session s, string to, string via, CancellationToken ct)
        {
            var o = Pending;
            if (o == null) return;
            Log.I($"Wave Drop : envoi de {o.Name} a {to} ({via})");
            _ = _ui.BeginInvoke(new Action(() => Progress?.Invoke("En attente de " + to, "Il doit accepter " + o.Name, 0, o.Size)));
            bool ok;
            using (var f = File.OpenRead(o.Path))
                ok = await WdProtocol.SendFileAsync(s, new WdProtocol.Offer { Name = o.Name, Size = o.Size, Mime = o.Mime }, f,
                    sent => _ui.BeginInvoke(new Action(() => Progress?.Invoke("Envoi à " + to, o.Name, sent, o.Size))), ct);
            Log.I("Wave Drop : envoi " + (ok ? "termine" : "refuse"));
            if (_offerFor != 0) { _offerFor = 0; _ = _ui.BeginInvoke(new Action(Advertise)); }
            if (ok) Pending = null;
            _ = _ui.BeginInvoke(new Action(() => Finished?.Invoke(ok ? "Envoyé à " + to : to + " a refusé", o.Name, ok, null)));
        }

        /// <summary>Un flux pour lire et ecrire (le Bluetooth en donne deux separes).</summary>
        private sealed class DuplexStream : Stream
        {
            private readonly Stream _r, _w;
            public DuplexStream(Stream r, Stream w) { _r = r; _w = w; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _w.Flush();
            public override Task FlushAsync(CancellationToken ct) => _w.FlushAsync(ct);
            public override int Read(byte[] b, int o, int c) => _r.Read(b, o, c);
            public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => _r.ReadAsync(b, o, c, ct);
            public override ValueTask<int> ReadAsync(Memory<byte> m, CancellationToken ct = default) => _r.ReadAsync(m, ct);
            public override void Write(byte[] b, int o, int c) => _w.Write(b, o, c);
            public override Task WriteAsync(byte[] b, int o, int c, CancellationToken ct) => _w.WriteAsync(b, o, c, ct);
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> m, CancellationToken ct = default) => _w.WriteAsync(m, ct);
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            protected override void Dispose(bool d) { if (d) { try { _r.Dispose(); } catch { } try { _w.Dispose(); } catch { } } base.Dispose(d); }
        }
    }
}
