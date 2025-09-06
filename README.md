# MusicPlayerBingBong

### Play YouYube Music inside Peak

Open the Music Player by holding a BingBong and pausing the game.

#### Functionality:

* Play YouTube (and more) links, synced for everyone
* Queue with shuffle/next/remove/clear, per-user limits, and caching for quick replays
* Public enqueue \& controls (toggle) — non-holders can add songs and use transport/queue actions
* Global (2D) audio or 3D positional with adjustable max distance
* Shows current title and playback time
* Works even if the host doesn’t have the mod (acting-host fallback)
* Mouth animation for BingBong based on currently playing audio

#### Usage

* Paste a supported URL and press Add.
* Open Advanced to toggle Public Enqueue, Global Audio, Bypass Environment, distance, and UI scale.

Supports YouTube/YouTube Shorts, SoundCloud, Bandcamp, Twitch (clips), Vimeo, TikTok, Mixcloud.

#### Disclaimer

Mouth animation depends on the prefab having an Animator float parameter whose name contains “mouth”. If none exists due to future updates, it won’t animate until fixed.

Uses YT-DLP to download and ffmpeg (LGPL 2.1 license https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html) to convert clips, no warranties provided project is under MIT license https://mit-license.org/
YT-DLP repo: https://github.com/yt-dlp/yt-dlp
ffmpeg download source: https://ffmpeg.org/download.html

