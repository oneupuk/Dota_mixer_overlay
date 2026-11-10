using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI.Composition;

namespace DotaGlass
{
    /// <summary>
    /// Real rounded glass on Windows 11. A bare Win32 window (no redirection bitmap - it draws nothing itself)
    /// hosts a Windows.UI.Composition visual painted with the host backdrop brush - DWM's blurred copy of
    /// whatever is behind it (the game) - clipped to an anti-aliased rounded rect of any radius.
    /// The WPF content window is owned by this one, so it always sits directly on top.
    /// All of the glass is rendered by DWM on the GPU; this process does no per-frame work for it.
    /// </summary>
    internal sealed class GlassBackdrop
    {
        static IntPtr dispatcherQueueController; // must outlive the compositor
        static ushort windowClass;

        public IntPtr Hwnd { get; private set; }
        Compositor compositor;
        CompositionTarget target;
        SpriteVisual visual;
        CompositionRoundedRectangleGeometry shape;
        int curW, curH;

        /// <summary>Animate out of the bottom edge instead of the top (widget standing on PLAY DOTA).</summary>
        public bool GrowUp;

        public static bool Supported
        {
            get { return Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000; }
        }

        public static GlassBackdrop TryCreate()
        {
            if (!Supported) return null;
            IntPtr hwnd = IntPtr.Zero;
            try
            {
                hwnd = CreateHostWindow();
                if (hwnd == IntPtr.Zero) return null;

                int on = 1, noRound = 1, noBorder = unchecked((int)0xFFFFFFFE);
                DwmSetWindowAttribute(hwnd, 17 /* DWMWA_USE_HOSTBACKDROPBRUSH */, ref on, 4);
                DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref noRound, 4);
                DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref noBorder, 4);
                Native.ExtendGlass(hwnd); // DWM only produces a host backdrop for "glass" windows

                EnsureDispatcherQueue();
                var c = new Compositor();
                var interop = (ICompositorDesktopInterop)(object)c;
                IntPtr raw;
                if (interop.CreateDesktopWindowTarget(hwnd, true, out raw) != 0 || raw == IntPtr.Zero)
                {
                    DestroyWindow(hwnd);
                    return null;
                }
                object t = Marshal.GetObjectForIUnknown(raw);
                Marshal.Release(raw);

                var b = new GlassBackdrop { Hwnd = hwnd, compositor = c, target = (CompositionTarget)t };
                b.visual = c.CreateSpriteVisual();
                b.visual.RelativeSizeAdjustment = Vector2.One;
                b.visual.Brush = c.CreateHostBackdropBrush();
                b.shape = c.CreateRoundedRectangleGeometry();
                b.visual.Clip = c.CreateGeometricClip(b.shape);
                b.target.Root = b.visual;
                return b;
            }
            catch
            {
                if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
                return null;
            }
        }

        /// <summary>Physical pixels.</summary>
        public void Place(int x, int y, int w, int h, double radius)
        {
            SetWindowPos(Hwnd, new IntPtr(-1) /* HWND_TOPMOST */, x, y, w, h, 0x0010 /* SWP_NOACTIVATE */);
            if (w == curW && h == curH) return;
            curW = w; curH = h;
            shape.Size = new Vector2(w, h);
            shape.CornerRadius = new Vector2((float)radius, (float)radius);
        }

        // ---- the same drop-down / fold-up as the WPF content, run by the compositor on the GPU

        public void AnimateIn(int ms)
        {
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.45f), new Vector2(0.64f, 1f));
            Animate(new Vector3(0.88f, 0.55f, 1f), Vector3.One, 0f, 1f, ms, ease);
        }

        public void AnimateOut(int ms)
        {
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.5f, 0f), new Vector2(0.75f, 0f));
            Animate(Vector3.One, new Vector3(0.94f, 0.7f, 1f), 1f, 0f, ms, ease);
        }

        public void ResetVisual()
        {
            visual.StopAnimation("Scale");
            visual.StopAnimation("Opacity");
            visual.Scale = Vector3.One;
            visual.Opacity = 1f;
        }

        void Animate(Vector3 s0, Vector3 s1, float o0, float o1, int ms, CompositionEasingFunction ease)
        {
            visual.CenterPoint = new Vector3(curW / 2f, GrowUp ? curH : 0f, 0f); // grow out of the clock above / PLAY DOTA below
            var scale = compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0f, s0);
            scale.InsertKeyFrame(1f, s1, ease);
            scale.Duration = TimeSpan.FromMilliseconds(ms);
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, o0);
            fade.InsertKeyFrame(1f, o1);
            fade.Duration = TimeSpan.FromMilliseconds(Math.Min(ms, 160));
            visual.StartAnimation("Scale", scale);
            visual.StartAnimation("Opacity", fade);
        }

        public void Show() { ShowWindow(Hwnd, 4 /* SW_SHOWNOACTIVATE */); }
        public void Hide() { ShowWindow(Hwnd, 0 /* SW_HIDE */); }

        static IntPtr CreateHostWindow()
        {
            IntPtr inst = GetModuleHandle(null);
            if (windowClass == 0)
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf(typeof(WNDCLASSEX)),
                    // no managed WndProc needed: the window only hosts a composition visual
                    lpfnWndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
                    hInstance = inst,
                    hCursor = LoadCursor(IntPtr.Zero, new IntPtr(32512) /* IDC_ARROW */),
                    lpszClassName = "DotaGlassBackdrop"
                };
                windowClass = RegisterClassEx(ref wc);
                if (windowClass == 0) return IntPtr.Zero;
            }
            const int WS_POPUP = unchecked((int)0x80000000);
            const int exStyle = 0x00000008 /* TOPMOST */ | 0x00000080 /* TOOLWINDOW */
                              | 0x08000000 /* NOACTIVATE */ | 0x00200000 /* NOREDIRECTIONBITMAP */;
            return CreateWindowEx(exStyle, "DotaGlassBackdrop", "Dota Glass Backdrop", WS_POPUP,
                -32000, -32000, 1, 1, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        }

        static void EnsureDispatcherQueue()
        {
            if (dispatcherQueueController != IntPtr.Zero) return;
            var opts = new DispatcherQueueOptions
            {
                dwSize = Marshal.SizeOf(typeof(DispatcherQueueOptions)),
                threadType = 2,     // DQTYPE_THREAD_CURRENT
                apartmentType = 2   // DQTAT_COM_STA
            };
            IntPtr controller;
            if (CreateDispatcherQueueController(opts, out controller) == 0) dispatcherQueueController = controller;
        }

        // ---------------------------------------------------------------- interop

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public int cbSize, style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string lpszMenuName, lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DispatcherQueueOptions { public int dwSize; public int threadType; public int apartmentType; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowEx(int exStyle, string cls, string title, int style, int x, int y, int w, int h,
                                            IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr inst, IntPtr name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("CoreMessaging.dll")] static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ICompositorDesktopInterop
        {
            [PreserveSig] int CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.U1)] bool isTopmost, out IntPtr target);
        }
    }
}
