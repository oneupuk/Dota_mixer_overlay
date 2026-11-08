using System;
using System.Diagnostics;
using System.IO;

namespace DotaGlass
{
    /// <summary>
    /// Steam launch-option mode:  "...\DotaGlassOverlay.exe" %command% -gamestateintegration
    /// Steam replaces %command% with Dota's own command line, so we start the game ourselves, run the
    /// overlay next to it, and quit when the game quits.
    /// </summary>
    internal static class GameLauncher
    {
        /// <summary>Starts the game if our command line is a Steam %command% wrapper call; otherwise null.
        /// Must run before we lower our own priority (children inherit below-normal priority).</summary>
        public static Process TryLaunch()
        {
            string rest = AfterFirstToken(Environment.CommandLine);
            string exe, args;
            if (!SplitFirstToken(rest, out exe, out args)) return null;
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) return null;

            PrepareGsiConfig(exe);
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,                         // inherit Steam's environment (SteamAppId etc.)
                WorkingDirectory = Environment.CurrentDirectory  // Steam already set the game's directory
            };
            return Process.Start(psi);
        }

        /// <summary>Dota reads GSI configs only at startup, so make sure ours is current before the game starts.</summary>
        static void PrepareGsiConfig(string gameExe)
        {
            try
            {
                var s = Settings.Load();
                // ...\dota 2 beta\game\bin\win64\dota2.exe  ->  ...\dota 2 beta\game\dota
                string dir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(gameExe), @"..\..\dota"));
                if (!Directory.Exists(Path.Combine(dir, "cfg"))) dir = DotaInstall.FindDotaGameDir(s.DotaPath);
                string cfg;
                if (dir != null) DotaInstall.EnsureConfig(dir, s.Port, out cfg);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public static string LaunchOption
        {
            get { return "\"" + System.Windows.Forms.Application.ExecutablePath + "\" %command% -gamestateintegration"; }
        }

        static string AfterFirstToken(string cmd)
        {
            string first, rest;
            return SplitFirstToken(cmd, out first, out rest) ? rest : "";
        }

        /// <summary>Windows program-name rules: a quoted token runs to the next quote, otherwise to whitespace.</summary>
        static bool SplitFirstToken(string s, out string token, out string rest)
        {
            token = rest = "";
            if (s == null) return false;
            s = s.TrimStart();
            if (s.Length == 0) return false;
            int end;
            if (s[0] == '"')
            {
                end = s.IndexOf('"', 1);
                if (end < 0) { token = s.Substring(1); return true; }
                token = s.Substring(1, end - 1);
                end++;
            }
            else
            {
                end = 0;
                while (end < s.Length && !char.IsWhiteSpace(s[end])) end++;
                token = s.Substring(0, end);
            }
            rest = end < s.Length ? s.Substring(end).TrimStart() : "";
            return token.Length > 0;
        }
    }
}
