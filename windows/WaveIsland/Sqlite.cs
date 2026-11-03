using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WaveIsland
{
    /// <summary>Lecture de petites bases SQLite avec winsqlite3.dll (fourni avec Windows).</summary>
    public sealed class Sqlite : IDisposable
    {
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll")] private static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll")] private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_column_bytes(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll")] private static extern IntPtr sqlite3_errmsg(IntPtr db);

        private IntPtr _db;
        public string Error { get; private set; }

        private Sqlite(IntPtr db) { _db = db; }

        private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

        /// <summary>Ouvre une base. copy = travaille sur une copie (base verrouillee par une autre appli).</summary>
        public static Sqlite Open(string path, bool copy)
        {
            if (copy)
            {
                string tmp = Path.Combine(Path.GetTempPath(), "WaveIsland-db", Path.GetFileName(Path.GetDirectoryName(path)) + "-" + Path.GetFileName(path));
                Directory.CreateDirectory(Path.GetDirectoryName(tmp));
                foreach (var ext in new[] { "", "-wal", "-shm", "-journal" })
                {
                    string src = path + ext, dst = tmp + ext;
                    if (!File.Exists(src)) { try { if (File.Exists(dst)) File.Delete(dst); } catch { } continue; }
                    using var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var o = new FileStream(dst, FileMode.Create, FileAccess.Write);
                    i.CopyTo(o);
                }
                path = tmp;
            }
            int rc = sqlite3_open_v2(Utf8(path), out IntPtr db, copy ? 2 : 1, IntPtr.Zero);
            if (rc != 0) { sqlite3_close(db); return null; }
            sqlite3_busy_timeout(db, 500);
            return new Sqlite(db);
        }

        public sealed class Row
        {
            internal IntPtr St;
            public long Long(int col) => sqlite3_column_int64(St, col);
            public string Text(int col)
            {
                int n = sqlite3_column_bytes(St, col);
                if (n <= 0) return "";
                var b = new byte[n];
                Marshal.Copy(sqlite3_column_blob(St, col), b, 0, n);
                return Encoding.UTF8.GetString(b).TrimEnd('\0');
            }
        }

        /// <summary>Execute une requete ; false si elle est invalide (Error contient la raison).</summary>
        public bool Query(string sql, Action<Row> each)
        {
            if (sqlite3_prepare_v2(_db, Utf8(sql), -1, out IntPtr st, IntPtr.Zero) != 0)
            {
                Error = Marshal.PtrToStringUTF8(sqlite3_errmsg(_db));
                return false;
            }
            try
            {
                var row = new Row { St = st };
                while (sqlite3_step(st) == 100) each(row);
                return true;
            }
            finally { sqlite3_finalize(st); }
        }

        public static string Quote(string s) => "'" + (s ?? "").Replace("'", "''") + "'";

        public void Dispose()
        {
            if (_db != IntPtr.Zero) sqlite3_close(_db);
            _db = IntPtr.Zero;
        }
    }
}
