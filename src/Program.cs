using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

// Opt into .NET 4.8 runtime behaviours (per-monitor DPI etc.); csc.exe doesn't add this by itself.
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: System.Reflection.AssemblyTitle("Dota Glass Overlay")]
[assembly: System.Reflection.AssemblyProduct("Dota Glass Overlay")]
[assembly: System.Reflection.AssemblyVersion("1.1.1.0")]

namespace DotaGlass
{
    internal static class Program
    {
#if TESTINSTANCE
        const string MutexName = @"Local\DotaGlassOverlay.TestInstance"; // dev builds can run beside the real one
#else
        const string MutexName = @"Local\DotaGlassOverlay.SingleInstance";
#endif

        [STAThread]
        static int Main(string[] args)
        {
            // Started by Steam as a %command% wrapper? Launch Dota first, at normal priority.
            Process game = null;
            try { game = GameLauncher.TryLaunch(); }
            catch (Exception ex) { Log(ex); }

            bool first;
            using (var mutex = new Mutex(true, MutexName, out first))
            {
                if (!first)
                {
                    // overlay already running (e.g. "Start with Windows"): just keep Steam's game session alive
                    if (game != null) game.WaitForExit();
                    return 0;
                }
                RunOverlay(args, game);
            }
            return 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)] // keeps WPF from loading in the wait-only path above
        static void RunOverlay(string[] args, Process game)
        {
            Native.EnterEfficiencyMode();
            // A ~400x80 px surface redrawn ~1x/sec is cheaper on the CPU than a D3D device +
            // GPU readback, and keeps the GPU 100% for Dota.
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) => { Log(e.Exception); e.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log(e.ExceptionObject as Exception);

            var controller = new Controller(args, game);
            app.Startup += (s, e) => controller.Start();
            app.Run();
            controller.Dispose();
        }

        internal static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "overlay.log"),
                    DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine);
            }
            catch { }
        }
    }
}
