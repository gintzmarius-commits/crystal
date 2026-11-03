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

        /// <summary>Molette sur l'ilot : +1 cran vers le haut, -1 vers le bas.</summary>
        public void Wheel(int delta) => Step(delta > 0 ? Native.VK_VOLUME_UP : Native.VK_VOLUME_DOWN);

        public void ToggleMute() => Step(Native.VK_VOLUME_MUTE);

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            try { if (_device != null) _device.AudioEndpointVolume.OnVolumeNotification -= OnNotification; } catch { }
        }
    }
}
