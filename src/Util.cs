using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KilatDesk
{
    internal static class AppPaths
    {
        public static string Dir { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KilatDesk");
        static AppPaths() { try { Directory.CreateDirectory(Dir); } catch { } }
        public static string File(string name) => Path.Combine(Dir, name);

        /// <summary>Test modes use a throwaway folder so they never touch the real settings.</summary>
        public static void Redirect(string dir) { Dir = dir; Directory.CreateDirectory(dir); }
    }

    internal static class Log
    {
        static readonly object Gate = new object();
        static string LogPath => AppPaths.File("log.txt");

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg, Exception ex = null) => Write("ERR ", ex == null ? msg : msg + ": " + ex);

        static void Write(string level, string msg)
        {
            lock (Gate)
            {
                try
                {
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024) fi.Delete();
                    System.IO.File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + msg + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    /// <summary>Tiny key=value settings store in %APPDATA%\KilatDesk\settings.txt.</summary>
    internal static class Settings
    {
        static readonly object Gate = new object();
        static Dictionary<string, string> _map;
        static string FilePath => AppPaths.File("settings.txt");

        static Dictionary<string, string> Map
        {
            get
            {
                if (_map != null) return _map;
                _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (System.IO.File.Exists(FilePath))
                        foreach (var line in System.IO.File.ReadAllLines(FilePath, Encoding.UTF8))
                        {
                            int i = line.IndexOf('=');
                            if (i > 0) _map[line.Substring(0, i)] = Unescape(line.Substring(i + 1));
                        }
                }
                catch (Exception ex) { Log.Error("load settings", ex); }
                return _map;
            }
        }

        public static string Get(string key, string def = "")
        {
            lock (Gate) return Map.TryGetValue(key, out var v) ? v : def;
        }

        public static int GetInt(string key, int def)
        {
            return int.TryParse(Get(key, ""), out int v) ? v : def;
        }

        public static void Set(string key, string value)
        {
            lock (Gate)
            {
                Map[key] = value ?? "";
                try
                {
                    var sb = new StringBuilder();
                    foreach (var kv in Map) sb.Append(kv.Key).Append('=').Append(Escape(kv.Value)).Append("\r\n");
                    System.IO.File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
                }
                catch (Exception ex) { Log.Error("save settings", ex); }
            }
        }

        /// <summary>Secret values are encrypted with Windows DPAPI (only this Windows user can read them).</summary>
        public static string GetSecret(string key)
        {
            string b64 = Get(key, "");
            if (b64.Length == 0) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }

        public static void SetSecret(string key, string value)
        {
            Set(key, Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value ?? ""), null, DataProtectionScope.CurrentUser)));
        }

        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
        static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char c = s[++i];
                    sb.Append(c == 'n' ? '\n' : c == 'r' ? '\r' : c);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }

    internal static class Util
    {
        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        public static byte[] RandomBytes(int n) { var b = new byte[n]; lock (Rng) Rng.GetBytes(b); return b; }

        /// <summary>Readable random password, e.g. "k7Qm-2Rpx-9vLt" (~70 bits).</summary>
        public static string NewPassword()
        {
            const string chars = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var b = RandomBytes(12);
            var sb = new StringBuilder();
            for (int i = 0; i < 12; i++)
            {
                if (i > 0 && i % 4 == 0) sb.Append('-');
                sb.Append(chars[b[i] % chars.Length]);
            }
            return sb.ToString();
        }

        public static string Hex(byte[] b, string sep = "")
        {
            var sb = new StringBuilder();
            for (int i = 0; i < b.Length; i++) { if (i > 0) sb.Append(sep); sb.Append(b[i].ToString("X2")); }
            return sb.ToString();
        }

        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        /// <summary>Parses "host:port" (or "host" with a default port).</summary>
        public static bool TryParseEndpoint(string s, int defPort, out string host, out int port)
        {
            host = null; port = defPort;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            int colon = s.LastIndexOf(':');
            if (colon > 0 && s.IndexOf(':') == colon)
            {
                if (!int.TryParse(s.Substring(colon + 1), out port) || port < 1 || port > 65535) return false;
                host = s.Substring(0, colon);
            }
            else host = s.Trim('[', ']');
            return host.Length > 0;
        }
    }
}
