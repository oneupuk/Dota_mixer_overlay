using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace DotaGlass
{
    /// <summary>
    /// Glue: shows the overlay only while (in a match) AND (Dota is the focused window) AND (something is playing).
    /// Everything is event-driven - GSI posts, the foreground-window hook and media events.
    /// The only timer is a 1 Hz progress tick that runs only while the overlay is visible and music is playing.
    /// </summary>
    internal sealed class Controller : IDisposable
    {
        const string InProgress = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS";
        const string PreGame = "DOTA_GAMERULES_STATE_PRE_GAME";

        readonly string[] args;
        Process game;                      // set when Steam launched us as a %command% wrapper
        string gameName = "dota2";
        Settings settings;
        Dispatcher ui;
        OverlayWindow overlay;
        HotspotWindow clockButton;        // invisible button over the sun/moon in Dota's clock
        MediaController media;
        AppVolume volume;
        GsiServer gsi;
        Tray tray;
        DispatcherTimer ticker;
        DispatcherTimer idleTimer;         // auto-hide after N seconds without interaction
        Native.WinEventProc foregroundProc;
        IntPtr foregroundHook;

        volatile bool inMatch, overlayShown, buttonShown, gsiSeen;
        bool widgetOpen;                   // toggled by the clock button
        string lastState = "\0";           // I/O thread only
        bool dotaFocused, preview;
        IntPtr dotaHwnd;                   // written on UI thread, read on I/O thread (atomic)
        IntPtr trackedForeground;          // last window TrackForeground saw (same threading as dotaHwnd)
        uint dotaPid;
        RECT lastRect;
        int artShown = -1, sessionShown = -1;
        string gsiProblem;
        string lastVis;                    // DebugLog only

        public Controller(string[] args, Process game) { this.args = args; this.game = game; }

        public bool IsPreview { get { return preview; } }

        public void Start()
        {
            ui = Dispatcher.CurrentDispatcher;
            settings = Settings.Load();
            preview = Array.IndexOf(args, "--preview") >= 0;

            overlay = new OverlayWindow(settings.Glass);
            volume = new AppVolume();
            media = new MediaController(ui);
            media.Changed += OnMediaChanged;

            overlay.PrevClicked += () => media.Previous();
            overlay.NextClicked += () => media.Next();
            overlay.PlayPauseClicked += () => media.TogglePlayPause();
            overlay.SeekRequested += f => { media.Seek(f); overlay.SetProgress(media.Position, media.Duration); };
            overlay.VolumeRequested += v => volume.Set(v);

            idleTimer = new DispatcherTimer(DispatcherPriority.Background, ui);
            idleTimer.Tick += OnIdle;
            overlay.Interacted += RestartIdle;

            clockButton = new HotspotWindow();
            clockButton.Clicked += ToggleWidget;
            widgetOpen = preview;

            ticker = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
            ticker.Tick += (s, e) => overlay.SetProgress(media.Position, media.Duration);

            tray = new Tray(this);
            tray.SetPreviewChecked(preview);

            InstallGsiConfig(true);
            StartGsi();
            media.Start();

            foregroundProc = OnForegroundChanged; // keep the delegate alive
            foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                foregroundProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            TrackForeground(Native.GetForegroundWindow());

            UpdateVisibility();
            UpdateStatus();
            if (game != null) WatchGame(game);
        }

        // ---------------------------------------------------------------- launched with the game

        /// <summary>Quit together with Dota (no polling: the process handle signals us).</summary>
        void WatchGame(Process p)
        {
            game = p;
            try
            {
                dotaPid = (uint)p.Id;
                try { gameName = p.ProcessName; } catch { }
                p.EnableRaisingEvents = true;
                p.Exited += (s, e) => ui.BeginInvoke(new Action(OnGameExited));
                if (p.HasExited) ui.BeginInvoke(new Action(OnGameExited));
            }
            catch { ui.BeginInvoke(new Action(OnGameExited)); }
        }

        async void OnGameExited()
        {
            // Dota occasionally restarts itself (updates, "restart to apply"): follow the new process
            DateTime exitedAt = DateTime.Now.AddSeconds(-2);
            string name = gameName;
            await System.Threading.Tasks.Task.Delay(8000);
            foreach (var p in Process.GetProcessesByName(name))
            {
                bool restarted = false;
                try { restarted = p.StartTime >= exitedAt; } catch { }
                if (restarted) { WatchGame(p); return; }
                p.Dispose();
            }
            Exit();
        }

        public bool LaunchedWithGame { get { return game != null; } }

        // ---------------------------------------------------------------- Dota GSI

        void StartGsi()
        {
            gsi = new GsiServer(settings.Port);
            gsi.StateReceived += OnGsiState;
            string err;
            if (!gsi.Start(out err))
            {
                gsiProblem = "Port " + settings.Port + " is busy";
                tray.Notify("Dota Glass Overlay", "Can't listen on port " + settings.Port + " (" + err + "). Change Port in settings.", true);
            }
        }

        void InstallGsiConfig(bool notify)
        {
            string dir = DotaInstall.FindDotaGameDir(settings.DotaPath);
            if (dir == null)
            {
                gsiProblem = "Dota 2 folder not found";
                if (notify) tray.Notify("Dota 2 not found", "Set DotaPath in settings.ini, then Reload settings.", true);
                return;
            }
            try
            {
                string cfg;
                if (DotaInstall.EnsureConfig(dir, settings.Port, out cfg) && notify)
                    tray.Notify("Dota GSI config installed",
                        "Restart Dota 2 if it's running. Make sure its launch options contain -gamestateintegration", false);
                gsiProblem = null;
            }
            catch (Exception ex)
            {
                gsiProblem = "Can't write GSI config";
                if (notify) tray.Notify("Can't write Dota GSI config", ex.Message, true);
            }
        }

        /// <summary>I/O thread. Only wakes the UI thread when something actually changed.</summary>
        void OnGsiState(string state)
        {
            bool firstPacket = !gsiSeen;
            gsiSeen = true;
            bool match = state == InProgress || (state == PreGame && settings.ShowInPreGame);
            bool stateChanged = state != lastState;
            lastState = state;

            if (match != inMatch || stateChanged || firstPacket)
            {
                inMatch = match;
                ui.BeginInvoke(new Action(() => { RecheckForeground(); UpdateVisibility(); UpdateStatus(); }));
                return;
            }
            if (!match) return;
            // Safety net: Windows doesn't always report a focus change (e.g. when the focused window is
            // hidden), so compare against the real foreground window once per packet.
            if (Native.GetForegroundWindow() != trackedForeground)
            {
                ui.BeginInvoke(new Action(() => { RecheckForeground(); UpdateVisibility(); }));
                return;
            }
            if (overlayShown || buttonShown)
            {
                // resolution / window changes: one cheap syscall per packet instead of a location hook
                RECT r;
                if (Native.GetClientScreenRect(dotaHwnd, out r) && !r.Same(lastRect))
                    ui.BeginInvoke(new Action(Reposition));
            }
        }

        void RecheckForeground()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg != trackedForeground) TrackForeground(fg);
        }

        // ---------------------------------------------------------------- foreground tracking

        void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            TrackForeground(hwnd);
            UpdateVisibility();
        }

        void TrackForeground(IntPtr hwnd)
        {
            trackedForeground = hwnd;
            bool isDota = false;
            if (hwnd != IntPtr.Zero)
            {
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if (pid != 0 && pid == dotaPid) isDota = true;
                else if (pid != 0)
                {
                    string exe = Native.GetProcessFileName(pid);
                    isDota = exe != null
                        ? string.Equals(exe, "dota2.exe", StringComparison.OrdinalIgnoreCase)
                        : Native.GetTitle(hwnd) == "Dota 2";
                    if (isDota) dotaPid = pid;
                }
            }
            dotaFocused = isDota;
            if (isDota) dotaHwnd = hwnd;
            if (DebugLog.On) DebugLog.Write("fg   hwnd=" + hwnd.ToString("X") + " isDota=" + isDota + " title=" + Native.GetTitle(hwnd));
        }

        // ---------------------------------------------------------------- visibility & layout

        /// <summary>
        /// In a match with Dota focused: the clock button is there; the widget shows when it's toggled open
        /// (or, with ClockButton=0, whenever something is playing).
        /// </summary>
        void UpdateVisibility() { UpdateVisibility(false); }

        void UpdateVisibility(bool animate)
        {
            bool dotaOk = dotaHwnd != IntPtr.Zero && Native.IsWindow(dotaHwnd) && !Native.IsIconic(dotaHwnd);
            bool ingame = preview || (inMatch && dotaOk && (dotaFocused || !settings.OnlyWhenDotaFocused));

            bool showButton = ingame && settings.ClockButton;
            bool showWidget = ingame && (settings.ClockButton
                ? widgetOpen
                : (preview || media.HasSession || !settings.HideWhenNoMedia));

            if (DebugLog.On)
            {
                string v = "inMatch=" + inMatch + " dotaOk=" + dotaOk + " focused=" + dotaFocused
                         + " -> button=" + showButton + " widget=" + showWidget;
                if (v != lastVis) { lastVis = v; DebugLog.Write("vis  " + v); }
            }

            if (showButton || showWidget) Reposition();

            if (showButton && !buttonShown) { buttonShown = true; clockButton.ShowButton(); }
            else if (!showButton && buttonShown) { buttonShown = false; clockButton.Hide(); }
            clockButton.SetActive(widgetOpen);

            if (showWidget)
            {
                if (!overlayShown)
                {
                    overlayShown = true;
                    Render(true);
                    overlay.ShowOverlay(animate);
                    RestartIdle();
                }
            }
            else if (overlayShown)
            {
                overlayShown = false;
                ticker.Stop();
                idleTimer.Stop();
                overlay.HideOverlay(animate);
            }
        }

        void ToggleWidget()
        {
            widgetOpen = !widgetOpen;
            UpdateVisibility(true);
        }

        // ---------------------------------------------------------------- idle auto-hide

        bool AutoHideActive
        {
            get { return settings.ClockButton && settings.AutoHideSeconds > 0 && !preview && overlayShown && widgetOpen; }
        }

        void RestartIdle()
        {
            idleTimer.Stop();
            if (!AutoHideActive) return;
            idleTimer.Interval = TimeSpan.FromSeconds(settings.AutoHideSeconds);
            idleTimer.Start();
        }

        void OnIdle(object sender, EventArgs e)
        {
            idleTimer.Stop();
            if (!AutoHideActive) return;
            if (overlay.IsInUse) { idleTimer.Start(); return; } // pointer still on it: check again later
            widgetOpen = false;
            UpdateVisibility(true);                              // same fold-up as clicking the sun/moon
        }

        void Reposition()
        {
            RECT r = new RECT();
            bool onGame = (inMatch || !preview) && Native.IsWindow(dotaHwnd) && Native.GetClientScreenRect(dotaHwnd, out r);
            if (!onGame)
            {
                var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                r = new RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
            }
            lastRect = r;

            double hud = r.Height / 1080.0;            // Dota's HUD scales with the game's height
            double s = hud * settings.Scale;
            int w = (int)Math.Round(OverlayWindow.BaseWidth * s);
            int h = (int)Math.Round(OverlayWindow.BaseHeight * s);
            int x = r.Left + (r.Width - w) / 2;
            int y = r.Top + (int)Math.Round(settings.OffsetY * hud);
            overlay.Place(x, y, w, h);

            // the button follows Dota's HUD only (not the widget's Scale)
            int bs = (int)Math.Round(HotspotWindow.BaseSize * hud);
            int bx = r.Left + (r.Width - bs) / 2;
            int by = r.Top + (int)Math.Round(settings.ButtonY * hud) - bs / 2;
            clockButton.Place(bx, by, bs);
        }

        // ---------------------------------------------------------------- media -> overlay

        void OnMediaChanged()
        {
            bool autoHide = !settings.ClockButton && settings.HideWhenNoMedia;
            if (!overlayShown)
            {
                // a player starting/stopping can change whether we should be visible at all
                if (autoHide && inMatch) UpdateVisibility();
                return;
            }
            if (autoHide && !media.HasSession && !preview) { UpdateVisibility(); return; }
            Render(false);
        }

        void Render(bool full)
        {
            overlay.SetTrack(media.Title, media.Artist, media.AppName, media.HasSession);
            overlay.SetPlaying(media.IsPlaying);
            overlay.SetControls(media.CanPrev, media.CanPlayPause, media.CanNext, media.CanSeek);
            overlay.SetProgress(media.Position, media.Duration);

            if (full || sessionShown != media.SessionVersion)
            {
                sessionShown = media.SessionVersion;
                volume.Bind(media.AppId);
                overlay.SetVolume(volume.Get());
            }
            if (artShown != media.ArtVersion) LoadArt();

            bool tick = media.IsPlaying && media.Duration > TimeSpan.Zero;
            if (tick != ticker.IsEnabled) ticker.IsEnabled = tick;
        }

        async void LoadArt()
        {
            int version = media.ArtVersion;
            artShown = version;
            int px = (int)Math.Max(64, Math.Ceiling(52.0 * lastRect.Height / 1080.0 * settings.Scale * 1.5));
            var bmp = await media.LoadArtAsync(px);
            if (version == media.ArtVersion) overlay.SetArt(bmp);
            else artShown = -1;
        }

        // ---------------------------------------------------------------- tray actions

        void UpdateStatus()
        {
            string s;
            if (gsiProblem != null) s = gsiProblem;
            else if (!gsiSeen) s = "Waiting for Dota (needs -gamestateintegration)";
            else if (inMatch) s = "In match";
            else s = "Waiting for a match";
            tray.SetStatus(s);
        }

        public void TogglePreview()
        {
            preview = !preview;
            if (preview) widgetOpen = true;
            UpdateVisibility();
        }

        public void OpenSettings()
        {
            try { Process.Start("notepad.exe", "\"" + Settings.FilePath + "\""); } catch { }
        }

        public void ReloadSettings()
        {
            int oldPort = settings.Port;
            string oldGlass = settings.Glass;
            settings = Settings.Load();
            if (settings.Port != oldPort)
            {
                gsi.Stop();
                StartGsi();
            }
            InstallGsiConfig(true);
            if (settings.Glass != oldGlass)
                tray.Notify("Dota Glass Overlay", "Glass mode changes apply after restarting the overlay.", false);
            if (overlayShown) Reposition();
            UpdateVisibility();
            UpdateStatus();
        }

        public void Exit()
        {
            Application.Current.Shutdown();
        }

        public void Dispose()
        {
            if (foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(foregroundHook);
            if (gsi != null) gsi.Stop();
            if (tray != null) tray.Dispose();
        }
    }
}
