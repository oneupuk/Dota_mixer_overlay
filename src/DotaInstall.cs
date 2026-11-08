using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DotaGlass
{
    /// <summary>Finds the Dota 2 install and drops the Game State Integration config into it.</summary>
    internal static class DotaInstall
    {
        public const string CfgName = "gamestate_integration_glass_overlay.cfg";

        public static string FindDotaGameDir(string overridePath)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(overridePath)) candidates.Add(overridePath);

            // The library that holds appmanifest_570.acf is where Steam actually has Dota installed;
            // other folders (and the uninstall registry key) can point at stale leftovers.
            var libs = SteamLibraries();
            foreach (string lib in libs)
                if (File.Exists(Path.Combine(lib, @"steamapps\appmanifest_570.acf")))
                    candidates.Add(Path.Combine(lib, @"steamapps\common\dota 2 beta"));

            string uninstall = RegString(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 570", "InstallLocation");
            if (uninstall != null) candidates.Add(uninstall);

            foreach (string lib in libs)
                candidates.Add(Path.Combine(lib, @"steamapps\common\dota 2 beta"));

            foreach (string c in candidates)
            {
                try
                {
                    string game = Path.Combine(c, @"game\dota");
                    if (Directory.Exists(game)) return game;
                    if (Directory.Exists(Path.Combine(c, "cfg")) && File.Exists(Path.Combine(c, "gameinfo.gi"))) return c;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Writes the cfg if missing or outdated. Returns true when the file was (re)written.</summary>
        public static bool EnsureConfig(string dotaGameDir, int port, out string cfgPath)
        {
            cfgPath = Path.Combine(Path.Combine(dotaGameDir, @"cfg\gamestate_integration"), CfgName);
            string content =
                "\"Dota Glass Overlay\"\r\n" +
                "{\r\n" +
                "\t\"uri\"\t\t\"http://127.0.0.1:" + port + "/\"\r\n" +
                "\t\"timeout\"\t\"5.0\"\r\n" +
                "\t\"buffer\"\t\"0.1\"\r\n" +
                "\t\"throttle\"\t\"1.0\"\r\n" +
                // Dota doesn't post when a match starts; it reports it on the next heartbeat. Keep it short.
                "\t\"heartbeat\"\t\"1.0\"\r\n" +
                "\t\"data\"\r\n" +
                "\t{\r\n" +
                "\t\t\"provider\"\t\"1\"\r\n" +
                "\t\t\"map\"\t\t\"1\"\r\n" +
                "\t}\r\n" +
                "}\r\n";

            if (File.Exists(cfgPath) && File.ReadAllText(cfgPath) == content) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(cfgPath));
            File.WriteAllText(cfgPath, content);
            return true;
        }

        static IEnumerable<string> SteamLibraries()
        {
            var roots = new List<string>();
            string steam = RegString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")
                        ?? RegString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
                        ?? RegString(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            if (steam == null) return roots;

            steam = steam.Replace('/', '\\');
            roots.Add(steam);
            foreach (string vdf in new[] { Path.Combine(steam, @"steamapps\libraryfolders.vdf"), Path.Combine(steam, @"config\libraryfolders.vdf") })
            {
                try
                {
                    if (!File.Exists(vdf)) continue;
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                        roots.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
                catch { }
            }
            return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static string RegString(RegistryKey root, string path, string name)
        {
            try
            {
                using (var k = root.OpenSubKey(path))
                {
                    var v = k == null ? null : k.GetValue(name) as string;
                    return string.IsNullOrEmpty(v) ? null : v;
                }
            }
            catch { return null; }
        }
    }
}
