using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WaveIsland.Drop
{
    /// <summary>
    /// Wave Drop : protocole commun Windows / Android (le meme est ecrit en Kotlin dans WaveDropProtocol.kt).
    /// Detection par une petite balise Bluetooth (ou NFC sur telephone), connexion par le Wi-Fi local ou le Bluetooth,
    /// cles echangees en ECDH P-256, tout chiffre en AES-256-GCM, le destinataire doit accepter,
    /// empreinte SHA-256 verifiee a la fin.
    /// </summary>
    public static class WdProtocol
    {
        public const ushort CompanyId = 0xFFFF;
        public const string RfcommUuid = "7a1e0001-5d2b-4c8e-9f3a-576176654472";
        public const int Chunk = 64 * 1024;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("WDP1");
        private static readonly byte[] KdfLabel = Encoding.ASCII.GetBytes("WaveDrop1");

        // ---------- Balise (23 octets) ----------

        public sealed class Beacon
        {
            public int Id;
            public bool IsPc;
            public byte[] Ip;      // IPv4 (null = pas de Wi-Fi)
            public int Port;
            public byte[] BtAddr;  // adresse Bluetooth classique (PC)
            public int Psm;        // canal L2CAP (telephone)
            public int OfferFor;   // != 0 : fichier en attente pour cet appareil

            public byte[] Encode()
            {
                var b = new byte[23];
                b[0] = (byte)'W'; b[1] = (byte)'D';
                int flags = 0x10;
                if (Ip != null) flags |= 0x01;
                if (BtAddr != null) flags |= 0x02;
                if (Psm > 0) flags |= 0x04;
                if (OfferFor != 0) flags |= 0x08;
                if (IsPc) flags |= 0x20;
                b[2] = (byte)flags;
                BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(3), Id);
                if (Ip != null) Array.Copy(Ip, 0, b, 7, 4);
                BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(11), (ushort)Port);
                if (BtAddr != null) Array.Copy(BtAddr, 0, b, 13, 6);
                else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(13), (ushort)Psm);
                BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(19), OfferFor);
                return b;
            }

            public static Beacon Decode(byte[] d)
            {
                if (d == null || d.Length < 23 || d[0] != 'W' || d[1] != 'D') return null;
                int flags = d[2];
                if ((flags & 0x10) == 0) return null;
                var ip = new byte[4];
                Array.Copy(d, 7, ip, 0, 4);
                var rest = new byte[6];
                Array.Copy(d, 13, rest, 0, 6);
                return new Beacon
                {
                    Id = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(3)),
                    IsPc = (flags & 0x20) != 0,
                    Ip = (flags & 0x01) != 0 ? ip : null,
                    Port = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(11)),
                    BtAddr = (flags & 0x02) != 0 ? rest : null,
                    Psm = (flags & 0x04) != 0 ? (rest[0] << 8) | rest[1] : 0,
                    OfferFor = (flags & 0x08) != 0 ? BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(19)) : 0
                };
            }

            public string IpString => Ip == null ? null : $"{Ip[0]}.{Ip[1]}.{Ip[2]}.{Ip[3]}";
        }

        // ---------- Messages cle=valeur ----------

        public static byte[] EncodeMap(IDictionary<string, string> m)
        {
            var sb = new StringBuilder();
            foreach (var kv in m) sb.Append(Esc(kv.Key)).Append('=').Append(Esc(kv.Value ?? "")).Append('\n');
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        public static Dictionary<string, string> DecodeMap(byte[] b, int offset = 0)
        {
            var m = new Dictionary<string, string>();
            foreach (var line in Encoding.UTF8.GetString(b, offset, b.Length - offset).Split('\n'))
            {
                int i = line.IndexOf('=');
                if (i > 0) m[Unesc(line.Substring(0, i))] = Unesc(line.Substring(i + 1));
            }
            return m;
        }

        private static string Esc(string s)
        {
            var sb = new StringBuilder();
            foreach (byte c in Encoding.UTF8.GetBytes(s))
            {
                char ch = (char)c;
                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_' || ch == '.') sb.Append(ch);
                else sb.Append('%').Append("0123456789ABCDEF"[c >> 4]).Append("0123456789ABCDEF"[c & 15]);
            }
            return sb.ToString();
        }

        private static string Unesc(string s)
        {
            var o = new MemoryStream();
            for (int i = 0; i < s.Length;)
            {
                if (s[i] == '%' && i + 2 < s.Length) { o.WriteByte(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 3; }
                else { o.WriteByte((byte)s[i]); i++; }
            }
            return Encoding.UTF8.GetString(o.ToArray());
        }

        // ---------- Session securisee ----------

        public sealed class Session : IDisposable
        {
            private readonly Stream _s;
            private readonly bool _connector;
            private AesGcm _aes;
            private long _send, _recv;
            public Dictionary<string, string> Peer { get; private set; }

            public Session(Stream s, bool connector) { _s = s; _connector = connector; }

            public Task HandshakeAsync(IDictionary<string, string> mine, CancellationToken ct) => HandshakeAsync(_ => mine, ct);

            /// <summary>mine(peer) : la presentation peut dependre de l'autre (cote qui attend, il parle en second).</summary>
            public async Task HandshakeAsync(Func<Dictionary<string, string>, IDictionary<string, string>> mine, CancellationToken ct)
            {
                using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                byte[] Frame(Dictionary<string, string> peer)
                {
                    var hello = new Dictionary<string, string>(mine(peer)) { ["v"] = "1", ["pub"] = Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo()) };
                    return Concat(Magic, EncodeMap(hello));
                }
                if (_connector) { await WriteFrame(Frame(null), ct); Peer = await ReadHello(ct); }
                else { Peer = await ReadHello(ct); await WriteFrame(Frame(Peer), ct); }
                if (!Peer.TryGetValue("pub", out var pub)) throw new IOException("cle absente");
                using var other = ECDiffieHellman.Create();
                other.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pub), out _);
                // cle = SHA-256(secret ECDH || "WaveDrop1"), identique cote Android
                byte[] key = ecdh.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256, null, KdfLabel);
                _aes = new AesGcm(key, 16);
            }

            private async Task<Dictionary<string, string>> ReadHello(CancellationToken ct)
            {
                var f = await ReadFrame(ct);
                if (f.Length < 4 || f[0] != Magic[0] || f[1] != Magic[1] || f[2] != Magic[2] || f[3] != Magic[3]) throw new IOException("pas un appareil Wave Drop");
                return DecodeMap(f, 4);
            }

            private async Task WriteFrame(byte[] b, CancellationToken ct)
            {
                var len = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(len, b.Length);
                await _s.WriteAsync(len, ct);
                await _s.WriteAsync(b, ct);
                await _s.FlushAsync(ct);
            }

            private async Task<byte[]> ReadFrame(CancellationToken ct)
            {
                var len = new byte[4];
                await _s.ReadExactlyAsync(len, ct);
                int n = BinaryPrimitives.ReadInt32BigEndian(len);
                if (n < 0 || n > Chunk + 1024) throw new IOException("trame invalide");
                var b = new byte[n];
                await _s.ReadExactlyAsync(b, ct);
                return b;
            }

            private byte[] Nonce(bool mine, long counter)
            {
                var n = new byte[12];
                bool fromConnector = mine ? _connector : !_connector;
                BinaryPrimitives.WriteInt32BigEndian(n, fromConnector ? 1 : 2);
                BinaryPrimitives.WriteInt64BigEndian(n.AsSpan(4), counter);
                return n;
            }

            /// <summary>type : 1 = message, 2 = morceau de fichier.</summary>
            public async Task SendAsync(int type, byte[] body, int len, CancellationToken ct)
            {
                var plain = new byte[len + 1];
                plain[0] = (byte)type;
                Array.Copy(body, 0, plain, 1, len);
                var frame = new byte[plain.Length + 16];
                _aes.Encrypt(Nonce(true, _send++), plain, frame.AsSpan(0, plain.Length), frame.AsSpan(plain.Length, 16));
                await WriteFrame(frame, ct);
            }

            public async Task<(int type, byte[] body)> ReceiveAsync(CancellationToken ct)
            {
                var f = await ReadFrame(ct);
                if (f.Length < 17) throw new IOException("trame trop courte");
                var plain = new byte[f.Length - 16];
                _aes.Decrypt(Nonce(false, _recv++), f.AsSpan(0, plain.Length), f.AsSpan(plain.Length, 16), plain);
                return (plain[0], plain.AsSpan(1).ToArray());
            }

            public Task SendMapAsync(IDictionary<string, string> m, CancellationToken ct)
            {
                var b = EncodeMap(m);
                return SendAsync(1, b, b.Length, ct);
            }

            public async Task<Dictionary<string, string>> ReceiveMapAsync(CancellationToken ct)
            {
                var (t, b) = await ReceiveAsync(ct);
                if (t != 1) throw new IOException("message inattendu");
                return DecodeMap(b);
            }

            public void Dispose() => _aes?.Dispose();
        }

        // ---------- Transfert ----------

        public sealed class Offer
        {
            public string Name, Mime;
            public long Size;
        }

        /// <summary>Envoie un fichier ; false si l'autre a refuse.</summary>
        public static async Task<bool> SendFileAsync(Session s, Offer offer, Stream source, Action<long> progress, CancellationToken ct)
        {
            await s.SendMapAsync(new Dictionary<string, string> { ["t"] = "offer", ["name"] = offer.Name, ["size"] = offer.Size.ToString(), ["mime"] = offer.Mime }, ct);
            var answer = await s.ReceiveMapAsync(ct);
            if (!answer.TryGetValue("ok", out var ok) || ok != "1") return false;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[Chunk];
            long sent = 0;
            while (true)
            {
                int n = await ReadSome(source, buf, ct);
                if (n <= 0) break;
                sha.AppendData(buf, 0, n);
                await s.SendAsync(2, buf, n, ct);
                sent += n;
                progress?.Invoke(sent);
            }
            await s.SendMapAsync(new Dictionary<string, string> { ["t"] = "end", ["sha"] = Convert.ToBase64String(sha.GetHashAndReset()), ["size"] = sent.ToString() }, ct);
            var done = await s.ReceiveMapAsync(ct);
            if (!done.TryGetValue("ok", out var d) || d != "1") throw new IOException("le fichier est arrivé abîmé");
            return true;
        }

        private static async Task<int> ReadSome(Stream s, byte[] buf, CancellationToken ct)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int n = await s.ReadAsync(buf.AsMemory(total), ct);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        /// <summary>Recoit un fichier ; ask demande a l'utilisateur ; sink donne le flux d'ecriture. null si refuse.</summary>
        public static async Task<Offer> ReceiveFileAsync(Session s, Func<Offer, Task<bool>> ask, Func<Offer, Stream> sink, Action<long> progress, CancellationToken ct)
        {
            var m = await s.ReceiveMapAsync(ct);
            if (!m.TryGetValue("t", out var t) || t != "offer") throw new IOException("offre attendue");
            var offer = new Offer
            {
                Name = m.TryGetValue("name", out var nm) ? nm : "fichier",
                Size = m.TryGetValue("size", out var sz) && long.TryParse(sz, out var l) ? l : -1,
                Mime = m.TryGetValue("mime", out var mi) ? mi : "application/octet-stream"
            };
            bool accept = await ask(offer);
            await s.SendMapAsync(new Dictionary<string, string> { ["t"] = "answer", ["ok"] = accept ? "1" : "0" }, ct);
            if (!accept) return null;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long got = 0;
            using (var o = sink(offer))
            {
                while (true)
                {
                    var (type, body) = await s.ReceiveAsync(ct);
                    if (type == 2)
                    {
                        sha.AppendData(body);
                        await o.WriteAsync(body, ct);
                        got += body.Length;
                        progress?.Invoke(got);
                    }
                    else
                    {
                        var end = DecodeMap(body);
                        bool good = end.TryGetValue("t", out var et) && et == "end" &&
                                    end.TryGetValue("sha", out var h) && h == Convert.ToBase64String(sha.GetHashAndReset());
                        await s.SendMapAsync(new Dictionary<string, string> { ["t"] = "done", ["ok"] = good ? "1" : "0" }, ct);
                        if (!good) throw new IOException("empreinte différente : fichier abîmé");
                        break;
                    }
                }
            }
            return offer;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }
    }
}
