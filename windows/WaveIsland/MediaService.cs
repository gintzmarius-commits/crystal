using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace WaveIsland
{
    /// <summary>Ce qui est en train de jouer (Spotify, YouTube dans le navigateur, Deezer, VLC...).</summary>
    public class MediaInfo
    {
        public string Title = "", Artist = "", App = "";
        public ImageSource Art;
        public bool Playing, CanPrev = true, CanNext = true;
        public TimeSpan Position, Duration;
        public DateTime PositionAt = DateTime.Now;

        /// <summary>Position estimee maintenant (elle avance pendant la lecture).</summary>
        public TimeSpan Now
        {
            get
            {
                var p = Position;
                if (Playing) p += DateTime.Now - PositionAt;
                if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
                return p < TimeSpan.Zero ? TimeSpan.Zero : p;
            }
        }
    }

    /// <summary>
    /// Lit les commandes multimedia de Windows (les memes que celles du panneau de son de Windows) :
    /// titre, artiste, pochette, position, et permet precedent / lecture-pause / suivant.
    /// </summary>
    public sealed class MediaService
    {
        public event Action<MediaInfo> Changed;
        public MediaInfo Current { get; private set; }

        private readonly Dispatcher _ui;
        private GlobalSystemMediaTransportControlsSessionManager _mgr;
        private GlobalSystemMediaTransportControlsSession _session;
        private DispatcherTimer _poll;
        private string _artKey;
        private ImageSource _art;
        private DateTime _pausedSince = DateTime.MinValue;
        private bool _busy;
        /// <summary>Tests automatiques : un faux morceau.</summary>
        private MediaInfo _fake;
        /// <summary>Musique simulee (tests) : relue tout de suite quand elle change.</summary>
        public MediaInfo Fake { get => _fake; set { _fake = value; Soon(); } }

        public MediaService(Dispatcher ui) { _ui = ui; }

        private DispatcherTimer _soon;
        /// <summary>Sources suivies (Spotify, navigateur...) avec leurs abonnements, pour pouvoir se desabonner proprement.</summary>
        private readonly System.Collections.Generic.List<GlobalSystemMediaTransportControlsSession> _watched =
            new System.Collections.Generic.List<GlobalSystemMediaTransportControlsSession>();
        private volatile bool _rewatch = true;

        /// <summary>
        /// Windows previent quand un morceau change, demarre ou se met en pause : on relit a ce moment-la
        /// (avant : 1,4 fois par seconde en permanence, meme sans musique). Une verification lente reste par securite.
        /// </summary>
        public async void Start()
        {
            _soon = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMilliseconds(150) };
            _soon.Tick += async (a, b) => { _soon.Stop(); Perf.Hit("media-evenement"); await Refresh(); };
            try
            {
                _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _mgr.SessionsChanged += (a, b) => { _rewatch = true; Soon(); };
                _mgr.CurrentSessionChanged += (a, b) => Soon();
                Log.I("Musique : commandes multimedia de Windows disponibles");
            }
            catch (Exception e) { Log.I("Musique : indisponible (" + e.Message + ")"); }
            _poll = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, async (a, b) => { Perf.Hit("media-sondage"); await Refresh(); }, _ui);
            Power.Register(_poll);
            Soon();
        }

        private void OnMediaProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => Soon();
        private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Soon();
        private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => Soon();

        /// <summary>Relecture dans 150 ms (une seule pour une rafale d'evenements).</summary>
        private void Soon() => _ui.BeginInvoke(new Action(() => { if (_soon != null && !_soon.IsEnabled) _soon.Start(); }));

        /// <summary>S'abonne aux changements de chaque source (Spotify, navigateur...).</summary>
        private void WatchSessions()
        {
            if (_mgr == null) return;
            try
            {
                if (!_rewatch) return;   // les sources n'ont pas change depuis le dernier abonnement
                _rewatch = false;
                // Une seule serie d'abonnements a la fois : on se desabonne des anciennes sources avant
                foreach (var old in _watched)
                {
                    try
                    {
                        old.MediaPropertiesChanged -= OnMediaProps;
                        old.PlaybackInfoChanged -= OnPlayback;
                        old.TimelinePropertiesChanged -= OnTimeline;
                    }
                    catch { }
                }
                _watched.Clear();
                foreach (var s in _mgr.GetSessions())
                {
                    s.MediaPropertiesChanged += OnMediaProps;
                    s.PlaybackInfoChanged += OnPlayback;
                    s.TimelinePropertiesChanged += OnTimeline;
                    _watched.Add(s);
                }
            }
            catch { }
        }

        private GlobalSystemMediaTransportControlsSession Pick()
        {
            if (_mgr == null) return null;
            try
            {
                var all = _mgr.GetSessions();
                // On prefere une source qui joue vraiment
                var playing = all.FirstOrDefault(s =>
                {
                    try { return s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
                    catch { return false; }
                });
                return playing ?? _mgr.GetCurrentSession();
            }
            catch { return null; }
        }

        private async Task Refresh()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                MediaInfo info = null;
                if (Fake != null) info = Fake;
                else if (Settings.Current.LiveMedia)
                {
                    var s = Pick();
                    _session = s;
                    if (s != null)
                    {
                        var pb = s.GetPlaybackInfo();
                        var props = await s.TryGetMediaPropertiesAsync();
                        var tl = s.GetTimelineProperties();
                        if (props != null && !string.IsNullOrWhiteSpace(props.Title))
                        {
                            bool playing = pb?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                            if (playing) _pausedSince = DateTime.MinValue;
                            else if (_pausedSince == DateTime.MinValue) _pausedSince = DateTime.Now;
                            // En pause depuis longtemps : on n'encombre plus l'ilot
                            if (playing || (DateTime.Now - _pausedSince).TotalSeconds < 25)
                            {
                                info = new MediaInfo
                                {
                                    Title = props.Title,
                                    Artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumArtist ?? "" : props.Artist,
                                    App = NotificationWatcher.AppName(s.SourceAppUserModelId),
                                    Playing = playing,
                                    CanPrev = pb?.Controls?.IsPreviousEnabled ?? true,
                                    CanNext = pb?.Controls?.IsNextEnabled ?? true,
                                };
                                if (tl != null)
                                {
                                    info.Duration = tl.EndTime - tl.StartTime;
                                    info.Position = tl.Position - tl.StartTime;
                                    info.PositionAt = tl.LastUpdatedTime.LocalDateTime;
                                    if (info.PositionAt > DateTime.Now || info.PositionAt.Year < 2000) info.PositionAt = DateTime.Now;
                                }
                                string key = props.Title + "|" + props.Artist + "|" + s.SourceAppUserModelId;
                                if (key != _artKey)
                                {
                                    _artKey = key;
                                    _art = await LoadArt(props.Thumbnail);
                                }
                                info.Art = _art;
                            }
                        }
                    }
                }
                if (!Same(info, Current))
                {
                    Current = info;
                    Changed?.Invoke(info);
                }
                else Current = info;
                WatchSessions();
                // Verification de securite : plus frequente quand quelque chose joue
                var iv = TimeSpan.FromSeconds(info == null ? 10 : info.Playing ? 3 : 5);
                if (_poll != null && _poll.Interval != iv) _poll.Interval = iv;
            }
            catch (Exception e) { Log.I("Musique : " + e.Message); }
            finally { _busy = false; }
        }

        private static bool Same(MediaInfo a, MediaInfo b)
        {
            if (a == null || b == null) return a == b;
            return a.Title == b.Title && a.Artist == b.Artist && a.Playing == b.Playing && a.Art == b.Art
                   && Math.Abs((a.Position - b.Position).TotalSeconds) < 1.5 && a.Duration == b.Duration;
        }

        private static async Task<ImageSource> LoadArt(Windows.Storage.Streams.IRandomAccessStreamReference r)
        {
            if (r == null) return null;
            try
            {
                using var ras = await r.OpenReadAsync();
                using var s = ras.AsStreamForRead();
                var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                ms.Position = 0;
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.StreamSource = ms;
                b.DecodePixelWidth = 120;
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch { return null; }
        }

        public async void PlayPause()
        {
            if (Fake != null) { Fake.Playing = !Fake.Playing; Soon(); return; }
            try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
            await Refresh();
        }

        public async void Next()
        {
            try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
            await Refresh();
        }

        public async void Previous()
        {
            try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
            await Refresh();
        }

        public async void Seek(TimeSpan pos)
        {
            try { if (_session != null) await _session.TryChangePlaybackPositionAsync(pos.Ticks); } catch { }
            await Refresh();
        }
    }
}
