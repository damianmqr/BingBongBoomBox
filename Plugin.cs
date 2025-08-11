using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.Networking;
using Zorro.Core;

#nullable enable

namespace BingBongPlayer
{
    [BepInPlugin("com.damianmqr.bingbongplayer", "BingBongBoomBox", "1.0.2")]
    public class BingBongPlayerPlugin : BaseUnityPlugin
    {
        private static ManualLogSource? logger;
        private static AudioSource? audioSource;
        private static PhotonView? photonView;

        private string? ytDlpPath;
        private string? ffmpegPath;
        private string youtubeUrl = "";

        private Coroutine? currentPlaybackCoroutine;

        private const ushort BingBongItemID = 13;
        private const int MaxAudioSizeMb = 100;
        private const int MaxTempFiles = 5;

        private float syncInterval = 5f;
        private float lastSyncTime = 0f;

        public float vocalLow = 300f;
        public float vocalHigh = 3400f;
        public int sampleSize = 256;
        public float currentVolume = 0.45f;
        private ConfigEntry<float>? volumeSetting;
        public FFTWindow fftWindow = FFTWindow.BlackmanHarris;
        private bool lastConnectedState = false;

        private float[]? spectrumData;
        private string? tempAudioDir;

        private string currentSongTitle = "";
        private string currentSongHash = "";
        private string lastUsedPlayer = "";
        private string userId = Guid.NewGuid().ToString();

        private enum SongLoadingState
        {
            Loaded,
            Loading,
            Error,
        }
        private SongLoadingState songLoadingState = SongLoadingState.Loaded;
        public static readonly string[] LoadingProgress = { "Loading.", "Loading..", "Loading...", "Loading" };
        private bool hostHasMod = PhotonNetwork.IsMasterClient;

        private class SongMetadata
        {
            public string? title;
        }

        private bool ActAsHost()
        {
            return PhotonNetwork.IsMasterClient || (!hostHasMod && lastUsedPlayer == userId);
        }

        private void Awake()
        {
            logger = Logger;

            photonView = gameObject.AddComponent<PhotonView>();
            photonView.ViewID = 215151321;

            spectrumData = new float[sampleSize];

            var pluginDir = Path.GetDirectoryName(Info.Location);
            ytDlpPath = Path.Combine(pluginDir, "yt-dlp.exe");
            ffmpegPath = Path.Combine(pluginDir, "ffmpeg.exe");
            tempAudioDir = Path.Combine(Path.GetTempPath(), "BingBongAudio");
            Directory.CreateDirectory(tempAudioDir);
            CleanupOldTempFiles();

            if (!File.Exists(ytDlpPath)) logger.LogError("yt-dlp.exe not found!");
            if (!File.Exists(ffmpegPath)) logger.LogError("ffmpeg.exe not found!");

            volumeSetting = Config.Bind("Audio", "Volume", 0.45f, "Default playback volume (0.0 to 1.0)");
            currentVolume = volumeSetting.Value;

            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.spatialBlend = 1f;
            audioSource.volume = currentVolume;
            audioSource.loop = false;
        }
        private float spectrumTimer = 0f;
        private float spectrumInterval = 0.05f;
        private void LateUpdate()
        {
            var connectedToGameServer = PhotonNetwork.Server == ServerConnection.GameServer;
            if (lastConnectedState && !connectedToGameServer)
            {
                Logger.LogInfo("Disconnected from game, resetting audio");
                StopAndResetSong();
            }

            lastConnectedState = connectedToGameServer;
            if (audioSource == null || !PhotonNetwork.IsConnectedAndReady || photonView == null) return;

            if (ActAsHost() && Time.time - lastSyncTime > syncInterval)
            {
                photonView.RPC(nameof(RPC_SyncPlaying), RpcTarget.Others, currentSongHash, audioSource.isPlaying, PhotonNetwork.IsMasterClient);
                if (audioSource.isPlaying)
                {
                    photonView.RPC(nameof(RPC_SyncTime), RpcTarget.Others, currentSongHash, audioSource.time);
                }
                lastSyncTime = Time.time;
            }

            var playingCinematic = Singleton<PeakHandler>.Instance?.isPlayingCinematic ?? false;
            var bingBong = BingBong.Instance;
            Vector3? position = null;

            if (bingBong != null)
            {
                position = bingBong.transform.position;
            } else
            {
                position = FindPlayerWithBingBong()?.character?.Center;
            }

            audioSource.volume = position.HasValue ? currentVolume : 0f;
            audioSource.spatialBlend = playingCinematic ? 0f : 1f;

            if (position.HasValue)
                audioSource.transform.position = position.Value;

            if (BingBong.Instance?.BingBongsVisuals != null)
            {
                spectrumTimer += Time.deltaTime;
                if (spectrumTimer >= spectrumInterval)
                {
                    spectrumTimer = 0f;
                    if (audioSource.isPlaying && spectrumData != null)
                    {
                        audioSource.GetSpectrumData(spectrumData, 0, fftWindow);
                        float freqResolution = AudioSettings.outputSampleRate / 2f / sampleSize;

                        int minIndex = Mathf.FloorToInt(vocalLow / freqResolution);
                        int maxIndex = Mathf.CeilToInt(vocalHigh / freqResolution);

                        float sum = 0f;
                        for (int i = minIndex; i <= maxIndex && i < sampleSize; i++)
                            sum += spectrumData[i];

                        float avg = sum / (maxIndex - minIndex + 1);
                        BingBong.Instance.BingBongsVisuals.mouthOpen = Mathf.Clamp01(avg * 50f);
                    }
                    else
                    {
                        BingBong.Instance.BingBongsVisuals.mouthOpen = 0f;
                    }
                }
            }
        }
        Player? FindPlayerWithBingBong()
        {
            foreach (var player in PlayerHandler.GetAllPlayers())
            {
                if (player.character != null && player.HasInAnySlot(BingBongItemID))
                {
                    return player;
                }
            }
            return null;
        }

        /* ==== UI Section ==== */

        private GUIStyle? boxStyle, labelStyle, buttonStyle, sliderStyle, sliderThumbStyle, headerStyle;
        private Texture2D? boxBgTex;
        private Texture2D? sliderBgTex;
        private Texture2D? sliderThumbTex;
        private Texture2D? buttonNormalBgTex;
        private Texture2D? buttonHoverBgTex;
        private bool initializedStyles;
        
        private Texture2D MakeSolidColorTex(Color col)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, col);
            tex.Apply();
            return tex;
        }

        private void InitStyles()
        {
            if (initializedStyles) return;

            Font? labelFont = null;
            foreach (var font in Resources.FindObjectsOfTypeAll<Font>())
            {
                switch (font.name)
                {
                    case "DarumaDropOne-Regular":
                        labelFont = font;
                        break;
                }
            }

            if (labelFont == null && !PhotonNetwork.IsConnectedAndReady)
                return;

            boxBgTex = MakeSolidColorTex(new Color(0f, 0f, 0f, 0.65f));

            boxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 10, 10),
                margin = new RectOffset(10, 10, 10, 10),
                normal = { background = boxBgTex }
            };

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                normal = { textColor = Color.white },
                richText = true,
            };

            headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                normal = { textColor = Color.white },
                richText = true,
            };

            buttonNormalBgTex = MakeSolidColorTex(new Color(0.2f, 0.2f, 0.2f, 0.95f));
            buttonHoverBgTex = MakeSolidColorTex(new Color(0.3f, 0.3f, 0.3f, 0.95f));
            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13,
                margin = new RectOffset(5, 5, 5, 5),
                normal = { background = buttonNormalBgTex },
                hover = { background = buttonHoverBgTex },
            };

            sliderBgTex = MakeSolidColorTex(new Color(0.15f, 0.15f, 0.15f, 1f));
            sliderThumbTex = MakeSolidColorTex(new Color(1f, 0.6f, 0f, 1f));
            sliderStyle = new GUIStyle(GUI.skin.horizontalSlider)
            {
                normal = { background = sliderBgTex },
                fixedHeight = 12,
            };

            sliderThumbStyle = new GUIStyle(GUI.skin.horizontalSliderThumb)
            {
                normal = { background = sliderThumbTex },
                fixedWidth = 16,
                fixedHeight = 16,
            };

            if (labelFont != null)
            {
                labelStyle.font = labelFont;
                headerStyle.font = labelFont;
                buttonStyle.font = labelFont;
            }

            initializedStyles = true;
        }

        private void OnGUI()
        {
            if (Player.localPlayer == null || photonView == null) return;

            InitStyles();

            if (!initializedStyles) return;

            if (GUIManager.instance?.pauseMenu?.activeSelf != true)
            {
                if (Player.localPlayer.HasInAnySlot(BingBongItemID))
                {
                    GUILayout.BeginArea(new Rect(10, 10, 400, 100));
                    GUILayout.Label("<color=#FFD700AA><b>Press [ESC] to access Music Player</b></color>", labelStyle);
                    GUILayout.EndArea();
                }
                return;
            }
            GUILayout.BeginArea(new Rect(10, 10, 400, 300), boxStyle);
            GUILayout.Label("<color=#FFD700><b>BingBong Player</b></color>", headerStyle);

            if (Player.localPlayer.HasInAnySlot(BingBongItemID))
            {
                GUILayout.Label("Video Url:", labelStyle);
                GUILayout.BeginHorizontal();
                youtubeUrl = GUILayout.TextField(youtubeUrl, GUILayout.Width(330), GUILayout.Height(25));
                GUILayout.Space(5);
                if (GUILayout.Button("✕", buttonStyle, GUILayout.Width(25), GUILayout.Height(25)))
                    youtubeUrl = "";
                GUILayout.EndHorizontal();
                GUILayout.Space(5);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("◎ Play", buttonStyle)) PlayNewSong(youtubeUrl);
                if (GUILayout.Button("■ Stop", buttonStyle))
                    photonView.RPC(nameof(RPC_StopAudio), RpcTarget.All);
                if (GUILayout.Button("〈 -10s", buttonStyle))
                    photonView.RPC(nameof(RPC_Rewind), RpcTarget.All, 10f);
                if (GUILayout.Button("+10s 〉", buttonStyle))
                    photonView.RPC(nameof(RPC_Forward), RpcTarget.All, 10f);
                GUILayout.EndHorizontal();
                GUILayout.Space(10);
            }

            GUILayout.Label($"Local Volume: {Mathf.RoundToInt(currentVolume * 100)}%", labelStyle);
            float newVolume = GUILayout.HorizontalSlider(currentVolume, 0f, 1f, sliderStyle, sliderThumbStyle, GUILayout.Width(360));
            if (Mathf.Abs(newVolume - currentVolume) > 0.001f)
            {
                currentVolume = newVolume;
                if (volumeSetting != null)
                {
                    volumeSetting.Value = newVolume;
                }
            }


            if (songLoadingState == SongLoadingState.Loading)
            {
                GUILayout.Space(8);
                GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1));
                GUILayout.Space(8);

                int idx = Mathf.FloorToInt(Time.time) % LoadingProgress.Length;
                GUILayout.Label(LoadingProgress[idx], labelStyle);
            }
            else if (songLoadingState == SongLoadingState.Error)
            {
                GUILayout.Space(8);
                GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1));
                GUILayout.Space(8);

                GUILayout.Label("<color=#FFD700>ERROR! Couldn't load audio.</color>", labelStyle);
            }
            else if (!string.IsNullOrEmpty(currentSongTitle) && audioSource?.clip != null && audioSource.isPlaying)
            {
                GUILayout.Space(8);
                GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1));
                GUILayout.Space(8);

                GUILayout.Label($"Now Playing: {currentSongTitle}", labelStyle);

                int currentMin = Mathf.FloorToInt(audioSource.time / 60f);
                int currentSec = Mathf.FloorToInt(audioSource.time % 60f);
                int totalMin = Mathf.FloorToInt(audioSource.clip.length / 60f);
                int totalSec = Mathf.FloorToInt(audioSource.clip.length % 60f);

                GUILayout.Label($"{currentMin:D2}:{currentSec:D2} / {totalMin:D2}:{totalSec:D2}", labelStyle);
            }

            GUILayout.EndArea();
        }

        /* ==== Audio Management Section ==== */
        private void PlayNewSong(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !IsValidYoutubeUrl(url)) return;

            photonView?.RPC(nameof(RPC_PlayAudio), RpcTarget.All, url, userId);
        }

        private Coroutine? debouncePlayCoroutine = null;
        private Coroutine? downloadCoroutine = null;

        private IEnumerator DebouncePlayCoroutine(float delay, string url, string userId)
        {
            yield return new WaitForSecondsRealtime(delay);
            PlayAudio(url, userId);
        }

        [PunRPC]
        private void RPC_PlayAudio(string url, string userId)
        {
            logger?.LogInfo($"Got Play Audio {url} {userId}");
            if (string.IsNullOrWhiteSpace(url) || !IsValidYoutubeUrl(url)) return;
            if (debouncePlayCoroutine != null)
            {
                Logger.LogWarning("Got spam play, debouncing");
                StopCoroutine(debouncePlayCoroutine);
                debouncePlayCoroutine = null;
            }
            debouncePlayCoroutine = StartCoroutine(DebouncePlayCoroutine(1f, url, userId));
        }

        void PlayAudio(string url, string userId)
        {
            debouncePlayCoroutine = null;
            if (string.IsNullOrWhiteSpace(url) || !IsValidYoutubeUrl(url)) return;
            lastUsedPlayer = userId;
            string hash = GetSafeSongId(url);
            logger?.LogInfo($"Playing audio with hash: {hash}");
            currentSongHash = hash;
            string path = Path.Combine(tempAudioDir, $"bingbong_{hash}.wav");
            string metadataPath = Path.Combine(tempAudioDir, $"bingbong_{hash}.json");
            if (File.Exists(metadataPath))
            {
                var json = File.ReadAllText(metadataPath);
                currentSongTitle = JsonUtility.FromJson<SongMetadata>(json)?.title ?? "Unknown";
            }

            if (currentPlaybackCoroutine != null)
                StopCoroutine(currentPlaybackCoroutine);

            if (File.Exists(path))
            {
                currentPlaybackCoroutine = StartCoroutine(LoadAndPlay(path));
            }
            else
            {
                if (downloadCoroutine != null)
                {
                    StopCoroutine(downloadCoroutine);
                    downloadCoroutine = null;
                }

                downloadCoroutine = StartCoroutine(DownloadAudio(url, path, metadataPath));
            }
        }

        private IEnumerator DownloadAudio(string url, string outputPath, string metadataPath)
        {
            CleanupOldTempFiles();
            songLoadingState = SongLoadingState.Loading;
            string title = "Unknown";

            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--no-playlist");
            psi.ArgumentList.Add($"--max-filesize");
            psi.ArgumentList.Add($"{MaxAudioSizeMb}M");
            psi.ArgumentList.Add("--extractor-args");
            psi.ArgumentList.Add("youtube:player_client=tv_simply,default,-tv");
            psi.ArgumentList.Add("-x");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("bestaudio[abr<=128]/bestaudio[abr<=160]/bestaudio");
            psi.ArgumentList.Add("--ffmpeg-location");
            psi.ArgumentList.Add(ffmpegPath);
            psi.ArgumentList.Add("--audio-format");
            psi.ArgumentList.Add("wav");
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("title-current:%(title)s");
            psi.ArgumentList.Add("--no-simulate");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outputPath);
            psi.ArgumentList.Add(url);
            var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    logger?.LogInfo($"ytdlp-log: {e.Data}");
                    if (e.Data.StartsWith("title-current:"))
                        title = e.Data.Substring("title-current:".Length).Trim();
                }
            };
            proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) logger?.LogError(e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            while (!proc.HasExited)
                yield return null;

            if (File.Exists(outputPath))
            {
                currentSongTitle = title;
                var metadata = new SongMetadata { title = title };
                File.WriteAllText(metadataPath, JsonUtility.ToJson(metadata));
                currentPlaybackCoroutine = StartCoroutine(LoadAndPlay(outputPath));
            }
            else
            {
                songLoadingState = SongLoadingState.Error;
                logger?.LogError("Download failed or file missing.");
            }
            downloadCoroutine = null;
        }

        private string GetSafeSongId(string url)
        {
            try
            {
                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();

                if (host.Contains("youtu.be"))
                {
                    var id = uri.AbsolutePath.Trim('/');
                    if (id.Length >= 11 && id.All(char.IsLetterOrDigit))
                        return id;
                }

                if (host.Contains("youtube.com"))
                {
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    var id = query["v"];
                    if (!string.IsNullOrEmpty(id) && id.Length >= 11 && id.All(c=> char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                        return id;
                }
            }
            catch
            {
                logger?.LogError("Failed to extract id, falling back to hash");
            }

            using var sha = System.Security.Cryptography.SHA1.Create();
            var hashBytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
            return BitConverter.ToString(hashBytes, 0, 4).Replace("-", "");
        }

        private IEnumerator LoadAndPlay(string path)
        {
            if (audioSource == null)
            {
                logger?.LogError("Failed to load audio: No audio source");
                songLoadingState = SongLoadingState.Error;
                yield break;
            }
            songLoadingState = SongLoadingState.Loaded;
            using var www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.WAV);
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                songLoadingState = SongLoadingState.Error;
                logger?.LogError("Failed to load audio: " + www.error);
                yield break;
            }

            var clip = DownloadHandlerAudioClip.GetContent(www);
            audioSource.clip = clip;
            if (ActAsHost())
            {
                audioSource.Play();
            }
        }

        [PunRPC]
        private void RPC_StopAudio()
        {
            logger?.LogInfo($"Got Stop Audio");
            if (audioSource != null)
            {
                audioSource.Stop();
            }
        }

        [PunRPC]
        private void RPC_Rewind(float seconds)
        {
            logger?.LogInfo($"Got Stop Rewind");
            if (audioSource == null || audioSource.clip == null || seconds < 0f)
                return;
            audioSource.time = Mathf.Max(0f, audioSource.time - seconds);
        }
        

        [PunRPC]
        private void RPC_Forward(float seconds)
        {
            logger?.LogInfo($"Got Forward");
            if (audioSource == null || audioSource.clip == null || seconds < 0f)
                return;
            audioSource.time = Mathf.Min(audioSource.clip.length, audioSource.time + seconds);
        }

        [PunRPC]
        private void RPC_SyncTime(string songHash, float hostTime)
        {
            logger?.LogInfo($"Got Sync time {songHash} ${hostTime}");
            if (!ActAsHost() && audioSource != null && audioSource.clip != null && songHash == currentSongHash)
            {
                if (Mathf.Abs(audioSource.time - hostTime) > 1.5f && hostTime >= 0f && hostTime < audioSource.clip.length)
                {
                    audioSource.time = hostTime;
                }
            }
            else
            {
                logger?.LogWarning($"Invalid sync time received: {hostTime}");
            }
        }

        [PunRPC]
        private void RPC_SyncPlaying(string songHash, bool hostIsPlaying, bool isOwner)
        {

            logger?.LogInfo($"Got Sync Playing {songHash}");
            if (isOwner)
            {
                hostHasMod = true;
            }

            if (!ActAsHost() && audioSource != null)
            {
                if (!audioSource.isPlaying && hostIsPlaying && audioSource.clip != null && songHash == currentSongHash)
                {
                    audioSource.Play();
                }
                if (audioSource.isPlaying && (!hostIsPlaying || songHash != currentSongHash))
                {
                    audioSource.Stop();
                }
            }
        }

        private void StopAndResetSong()
        {
            if (currentPlaybackCoroutine != null)
                StopCoroutine(currentPlaybackCoroutine);
            audioSource?.Stop();
            songLoadingState = SongLoadingState.Loaded;
            currentSongTitle = "";
            currentSongHash = "";
            lastUsedPlayer = "";
            hostHasMod = PhotonNetwork.IsMasterClient;
        }

        private void CleanupOldTempFiles()
        {
            if (string.IsNullOrEmpty(tempAudioDir) || !Directory.Exists(tempAudioDir))
                return;

            var wavFiles = new DirectoryInfo(tempAudioDir)
                .GetFiles("bingbong_*.wav")
                .OrderByDescending(f => f.LastAccessTime)
                .ToList();

            for (var i = MaxTempFiles; i < wavFiles.Count; i++)
            {
                var wav = wavFiles[i];
                try
                {
                    wav.Delete();
                    logger?.LogInfo($"Deleted old audio file: {wav.Name}");
                }
                catch (Exception e)
                {
                    logger?.LogWarning($"Failed to delete {wav.Name}: {e.Message}");
                }

                var jsonPath = Path.ChangeExtension(wav.FullName, ".json");
                if (File.Exists(jsonPath))
                {
                    try
                    {
                        File.Delete(jsonPath);
                        logger?.LogInfo($"Deleted matching metadata: {Path.GetFileName(jsonPath)}");
                    }
                    catch (Exception e)
                    {
                        logger?.LogWarning($"Failed to delete {Path.GetFileName(jsonPath)}: {e.Message}");
                    }
                }
            }
        }

        private bool IsValidYoutubeUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return false;
            var host = uri.Host.ToLowerInvariant();
            return host == "youtube.com" || host == "www.youtube.com" || host == "youtu.be" || host == "www.youtu.be";
        }
    }

}