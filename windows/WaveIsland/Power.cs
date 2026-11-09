using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WaveIsland
{
    /// <summary>
    /// Ecran eteint, session verrouillee ou PC en veille : plus rien n'est visible, donc tous les minuteurs
    /// enregistres ici sont mis en pause (copies du fond, horloge, sondages...) puis relances au retour.
    /// </summary>
    public static class Power
    {
        public static bool Asleep { get; private set; }
        /// <summary>Retour de l'ecran (pour rafraichir tout de suite ce qui a pu changer).</summary>
        public static event Action Woke;

        private static readonly List<DispatcherTimer> _timers = new List<DispatcherTimer>();
        private static readonly List<DispatcherTimer> _paused = new List<DispatcherTimer>();
        private static bool _displayOff, _locked, _suspended;
        private static HwndSource _sink;
        private static IntPtr _notify;

        /// <summary>Ce minuteur s'arrete quand l'ecran s'eteint et repart quand il se rallume.</summary>
        public static void Register(DispatcherTimer t)
        {
            if (t == null || _timers.Contains(t)) return;
            _timers.Add(t);
            if (Asleep && t.IsEnabled) { t.Stop(); _paused.Add(t); }
        }

        /// <summary>Fenetre fermee : on oublie son minuteur (sinon il resterait garde en memoire).</summary>
        public static void Unregister(DispatcherTimer t)
        {
            if (t == null) return;
            _timers.Remove(t);
            _paused.Remove(t);
        }

        [DllImport("user32.dll")] private static extern IntPtr RegisterPowerSettingNotification(IntPtr h, ref Guid g, int flags);
        private static Guid GUID_CONSOLE_DISPLAY_STATE = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");

        public static void Start()
        {
            try
            {
                _sink = new HwndSource(new HwndSourceParameters("WavePower") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
                _sink.AddHook(WndProc);
                _notify = RegisterPowerSettingNotification(_sink.Handle, ref GUID_CONSOLE_DISPLAY_STATE, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
            }
            catch (Exception e) { Log.I("Ecran eteint : surveillance impossible (" + e.Message + ")"); }
            Microsoft.Win32.SystemEvents.SessionSwitch += (a, e) =>
            {
                if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock || e.Reason == Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect) Set(ref _locked, true);
                if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock || e.Reason == Microsoft.Win32.SessionSwitchReason.ConsoleConnect) Set(ref _locked, false);
            };
            Microsoft.Win32.SystemEvents.PowerModeChanged += (a, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Suspend) Set(ref _suspended, true);
                if (e.Mode == Microsoft.Win32.PowerModes.Resume) Set(ref _suspended, false);
            };
        }

        private static IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            // WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE : ecran allume (1), eteint (0) ou assombri (2)
            if (msg == 0x0218 && w.ToInt32() == 0x8013 && l != IntPtr.Zero)
            {
                var g = Marshal.PtrToStructure<Guid>(l);
                if (g == GUID_CONSOLE_DISPLAY_STATE)
                {
                    int state = Marshal.ReadInt32(l, 20);
                    Set(ref _displayOff, state == 0);
                }
            }
            return IntPtr.Zero;
        }

        private static void Set(ref bool flag, bool value)
        {
            flag = value;
            bool asleep = _displayOff || _locked || _suspended;
            var ui = System.Windows.Application.Current?.Dispatcher;
            if (ui == null) return;
            ui.BeginInvoke(new Action(() => Apply(asleep)));
        }

        private static void Apply(bool asleep)
        {
            if (asleep == Asleep) return;
            Asleep = asleep;
            if (asleep)
            {
                foreach (var t in _timers) if (t.IsEnabled) { t.Stop(); _paused.Add(t); }
                Log.I($"Ecran eteint ou session verrouillee : {_paused.Count} minuteurs en pause");
            }
            else
            {
                foreach (var t in _paused) t.Start();
                Log.I($"Ecran de retour : {_paused.Count} minuteurs relances");
                _paused.Clear();
                try { Woke?.Invoke(); } catch (Exception e) { Log.I("Retour de l'ecran : " + e.Message); }
            }
        }
    }

    /// <summary>
    /// Cadence adaptative de la copie du fond derriere le verre : rapide quand le fond bouge (video, defilement),
    /// de plus en plus lente quand il reste identique (bureau immobile). Un changement ou une activite la remet a vitesse rapide.
    /// </summary>
    public sealed class Pacer
    {
        private readonly DispatcherTimer _t;
        private readonly double _fastMs, _maxMs;
        private int _still;
        private bool _held;

        public Pacer(DispatcherTimer t, double fastMs, double maxMs = 250)
        {
            _t = t;
            _fastMs = fastMs;
            _maxMs = maxMs;
        }

        /// <summary>Animation en cours : cadence imposee par l'animation (le rythme adaptatif est suspendu).</summary>
        public void Hold(TimeSpan interval)
        {
            _held = true;
            if (_t.Interval != interval) _t.Interval = interval;
        }

        /// <summary>Fin de l'animation : retour au rythme adaptatif (rapide d'abord).</summary>
        public void Release() { _held = false; Wake(); }

        /// <summary>Activite (souris qui bouge, fenetre au premier plan qui change...) : cadence rapide.</summary>
        public void Wake()
        {
            _still = 0;
            if (_held) return;
            var f = TimeSpan.FromMilliseconds(_fastMs);
            if (_t.Interval != f) _t.Interval = f;
        }

        /// <summary>Resultat de la derniere copie : le fond a-t-il change ?</summary>
        public void Result(bool changed)
        {
            if (_held) return;
            if (changed) { Wake(); return; }
            _still++;
            // ~0,6 s immobile : 2x plus lent ; ~1,5 s : 4x ; ensuite le maximum
            double ms = _still < 10 ? _fastMs : _still < 18 ? _fastMs * 2 : _still < 26 ? _fastMs * 4 : _maxMs;
            ms = Math.Min(_maxMs, ms);
            var iv = TimeSpan.FromMilliseconds(ms);
            if (_t.Interval != iv) _t.Interval = iv;
        }
    }
}
