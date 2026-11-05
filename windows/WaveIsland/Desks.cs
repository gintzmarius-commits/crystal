using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WaveIsland
{
    /// <summary>
    /// Bureaux virtuels de Windows : liste (lue dans le registre), changer de bureau, en creer, en fermer,
    /// et deplacer une appli vers un autre bureau. Windows ne laisse pas les autres applis deplacer une fenetre
    /// directement : on la cache, on va sur le bureau voulu et on la remontre (elle y apparait).
    /// </summary>
    public static class VDesk
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

        public static List<(Guid id, string name)> List()
        {
            var list = new List<(Guid, string)>();
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                if (k?.GetValue("VirtualDesktopIDs") is byte[] ids)
                    for (int i = 0; i + 16 <= ids.Length; i += 16)
                    {
                        var g = new Guid(new ReadOnlySpan<byte>(ids, i, 16));
                        string name = null;
                        try { using var d = Registry.CurrentUser.OpenSubKey(Key + @"\Desktops\{" + g + "}"); name = d?.GetValue("Name") as string; } catch { }
                        list.Add((g, string.IsNullOrWhiteSpace(name) ? "Bureau " + (list.Count + 1) : name));
                    }
            }
            catch { }
            if (list.Count == 0) list.Add((Guid.Empty, "Bureau 1"));
            return list;
        }

        public static int Current()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(Key);
                if (k?.GetValue("CurrentVirtualDesktop") is byte[] cur && cur.Length == 16)
                {
                    var g = new Guid(cur);
                    var l = List();
                    for (int i = 0; i < l.Count; i++) if (l[i].id == g) return i;
                }
            }
            catch { }
            return 0;
        }

        private static void Keys(params byte[] vks)
        {
            foreach (var v in vks) Native.keybd_event(v, 0, 0x1 /* EXTENDED */, UIntPtr.Zero);
            for (int i = vks.Length - 1; i >= 0; i--) Native.keybd_event(vks[i], 0, 0x1 | Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private const byte Ctrl = 0x11, Win = 0x5B, Left = 0x25, Right = 0x27, D = 0x44, F4 = 0x73;

        /// <summary>Va sur le bureau n (Ctrl + Win + fleches, un pas a la fois).</summary>
        public static async Task Switch(int to)
        {
            int cur = Current();
            int steps = to - cur;
            for (int i = 0; i < Math.Abs(steps); i++)
            {
                Keys(Ctrl, Win, steps > 0 ? Right : Left);
                await Task.Delay(140);
            }
            Log.I("Bureaux : bureau " + (to + 1));
        }

        /// <summary>Nouveau bureau (Windows y va directement).</summary>
        public static void New() { Keys(Ctrl, Win, D); Log.I("Bureaux : nouveau bureau"); }

        /// <summary>Ferme le bureau n (ses applis passent sur le bureau voisin).</summary>
        public static async Task Close(int n)
        {
            int back = Current();
            await Switch(n);
            await Task.Delay(150);
            Keys(Ctrl, Win, F4);
            Log.I("Bureaux : bureau " + (n + 1) + " ferme");
            await Task.Delay(250);
            if (back != n) await Switch(back > n ? back - 1 : back);
        }

        /// <summary>Deplace la fenetre vers le bureau n (on y va avec elle).</summary>
        public static async Task Move(IntPtr h, int n)
        {
            if (n == Current()) return;
            Native.ShowWindow(h, 0 /* SW_HIDE */);
            await Switch(n);
            await Task.Delay(250);
            Native.ShowWindow(h, 5 /* SW_SHOW */);
            Native.SetForegroundWindow(h);
            Log.I("Bureaux : appli deplacee vers le bureau " + (n + 1));
        }
    }
}
