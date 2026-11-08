using System;
using System.Globalization;
using System.IO;

namespace DotaGlass
{
    internal sealed class Settings
    {
        public int Port = 52190;
        public double OffsetY = 48;        // distance from the top of the game, in 1080p pixels
        public double Scale = 1.0;         // extra size multiplier on top of Dota's HUD scale
        public bool ShowInPreGame = true;  // show during the -1:30 pre-horn countdown too
        public bool HideWhenNoMedia = true;   // only used when ClockButton = false
        public bool ClockButton = true;       // click the sun/moon in Dota's clock to show/hide the widget
        public double ButtonY = 15.5;         // center of the sun/moon from the top, in 1080p pixels
        public double AutoHideSeconds = 5;    // close the widget after this long without interaction (0 = never)
        public bool OnlyWhenDotaFocused = true;
        public string Glass = "blur";      // blur | acrylic | none
        public string DotaPath = "";       // optional override, e.g. D:\Steam\steamapps\common\dota 2 beta

        public static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini"); }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) { s.Save(); return s; }
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    string line = raw;
                    int c = line.IndexOf(';');
                    if (c >= 0) line = line.Substring(0, c);
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    int i; double d;
                    switch (k)
                    {
                        case "port": if (int.TryParse(v, out i) && i > 0 && i < 65536) s.Port = i; break;
                        case "offsety": if (TryDouble(v, out d)) s.OffsetY = d; break;
                        case "scale": if (TryDouble(v, out d) && d > 0.2 && d < 5) s.Scale = d; break;
                        case "showinpregame": s.ShowInPreGame = Bool(v, s.ShowInPreGame); break;
                        case "hidewhennomedia": s.HideWhenNoMedia = Bool(v, s.HideWhenNoMedia); break;
                        case "clockbutton": s.ClockButton = Bool(v, s.ClockButton); break;
                        case "buttony": if (TryDouble(v, out d)) s.ButtonY = d; break;
                        case "autohideseconds": if (TryDouble(v, out d) && d >= 0) s.AutoHideSeconds = d; break;
                        case "onlywhendotafocused": s.OnlyWhenDotaFocused = Bool(v, s.OnlyWhenDotaFocused); break;
                        case "glass": s.Glass = v.ToLowerInvariant(); break;
                        case "dotapath": s.DotaPath = v.Trim('"'); break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(FilePath, string.Format(CultureInfo.InvariantCulture,
@"; Dota Glass Overlay settings  (tray icon -> Reload settings after editing)

; Local port Dota's Game State Integration posts to. The GSI .cfg is rewritten automatically.
Port={0}
; Gap between the top of the game and the overlay, in 1080p pixels (scaled to your resolution).
OffsetY={1}
; Size multiplier. 1.0 = sized to match Dota's HUD.
Scale={2}
; 1 = also show during the pre-game countdown (-1:30 .. 0:00)
ShowInPreGame={3}
; 1 = the sun/moon in Dota's clock is a button that shows/hides the widget
; 0 = the widget shows by itself for the whole match
ClockButton={8}
; Vertical center of the sun/moon, in 1080p pixels (nudge if the button is off on your HUD)
ButtonY={9}
; Close the widget after this many seconds without touching it (0 = stay open until you click the sun/moon)
AutoHideSeconds={10}
; (ClockButton=0 only) 1 = hide the widget when no app is playing media
HideWhenNoMedia={4}
; 1 = hide when you alt-tab out of Dota
OnlyWhenDotaFocused={5}
; blur    = rounded live glass (Windows 11 composition backdrop; plain accent blur on Windows 10)
; acrylic = Windows acrylic accent (square-ish corners)   none = tint only, no blur
Glass={6}
; Optional: Dota folder if auto-detect fails, e.g. D:\SteamLibrary\steamapps\common\dota 2 beta
DotaPath={7}
", Port, OffsetY, Scale, ShowInPreGame ? 1 : 0, HideWhenNoMedia ? 1 : 0, OnlyWhenDotaFocused ? 1 : 0, Glass, DotaPath, ClockButton ? 1 : 0, ButtonY, AutoHideSeconds));
            }
            catch { }
        }

        static bool TryDouble(string v, out double d)
        {
            return double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        static bool Bool(string v, bool fallback)
        {
            v = v.ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off") return false;
            return fallback;
        }
    }
}
