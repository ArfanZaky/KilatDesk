using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace KilatDesk
{
    internal static class AppRole
    {
        public const byte Host = 1, Viewer = 2;
        public static string Name(byte r) => r == Host ? "Host" : "Viewer";
    }

    internal sealed class AuthException : Exception
    {
        public readonly byte Code;
        public AuthException(byte code, string msg) : base(msg) { Code = code; }
    }

    /// <summary>Password-derived key of a listening side (derived once; PBKDF2 is deliberately slow).</summary>
    internal sealed class ListenerSecret
    {
        public readonly byte[] Salt, Key;
        public ListenerSecret(string password) { Salt = Util.RandomBytes(16); Key = Secure.DeriveKey(password, Salt); }
    }

    /// <summary>
    /// TLS 1.2 tunnel + mutual password proof bound to the server certificate (defeats man-in-the-middle relays).
    /// The password itself is never sent. Repeated failures from one IP get it blocked for 15 minutes.
    /// </summary>
    internal static class Secure
    {
        public const byte R_OK = 1, R_BADPASS = 2, R_BUSY = 3, R_BANNED = 4, R_ROLE = 5, R_VERSION = 6, R_FP = 7, R_DENIED = 8;
        const int Iterations = 200_000;
        const byte Version = 1;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("KDSK");
        static X509Certificate2 _cert;
        static readonly object CertGate = new object();

        public static string ResultText(byte code)
        {
            switch (code)
            {
                case R_BADPASS: return "Password salah.";
                case R_BUSY: return "Komputer tujuan sedang dikontrol orang lain.";
                case R_BANNED: return "Terlalu banyak percobaan gagal dari IP ini. Coba lagi 15 menit lagi.";
                case R_ROLE: return "Kedua komputer memakai mode yang sama (harus satu Host, satu Viewer).";
                case R_VERSION: return "Versi KilatDesk di kedua komputer berbeda / bukan KilatDesk.";
                case R_FP: return "Sertifikat komputer tujuan berubah, koneksi dibatalkan demi keamanan.";
                case R_DENIED: return "Koneksi ditolak oleh pemilik komputer tujuan (atau tidak dijawab).";
                default: return "Autentikasi gagal.";
            }
        }

        public static X509Certificate2 ServerCert
        {
            get { lock (CertGate) return _cert ?? (_cert = LoadOrCreateCert()); }
        }

        static X509Certificate2 LoadOrCreateCert()
        {
            string path = AppPaths.File("cert.pfx");
            const string pwd = "kilatdesk";
            const X509KeyStorageFlags flags = X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable;
            if (File.Exists(path))
            {
                try { return new X509Certificate2(File.ReadAllBytes(path), pwd, flags); }
                catch (Exception ex) { Log.Warn("cert.pfx rusak, membuat baru: " + ex.Message); }
            }
            using (var rsa = new RSACng(2048))
            {
                var req = new CertificateRequest("CN=KilatDesk " + Environment.MachineName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using (var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20)))
                {
                    var pfx = c.Export(X509ContentType.Pfx, pwd);
                    File.WriteAllBytes(path, pfx);
                    return new X509Certificate2(pfx, pwd, flags);
                }
            }
        }

        public static string Fingerprint(byte[] certHash) => Util.Hex(certHash.Take(10).ToArray(), ":");
        public static string MyFingerprint => Fingerprint(Sha256(ServerCert.RawData));

        public static byte[] DeriveKey(string password, byte[] salt)
        {
            using (var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes((password ?? "").Trim()), salt, Iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(32);
        }

        static byte[] Sha256(byte[] d) { using (var h = SHA256.Create()) return h.ComputeHash(d); }

        static byte[] Proof(byte[] key, string label, byte[] salt, byte[] nS, byte[] nC, byte[] certHash, byte listenerRole, byte dialerRole)
        {
            using (var h = new HMACSHA256(key))
                return h.ComputeHash(Concat(Encoding.ASCII.GetBytes(label), salt, nS, nC, certHash, new[] { listenerRole, dialerRole }));
        }

        static byte[] Concat(params byte[][] parts)
        {
            var ms = new MemoryStream();
            foreach (var p in parts) ms.Write(p, 0, p.Length);
            return ms.ToArray();
        }

        static byte[] ReadExact(Stream s, int n)
        {
            var b = new byte[n];
            for (int off = 0; off < n;)
            {
                int r = s.Read(b, off, n - off);
                if (r <= 0) throw new EndOfStreamException("koneksi ditutup saat autentikasi");
                off += r;
            }
            return b;
        }

        static byte[] Slice(byte[] b, int off, int len) { var r = new byte[len]; Buffer.BlockCopy(b, off, r, 0, len); return r; }

        public static void Tune(TcpClient c)
        {
            c.NoDelay = true;
            try
            {
                c.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                var v = new byte[12];
                BitConverter.GetBytes(1).CopyTo(v, 0); BitConverter.GetBytes(10000).CopyTo(v, 4); BitConverter.GetBytes(3000).CopyTo(v, 8);
                c.Client.IOControl(IOControlCode.KeepAliveValues, v, null);
            }
            catch { }
        }

        // ------------------------------------------------------------------ listening side
        public static Connection Accept(TcpClient tcp, byte myRole, ListenerSecret secret, Func<string, byte> admit)
        {
            var rep = (IPEndPoint)tcp.Client.RemoteEndPoint;
            var ep = new IPEndPoint(rep.Address.IsIPv4MappedToIPv6 ? rep.Address.MapToIPv4() : rep.Address, rep.Port);
            string remote = ep.ToString();
            if (Bans.IsBanned(ep.Address)) { tcp.Close(); throw new AuthException(R_BANNED, "IP diblokir: " + remote); }
            Tune(tcp);
            tcp.Client.ReceiveTimeout = 15000; tcp.Client.SendTimeout = 15000;
            var ssl = new SslStream(tcp.GetStream(), false);
            try
            {
                ssl.AuthenticateAsServer(ServerCert, false, SslProtocols.Tls12, false);
                byte[] nS = Util.RandomBytes(32);
                var hello = Concat(Magic, new[] { Version, myRole }, secret.Salt, nS);
                ssl.Write(hello, 0, hello.Length); ssl.Flush();

                byte[] r = ReadExact(ssl, 70);
                if (!r.Take(4).SequenceEqual(Magic) || r[4] != Version) { Result(ssl, R_VERSION, null); throw new AuthException(R_VERSION, "versi berbeda dari " + remote); }
                byte role = r[5];
                if (role == myRole || (role != AppRole.Host && role != AppRole.Viewer)) { Result(ssl, R_ROLE, null); throw new AuthException(R_ROLE, "mode sama dari " + remote); }
                byte[] nC = Slice(r, 6, 32), proofC = Slice(r, 38, 32);
                byte[] certHash = Sha256(ServerCert.RawData);
                if (!Util.FixedTimeEquals(Proof(secret.Key, "C", secret.Salt, nS, nC, certHash, myRole, role), proofC))
                {
                    Bans.Fail(ep.Address);
                    Thread.Sleep(1500);
                    Result(ssl, R_BADPASS, null);
                    throw new AuthException(R_BADPASS, "password salah dari " + remote);
                }
                Bans.Success(ep.Address);
                byte code = admit(ep.Address.ToString());
                if (code != R_OK) { Result(ssl, code, null); throw new AuthException(code, ResultText(code)); }
                Result(ssl, R_OK, Proof(secret.Key, "S", secret.Salt, nS, nC, certHash, myRole, role));
                return new Connection(tcp, ssl, remote);
            }
            catch
            {
                try { ssl.Dispose(); } catch { }
                tcp.Close();
                throw;
            }
        }

        static void Result(Stream s, byte code, byte[] proof)
        {
            var b = new byte[33];
            b[0] = code;
            if (proof != null) Buffer.BlockCopy(proof, 0, b, 1, 32);
            try { s.Write(b, 0, b.Length); s.Flush(); } catch { }
        }

        // ------------------------------------------------------------------ dialing side
        /// <param name="confirmChanged">Called with (oldFingerprint, newFingerprint) if the remote certificate changed.</param>
        public static Connection Dial(string host, int port, byte myRole, string password, Func<string, string, bool> confirmChanged, int timeoutMs = 10000)
        {
            IPAddress addr;
            if (!IPAddress.TryParse(host, out addr))
            {
                var all = Dns.GetHostAddresses(host);
                addr = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.First();
            }
            var tcp = new TcpClient(addr.AddressFamily);
            try
            {
                if (!tcp.ConnectAsync(addr, port).Wait(timeoutMs)) throw new TimeoutException("Tidak bisa terhubung ke " + host + ":" + port + " (timeout). Cek IP, port forwarding, dan firewall.");
            }
            catch (AggregateException ae)
            {
                tcp.Close();
                throw new IOException("Tidak bisa terhubung ke " + host + ":" + port + ": " + ae.InnerException?.Message, ae.InnerException);
            }
            catch { tcp.Close(); throw; }
            Tune(tcp);
            tcp.Client.ReceiveTimeout = 20000; tcp.Client.SendTimeout = 15000;
            var ssl = new SslStream(tcp.GetStream(), false, (s, c, ch, e) => true);
            try
            {
                ssl.AuthenticateAsClient("KilatDesk", null, SslProtocols.Tls12, false);
                byte[] certHash = Sha256(ssl.RemoteCertificate.GetRawCertData());
                string fp = Fingerprint(certHash);
                string key = IPAddress.IsLoopback(addr) ? null : "fp." + host.ToLowerInvariant() + ":" + port;
                string known = key == null ? "" : Settings.Get(key, "");
                if (known.Length > 0 && known != fp && (confirmChanged == null || !confirmChanged(known, fp)))
                    throw new AuthException(R_FP, ResultText(R_FP));

                byte[] hello = ReadExact(ssl, 54);
                if (!hello.Take(4).SequenceEqual(Magic) || hello[4] != Version) throw new AuthException(R_VERSION, ResultText(R_VERSION));
                byte theirRole = hello[5];
                if (theirRole == myRole) throw new AuthException(R_ROLE, ResultText(R_ROLE));
                byte[] salt = Slice(hello, 6, 16), nS = Slice(hello, 22, 32);
                byte[] k = DeriveKey(password, salt);
                byte[] nC = Util.RandomBytes(32);
                var msg = Concat(Magic, new[] { Version, myRole }, nC, Proof(k, "C", salt, nS, nC, certHash, theirRole, myRole));
                ssl.Write(msg, 0, msg.Length); ssl.Flush();

                tcp.Client.ReceiveTimeout = 75000; // the host owner may be asked to approve first
                byte[] res = ReadExact(ssl, 33);
                if (res[0] != R_OK) throw new AuthException(res[0], ResultText(res[0]));
                if (!Util.FixedTimeEquals(Slice(res, 1, 32), Proof(k, "S", salt, nS, nC, certHash, theirRole, myRole)))
                    throw new AuthException(R_BADPASS, "Komputer tujuan tidak bisa membuktikan password yang sama.");
                if (key != null) Settings.Set(key, fp);
                return new Connection(tcp, ssl, host + ":" + port);
            }
            catch
            {
                try { ssl.Dispose(); } catch { }
                tcp.Close();
                throw;
            }
        }
    }

    internal static class Bans
    {
        sealed class Entry { public int Fails; public DateTime First, Until; }
        static readonly Dictionary<string, Entry> Map = new Dictionary<string, Entry>();

        public static bool IsBanned(IPAddress ip)
        {
            lock (Map) return Map.TryGetValue(ip.ToString(), out var e) && e.Until > DateTime.UtcNow;
        }

        public static void Fail(IPAddress ip)
        {
            lock (Map)
            {
                var now = DateTime.UtcNow;
                if (!Map.TryGetValue(ip.ToString(), out var e)) Map[ip.ToString()] = e = new Entry { First = now };
                if (now - e.First > TimeSpan.FromMinutes(10)) { e.Fails = 0; e.First = now; }
                if (++e.Fails >= 5) { e.Until = now.AddMinutes(15); Log.Warn("IP diblokir 15 menit: " + ip); }
            }
        }

        public static void Success(IPAddress ip) { lock (Map) Map.Remove(ip.ToString()); }
    }
}
