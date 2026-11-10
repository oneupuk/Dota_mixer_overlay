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
    /// Supported for now: SoundCloud in Chromium browsers, whose pages expose their HTML classes to UI Automation.
    /// </summary>
    internal sealed class UpNext
    {
        public const int MaxItems = 5;

        public sealed class Track
        {
            public string Title = "", Artist = "", Duration = "";
            internal AutomationElement Row;
        }

        public sealed class Result
        {
            public readonly List<Track> Tracks = new List<Track>();
            public string Problem;            // shown instead of the list
        }

        /// <summary>Chromium browsers (Chrome, Edge, Opera, Brave, Yandex, Vivaldi).</summary>
        public static bool Supports(string aumid)
        {
            string a = (aumid ?? "").ToLowerInvariant();
            return a.Contains("chrome") || a.Contains("msedge") || a.Contains("opera") || a.Contains("brave")
                || a.Contains("yandex") || a.Contains("vivaldi");
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
                // the browser builds its accessibility view of the page on first request: give it a moment
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    root = Snapshot(hwnd);
                    if (root != null && Find(root, e => e.Cached.AutomationId == "RootWebArea") != null) break;
                    Thread.Sleep(800);
                }
                if (root == null) continue;
                if (Find(root, e => e.Cached.AutomationId == "RootWebArea") != null) sawPage = true;
                if (Find(root, e => HasClass(e, "playbackSoundBadge")) == null) continue;
                sawSoundCloud = true;

                if (Rows(root).Count == 0)
                {
                    // SoundCloud only loads "Next up" once its panel has been opened
                    if (!OpenQueuePanel(hwnd, currentTitle)) continue;
                    opened = true;
                    for (int i = 0; i < 8 && Rows(root).Count == 0; i++) { Thread.Sleep(250); root = Snapshot(hwnd) ?? root; }
                    Click(Find(root, e => HasClass(e, "queue__hide")));
                }
                Collect(Rows(root), result);
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
            return t != null && Click(t.Row);
        }

        // ---------------------------------------------------------------- SoundCloud's queue

        static List<AutomationElement> Rows(AutomationElement root)
        {
            var rows = new List<AutomationElement>();
            foreach (var e in All(root)) if (HasClass(e, "queueItemView")) rows.Add(e);
            return rows;
        }

        static void Collect(List<AutomationElement> rows, Result result)
        {
            int playing = rows.FindIndex(r => HasClass(r, "m-playing") || HasClass(r, "m-active"));
            for (int i = playing + 1; i < rows.Count && result.Tracks.Count < MaxItems; i++)
            {
                var r = rows[i];
                if (!HasClass(r, "m-upcoming")) continue;
                var t = new Track { Row = r };
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
        /// The queue icon in SoundCloud's play bar isn't offered as clickable to UI Automation, but Chrome runs the
        /// older MSAA "default action" on any element, which clicks it without the mouse or focus.
        /// The icon is the last child of the play bar's sound badge - the element holding the "&lt;title&gt;" and
        /// "Current track: &lt;title&gt;" links - which works in any page language.
        /// </summary>
        static bool OpenQueuePanel(IntPtr hwnd, string currentTitle)
        {
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

        static AutomationElement Find(AutomationElement root, Func<AutomationElement, bool> match)
        {
            foreach (var e in All(root)) if (match(e)) return e;
            return null;
        }

        /// <summary>Chromium reports an element's HTML class attribute as its UI Automation class name.</summary>
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
                if (cls.ToString() != "Chrome_WidgetWin_1") return true;
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
    }
}
