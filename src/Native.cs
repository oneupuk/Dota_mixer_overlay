using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DotaGlass
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public bool Same(RECT o) { return Left == o.Left && Top == o.Top && Right == o.Right && Bottom == o.Bottom; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }

    internal static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int MA_NOACTIVATE = 3;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;

        public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

        /// <summary>Topmost tool window that never takes focus from the game, even when clicked.</summary>
        public static void MakeNoActivateToolWindow(System.Windows.Window w, IntPtr hwnd)
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            System.Windows.Interop.HwndSource.FromHwnd(hwnd).AddHook(NoActivateHook);
        }

        static IntPtr NoActivateHook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE) { handled = true; return new IntPtr(MA_NOACTIVATE); }
            return IntPtr.Zero;
        }

        // ---------------------------------------------------------------- game window helpers

        public static bool GetClientScreenRect(IntPtr hwnd, out RECT r)
        {
            r = new RECT();
            if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out r)) return false;
            var p = new POINT();
            if (!ClientToScreen(hwnd, ref p)) return false;
            r.Left += p.X; r.Right += p.X; r.Top += p.Y; r.Bottom += p.Y;
            return r.Width > 0 && r.Height > 0;
        }

        public static string GetProcessFileName(uint pid)
        {
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? System.IO.Path.GetFileName(sb.ToString()) : null;
            }
            finally { CloseHandle(h); }
        }

        public static string GetTitle(IntPtr hwnd)
        {
            var sb = new StringBuilder(64);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        // ---------------------------------------------------------------- glass (DWM backdrop blur)

        [StructLayout(LayoutKind.Sequential)]
        struct ACCENT_POLICY { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }

        [StructLayout(LayoutKind.Sequential)]
        struct WINCOMPATTRDATA { public int Attribute; public IntPtr Data; public int SizeOfData; }

        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPATTRDATA data);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Fallback glass (Windows 10, or Glass=acrylic): accent blur behind the whole window.
        /// The accent blur ignores window regions, so the only way to round it is DWM's own (fixed ~8px) rounding.</summary>
        public static void ApplyAccentGlass(IntPtr hwnd, string mode)
        {
            int state = mode == "none" ? 0 : mode == "acrylic" ? 4 : 3;
            var accent = new ACCENT_POLICY { AccentState = state, GradientColor = 0x18101010 /* AABBGGRR */ };
            int size = Marshal.SizeOf(typeof(ACCENT_POLICY));
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, p, false);
                var data = new WINCOMPATTRDATA { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = p, SizeOfData = size };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            catch { }
            finally { Marshal.FreeHGlobal(p); }
        }

        /// <summary>Windows 11 DWM corner rounding (2 = round, 1 = none) and no DWM border.</summary>
        public static void SetDwmCorners(IntPtr hwnd, bool round)
        {
            try
            {
                int pref = round ? 2 : 1; DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, 4);
                int noBorder = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref noBorder, 4);
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MARGINS { public int Left, Right, Top, Bottom; }

        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

        /// <summary>"Sheet of glass": DWM honours the client area's per-pixel alpha on a normal (non-layered) window.</summary>
        public static void ExtendGlass(IntPtr hwnd)
        {
            var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            try { DwmExtendFrameIntoClientArea(hwnd, ref m); } catch { }
        }

        // ---------------------------------------------------------------- stay out of the game's way

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

        [DllImport("kernel32.dll")]
        static extern bool SetProcessInformation(IntPtr hProcess, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

        /// <summary>EcoQoS (efficiency cores / low clocks on hybrid CPUs) + below-normal priority,
        /// so the overlay never competes with Dota for CPU time.</summary>
        public static void EnterEfficiencyMode()
        {
            try
            {
                var st = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = 1 }; // EXECUTION_SPEED
                SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref st, Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)));
            }
            catch { }
            try { using (var p = Process.GetCurrentProcess()) p.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch { }
        }
    }
}
