
# MusicPlayerBingBong
### Play YouYube Music inside Peak
Access MusicPlayer menu when holding BingBong in your inventory and pausing the game

#### Functionality:
- Play YouTube links synchronised among players from BingBong controlled with UI in pause menu
- Animate BingBong mouth based on currently playing music
- Set local volume of the music
- Work among players with mod installed even if host doesn't have the mod
- Keep cache of last played songs for quicker replay
- Display current title and playback time in pause menu
- Support for music queue

#### Disclaimer
Animated mouth is currently broken due to game update removing support :(
Will try to resolve in next updates if possible

Uses YT-DLP to download and ffmpeg (LGPL 2.1 license https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html) to convert clips, no warranties provided project is under MIT license https://mit-license.org/
YT-DLP repo: https://github.com/yt-dlp/yt-dlp
ffmpeg download source: https://ffmpeg.org/download.html
