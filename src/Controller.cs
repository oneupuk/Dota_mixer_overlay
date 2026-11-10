using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace DotaGlass
{
    /// <summary>
    /// Glue: shows the overlay only while (in a match or in the main menu) AND (Dota is the focused window).
    /// Everything is event-driven - GSI posts, the foreground-window hook and media events.
    /// The only timer is a 1 Hz progress tick that runs only while the overlay is visible and music is playing.
    /// </summary>
    internal sealed class Controller : IDisposable
    {
        const string InProgress = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS";
        const string PreGame = "DOTA_GAMERULES_STATE_PRE_GAME";

        // Dota sends no game state at all in its main menu, while in a game it posts at least once a second
        // (heartbeat 1.0): this long without a post means the menu.
        const int MenuAfterSilenceMs = 5000;

        // Dota's main menu, in 1080p pixels (its dashboard scales with the window height like the HUD).
        // PLAY DOTA, measured on a 16:9 dashboard from the bottom-right corner it hangs off.
        const double MenuButtonSize = 42;
        const double PlayRight = 60, PlayWidth = 330, PlayTop = 70, PlayCenterY = 45.5;

        readonly string[] args;
        Process game;                      // set when Steam launched us as a %command% wrapper
        string gameName = "dota2";
        Settings settings;
        Dispatcher ui;
        OverlayWindow overlay;
        HotspotWindow clockButton;        // invisible button over the sun/moon in Dota's clock
        UpNextWindow upNextList;           // the music app's next songs, under/over the widget
        readonly UpNext upNext = new UpNext();
        bool upNextOpen;
        int upNextToken;                   // drops results of a read the user no longer waits for
        string upNextFor;                  // the song that was playing when the list was read
        int widgetX, widgetY, widgetW, widgetH;
        MediaController media;
        AppVolume volume;
        GsiServer gsi;
        Tray tray;
        DispatcherTimer ticker;
        DispatcherTimer idleTimer;         // auto-hide after N seconds without interaction
        System.Threading.Timer gsiSilence; // fires when Dota stops posting: back in the main menu
        volatile int lastGsiTick;          // Environment.TickCount of the last post
        Native.WinEventProc foregroundProc;
        IntPtr foregroundHook;

        volatile bool inMatch, inMenu, overlayShown, buttonShown, gsiSeen;
        bool widgetOpen;                   // toggled by the clock button
        bool menuLayout;                   // button and widget are placed for the main menu, not the match HUD
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

            upNextList = new UpNextWindow(settings.Glass);
            overlay.UpNextClicked += ToggleUpNext;
            upNextList.TrackClicked += PlayFromUpNext;
            upNextList.Interacted += RestartIdle;

            clockButton = new HotspotWindow();
            clockButton.Clicked += ToggleWidget;
            widgetOpen = preview;

            ticker = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
            ticker.Tick += (s, e) => overlay.SetProgress(media.Position, media.Duration);

            tray = new Tray(this);
            tray.SetPreviewChecked(preview);

            InstallGsiConfig(true);
            gsiSilence = new System.Threading.Timer(OnGsiSilence, null, MenuAfterSilenceMs, System.Threading.Timeout.Infinite);
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
            lastGsiTick = Environment.TickCount;
            gsiSilence.Change(MenuAfterSilenceMs, System.Threading.Timeout.Infinite);
            bool firstPacket = !gsiSeen;
            gsiSeen = true;
            bool match = state == InProgress || (state == PreGame && settings.ShowInPreGame);
            bool menu = state == null;     // a post without a map: not in a game
            bool stateChanged = state != lastState;
            lastState = state;

            if (match != inMatch || menu != inMenu || stateChanged || firstPacket)
            {
                inMatch = match;
                inMenu = menu;
                ui.BeginInvoke(new Action(() => { RecheckForeground(); UpdateVisibility(); UpdateStatus(); }));
                return;
            }
            if (!match && !menu) return;
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

        /// <summary>Timer thread. No game state for a while: Dota is in its main menu (or not running at all).</summary>
        void OnGsiSilence(object _)
        {
            if (inMenu && !inMatch) return;
            if (gsiSeen && Environment.TickCount - lastGsiTick < MenuAfterSilenceMs - 250) return; // a post just came in
            if (DebugLog.On) DebugLog.Write("gsi  silent for " + MenuAfterSilenceMs + " ms -> main menu");
            inMatch = false;
            inMenu = true;
            ui.BeginInvoke(new Action(() => { RecheckForeground(); UpdateVisibility(); UpdateStatus(); }));
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
            if (!dotaFocused && overlayShown && (uint)(Environment.TickCount - UpNext.LastBrowserClick) < 1000)
            {
                // Firefox steps in front for a moment after an up-next click and UpNext puts Dota back:
                // don't blink the widget meanwhile, just check again in a second
                var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                later.Tick += (s, e) => { later.Stop(); RecheckForeground(); UpdateVisibility(); };
                later.Start();
                return;
            }
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
        /// In the main menu: a visible music button (MenuButton setting) toggles the widget the same way.
        /// </summary>
        void UpdateVisibility() { UpdateVisibility(false); }

        void UpdateVisibility(bool animate)
        {
            bool dotaOk = dotaHwnd != IntPtr.Zero && Native.IsWindow(dotaHwnd) && !Native.IsIconic(dotaHwnd);
            bool onDota = dotaOk && (dotaFocused || !settings.OnlyWhenDotaFocused);
            bool ingame = preview || (inMatch && onDota);
            bool inmenu = !ingame && inMenu && onDota && settings.MenuButton;
            if (ingame || inmenu) SetLayout(inmenu);

            bool showButton = (ingame && settings.ClockButton) || inmenu;
            bool showWidget = inmenu ? widgetOpen : ingame && (settings.ClockButton
                ? widgetOpen
                : (preview || media.HasSession || !settings.HideWhenNoMedia));

            if (DebugLog.On)
            {
                string v = "inMatch=" + inMatch + " inMenu=" + inMenu + " dotaOk=" + dotaOk + " focused=" + dotaFocused
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
                CloseUpNext();
                overlay.HideOverlay(animate);
            }
        }

        /// <summary>Switch the button's look between the clock (match) and the main menu.</summary>
        void SetLayout(bool menu)
        {
            if (menu == menuLayout) return;
            menuLayout = menu;
            if (!preview) widgetOpen = false; // an open widget doesn't follow you from the menu into a match
            clockButton.SetMenuStyle(menu);
            overlay.SetGrowUp(menu);          // menu: unfolds upward from PLAY DOTA; match: drops down from the clock
        }

        void ToggleWidget()
        {
            widgetOpen = !widgetOpen;
            if (DebugLog.On) DebugLog.Write("btn  clicked -> widgetOpen=" + widgetOpen);
            UpdateVisibility(true);
        }

        // ---------------------------------------------------------------- idle auto-hide

        bool AutoHideActive
        {
            get { return (settings.ClockButton || menuLayout) && settings.AutoHideSeconds > 0 && !preview && overlayShown && widgetOpen; }
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
            if (overlay.IsInUse || upNextList.IsInUse) { idleTimer.Start(); return; } // pointer still on it: check again later
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
            if (menuLayout) { PlaceForMenu(r, hud); return; }
            double s = hud * settings.Scale;
            int w = (int)Math.Round(OverlayWindow.BaseWidth * s);
            int h = (int)Math.Round(OverlayWindow.BaseHeight * s);
            int x = r.Left + (r.Width - w) / 2;
            int y = r.Top + (int)Math.Round(settings.OffsetY * hud);
            PlaceWidget(x, y, w, h);

            // the button follows Dota's HUD only (not the widget's Scale)
            int bs = (int)Math.Round(HotspotWindow.BaseSize * hud);
            int bx = r.Left + (r.Width - bs) / 2;
            int by = r.Top + (int)Math.Round(settings.ButtonY * hud) - bs / 2;
            clockButton.Place(bx, by, bs);
        }

        /// <summary>Main menu: the button sits just left of PLAY DOTA; the widget stands on PLAY DOTA, just as wide.</summary>
        void PlaceForMenu(RECT r, double hud)
        {
            int right = r.Right - (int)Math.Round(PlayRight * hud);
            int w = (int)Math.Round(PlayWidth * hud * settings.Scale);
            int h = (int)Math.Round(w * OverlayWindow.BaseHeight / OverlayWindow.BaseWidth);
            PlaceWidget(right - w, r.Bottom - (int)Math.Round((PlayTop + 12) * hud) - h, w, h);

            int bs = (int)Math.Round(MenuButtonSize * hud);
            int bx = r.Right - (int)Math.Round((PlayRight + PlayWidth + 10) * hud) - bs;
            clockButton.Place(bx, r.Bottom - (int)Math.Round(PlayCenterY * hud) - bs / 2, bs);
        }

        void PlaceWidget(int x, int y, int w, int h)
        {
            widgetX = x; widgetY = y; widgetW = w; widgetH = h;
            overlay.Place(x, y, w, h);
            if (upNextOpen) PlaceUpNext();
        }

        /// <summary>Same width as the widget; below it in a match, above it in the main menu.</summary>
        void PlaceUpNext()
        {
            int h = upNextList.HeightFor(widgetW);
            int gap = (int)Math.Round(8 * lastRect.Height / 1080.0);
            upNextList.Place(widgetX, menuLayout ? widgetY - gap - h : widgetY + widgetH + gap, widgetW, h);
        }

        // ---------------------------------------------------------------- up next

        void ToggleUpNext()
        {
            RestartIdle();
            if (upNextOpen) { CloseUpNext(); return; }
            upNextOpen = true;
            upNextList.SetMessage("Loading…");
            PlaceUpNext();
            upNextList.ShowList();
            LoadUpNext();
        }

        /// <summary>Reads the list on a worker thread (UI Automation calls block for ~0.1-1 s).</summary>
        void LoadUpNext()
        {
            int token = ++upNextToken;
            string app = media.AppId, title = media.Title;
            upNextFor = title;
            System.Threading.Tasks.Task.Run(() =>
            {
                UpNext.Result r;
                try { r = upNext.Read(app, title); }
                catch (Exception ex) { Program.Log(ex); r = new UpNext.Result { Problem = "Couldn't read the list" }; }
                ui.BeginInvoke(new Action(() =>
                {
                    if (token != upNextToken || !upNextOpen) return;
                    if (r.Tracks.Count > 0) upNextList.SetTracks(r.Tracks);
                    else upNextList.SetMessage(r.Problem);
                    PlaceUpNext();
                }));
            });
        }

        void CloseUpNext()
        {
            if (!upNextOpen) return;
            upNextOpen = false;
            upNextToken++;
            upNextList.HideList();
        }

        void PlayFromUpNext(UpNext.Track t)
        {
            if (DebugLog.On) DebugLog.Write("next play \"" + t.Title + "\"");
            CloseUpNext();
            RestartIdle();
            System.Threading.Tasks.Task.Run(() =>
            {
                try { upNext.Play(t); } catch (Exception ex) { Program.Log(ex); }
            });
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
                if (sessionShown != media.SessionVersion) CloseUpNext(); // another app: its list would be wrong
                sessionShown = media.SessionVersion;
                volume.Bind(media.AppId);
                overlay.SetVolume(volume.Get());
            }
            overlay.SetUpNextAvailable(media.HasSession && UpNext.Supports(media.AppId));
            if (upNextOpen && media.Title != upNextFor) LoadUpNext();  // next song started: move the list on
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
            else if (inMenu) s = "In main menu";
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
            if (gsiSilence != null) gsiSilence.Dispose();
            if (gsi != null) gsi.Stop();
            if (tray != null) tray.Dispose();
        }
    }
}
