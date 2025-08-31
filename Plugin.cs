using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.Networking;
using Zorro.Core;

#nullable enable

namespace BingBongPlayer
{
    [BepInPlugin("com.damianmqr.bingbongplayer", "BingBongBoomBox", "1.1.1")]
    public class BingBongPlayerPlugin : BaseUnityPlugin
    {
        private static ManualLogSource? logger;
        private static AudioSource? audioSource;
        private static PhotonView? photonView;

        private string? ytDlpPath;
        private string? ffmpegPath;
        private string urlInput = "";

        private Coroutine? currentPlaybackCoroutine;
        private Coroutine? debouncePlayCoroutine = null;
        private Coroutine? downloadCoroutine = null;

        private const ushort BingBongItemID = 13;
        public static Transform? bingBong = null;

        private ConfigEntry<float>? volumeSetting;
        private ConfigEntry<int>? maxAudioSizeMbCfg;
        private ConfigEntry<int>? maxTempFilesCfg;
        private ConfigEntry<int>? maxQueueSizeCfg;
        private ConfigEntry<int>? perUserQueueLimitCfg;
        private ConfigEntry<bool>? allowDuplicatesCfg;
        private ConfigEntry<bool>? autoAdvanceCfg;
        private ConfigEntry<int>? titleProbeTimeoutSecCfg;
        private ConfigEntry<int>? playlistProbeTimeoutSecCfg;

        private int MaxAudioSizeMb => Mathf.Max(1, maxAudioSizeMbCfg?.Value ?? 100);
        private int MaxTempFiles => Mathf.Max(1, maxTempFilesCfg?.Value ?? 5);
        private int MaxQueueSize => Mathf.Max(1, maxQueueSizeCfg?.Value ?? 20);
        private int PerUserQueueLimit => Mathf.Max(1, perUserQueueLimitCfg?.Value ?? 5);
        private bool AllowDuplicates => allowDuplicatesCfg?.Value ?? false;
        private bool AutoAdvance => autoAdvanceCfg?.Value ?? true;
        private int TitleProbeTimeoutSec => Mathf.Max(5, titleProbeTimeoutSecCfg?.Value ?? 20);
        private int PlaylistProbeTimeoutSec => Mathf.Max(5, playlistProbeTimeoutSecCfg?.Value ?? 30);

        private float syncInterval = 5f;
        private float lastSyncTime = 0f;

        public float vocalLow = 300f;
        public float vocalHigh = 3400f;
        public int sampleSize = 256;
        public float currentVolume = 0.45f;
        public FFTWindow fftWindow = FFTWindow.BlackmanHarris;

        private bool lastConnectedState = false;
        private float[]? spectrumData;
        private string? tempAudioDir;
        private string currentSongTitle = "";
        private string currentSongHash = "";
        private string lastUsedPlayer = "";
        private string userId = Guid.NewGuid().ToString();
        private bool manualStop = false;

        private enum SongLoadingState { Loaded, Loading, Error }
        private SongLoadingState songLoadingState = SongLoadingState.Loaded;
        public static readonly string[] LoadingProgress = { "Loading.", "Loading..", "Loading...", "Loading" };
        private bool hostHasMod = PhotonNetwork.IsMasterClient;

        private class SongMetadata { public string? title; }

        [Serializable]
        private class SongRequest
        {
            public string url = "";
            public string hash = "";
            public string requestedBy = "";
            public string title = "";
        }

        [Serializable]
        private class SongQueueWrapper { public List<SongRequest> items = new(); }

        private readonly List<SongRequest> queue = new();
        private Vector2 queueScroll = Vector2.zero;
        private readonly HashSet<string> titleFetchInProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private bool ActAsHost()
        {
            return PhotonNetwork.IsMasterClient || (!hostHasMod && lastUsedPlayer == userId);
        }

        private int LocalActor() => PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : -1;

        private int GetCurrentHolderActorNumber()
        {
            try
            {
                foreach (var p in PlayerHandler.GetAllPlayers())
                {
                    if (p == null || p.character == null) continue;
                    if (!p.HasInAnySlot(BingBongItemID)) continue;

                    int actor = -1;
                    try { actor = p.character?.photonView?.OwnerActorNr ?? -1; } catch { }
                    if (actor <= 0)
                    {
                        try { actor = p.photonView?.OwnerActorNr ?? -1; } catch { }
                    }
                    if (actor > 0) return actor;
                }
            }
            catch (Exception e)
            {
                logger?.LogWarning($"GetCurrentHolderActorNumber failed: {e.Message}");
            }
            return -1;
        }

        private bool IsRequestorHolder(int requestorActorNumber)
        {
            var holder = GetCurrentHolderActorNumber();
            return holder > 0 && requestorActorNumber == holder;
        }

        private void Awake()
        {
            logger = Logger;

            photonView = gameObject.AddComponent<PhotonView>();
            photonView.ViewID = 215151321;

            spectrumData = new float[sampleSize];

            var pluginDir = Path.GetDirectoryName(Info.Location) ?? Paths.PluginPath;
            ytDlpPath = Path.Combine(pluginDir, "yt-dlp.exe");
            ffmpegPath = Path.Combine(pluginDir, "ffmpeg.exe");
            tempAudioDir = Path.Combine(Path.GetTempPath(), "BingBongAudio");

            Directory.CreateDirectory(tempAudioDir);

            CleanupOldTempFiles();

            if (!File.Exists(ytDlpPath)) logger?.LogError("yt-dlp.exe not found!");
            if (!File.Exists(ffmpegPath)) logger?.LogError("ffmpeg.exe not found!");

            volumeSetting = Config.Bind("Audio", "Volume", 0.45f, "Default playback volume (0.0 to 1.0)");
            currentVolume = Mathf.Clamp01(volumeSetting.Value);

            maxAudioSizeMbCfg = Config.Bind("Limits", "MaxAudioSizeMB", 100, "Max file size yt-dlp may download.");
            maxTempFilesCfg = Config.Bind("Limits", "MaxTempFiles", 5, "How many cached WAVs to keep.");
            maxQueueSizeCfg = Config.Bind("Limits", "MaxQueueSize", 20, "Max items allowed in queue.");
            perUserQueueLimitCfg = Config.Bind("Limits", "PerUserQueueLimit", 5, "Max items one user can have pending in the queue.");
            allowDuplicatesCfg = Config.Bind("Queue", "AllowDuplicates", false, "Allow the same URL to be enqueued multiple times.");
            autoAdvanceCfg = Config.Bind("Queue", "AutoAdvance", true, "Automatically advance to next track when one finishes.");
            titleProbeTimeoutSecCfg = Config.Bind("Advanced", "TitleProbeTimeoutSec", 20, "Timeout for title probes (seconds).");
            playlistProbeTimeoutSecCfg = Config.Bind("Advanced", "PlaylistProbeTimeoutSec", 30, "Timeout for playlist expansion (seconds).");

            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.spatialBlend = 1f;
            audioSource.volume = currentVolume;
            audioSource.loop = false;

            new Harmony("com.damianmqr.bingbongplayer").PatchAll();
        }


        private void OnDestroy()
        {
            if (currentPlaybackCoroutine != null) StopCoroutine(currentPlaybackCoroutine);
            if (debouncePlayCoroutine != null) StopCoroutine(debouncePlayCoroutine);
            if (downloadCoroutine != null) StopCoroutine(downloadCoroutine);
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
                queue.Clear();
            }
            lastConnectedState = connectedToGameServer;

            if (audioSource == null || !PhotonNetwork.IsConnectedAndReady || photonView == null) return;

            if (ActAsHost() && Time.time - lastSyncTime > syncInterval)
            {
                photonView.RPC(nameof(RPC_SyncPlaying), RpcTarget.Others, currentSongHash, audioSource.isPlaying, PhotonNetwork.IsMasterClient, currentSongTitle);
                if (audioSource.isPlaying)
                {
                    photonView.RPC(nameof(RPC_SyncTime), RpcTarget.Others, currentSongHash, audioSource.time);
                }
                lastSyncTime = Time.time;
            }

            var playingCinematic = Singleton<PeakHandler>.Instance?.isPlayingCinematic ?? false;
            Vector3? position = null;
            if (bingBong != null) position = bingBong.position;
            else position = FindPlayerWithBingBong()?.character?.Center;

            audioSource.volume = position.HasValue ? currentVolume : 0f;
            audioSource.spatialBlend = playingCinematic ? 0f : 1f;
            if (position.HasValue) audioSource.transform.position = position.Value;
            /*
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
                        for (int i = minIndex; i <= maxIndex && i < sampleSize; i++) sum += spectrumData[i];
                        float avg = sum / Mathf.Max(1, (maxIndex - minIndex + 1));
                        BingBong.Instance.BingBongsVisuals.mouthOpen = Mathf.Clamp01(avg * 50f);
                    }
                    else
                    {
                        BingBong.Instance.BingBongsVisuals.mouthOpen = 0f;
                    }
                }
            }
            */

            if (ActAsHost() && AutoAdvance && !manualStop && audioSource.clip != null && !audioSource.isPlaying && !string.IsNullOrEmpty(currentSongHash) && songLoadingState == SongLoadingState.Loaded)
            {
                PlayNextImpl();
            }
        }

        Player? FindPlayerWithBingBong()
        {
            foreach (var player in PlayerHandler.GetAllPlayers())
            {
                if (player.character != null && player.HasInAnySlot(BingBongItemID))
                    return player;
            }
            return null;
        }

        private GUIStyle? boxStyle, labelStyle, buttonStyle, sliderStyle, sliderThumbStyle, headerStyle, smallLabelStyle;
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
                if (font.name == "DarumaDropOne-Regular") { labelFont = font; break; }
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

            smallLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
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
                smallLabelStyle.font = labelFont;
                headerStyle.font = labelFont;
                buttonStyle.font = labelFont;
            }

            initializedStyles = true;
        }

        private float CalcDesiredHeight()
        {
            int baseLines = 15;
            int perItem = 2;
            int dyn = baseLines + Mathf.Clamp(queue.Count * perItem, 4, 36);
            return dyn * 18f + 60f;
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
                    GUILayout.BeginArea(new Rect(10, 10, 420, 100));
                    GUILayout.Label("<b>Press [ESC] to access Music Player</b>", labelStyle);
                    GUILayout.EndArea();
                }
                return;
            }

            float areaW = Mathf.Min(560f, Screen.width - 20f);
            float desiredH = CalcDesiredHeight();
            float areaH = Mathf.Min(desiredH, Screen.height - 20f);
            Rect area = new Rect(10, 10, areaW, areaH);

            GUILayout.BeginArea(area, boxStyle);
            GUILayout.Label("【BingBong Player】", headerStyle);

            GUILayout.Label(ActAsHost()
                ? "※ You are the acting host."
                : "※ Waiting for host…", smallLabelStyle);

            bool isLocalHolder = Player.localPlayer.HasInAnySlot(BingBongItemID);

            if (isLocalHolder)
            {
                GUILayout.Space(4);
                GUILayout.Label("URL:", labelStyle);
                GUILayout.BeginHorizontal();
                urlInput = GUILayout.TextField(urlInput, GUILayout.Width(areaW - 100), GUILayout.Height(25));
                if (GUILayout.Button("Clear", buttonStyle, GUILayout.Width(55), GUILayout.Height(25))) urlInput = "";
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Add »", buttonStyle, GUILayout.Height(26))) RequestEnqueue(urlInput);
                if (GUILayout.Button("Stop", buttonStyle, GUILayout.Height(26))) photonView.RPC(nameof(RPC_StopAudio), RpcTarget.All);
                if (GUILayout.Button("〈〈 10s", buttonStyle, GUILayout.Height(26))) photonView.RPC(nameof(RPC_Rewind), RpcTarget.All, 10f);
                if (GUILayout.Button("10s 〉〉", buttonStyle, GUILayout.Height(26))) photonView.RPC(nameof(RPC_Forward), RpcTarget.All, 10f);
                if (GUILayout.Button("Next »", buttonStyle, GUILayout.Height(26))) photonView.RPC(nameof(RPC_RequestNext), RpcTarget.All, LocalActor());
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(8);
            GUILayout.Label($"Local Volume: {Mathf.RoundToInt(currentVolume * 100)}%", labelStyle);
            float newVolume = GUILayout.HorizontalSlider(currentVolume, 0f, 1f, sliderStyle, sliderThumbStyle, GUILayout.Width(areaW - 40));
            if (Mathf.Abs(newVolume - currentVolume) > 0.001f)
            {
                currentVolume = newVolume;
                if (volumeSetting != null) volumeSetting.Value = newVolume;
            }

            if (songLoadingState == SongLoadingState.Loading)
            {
                GUILayout.Space(8); GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1)); GUILayout.Space(8);
                int idx = Mathf.FloorToInt(Time.time) % LoadingProgress.Length;
                GUILayout.Label(LoadingProgress[idx], labelStyle);
            }
            else if (songLoadingState == SongLoadingState.Error)
            {
                GUILayout.Space(8); GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1)); GUILayout.Space(8);
                GUILayout.Label("ERROR! Couldn't load audio.", labelStyle);
            }
            else if (!string.IsNullOrEmpty(currentSongTitle) && audioSource?.clip != null && (audioSource.isPlaying || audioSource.time > 0f))
            {
                GUILayout.Space(8); GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1)); GUILayout.Space(8);
                GUILayout.Label($"Now Playing: 「{currentSongTitle}」", labelStyle);
                int currentMin = Mathf.FloorToInt(audioSource.time / 60f);
                int currentSec = Mathf.FloorToInt(audioSource.time % 60f);
                int totalMin = Mathf.FloorToInt(audioSource.clip.length / 60f);
                int totalSec = Mathf.FloorToInt(audioSource.clip.length % 60f);
                GUILayout.Label($"{currentMin:D2}:{currentSec:D2} / {totalMin:D2}:{totalSec:D2}", labelStyle);
            }

            GUILayout.Space(8); GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1)); GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"【Queue】 ({queue.Count}/{MaxQueueSize})", labelStyle);
            GUILayout.FlexibleSpace();
            if (isLocalHolder)
            {
                if (GUILayout.Button("〜 Shuffle", buttonStyle, GUILayout.Height(24)))
                    photonView.RPC(nameof(RPC_RequestShuffle), RpcTarget.All, LocalActor());
                if (GUILayout.Button("[ ] Clear", buttonStyle, GUILayout.Height(24)))
                    photonView.RPC(nameof(RPC_RequestClearQueue), RpcTarget.All, LocalActor());
            }

            GUILayout.EndHorizontal();

            float minQueue = 100f, maxQueue = Mathf.Max(160f, areaH * 0.40f);
            float estRow = 22f;
            float desiredQueue = Mathf.Clamp(queue.Count * estRow + 6f, minQueue, maxQueue);

            queueScroll = GUILayout.BeginScrollView(queueScroll, GUILayout.Height(desiredQueue), GUILayout.Width(areaW - 20));
            for (int i = 0; i < queue.Count; i++)
            {
                var it = queue[i];
                string display = string.IsNullOrEmpty(it.title) ? "Fetching title…" : it.title;
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{i + 1}. • {display} ({ShortId(it.requestedBy)})", smallLabelStyle, GUILayout.Width(areaW - 140));
                bool canRemove = isLocalHolder || it.requestedBy == userId;
                GUI.enabled = canRemove;
                if (GUILayout.Button("Remove", buttonStyle, GUILayout.Width(70)))
                    photonView.RPC(nameof(RPC_RequestRemoveAt), RpcTarget.All, i, userId, LocalActor());
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            GUILayout.EndArea();
        }

        private string ShortId(string id) => id.Length <= 6 ? id : id.Substring(0, 6);

        private void RequestEnqueue(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !IsSupportedMediaUrl(url)) return;
            if (debouncePlayCoroutine != null) { StopCoroutine(debouncePlayCoroutine); debouncePlayCoroutine = null; }
            debouncePlayCoroutine = StartCoroutine(DebounceRequestCoroutine(0.4f, url));
        }

        private IEnumerator DebounceRequestCoroutine(float delay, string url)
        {
            yield return new WaitForSecondsRealtime(delay);
            photonView?.RPC(nameof(RPC_RequestEnqueue), RpcTarget.All, url, userId);
        }

        [PunRPC]
        private void RPC_RequestEnqueue(string url, string requestorId)
        {
            if (!IsSupportedMediaUrl(url)) return;
            if (!ActAsHost()) return;

            if (IsYouTubePlaylistUrl(url))
            {
                StartCoroutine(EnqueueYouTubePlaylist(url, requestorId));
                return;
            }

            var added = TryEnqueueInternal(url, requestorId, out var addedItem, knownTitle: null);
            BroadcastQueue();

            if (added && addedItem != null && string.IsNullOrEmpty(addedItem.title))
            {
                StartTitleFetchIfNeeded(addedItem.hash, addedItem.url);
            }

            if (!audioSource!.isPlaying && songLoadingState != SongLoadingState.Loading)
            {
                PlayNextImpl();
            }
        }

        [PunRPC]
        private void RPC_RequestRemoveAt(int index, string requestorId, int requestorActorNumber)
        {
            if (!ActAsHost()) return;
            if (index < 0 || index >= queue.Count) return;

            var item = queue[index];
            bool allowed = (requestorId == item.requestedBy) || IsRequestorHolder(requestorActorNumber);
            if (!allowed) return;

            queue.RemoveAt(index);
            BroadcastQueue();
        }

        [PunRPC]
        private void RPC_RequestClearQueue(int requestorActorNumber)
        {
            if (!ActAsHost()) return;
            if (!IsRequestorHolder(requestorActorNumber)) return;
            queue.Clear();
            BroadcastQueue();
        }

        [PunRPC]
        private void RPC_RequestNext(int requestorActorNumber)
        {
            if (!ActAsHost()) return;
            if (!IsRequestorHolder(requestorActorNumber)) return;
            PlayNextImpl();
        }

        [PunRPC]
        private void RPC_RequestShuffle(int requestorActorNumber)
        {
            if (!ActAsHost()) return;
            if (!IsRequestorHolder(requestorActorNumber)) return;

            var rng = new System.Random();
            for (int i = queue.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (queue[i], queue[j]) = (queue[j], queue[i]);
            }
            BroadcastQueue();
        }

        private bool IsYouTubePlaylistUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            var host = uri.Host.ToLowerInvariant();
            if (!(host.Contains("youtube.com") || host.Contains("youtu.be"))) return false;

            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            return !string.IsNullOrEmpty(query["list"]);
        }

        private IEnumerator EnqueueYouTubePlaylist(string playlistUrl, string requestorId)
        {
            if (string.IsNullOrEmpty(ytDlpPath) || !File.Exists(ytDlpPath))
            {
                logger?.LogError("Cannot expand playlist: yt-dlp.exe not found.");
                yield break;
            }

            int remainingQueueSlots = Math.Max(0, MaxQueueSize - queue.Count);
            int userCount = queue.Count(it => it.requestedBy == requestorId);
            int remainingUserSlots = Math.Max(0, PerUserQueueLimit - userCount);
            int maxToAdd = Math.Min(remainingQueueSlots, remainingUserSlots);
            if (maxToAdd <= 0)
            {
                logger?.LogInfo("Playlist ignored: no capacity left to enqueue items.");
                yield break;
            }

            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--yes-playlist");
            psi.ArgumentList.Add("--flat-playlist");
            psi.ArgumentList.Add("--ignore-errors");
            psi.ArgumentList.Add("--playlist-items");
            psi.ArgumentList.Add($"1-{maxToAdd}");
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("%(title)s\t%(webpage_url)s");
            psi.ArgumentList.Add(playlistUrl);

            var proc = new Process { StartInfo = psi };
            var lines = new List<string>();
            proc.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) lines.Add(e.Data);
            };
            proc.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) logger?.LogWarning("yt-dlp (playlist): " + e.Data);
            };

            bool started = false;
            try
            {
                started = proc.Start();
            }
            catch (Exception ex)
            {
                logger?.LogError("Failed to start yt-dlp (playlist): " + ex.Message);
            }
            if (!started) yield break;

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            float startTime = Time.realtimeSinceStartup;
            while (!proc.HasExited)
            {
                if (Time.realtimeSinceStartup - startTime > PlaylistProbeTimeoutSec)
                {
                    try { proc.Kill(); } catch { }
                    logger?.LogWarning("yt-dlp (playlist) timed out and was killed.");
                    break;
                }
                yield return null;
            }

            int added = 0;
            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                int tab = raw.IndexOf('\t');
                if (tab <= 0 || tab >= raw.Length - 1) continue;

                string title = raw.Substring(0, tab).Trim();
                string url = raw.Substring(tab + 1).Trim();

                if (queue.Count >= MaxQueueSize) break;
                int uCount = queue.Count(it => it.requestedBy == requestorId);
                if (uCount >= PerUserQueueLimit) break;

                if (!AllowDuplicates && queue.Any(it => it.url.Equals(url, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var ok = TryEnqueueInternal(url, requestorId, out var addedItem, knownTitle: title);
                if (ok && addedItem != null) added++;
            }

            if (added > 0) BroadcastQueue();

            if (added > 0 && !audioSource!.isPlaying && songLoadingState != SongLoadingState.Loading)
            {
                PlayNextImpl();
            }
        }

        private bool TryEnqueueInternal(string url, string requestorId, out SongRequest? addedItem, string? knownTitle)
        {
            addedItem = null;
            if (queue.Count >= MaxQueueSize) { logger?.LogWarning("Queue is full."); return false; }
            int userCount = queue.Count(it => it.requestedBy == requestorId);
            if (userCount >= PerUserQueueLimit) { logger?.LogWarning("User queue limit reached."); return false; }
            if (!AllowDuplicates && queue.Any(it => it.url.Equals(url, StringComparison.OrdinalIgnoreCase)))
            {
                logger?.LogWarning("Duplicate URL blocked by setting.");
                return false;
            }

            var hash = GetSafeSongId(url);
            string metadataPath = Path.Combine(tempAudioDir!, $"bingbong_{hash}.json");
            string title = knownTitle ?? "";
            if (string.IsNullOrEmpty(title) && File.Exists(metadataPath))
            {
                try { title = JsonUtility.FromJson<SongMetadata>(File.ReadAllText(metadataPath))?.title ?? ""; }
                catch { title = ""; }
            }

            var req = new SongRequest
            {
                url = url,
                hash = hash,
                requestedBy = requestorId,
                title = title
            };
            queue.Add(req);
            addedItem = req;

            if (!string.IsNullOrEmpty(title))
            {
                try { File.WriteAllText(metadataPath, JsonUtility.ToJson(new SongMetadata { title = title })); }
                catch { }
            }

            if (string.IsNullOrEmpty(title)) StartTitleFetchIfNeeded(hash, url);

            return true;
        }

        private void PlayNextImpl()
        {
            manualStop = false;

            if (queue.Count == 0)
            {
                StopAndResetSong();
                BroadcastQueue();
                return;
            }

            var next = queue[0];
            queue.RemoveAt(0);
            BroadcastQueue();
            lastUsedPlayer = next.requestedBy;

            photonView?.RPC(nameof(RPC_StopAudio), RpcTarget.All);
            PlayAudio(next.url, lastUsedPlayer);

            if (string.IsNullOrEmpty(next.title))
            {
                StartTitleFetchIfNeeded(next.hash, next.url);
            }
        }

        private void BroadcastQueue()
        {
            var wrapper = new SongQueueWrapper { items = queue };
            string json = JsonUtility.ToJson(wrapper);
            photonView!.RPC(nameof(RPC_SyncQueue), RpcTarget.Others, json);
        }

        [PunRPC]
        private void RPC_SyncQueue(string json)
        {
            try
            {
                var wrapper = JsonUtility.FromJson<SongQueueWrapper>(json);
                if (wrapper != null && wrapper.items != null)
                {
                    queue.Clear();
                    queue.AddRange(wrapper.items.Take(MaxQueueSize));
                }
            }
            catch (Exception e)
            {
                logger?.LogWarning($"Failed to sync queue: {e.Message}");
            }
        }
        private IEnumerator DebouncePlayCoroutine(float delay, string url, string userId)
        {
            yield return new WaitForSecondsRealtime(delay);
            PlayAudio(url, userId);
        }

        [PunRPC]
        private void RPC_PlayAudio(string url, string userId)
        {
            logger?.LogInfo($"Got Play Audio {url} {userId}");
            if (string.IsNullOrWhiteSpace(url) || !IsSupportedMediaUrl(url)) return;
            if (debouncePlayCoroutine != null)
            {
                Logger.LogWarning("Got spam play, debouncing");
                StopCoroutine(debouncePlayCoroutine);
                debouncePlayCoroutine = null;
            }
            debouncePlayCoroutine = StartCoroutine(DebouncePlayCoroutine(1f, url, userId));
        }

        private void PlayAudio(string url, string userId)
        {
            debouncePlayCoroutine = null;
            if (string.IsNullOrWhiteSpace(url) || !IsSupportedMediaUrl(url)) return;

            lastUsedPlayer = userId;
            manualStop = false;

            if (ActAsHost())
            {
                audioSource?.Stop();
                photonView?.RPC(nameof(RPC_StopAudio), RpcTarget.Others);
            }
            currentSongTitle = "Unknown";
            currentSongHash = GetSafeSongId(url);

            string path = Path.Combine(tempAudioDir!, $"bingbong_{currentSongHash}.wav");
            string metadataPath = Path.Combine(tempAudioDir!, $"bingbong_{currentSongHash}.json");
            if (File.Exists(metadataPath))
            {
                try
                {
                    var json = File.ReadAllText(metadataPath);
                    var meta = JsonUtility.FromJson<SongMetadata>(json);
                    if (!string.IsNullOrEmpty(meta?.title)) currentSongTitle = meta.title!;
                }
                catch { currentSongTitle = "Unknown"; }
            }

            if (currentPlaybackCoroutine != null) StopCoroutine(currentPlaybackCoroutine);

            if (File.Exists(path))
            {
                currentPlaybackCoroutine = StartCoroutine(LoadAndPlay(path));
            }
            else
            {
                if (downloadCoroutine != null) { StopCoroutine(downloadCoroutine); downloadCoroutine = null; }
                downloadCoroutine = StartCoroutine(DownloadAudio(url, path, metadataPath));
            }
        }

        private IEnumerator DownloadAudio(string url, string outputPath, string metadataPath)
        {
            CleanupOldTempFiles();
            songLoadingState = SongLoadingState.Loading;

            string titleFromPrint = "";

            if (string.IsNullOrEmpty(ytDlpPath) || !File.Exists(ytDlpPath) || string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                songLoadingState = SongLoadingState.Error;
                logger?.LogError("yt-dlp.exe or ffmpeg.exe missing.");
                yield break;
            }

            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--no-playlist");
            psi.ArgumentList.Add("--max-filesize");
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
                        titleFromPrint = e.Data.Substring("title-current:".Length).Trim();
                }
            };
            proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) logger?.LogError(e.Data); };
            try
            {
                proc.Start();
            }
            catch (Exception e)
            {
                songLoadingState = SongLoadingState.Error;
                logger?.LogError("Failed to start yt-dlp: " + e.Message);
                downloadCoroutine = null;
                yield break;
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            float startTime = Time.realtimeSinceStartup;
            while (!proc.HasExited)
            {
                if (Time.realtimeSinceStartup - startTime > TitleProbeTimeoutSec + 60)
                {
                    try { proc.Kill(); } catch { }
                    logger?.LogWarning("yt-dlp (download) timed out and was killed.");
                    break;
                }
                yield return null;
            }

            if (File.Exists(outputPath))
            {
                if (!string.IsNullOrWhiteSpace(titleFromPrint)) currentSongTitle = titleFromPrint;

                try
                {
                    var metadata = new SongMetadata { title = currentSongTitle };
                    File.WriteAllText(metadataPath, JsonUtility.ToJson(metadata));
                }
                catch { }

                currentPlaybackCoroutine = StartCoroutine(LoadAndPlay(outputPath));
            }
            else
            {
                songLoadingState = SongLoadingState.Error;
                logger?.LogError("Download failed or file missing.");
                if (ActAsHost()) PlayNextImpl();
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
                    if (id.Length >= 8 && id.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                        return id;
                }
                if (host.Contains("youtube.com"))
                {
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    var id = query["v"];
                    if (!string.IsNullOrEmpty(id) && id.Length >= 8 && id.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                        return id;
                }
            }
            catch
            {
                logger?.LogError("Failed to extract id, falling back to hash");
            }

            using var sha = System.Security.Cryptography.SHA1.Create();
            var hashBytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
            return BitConverter.ToString(hashBytes, 0, 6).Replace("-", "");
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
            logger?.LogInfo("Got Stop Audio");
            manualStop = true;
            if (audioSource != null)
            {
                audioSource.Stop();
                audioSource.clip = null;
            }
        }

        [PunRPC]
        private void RPC_Rewind(float seconds)
        {
            logger?.LogInfo("Got Rewind");
            if (audioSource == null || audioSource.clip == null || seconds < 0f) return;
            audioSource.time = Mathf.Max(0f, audioSource.time - seconds);
        }

        [PunRPC]
        private void RPC_Forward(float seconds)
        {
            logger?.LogInfo("Got Forward");
            if (audioSource == null || audioSource.clip == null || seconds < 0f) return;
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
        private void RPC_SyncPlaying(string songHash, bool hostIsPlaying, bool isOwner, string hostTitle)
        {
            logger?.LogInfo($"Got Sync Playing {songHash}");
            if (isOwner) hostHasMod = true;
            if (!string.IsNullOrEmpty(hostTitle)) currentSongTitle = hostTitle;

            if (!ActAsHost() && audioSource != null)
            {
                if (!audioSource.isPlaying && hostIsPlaying && audioSource.clip != null && songHash == currentSongHash)
                    audioSource.Play();

                if (audioSource.isPlaying && (!hostIsPlaying || songHash != currentSongHash))
                    audioSource.Stop();
            }
        }

        private void StopAndResetSong()
        {
            if (currentPlaybackCoroutine != null) StopCoroutine(currentPlaybackCoroutine);
            audioSource?.Stop();
            if (audioSource != null) audioSource.clip = null;
            songLoadingState = SongLoadingState.Loaded;
            currentSongTitle = "";
            currentSongHash = "";
            lastUsedPlayer = "";
            manualStop = false;
            hostHasMod = PhotonNetwork.IsMasterClient;
        }

        private void CleanupOldTempFiles()
        {
            if (string.IsNullOrEmpty(tempAudioDir) || !Directory.Exists(tempAudioDir)) return;

            var wavFiles = new DirectoryInfo(tempAudioDir)
                .GetFiles("bingbong_*.wav")
                .OrderByDescending(f => f.LastAccessTimeUtc)
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

        private static readonly string[] AllowedHosts =
        {
            "youtube.com","www.youtube.com","m.youtube.com","youtu.be","www.youtu.be",
            "soundcloud.com","www.soundcloud.com",
            "vimeo.com","www.vimeo.com",
            "bandcamp.com","www.bandcamp.com",
            "twitch.tv","www.twitch.tv","clips.twitch.tv",
            "tiktok.com","www.tiktok.com",
            "mixcloud.com","www.mixcloud.com"
        };

        private bool IsSupportedMediaUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            var host = uri.Host.ToLowerInvariant();
            if (AllowedHosts.Contains(host)) return true;
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }

        private void StartTitleFetchIfNeeded(string hash, string url)
        {
            if (!ActAsHost()) return;
            if (titleFetchInProgress.Contains(hash)) return;

            string metadataPath = Path.Combine(tempAudioDir!, $"bingbong_{hash}.json");
            if (File.Exists(metadataPath))
            {
                try
                {
                    var meta = JsonUtility.FromJson<SongMetadata>(File.ReadAllText(metadataPath));
                    if (!string.IsNullOrEmpty(meta?.title))
                    {
                        UpdateQueueTitle(hash, meta!.title!);
                        return;
                    }
                }
                catch { }
            }

            StartCoroutine(FetchTitleCoroutine(hash, url));
        }

        private IEnumerator FetchTitleCoroutine(string hash, string url)
        {
            titleFetchInProgress.Add(hash);

            if (string.IsNullOrEmpty(ytDlpPath) || !File.Exists(ytDlpPath))
            {
                titleFetchInProgress.Remove(hash);
                yield break;
            }

            string fetched = "";

            var psi = new ProcessStartInfo
            {
                FileName = ytDlpPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--no-playlist");
            psi.ArgumentList.Add("--skip-download");
            psi.ArgumentList.Add("--print");
            psi.ArgumentList.Add("title-current:%(title)s");
            psi.ArgumentList.Add(url);

            var proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                if (e.Data.StartsWith("title-current:"))
                    fetched = e.Data.Substring("title-current:".Length).Trim();
            };
            proc.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) logger?.LogWarning($"yt-dlp (title) error: {e.Data}");
            };

            bool started = false;
            try
            {
                started = proc.Start();
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Failed to start yt-dlp (title): {ex.Message}");
            }
            if (!started)
            {
                titleFetchInProgress.Remove(hash);
                yield break;
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            float startTime = Time.realtimeSinceStartup;
            while (!proc.HasExited)
            {
                if (Time.realtimeSinceStartup - startTime > TitleProbeTimeoutSec)
                {
                    try { proc.Kill(); } catch { }
                    logger?.LogWarning("yt-dlp (title) timed out and was killed.");
                    break;
                }
                yield return null;
            }

            if (!string.IsNullOrWhiteSpace(fetched))
            {
                UpdateQueueTitle(hash, fetched);
                try
                {
                    string metadataPath = Path.Combine(tempAudioDir!, $"bingbong_{hash}.json");
                    File.WriteAllText(metadataPath, JsonUtility.ToJson(new SongMetadata { title = fetched }));
                }
                catch { }
            }

            titleFetchInProgress.Remove(hash);
        }

        private void UpdateQueueTitle(string hash, string title)
        {
            bool changed = false;
            for (int i = 0; i < queue.Count; i++)
            {
                if (queue[i].hash == hash && string.IsNullOrEmpty(queue[i].title))
                {
                    queue[i].title = title;
                    changed = true;
                }
            }
            if (changed) BroadcastQueue();

            if (currentSongHash == hash && (string.IsNullOrEmpty(currentSongTitle) || currentSongTitle == "Unknown"))
            {
                currentSongTitle = title;
            }
        }

        [HarmonyPatch(typeof(Item))]
        static class ItemPatches
        {
            static bool IsBingBong(Item it) =>
                (it.itemTags & Item.ItemTags.BingBong) != 0 ||
                (it.UIData?.itemName?.IndexOf("BingBong", System.StringComparison.OrdinalIgnoreCase) >= 0) ||
                (it.name?.IndexOf("BingBong", System.StringComparison.OrdinalIgnoreCase) >= 0);

            [HarmonyPostfix, HarmonyPatch("Awake")]
            static void Awake_Post(Item __instance)
            {
                if (IsBingBong(__instance))
                {
                    BingBongPlayerPlugin.bingBong = __instance.transform;
                    BingBongPlayerPlugin.logger?.LogInfo($"Found BingBong at {__instance.transform.position}");
                }
            }

            [HarmonyPrefix, HarmonyPatch("OnDestroy")]
            static void OnDestroy_Pre(Item __instance)
            {
                if (BingBongPlayerPlugin.bingBong == __instance.transform)
                {
                    BingBongPlayerPlugin.bingBong = null;
                    BingBongPlayerPlugin.logger?.LogInfo($"Destroyed BingBong");
                }
            }
        }
    }
}
