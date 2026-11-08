using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DotaGlass
{
    /// <summary>
    /// Wraps Windows' Global System Media Transport Controls (the same source as the volume flyout's
    /// media card). Works with Spotify, browsers (YouTube, SoundCloud...), Apple Music, Media Player, etc.
    /// Fully event-driven: WinRT events are coalesced into one UI-thread refresh, nothing is polled.
    /// </summary>
    internal sealed class MediaController
    {
        const int DirtyProps = 1, DirtyPlayback = 2, DirtyTimeline = 4, DirtyAll = 7;

        readonly Dispatcher ui;
        readonly object gate = new object();
        GlobalSystemMediaTransportControlsSessionManager manager;
        GlobalSystemMediaTransportControlsSession session;
        int dirty;
        bool queued;

        readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> onProps;
        readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> onPlayback;
        readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> onTimeline;

        // ---- current state (UI thread) ----
        public string AppId = "", Title = "", Artist = "", Album = "";
        public bool IsPlaying, CanPrev, CanNext, CanPlayPause, CanSeek;
        public TimeSpan Duration;
        public int SessionVersion, ArtVersion;
        TimeSpan posAtStamp;
        DateTime stampUtc = DateTime.UtcNow;
        double rate = 1.0;
        string trackKey = "";
        IRandomAccessStreamReference thumb;

        /// <summary>Raised on the UI thread after state changed.</summary>
        public event Action Changed;

        public bool HasSession { get { return session != null; } }

        public MediaController(Dispatcher ui)
        {
            this.ui = ui;
            onProps = (s, e) => Queue(DirtyProps);
            onPlayback = (s, e) => Queue(DirtyPlayback);
            onTimeline = (s, e) => Queue(DirtyTimeline);
        }

        public async void Start()
        {
            try { manager = await WinRt.Await(GlobalSystemMediaTransportControlsSessionManager.RequestAsync()); }
            catch { return; }
            manager.CurrentSessionChanged += (s, e) => Queue(0);
            manager.SessionsChanged += (s, e) => Queue(0);
            Queue(DirtyAll);
        }

        /// <summary>Estimated live position (apps only report the timeline occasionally).</summary>
        public TimeSpan Position
        {
            get
            {
                var p = posAtStamp;
                if (IsPlaying) p += TimeSpan.FromTicks((long)((DateTime.UtcNow - stampUtc).Ticks * rate));
                if (p < TimeSpan.Zero) p = TimeSpan.Zero;
                if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
                return p;
            }
        }

        public string AppName { get { return FriendlyAppName(AppId); } }

        void Queue(int flags)
        {
            lock (gate)
            {
                dirty |= flags;
                if (queued) return;
                queued = true;
            }
            ui.BeginInvoke(DispatcherPriority.Background, new Action(Refresh));
        }

        async void Refresh()
        {
            int flags;
            lock (gate) { queued = false; flags = dirty; dirty = 0; }
            if (manager == null) return;

            GlobalSystemMediaTransportControlsSession cur = null;
            try { cur = PickSession(); } catch { }

            if (!ReferenceEquals(cur, session))
            {
                Detach(session);
                session = cur;
                Attach(cur);
                SessionVersion++;
                flags = DirtyAll;
                AppId = cur == null ? "" : SafeAppId(cur);
            }

            var s = session;
            if (s == null)
            {
                Title = Artist = Album = "";
                IsPlaying = CanPrev = CanNext = CanPlayPause = CanSeek = false;
                Duration = posAtStamp = TimeSpan.Zero;
                if (thumb != null || trackKey != "") { thumb = null; trackKey = ""; ArtVersion++; }
                Raise();
                return;
            }

            try
            {
                if ((flags & DirtyPlayback) != 0) ReadPlayback(s);
                if ((flags & DirtyTimeline) != 0) ReadTimeline(s);
                if ((flags & DirtyProps) != 0)
                {
                    var p = await WinRt.Await(s.TryGetMediaPropertiesAsync());
                    if (!ReferenceEquals(s, session) || p == null) return;
                    Title = p.Title ?? "";
                    Artist = p.Artist ?? "";
                    Album = p.AlbumTitle ?? "";
                    var t = p.Thumbnail;
                    string key = Title + "\n" + Artist + "\n" + Album;
                    // apps often send the text first and the artwork in a second update
                    if (key != trackKey || (t == null) != (thumb == null)) { trackKey = key; ArtVersion++; }
                    thumb = t;
                }
            }
            catch { }
            Raise();
        }

        GlobalSystemMediaTransportControlsSession PickSession()
        {
            var cur = manager.GetCurrentSession();
            if (cur != null) return cur;
            var all = manager.GetSessions();
            foreach (var x in all)
            {
                try
                {
                    if (x.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        return x;
                }
                catch { }
            }
            return all.Count > 0 ? all[0] : null;
        }

        void ReadPlayback(GlobalSystemMediaTransportControlsSession s)
        {
            var pi = s.GetPlaybackInfo();
            bool playing = pi.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing != IsPlaying)
            {
                // re-base the estimate so resume/pause doesn't jump before the next timeline event
                posAtStamp = Position;
                stampUtc = DateTime.UtcNow;
                IsPlaying = playing;
            }
            var c = pi.Controls;
            CanPrev = c.IsPreviousEnabled;
            CanNext = c.IsNextEnabled;
            CanPlayPause = c.IsPlayPauseToggleEnabled || c.IsPlayEnabled || c.IsPauseEnabled;
            CanSeek = c.IsPlaybackPositionEnabled;
            double? r = pi.PlaybackRate;
            rate = r.HasValue && r.Value > 0 ? r.Value : 1.0;
        }

        void ReadTimeline(GlobalSystemMediaTransportControlsSession s)
        {
            var tl = s.GetTimelineProperties();
            var dur = tl.EndTime - tl.StartTime;
            Duration = dur > TimeSpan.Zero ? dur : TimeSpan.Zero;
            posAtStamp = tl.Position - tl.StartTime;
            DateTime now = DateTime.UtcNow, last;
            try { last = tl.LastUpdatedTime.UtcDateTime; } catch { last = now; }
            stampUtc = last > now.AddHours(-12) && last <= now.AddSeconds(2) ? last : now;
        }

        void Attach(GlobalSystemMediaTransportControlsSession s)
        {
            if (s == null) return;
            try
            {
                s.MediaPropertiesChanged += onProps;
                s.PlaybackInfoChanged += onPlayback;
                s.TimelinePropertiesChanged += onTimeline;
            }
            catch { }
        }

        void Detach(GlobalSystemMediaTransportControlsSession s)
        {
            if (s == null) return;
            try
            {
                s.MediaPropertiesChanged -= onProps;
                s.PlaybackInfoChanged -= onPlayback;
                s.TimelinePropertiesChanged -= onTimeline;
            }
            catch { }
        }

        void Raise()
        {
            var h = Changed;
            if (h != null) h();
        }

        // ---------------------------------------------------------------- commands

        public async void Previous()
        {
            var s = session; if (s == null) return;
            try { await WinRt.Await(s.TrySkipPreviousAsync()); } catch { }
        }

        public async void Next()
        {
            var s = session; if (s == null) return;
            try { await WinRt.Await(s.TrySkipNextAsync()); } catch { }
        }

        public async void TogglePlayPause()
        {
            var s = session; if (s == null) return;
            try { await WinRt.Await(s.TryTogglePlayPauseAsync()); } catch { }
        }

        public async void Seek(double fraction)
        {
            var s = session;
            if (s == null || Duration <= TimeSpan.Zero) return;
            var target = TimeSpan.FromTicks((long)(Duration.Ticks * Math.Max(0, Math.Min(1, fraction))));
            posAtStamp = target;
            stampUtc = DateTime.UtcNow;
            try
            {
                var start = s.GetTimelineProperties().StartTime;
                await WinRt.Await(s.TryChangePlaybackPositionAsync((start + target).Ticks));
            }
            catch { }
        }

        /// <summary>Decodes the cover art at display size (called lazily, only while the overlay is visible).</summary>
        public async Task<BitmapSource> LoadArtAsync(int decodeWidth)
        {
            var t = thumb;
            if (t == null) return null;
            try
            {
                byte[] bytes;
                using (var ws = await WinRt.Await(t.OpenReadAsync()))
                {
                    ulong size = ws.Size;
                    if (size == 0 || size > 16 * 1024 * 1024) return null;
                    using (var reader = new DataReader(ws.GetInputStreamAt(0)))
                    {
                        await WinRt.Await(reader.LoadAsync((uint)size));
                        bytes = new byte[size];
                        reader.ReadBytes(bytes);
                    }
                }
                var ms = new MemoryStream(bytes, false);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = decodeWidth;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- helpers

        static string SafeAppId(GlobalSystemMediaTransportControlsSession s)
        {
            try { return s.SourceAppUserModelId ?? ""; } catch { return ""; }
        }

        public static string FriendlyAppName(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return "";
            string a = aumid.ToLowerInvariant();
            if (a.Contains("spotify")) return "Spotify";
            if (a.Contains("msedge")) return "Edge";
            if (a.Contains("chrome")) return "Chrome";
            if (a.Contains("opera")) return "Opera";
            if (a.Contains("brave")) return "Brave";
            if (a.Contains("yandex")) return "Yandex";
            if (a.Contains("firefox") || IsHex(aumid)) return "Firefox";
            if (a.Contains("zunemusic")) return "Media Player";
            if (a.Contains("applemusic") || a.Contains("itunes")) return "Apple Music";
            if (a.Contains("deezer")) return "Deezer";
            if (a.Contains("tidal")) return "TIDAL";
            if (a.Contains("foobar")) return "foobar2000";
            if (a.Contains("vlc")) return "VLC";
            string n = aumid;
            int bang = n.LastIndexOf('!');
            if (bang >= 0) n = n.Substring(bang + 1);
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            int dot = n.LastIndexOf('.');
            if (dot >= 0 && dot < n.Length - 1) n = n.Substring(dot + 1);
            return n.Length > 0 ? char.ToUpperInvariant(n[0]) + n.Substring(1) : n;
        }

        internal static bool IsHex(string s)
        {
            if (s.Length != 16) return false;
            foreach (char c in s) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }
    }

    /// <summary>
    /// IAsyncOperation -> Task without System.Runtime.WindowsRuntime's extension methods
    /// (the in-box csc can't bind those against System32\WinMetadata).
    /// </summary>
    internal static class WinRt
    {
        public static Task<T> Await<T>(IAsyncOperation<T> op)
        {
            var tcs = new TaskCompletionSource<T>();
            op.Completed = (o, status) =>
            {
                try
                {
                    if (status == AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
                    else if (status == AsyncStatus.Error) tcs.TrySetException(o.ErrorCode ?? new Exception("WinRT operation failed"));
                    else tcs.TrySetCanceled();
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            };
            return tcs.Task;
        }
    }
}
