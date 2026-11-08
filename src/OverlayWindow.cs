using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DotaGlass
{
    /// <summary>
    /// Topmost, never-activating window with a DWM blur behind it, shaped to a rounded rect.
    /// It never takes focus from Dota (WS_EX_NOACTIVATE) and only redraws when something changes.
    /// </summary>
    internal sealed class OverlayWindow : Window
    {
        public const double BaseWidth = 360, BaseHeight = 72, BaseRadius = 24;

        static readonly Geometry PlayGeo = Freeze(Geometry.Parse("M8,5 V19 L19,12 Z"));
        static readonly Geometry PauseGeo = Freeze(Geometry.Parse("M6.5,5 H10.5 V19 H6.5 Z M13.5,5 H17.5 V19 H13.5 Z"));

        public event Action PrevClicked, PlayPauseClicked, NextClicked;
        public event Action<double> SeekRequested;   // 0..1
        public event Action<double> VolumeRequested; // 0..1
        /// <summary>Pointer left the widget, or a click / scroll on it (restarts the idle auto-hide).</summary>
        public event Action Interacted;

        const int InMs = 240, OutMs = 150;

        readonly string glassMode;
        IntPtr hwnd;
        readonly GlassBackdrop backdrop;
        readonly FrameworkElement root;
        readonly ScaleTransform rootScale;
        int animToken;

        readonly Border art, artFallback, btnPrev, btnPlay, btnNext;
        readonly Path playIcon, volWave1, volWave2;
        readonly TextBlock titleText, artistText, posText, durText;
        readonly Grid seekTrack, volTrack;
        readonly ScaleTransform seekScale, volScale;
        readonly TranslateTransform seekThumbT, volThumbT;
        readonly Ellipse seekThumb;

        bool seekDragging, volDragging, canSeek, playing;
        double seekFrac, volFrac = 0.5;
        TimeSpan duration;
        string lastPos, lastDur;

        public OverlayWindow(string glassMode)
        {
            this.glassMode = glassMode;
            Title = "Dota Glass Overlay";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false; // non-layered "sheet of glass": no per-frame readback of the surface
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            Width = BaseWidth; Height = BaseHeight;
            Left = -32000; Top = -32000;

            // Windows 11: rounded DWM glass lives in its own window right underneath this one.
            if (glassMode != "none" && glassMode != "acrylic") backdrop = GlassBackdrop.TryCreate();
            if (backdrop != null) new WindowInteropHelper(this).Owner = backdrop.Hwnd; // owned => always above it

            using (var s = typeof(OverlayWindow).Assembly.GetManifestResourceStream("Overlay.xaml"))
                root = (FrameworkElement)XamlReader.Load(s);
            rootScale = new ScaleTransform(1, 1, BaseWidth / 2, 0); // grows down out of the clock
            root.RenderTransform = rootScale;
            Content = new Viewbox { Child = root, Stretch = Stretch.Uniform };

            art = (Border)root.FindName("Art");
            artFallback = (Border)root.FindName("ArtFallback");
            btnPrev = (Border)root.FindName("BtnPrev");
            btnPlay = (Border)root.FindName("BtnPlay");
            btnNext = (Border)root.FindName("BtnNext");
            playIcon = (Path)root.FindName("PlayIcon");
            volWave1 = (Path)root.FindName("VolWave1");
            volWave2 = (Path)root.FindName("VolWave2");
            titleText = (TextBlock)root.FindName("TitleText");
            artistText = (TextBlock)root.FindName("ArtistText");
            posText = (TextBlock)root.FindName("PosText");
            durText = (TextBlock)root.FindName("DurText");
            seekTrack = (Grid)root.FindName("SeekTrack");
            volTrack = (Grid)root.FindName("VolTrack");
            seekThumb = (Ellipse)root.FindName("SeekThumb");
            seekScale = (ScaleTransform)((Border)root.FindName("SeekFill")).RenderTransform;
            volScale = (ScaleTransform)((Border)root.FindName("VolFill")).RenderTransform;
            seekThumbT = (TranslateTransform)seekThumb.RenderTransform;
            volThumbT = (TranslateTransform)((Ellipse)root.FindName("VolThumb")).RenderTransform;

            WireButton(btnPrev, () => Fire(PrevClicked));
            WireButton(btnNext, () => Fire(NextClicked));
            WireButton(btnPlay, () =>
            {
                SetPlaying(!playing); // optimistic; confirmed by the media event a moment later
                Fire(PlayPauseClicked);
            });
            WireSeek();
            WireVolume();
            root.MouseWheel += (s, e) =>
            {
                RequestVolume(volFrac + Math.Sign(e.Delta) * 0.04);
                e.Handled = true;
            };
            root.MouseLeave += (s, e) => Fire(Interacted);
            root.PreviewMouseDown += (s, e) => Fire(Interacted);
            root.PreviewMouseWheel += (s, e) => Fire(Interacted);

            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.MakeNoActivateToolWindow(this, hwnd);
                HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                Native.ExtendGlass(hwnd);
                if (backdrop == null) Native.ApplyAccentGlass(hwnd, glassMode); // Windows 10 fallback
                Native.SetDwmCorners(hwnd, backdrop == null && glassMode != "none");
            };
        }

        // ---------------------------------------------------------------- placement

        /// <summary>Position in physical screen pixels; content scales via the Viewbox.</summary>
        public void Place(int x, int y, int w, int h)
        {
            if (hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
            if (backdrop != null) backdrop.Place(x, y, w, h, BaseRadius * w / BaseWidth);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
        }

        /// <summary>animate = liquid drop-down out of the clock (used when toggled from the sun/moon button).</summary>
        public void ShowOverlay(bool animate)
        {
            animToken++; // cancels a hide that is still fading out
            if (backdrop != null)
            {
                backdrop.Show();
                if (animate) backdrop.AnimateIn(InMs); else backdrop.ResetVisual();
            }
            if (!IsVisible) Show();
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

            if (animate)
            {
                var spring = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut };
                AnimateRoot(0.88, 1, 0.55, 1, 0, 1, InMs, spring, null);
            }
            else
            {
                StopRootAnimations();
            }
        }

        public void HideOverlay(bool animate)
        {
            int token = ++animToken;
            if (!animate || !IsVisible)
            {
                Hide();
                if (backdrop != null) backdrop.Hide();
                return;
            }
            if (backdrop != null) backdrop.AnimateOut(OutMs);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            AnimateRoot(1, 0.94, 1, 0.7, 1, 0, OutMs, ease, (s, e) =>
            {
                if (token != animToken) return; // re-opened meanwhile
                Hide();
                if (backdrop != null) backdrop.Hide();
                StopRootAnimations();
            });
        }

        void AnimateRoot(double sx0, double sx1, double sy0, double sy1, double o0, double o1, int ms,
                         IEasingFunction ease, EventHandler done)
        {
            var d = TimeSpan.FromMilliseconds(ms);
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(sx0, sx1, d) { EasingFunction = ease });
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(sy0, sy1, d) { EasingFunction = ease });
            var fade = new DoubleAnimation(o0, o1, TimeSpan.FromMilliseconds(Math.Min(ms, 160)));
            if (done != null) fade.Completed += done;
            root.BeginAnimation(OpacityProperty, fade);
        }

        void StopRootAnimations()
        {
            rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            root.BeginAnimation(OpacityProperty, null);
            rootScale.ScaleX = rootScale.ScaleY = 1;
            root.Opacity = 1;
        }

        /// <summary>Pointer on the widget or a slider being dragged: don't auto-hide now.</summary>
        public bool IsInUse
        {
            get { return IsVisible && (root.IsMouseOver || seekDragging || volDragging); }
        }

        // ---------------------------------------------------------------- state -> visuals

        public void SetTrack(string title, string artist, string appName, bool hasSession)
        {
            string t, a;
            if (!hasSession) { t = "Nothing playing"; a = "Play music in any app"; }
            else
            {
                t = string.IsNullOrWhiteSpace(title) ? (appName.Length > 0 ? appName : "Unknown title") : title;
                a = string.IsNullOrWhiteSpace(artist) ? appName : artist;
            }
            if (titleText.Text != t) titleText.Text = t;
            if (artistText.Text != a) artistText.Text = a;
        }

        public void SetArt(BitmapSource bmp)
        {
            if (bmp == null)
            {
                art.Background = null;
                art.Visibility = Visibility.Collapsed;
                artFallback.Visibility = Visibility.Visible;
                return;
            }
            var brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            art.Background = brush;
            art.Visibility = Visibility.Visible;
            artFallback.Visibility = Visibility.Hidden;
            art.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }

        public void SetPlaying(bool isPlaying)
        {
            playing = isPlaying;
            playIcon.Data = isPlaying ? PauseGeo : PlayGeo;
            playIcon.Margin = isPlaying ? new Thickness(0) : new Thickness(2, 0, 0, 0);
        }

        public void SetControls(bool prev, bool playPause, bool next, bool seek)
        {
            btnPrev.IsEnabled = prev;
            btnPlay.IsEnabled = playPause;
            btnNext.IsEnabled = next;
            canSeek = seek;
            seekTrack.Cursor = seek ? Cursors.Hand : Cursors.Arrow;
        }

        public void SetProgress(TimeSpan pos, TimeSpan dur)
        {
            duration = dur;
            if (seekDragging) return;
            double f = dur.Ticks > 0 ? Math.Max(0, Math.Min(1, (double)pos.Ticks / dur.Ticks)) : 0;
            ShowSeek(f, pos);
        }

        public void SetVolume(double v)
        {
            if (volDragging) return;
            ShowVolume(v);
        }

        void ShowSeek(double f, TimeSpan pos)
        {
            seekFrac = f;
            seekScale.ScaleX = f;
            seekThumbT.X = f * seekTrack.ActualWidth - 4.5;
            string p = duration.Ticks > 0 ? Fmt(pos) : "";
            string d = duration.Ticks > 0 ? Fmt(duration) : "";
            if (p != lastPos) { lastPos = p; posText.Text = p; }
            if (d != lastDur) { lastDur = d; durText.Text = d; }
        }

        void ShowVolume(double v)
        {
            volFrac = Math.Max(0, Math.Min(1, v));
            volScale.ScaleX = volFrac;
            volThumbT.X = volFrac * volTrack.ActualWidth - 4.5;
            volWave1.Visibility = volFrac > 0.005 ? Visibility.Visible : Visibility.Hidden;
            volWave2.Visibility = volFrac > 0.5 ? Visibility.Visible : Visibility.Hidden;
        }

        static string Fmt(TimeSpan t)
        {
            return t.TotalHours >= 1
                ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
        }

        // ---------------------------------------------------------------- input

        void WireButton(Border b, Action action)
        {
            var st = new ScaleTransform(1, 1);
            b.RenderTransform = st;
            b.RenderTransformOrigin = new Point(0.5, 0.5);
            b.MouseLeftButtonDown += (s, e) =>
            {
                Squish(st, 0.86, 90, null);
                b.CaptureMouse();
                e.Handled = true;
            };
            b.MouseLeftButtonUp += (s, e) =>
            {
                if (!b.IsMouseCaptured) return;
                var p = e.GetPosition(b);
                b.ReleaseMouseCapture();
                if (p.X >= 0 && p.Y >= 0 && p.X <= b.ActualWidth && p.Y <= b.ActualHeight) action();
                e.Handled = true;
            };
            b.LostMouseCapture += (s, e) => Squish(st, 1.0, 380, new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut });
        }

        static void Squish(ScaleTransform st, double to, int ms, IEasingFunction ease)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        void WireSeek()
        {
            seekTrack.MouseEnter += (s, e) => { if (canSeek) seekThumb.Opacity = 1; };
            seekTrack.MouseLeave += (s, e) => { if (!seekDragging) seekThumb.Opacity = 0; };
            seekTrack.MouseLeftButtonDown += (s, e) =>
            {
                if (!canSeek || duration.Ticks <= 0) return;
                seekDragging = true;
                seekTrack.CaptureMouse();
                DragSeek(e);
                e.Handled = true;
            };
            seekTrack.MouseMove += (s, e) => { if (seekDragging) DragSeek(e); };
            seekTrack.MouseLeftButtonUp += (s, e) =>
            {
                if (!seekDragging) return;
                seekTrack.ReleaseMouseCapture(); // -> LostMouseCapture commits
                e.Handled = true;
            };
            seekTrack.LostMouseCapture += (s, e) =>
            {
                if (!seekDragging) return;
                seekDragging = false;
                if (!seekTrack.IsMouseOver) seekThumb.Opacity = 0;
                var h = SeekRequested;
                if (h != null) h(seekFrac);
            };
            seekTrack.SizeChanged += (s, e) => { seekThumbT.X = seekFrac * seekTrack.ActualWidth - 4.5; };
        }

        void DragSeek(MouseEventArgs e)
        {
            double w = seekTrack.ActualWidth;
            double f = w > 0 ? Math.Max(0, Math.Min(1, e.GetPosition(seekTrack).X / w)) : 0;
            ShowSeek(f, TimeSpan.FromTicks((long)(duration.Ticks * f)));
        }

        void WireVolume()
        {
            volTrack.MouseLeftButtonDown += (s, e) =>
            {
                volDragging = true;
                volTrack.CaptureMouse();
                DragVolume(e);
                e.Handled = true;
            };
            volTrack.MouseMove += (s, e) => { if (volDragging) DragVolume(e); };
            volTrack.MouseLeftButtonUp += (s, e) => { volTrack.ReleaseMouseCapture(); e.Handled = true; };
            volTrack.LostMouseCapture += (s, e) => { volDragging = false; };
            volTrack.SizeChanged += (s, e) => { volThumbT.X = volFrac * volTrack.ActualWidth - 4.5; };
        }

        void DragVolume(MouseEventArgs e)
        {
            double w = volTrack.ActualWidth;
            if (w > 0) RequestVolume(e.GetPosition(volTrack).X / w);
        }

        void RequestVolume(double v)
        {
            v = Math.Max(0, Math.Min(1, v));
            ShowVolume(v);
            var h = VolumeRequested;
            if (h != null) h(v);
        }

        static void Fire(Action a) { if (a != null) a(); }

        static Geometry Freeze(Geometry g) { g.Freeze(); return g; }
    }
}
