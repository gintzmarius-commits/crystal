using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WaveIsland.Drop;

public static class Program
{
    public static async Task<int> Main(string[] a)
    {
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;
        try
        {
            switch (a[0])
            {
                case "beacon":
                    var b = new WdProtocol.Beacon { Id = 0x12345678, IsPc = true, Ip = new byte[] { 192, 168, 1, 42 }, Port = 50123, BtAddr = new byte[] { 1, 2, 3, 4, 5, 6 }, OfferFor = 0x0A0B0C0D };
                    Console.WriteLine(Convert.ToHexString(b.Encode()));
                    var p = new WdProtocol.Beacon { Id = -2, Ip = null, Port = 0, Psm = 0x0081 };
                    Console.WriteLine(Convert.ToHexString(p.Encode()));
                    return 0;

                case "send":
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(a[1], int.Parse(a[2]), ct);
                    using var s = new WdProtocol.Session(tcp.GetStream(), true);
                    await s.HandshakeAsync(new Dictionary<string, string> { ["name"] = "PC de test", ["platform"] = "windows", ["id"] = "1", ["offer"] = "1" }, ct);
                    using var f = File.OpenRead(a[3]);
                    bool ok = await WdProtocol.SendFileAsync(s, new WdProtocol.Offer { Name = Path.GetFileName(a[3]), Size = f.Length, Mime = "application/octet-stream" }, f, null, ct);
                    Console.WriteLine(ok ? "SENT " + s.Peer["name"] : "REFUSED");
                    return ok ? 0 : 3;
                }

                case "receive":
                {
                    var l = new TcpListener(IPAddress.Loopback, int.Parse(a[1]));
                    l.Start();
                    Console.WriteLine("READY");
                    using var tcp = await l.AcceptTcpClientAsync(ct);
                    using var s = new WdProtocol.Session(tcp.GetStream(), false);
                    await s.HandshakeAsync(new Dictionary<string, string> { ["name"] = "PC de test", ["platform"] = "windows", ["id"] = "2", ["offer"] = "0" }, ct);
                    var o = await WdProtocol.ReceiveFileAsync(s, off => Task.FromResult(true), off => File.Create(a[2]), null, ct);
                    Console.WriteLine("RECEIVED " + o.Name + " " + o.Size + " from " + s.Peer["name"]);
                    return 0;
                }
            }
        }
        catch (Exception e) { Console.Error.WriteLine("ERREUR " + e); return 2; }
        return 1;
    }
}
