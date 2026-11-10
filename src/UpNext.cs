using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using Accessibility;

namespace DotaGlass
{
    /// <summary>
    /// The music app's own "Next up" list, read through Windows UI Automation - the interface screen readers use -
    /// so it needs no browser extension, no API and no network. It only runs when the list is opened, on a worker
    /// thread, and leaves the music app as it was: if the queue panel has to be opened to load the songs, it's
    /// closed again right away (SoundCloud keeps the loaded songs readable after that).
    /// Supported for now: SoundCloud in Chromium browsers and Firefox, whose pages expose their HTML classes to
    /// UI Automation. Chromium shows only each window's front tab; Firefox shows every loaded tab.
    /// </summary>
    internal sealed class UpNext
    {
        public const int MaxItems = 5;

        public sealed class Track
        {
            public string Title = "", Artist = "", Duration = "";
            internal AutomationElement Row;
            internal IntPtr Window;           // the browser window it's in
        }

        public sealed class Result
        {
            public readonly List<Track> Tracks = new List<Track>();
            public string Problem;            // shown instead of the list
        }

        /// <summary>Chromium browsers (Chrome, Edge, Opera, Brave, Yandex, Vivaldi) and Firefox (a hashed AUMID).</summary>
        public static bool Supports(string aumid)
        {
            string a = (aumid ?? "").ToLowerInvariant();
            return a.Contains("chrome") || a.Contains("msedge") || a.Contains("opera") || a.Contains("brave")
                || a.Contains("yandex") || a.Contains("vivaldi") || a.Contains("firefox") || MediaController.IsHex(aumid ?? "");
        }

        /// <summary>Worker thread. Up to MaxItems songs after the one playing now.</summary>
        public Result Read(string aumid, string currentTitle)
        {
            var sw = Stopwatch.StartNew();
            var result = new Result();
            bool sawPage = false, sawSoundCloud = false, opened = false;
            foreach (IntPtr hwnd in BrowserWindows(aumid, currentTitle))
            {
                AutomationElement root = null;
                // the browser builds its accessibility view of the page on first request, and Firefox fills in the
                // page's HTML classes only a moment after that: give it a moment
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    root = Snapshot(hwnd);
                    if (root != null && PagesReady(root)) break;
                    Thread.Sleep(700);
                }
                if (root == null) continue;
                if (Pages(root).Count > 0) sawPage = true;
                // the SoundCloud page showing this song (another tab may be playing something else)
                var page = SoundCloudPage(root, currentTitle);
                if (page == null) continue;
                sawSoundCloud = true;

                var rows = Rows(page);
                if (Current(rows, currentTitle) < 0)
                {
                    // SoundCloud only loads "Next up" once its panel has been opened, and doesn't keep it up to date
                    // while the panel is closed: open it (again) and wait until it shows this song
                    if (OpenQueuePanel(hwnd, page, currentTitle))
                    {
                        opened = true;
                        for (int i = 0; i < 8 && Current(rows, currentTitle) < 0; i++)
                        {
                            Thread.Sleep(250);
                            var fresh = Snapshot(hwnd);
                            page = (fresh == null ? null : SoundCloudPage(fresh, currentTitle)) ?? page;
                            rows = Rows(page);
                        }
                        ClickInBackground(Find(page, e => HasClass(e, "queue__hide")), hwnd);
                    }
                    else if (rows.Count == 0) continue;
                }
                int playing = Current(rows, currentTitle);
                Collect(rows, playing >= 0 ? playing : Marked(rows), hwnd, result);
                if (DebugLog.On)
                    DebugLog.Write("next read " + result.Tracks.Count + " songs in " + sw.ElapsedMilliseconds + " ms (opened panel: " + opened + ")");
                if (result.Tracks.Count == 0) result.Problem = "Nothing up next";
                return result;
            }
            result.Problem = sawSoundCloud ? "Couldn't read SoundCloud's Next up"
                           : sawPage ? "Up next works with SoundCloud for now"
                           : "Bring the music tab to the front of its browser window";
            if (DebugLog.On) DebugLog.Write("next read failed in " + sw.ElapsedMilliseconds + " ms: " + result.Problem);
            return result;
        }

        /// <summary>Worker thread. Plays a song from the list, like clicking it in the app.</summary>
        public bool Play(Track t)
        {
            return t != null && ClickInBackground(t.Row, t.Window);
        }

        // ---------------------------------------------------------------- SoundCloud's queue

        /// <summary>The page whose play bar shows this song, or null. Without a title, any SoundCloud page.</summary>
        static AutomationElement SoundCloudPage(AutomationElement root, string title)
        {
            title = (title ?? "").Trim();
            foreach (var page in Pages(root))
            {
                var badge = Find(page, e => HasClass(e, "playbackSoundBadge"));
                if (badge == null) continue;
                if (title.Length == 0 || Find(badge, e => IsTitle(e.Cached.Name, title)) != null) return page;
            }
            return null;
        }

        /// <summary>"title" itself or a label ending in it ("Current track: title"), in any page language.</summary>
        static bool IsTitle(string name, string title)
        {
            name = (name ?? "").Trim();
            return string.Equals(name, title, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(": " + title, StringComparison.OrdinalIgnoreCase);
        }

        static List<AutomationElement> Rows(AutomationElement root)
        {
            var rows = new List<AutomationElement>();
            foreach (var e in All(root)) if (HasClass(e, "queueItemView")) rows.Add(e);
            return rows;
        }

        /// <summary>
        /// Where the song playing now is in SoundCloud's list, or -1 if the list doesn't show it (not loaded yet, or
        /// out of date). "m-playing" marks the song playing now; "m-active" the song the list was started from, which
        /// keeps it even after jumping ahead to a later song. Paused, there is no "m-playing": the title tells.
        /// </summary>
        static int Current(List<AutomationElement> rows, string title)
        {
            title = (title ?? "").Trim();
            int i = Marked(rows);
            if (i >= 0 && (title.Length == 0 || IsTitle(RowTitle(rows[i]), title))) return i;
            if (title.Length == 0) return -1;
            return rows.FindIndex(r => HasClass(r, "m-upcoming") && IsTitle(RowTitle(r), title));
        }

        /// <summary>The row SoundCloud marks as playing, or else as where the list was started; -1 if none.</summary>
        static int Marked(List<AutomationElement> rows)
        {
            int i = rows.FindIndex(r => HasClass(r, "m-playing"));
            return i >= 0 ? i : rows.FindIndex(r => HasClass(r, "m-active"));
        }

        static string RowTitle(AutomationElement row)
        {
            var e = Find(row, d => HasClass(d, "queueItemView__title"));
            return e == null ? "" : FirstName(e);
        }

        /// <summary>The songs after the playing one (at index "playing"; -1 = from the top of the list).</summary>
        static void Collect(List<AutomationElement> rows, int playing, IntPtr window, Result result)
        {
            for (int i = playing + 1; i < rows.Count && result.Tracks.Count < MaxItems; i++)
            {
                var r = rows[i];
                if (!HasClass(r, "m-upcoming") || HasClass(r, "m-playing")) continue;
                var t = new Track { Row = r, Window = window };
                foreach (var e in All(r))
                {
                    if (HasClass(e, "queueItemView__username")) t.Artist = e.Cached.Name;
                    else if (HasClass(e, "queueItemView__title")) t.Title = FirstName(e);
                    else if (HasClass(e, "queueItemView__duration")) t.Duration = FirstName(e);
                }
                if (t.Title.Length > 0) result.Tracks.Add(t);
            }
        }

        static string FirstName(AutomationElement e)
        {
            foreach (var d in All(e)) if (!string.IsNullOrEmpty(d.Cached.Name)) return d.Cached.Name.Trim();
            return "";
        }

        /// <summary>
        /// Firefox offers the queue icon in SoundCloud's play bar as a button. Chrome doesn't offer it as clickable
        /// to UI Automation, but runs the older MSAA "default action" on any element, which clicks it without the
        /// mouse or focus. The icon is the last child of the play bar's sound badge - the element holding the
        /// "&lt;title&gt;" and "Current track: &lt;title&gt;" links - which works in any page language.
        /// </summary>
        static bool OpenQueuePanel(IntPtr hwnd, AutomationElement page, string currentTitle)
        {
            if (ClickInBackground(Find(page, e => HasClass(e, "playbackSoundBadge__showQueue")), hwnd)) return true;
            try
            {
                object o;
                Guid iid = IID_IAccessible;
                if (AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, ref iid, out o) != 0 || o == null) return false;
                var badge = FindBadge((IAccessible)o, currentTitle ?? "", 0);
                if (badge == null) return false;
                var kids = Children(badge);
                kids[kids.Count - 1].accDoDefaultAction(0);
                return true;
            }
            catch { return false; }
        }

        static IAccessible FindBadge(IAccessible node, string title, int depth)
        {
            if (depth > 60) return null;
            var kids = Children(node);
            if (kids.Count >= 4)
            {
                bool exact = false, labelled = false;
                foreach (var k in kids)
                {
                    string n = Name(k);
                    if (title.Length > 0 && n == title) exact = true;
                    else if (title.Length > 0 ? n.EndsWith(": " + title) : n.StartsWith("Current track:")) labelled = true;
                }
                if (labelled && (exact || title.Length == 0)) return node;
            }
            foreach (var k in kids) { var r = FindBadge(k, title, depth + 1); if (r != null) return r; }
            return null;
        }

        // ---------------------------------------------------------------- UI Automation helpers

        /// <summary>The whole window in one cross-process call (~0.1 s for a SoundCloud page).</summary>
        static AutomationElement Snapshot(IntPtr hwnd)
        {
            try
            {
                var cr = new CacheRequest { TreeScope = TreeScope.Subtree, AutomationElementMode = AutomationElementMode.Full };
                cr.TreeFilter = Automation.RawViewCondition;
                cr.Add(AutomationElement.NameProperty);
                cr.Add(AutomationElement.ClassNameProperty);
                cr.Add(AutomationElement.AutomationIdProperty);
                cr.Add(AutomationElement.ControlTypeProperty);
                using (cr.Activate()) return AutomationElement.FromHandle(hwnd);
            }
            catch { return null; }
        }

        static IEnumerable<AutomationElement> All(AutomationElement e)
        {
            yield return e;
            AutomationElementCollection kids;
            try { kids = e.CachedChildren; } catch { yield break; }
            foreach (AutomationElement c in kids)
                foreach (var d in All(c)) yield return d;
        }

        /// <summary>A web page: Chromium's "RootWebArea", Firefox's document. Frames inside a page stay part of it.</summary>
        static bool IsPage(AutomationElement e)
        {
            return e.Cached.AutomationId == "RootWebArea" || e.Cached.ControlType == ControlType.Document;
        }

        static List<AutomationElement> Pages(AutomationElement root)
        {
            var pages = new List<AutomationElement>();
            AddPages(root, pages);
            return pages;
        }

        static void AddPages(AutomationElement e, List<AutomationElement> pages)
        {
            if (IsPage(e)) { pages.Add(e); return; }
            AutomationElementCollection kids;
            try { kids = e.CachedChildren; } catch { return; }
            foreach (AutomationElement c in kids) AddPages(c, pages);
        }

        /// <summary>A page is there and its HTML classes are readable (Firefox adds them a moment after the page).</summary>
        static bool PagesReady(AutomationElement root)
        {
            foreach (var page in Pages(root))
                if (Find(page, e => !string.IsNullOrEmpty(e.Cached.ClassName)) != null) return true;
            return false;
        }

        static AutomationElement Find(AutomationElement root, Func<AutomationElement, bool> match)
        {
            foreach (var e in All(root)) if (match(e)) return e;
            return null;
        }

        /// <summary>Chromium and Firefox report an element's HTML class attribute as its UI Automation class name.</summary>
        static bool HasClass(AutomationElement e, string cls)
        {
            string c = e.Cached.ClassName;
            if (string.IsNullOrEmpty(c)) return false;
            foreach (string part in c.Split(' ')) if (part == cls) return true;
            return false;
        }

        static bool Click(AutomationElement e)
        {
            if (e == null) return false;
            try
            {
                object p;
                if (!e.TryGetCurrentPattern(InvokePattern.Pattern, out p)) return false;
                ((InvokePattern)p).Invoke();
                return true;
            }
            catch { return false; }
        }

        /// <summary>Environment.TickCount of the last click in a browser that may bring it to the front for a moment.</summary>
        public static volatile int LastBrowserClick = Environment.TickCount - 60000;

        /// <summary>
        /// Clicks a page element and keeps the game in front. Firefox treats an accessibility click like a real mouse
        /// click, and one on an element that can't take focus (SoundCloud's queue rows, its "Next up" icon) brings its
        /// window to the front. The overlay just got the user's click, so Windows lets it put back the window that was
        /// in front (Dota) - about 60 ms later.
        /// </summary>
        static bool ClickInBackground(AutomationElement e, IntPtr browser)
        {
            if (e == null) return false;
            IntPtr front = Native.GetForegroundWindow();
            bool watch = front != IntPtr.Zero && front != browser && IsFirefox(browser);
            if (watch) LastBrowserClick = Environment.TickCount;
            if (!Click(e)) return false;
            // Firefox acts on the click a few ms later, in the page's own process
            for (int i = 0; watch && i < 25; i++)
            {
                Thread.Sleep(20);
                if (Native.GetForegroundWindow() != browser) continue;
                SetForegroundWindow(front);
                if (DebugLog.On) DebugLog.Write("next browser came to the front, put back " + front.ToString("X"));
                break;
            }
            return true;
        }

        static bool IsFirefox(IntPtr hwnd)
        {
            var cls = new StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            return cls.ToString() == "MozillaWindowClass";
        }

        /// <summary>The browser's visible top-level windows playing this app's audio; the one titled after the song first.</summary>
        static List<IntPtr> BrowserWindows(string aumid, string currentTitle)
        {
            var first = new List<IntPtr>();
            var rest = new List<IntPtr>();
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() != "Chrome_WidgetWin_1" && cls.ToString() != "MozillaWindowClass") return true;
                string title = Native.GetTitle(h);
                if (title.Length == 0) return true;
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                string exe = Native.GetProcessFileName(pid);
                if (exe == null || !AppVolume.Matches(aumid, exe)) return true;
                if (!string.IsNullOrEmpty(currentTitle) && title.IndexOf(currentTitle, StringComparison.OrdinalIgnoreCase) >= 0) first.Add(h);
                else rest.Add(h);
                return true;
            }, IntPtr.Zero);
            first.AddRange(rest);
            return first;
        }

        // ---------------------------------------------------------------- MSAA interop

        static readonly Guid IID_IAccessible = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        const uint OBJID_CLIENT = 0xFFFFFFFC;

        [DllImport("oleacc.dll")]
        static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object acc);
        [DllImport("oleacc.dll")]
        static extern int AccessibleChildren(IAccessible parent, int start, int count, [Out] object[] children, out int obtained);

        static List<IAccessible> Children(IAccessible node)
        {
            var list = new List<IAccessible>();
            int n;
            try { n = node.accChildCount; } catch { return list; }
            if (n <= 0) return list;
            var arr = new object[n];
            int got;
            if (AccessibleChildren(node, 0, n, arr, out got) != 0) return list;
            for (int i = 0; i < got; i++) { var a = arr[i] as IAccessible; if (a != null) list.Add(a); }
            return list;
        }

        static string Name(IAccessible a)
        {
            try { return a.get_accName(0) ?? ""; } catch { return ""; }
        }

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    }
}
