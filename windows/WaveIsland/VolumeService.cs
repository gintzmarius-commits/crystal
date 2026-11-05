using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace WaveIsland
{
    /// <summary>
    /// Touches de volume du clavier : interceptees (le panneau de Windows en bas n'apparait plus),
    /// le son est regle ici et le niveau s'affiche dans l'ilot.
    /// </summary>
    public sealed class VolumeService : IDisposable
    {
        public event Action<float, bool> Changed;
        /// <summary>Touche Windows appuyee seule (le menu Demarrer est remplace par celui de l'ilot).</summary>
        public event Action WinTapped;
        /// <summary>Dit si la touche Windows doit ouvrir le menu de l'ilot en ce moment.</summary>
        public Func<bool> WinAllowed;
        private bool _winDown, _winOther;
        /// <summary>Touche Copilot ou Microsoft 365 : l'assistant IA de l'ilot.</summary>
        public event Action AssistKey;
        /// <summary>Lot 2 : Alt + Espace (recherche) et Impr. ecran (capture).</summary>
        public event Action SearchKey, ShotKey;
        /// <summary>Win + Tab (et le glissement a 3 doigts vers le haut) : notre vue des applis.</summary>
        public event Action TaskViewKey;
        private bool _hyper, _assistCombo, _hyperUsed;
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
        private static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        private IntPtr _hook;
        private Native.HookProc _proc; // garde une reference (sinon le ramasse-miettes la libere)
        private MMDeviceEnumerator _enumerator;
        private MMDevice _device;
        private string _deviceId;
        private readonly Dispatcher _ui;
        private DateTime _lastOwnChange = DateTime.MinValue;

        public VolumeService(Dispatcher ui) { _ui = ui; }

        public void Start()
        {
            _proc = Hook;
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
            Log.I(_hook == IntPtr.Zero ? "Touches de volume : interception impossible" : "Touches de volume interceptees");
            Attach();
        }

        /// <summary>Se branche sur la sortie audio par defaut (et la rebranche si elle change).</summary>
        /// <summary>Niveau du son qui sort en ce moment (0..1), pour faire pulser l'ilot avec la musique.</summary>
        public float Peak()
        {
            try { Endpoint(); return _device?.AudioMeterInformation?.MasterPeakValue ?? 0; }
            catch { return 0; }
        }

        private AudioEndpointVolume Endpoint()
        {
            try
            {
                _enumerator ??= new MMDeviceEnumerator();
                var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (dev.ID != _deviceId)
                {
                    if (_device != null) _device.AudioEndpointVolume.OnVolumeNotification -= OnNotification;
                    _device = dev;
                    _deviceId = dev.ID;
                    _device.AudioEndpointVolume.OnVolumeNotification += OnNotification;
                }
                return _device.AudioEndpointVolume;
            }
            catch (Exception e)
            {
                Log.I("Aucune sortie audio : " + e.Message);
                return null;
            }
        }

        private void Attach() => Endpoint();

        /// <summary>Le volume a change ailleurs (barre des taches, appli...) : on l'affiche aussi.</summary>
        private void OnNotification(AudioVolumeNotificationData d)
        {
            if ((DateTime.Now - _lastOwnChange).TotalMilliseconds < 400) return;
            _ui.BeginInvoke(new Action(() => Changed?.Invoke(d.MasterVolume, d.Muted)));
        }

        private IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                int m = wParam.ToInt32();
                bool down = m == Native.WM_KEYDOWN || m == Native.WM_SYSKEYDOWN;
                bool injected = (k.flags & Native.LLKHF_INJECTED) != 0 && !Program.Demo;
                if (!injected && k.vkCode == 0x09 /* Tab */ && _winDown && Settings.Current.OwnTaskView)
                {
                    if (down)
                    {
                        _winOther = true;
                        // Touche neutre : Windows ne verra pas « Win seul » au relachement (pas de menu Demarrer)
                        Native.keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                        Native.keybd_event(0xE8, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                        _ui.BeginInvoke(new Action(() => TaskViewKey?.Invoke()));
                    }
                    return new IntPtr(1);
                }
                if (!injected && down && k.vkCode == 0x20 /* Espace */ && Settings.Current.SearchKey && Held(0x12) && !Held(0x11) && !_winDown)
                {
                    // Alt + Espace : la recherche (une touche neutre evite que l'appli ouvre son menu au relachement d'Alt)
                    Native.keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                    Native.keybd_event(0xE8, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    _ui.BeginInvoke(new Action(() => SearchKey?.Invoke()));
                    return new IntPtr(1);
                }
                if (!injected && k.vkCode == 0x2C /* Impr. ecran */ && Settings.Current.ShotKey && !Held(0x12))
                {
                    if (down) _ui.BeginInvoke(new Action(() => ShotKey?.Invoke()));
                    return new IntPtr(1);
                }
                if (!injected && k.vkCode != 0xE8 && Settings.Current.AiKeyEnabled)
                {
                    // Touche Copilot = Win + Maj + F23 : on avale F23 (Copilot ne s'ouvre pas) et l'assistant demarre
                    if (k.vkCode == 0x86 /* VK_F23 */)
                    {
                        if (down && !_assistCombo) { _assistCombo = true; _ui.BeginInvoke(new Action(() => AssistKey?.Invoke())); }
                        if (!down) _assistCombo = false;
                        if (_winDown) _winOther = true;
                        _hyperUsed = true;
                        return new IntPtr(1);
                    }
                    // Touche Microsoft 365 / Office = Ctrl + Maj + Alt + Win appuyes ensemble
                    if ((k.vkCode == Native.VK_LWIN || k.vkCode == Native.VK_RWIN) && down && !_winDown && Held(0x11) && Held(0x10) && Held(0x12))
                    { _hyper = true; _hyperUsed = false; }
                    else if (_hyper && down && k.vkCode != Native.VK_LWIN && k.vkCode != Native.VK_RWIN && k.vkCode != 0x10 && k.vkCode != 0x11 && k.vkCode != 0x12
                             && k.vkCode != 0xA0 && k.vkCode != 0xA1 && k.vkCode != 0xA2 && k.vkCode != 0xA3 && k.vkCode != 0xA4 && k.vkCode != 0xA5)
                        _hyperUsed = true;   // Office + W, Office + T... : raccourcis de Windows, on ne touche a rien
                    if (_hyper && !down && (k.vkCode == Native.VK_LWIN || k.vkCode == Native.VK_RWIN))
                    {
                        _hyper = false;
                        _winDown = false;
                        if (!_hyperUsed)
                        {
                            // Relachee seule : l'assistant au lieu de l'appli Microsoft 365
                            Native.keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                            Native.keybd_event(0xE8, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                            Native.keybd_event((byte)k.vkCode, 0, Native.KEYEVENTF_KEYUP | 0x1, UIntPtr.Zero);
                            _ui.BeginInvoke(new Action(() => AssistKey?.Invoke()));
                            return new IntPtr(1);
                        }
                    }
                }
                if (!injected && k.vkCode != 0xE8)
                {
                    if (k.vkCode == Native.VK_LWIN || k.vkCode == Native.VK_RWIN)
                    {
                        if (down)
                        {
                            if (!_winDown) { _winDown = true; _winOther = false; }
                        }
                        else
                        {
                            bool alone = _winDown && !_winOther;
                            _winDown = false;
                            if (alone && Settings.Current.WinKeyMenu && (WinAllowed?.Invoke() ?? false))
                            {
                                // Windows ouvre le menu Demarrer si la touche est relachee seule : on glisse une
                                // touche neutre avant le relachement, puis on ouvre le menu de l'ilot a la place.
                                Native.keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                                Native.keybd_event(0xE8, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                                Native.keybd_event((byte)k.vkCode, 0, Native.KEYEVENTF_KEYUP | 0x1, UIntPtr.Zero);
                                _ui.BeginInvoke(new Action(() => WinTapped?.Invoke()));
                                return new IntPtr(1);
                            }
                        }
                    }
                    else if (down && _winDown) _winOther = true; // Win + E, Win + D... : comportement normal
                }
            }
            if (code >= 0 && Settings.Current.VolumeInIsland)
            {
                var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                if (k.vkCode == Native.VK_VOLUME_UP || k.vkCode == Native.VK_VOLUME_DOWN || k.vkCode == Native.VK_VOLUME_MUTE)
                {
                    int msg = wParam.ToInt32();
                    if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
                    {
                        int vk = k.vkCode;
                        _ui.BeginInvoke(new Action(() => Step(vk)));
                    }
                    return new IntPtr(1); // avale la touche : pas de panneau Windows
                }
            }
            return Native.CallNextHookEx(_hook, code, wParam, lParam);
        }

        /// <summary>Monte (+1), baisse (-1) ou coupe (0) le son.</summary>
        public void Step(int vk)
        {
            var ep = Endpoint();
            if (ep == null) return;
            try
            {
                _lastOwnChange = DateTime.Now;
                if (vk == Native.VK_VOLUME_MUTE) ep.Mute = !ep.Mute;
                else if (vk == Native.VK_VOLUME_UP) { if (ep.Mute) ep.Mute = false; ep.VolumeStepUp(); }
                else ep.VolumeStepDown();
                Log.I($"Volume : {Math.Round(ep.MasterVolumeLevelScalar * 100)} %{(ep.Mute ? " (coupe)" : "")}");
                Changed?.Invoke(ep.MasterVolumeLevelScalar, ep.Mute);
            }
            catch (Exception e) { Log.I("Volume : " + e.Message); }
        }

        /// <summary>Regle le volume directement (glisser sur l'ilot), 0..1.</summary>
        public void SetLevel(float f)
        {
            var ep = Endpoint();
            if (ep == null) return;
            try
            {
                _lastOwnChange = DateTime.Now;
                if (f > 0.001f && ep.Mute) ep.Mute = false;
                ep.MasterVolumeLevelScalar = Math.Max(0f, Math.Min(1f, f));
                Changed?.Invoke(ep.MasterVolumeLevelScalar, ep.Mute);
            }
            catch (Exception e) { Log.I("Volume : " + e.Message); }
        }

        /// <summary>Molette sur l'ilot : +1 cran vers le haut, -1 vers le bas.</summary>
        public void Wheel(int delta) => Step(delta > 0 ? Native.VK_VOLUME_UP : Native.VK_VOLUME_DOWN);

        public void ToggleMute() => Step(Native.VK_VOLUME_MUTE);

        /// <summary>Volume actuel (0..1) et son coupe.</summary>
        public (float level, bool muted) Read()
        {
            try { var ep = Endpoint(); if (ep != null) return (ep.MasterVolumeLevelScalar, ep.Mute); } catch { }
            return (0.5f, false);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            try { if (_device != null) _device.AudioEndpointVolume.OnVolumeNotification -= OnNotification; } catch { }
        }
    }
}
