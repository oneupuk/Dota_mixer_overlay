using System;
using System.IO;

namespace DotaGlass
{
    /// <summary>
    /// Opt-in timing log: create an empty "trace.log" next to the exe and restart the overlay.
    /// Off by default, so there is no cost in normal use.
    /// </summary>
    internal static class DebugLog
    {
        static readonly string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trace.log");
        static readonly object gate = new object();
        public static readonly bool On = File.Exists(path);

        public static void Write(string line)
        {
            if (!On) return;
            lock (gate)
            {
                try { File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + "\r\n"); }
                catch { }
            }
        }
    }
}
