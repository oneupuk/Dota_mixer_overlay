# Dota Glass Overlay

See and control the song playing on your PC without leaving Dota 2. Click the sun/moon in the game clock
and a small glass player drops down: play/pause, skip, seek and volume. Works with Spotify, YouTube,
Apple Music and most other players. Needs Windows 10 or 11.

![The player under the Dota 2 clock](docs/hero.jpg)

## Install

1. **Download** [DotaGlassOverlay.zip](https://github.com/oneupuk/Dota_mixer_overlay/releases/latest/download/DotaGlassOverlay.zip)
   and unzip it to a folder you'll keep, for example `C:\DotaGlassOverlay`.
2. **Start it:** double-click `DotaGlassOverlay.exe`. If Windows says it protected your PC, click
   **More info → Run anyway**. A red music-note icon appears in the taskbar corner next to the clock
   (click **^** if you don't see it).
3. **Connect it to Dota:** right-click that icon → **Copy Steam launch option**. In Steam, right-click
   **Dota 2** → **Properties** → paste it into **Launch options**.
4. **In Dota:** Settings → Video → Display mode → **Borderless window**.

That's it. Start Dota from Steam as usual: the overlay starts and closes together with it.

Prefer to build it yourself? Click **Code → Download ZIP** on this page, unzip, and double-click
`build.cmd`. The exe appears in the `bin` folder. Nothing to install.

## Use

![1. Launch Dota from Steam. 2. Click the sun/moon. 3. Control your music.](docs/how-to-use.png)

- Click the **sun/moon** in the clock to open or close the player. It also closes by itself after 5 s.
- In Dota's **main menu**, click the **♪** button next to **PLAY DOTA**: the same player opens above it.
- **Up next** (SoundCloud in Chrome, Edge, Opera, Brave or Yandex): click the small arrow next to the song title to
  see the next songs, and click one to play it. The overlay reads SoundCloud's own "Next up" list through Windows'
  screen-reader interface: no browser extension, no account, nothing sent over the internet.
- Scroll the mouse wheel on the player to change the music's volume. Only the music app (for example
  your browser) gets quieter or louder, not Dota or the rest of your PC.

## Help

- **Nothing shows up in a match:** check that Launch options contain `-gamestateintegration` and Dota is
  in Borderless window mode, then restart Dota.
- **Moved the folder:** copy the launch option again (step 3), otherwise Steam can't start Dota.
- **Uninstall:** delete the launch option in Steam, right-click the tray icon → **Exit**, then delete the
  folder.

[MIT license](LICENSE) · Not affiliated with Valve.
