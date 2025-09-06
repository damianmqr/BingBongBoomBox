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
    [BepInPlugin("com.damianmqr.bingbongplayer", "BingBongBoomBox", "1.2.0")]
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
        private static string? mouthOpenParam = null;
        private static int mouthOpenHash = 0;
        public static Transform? bingBong = null;
        public static Animator? bingBongMouth = null;

        private ConfigEntry<float>? volumeSetting;
        private ConfigEntry<int>? maxAudioSizeMbCfg;
        private ConfigEntry<int>? maxTempFilesCfg;
        private ConfigEntry<int>? maxQueueSizeCfg;
        private ConfigEntry<int>? perUserQueueLimitCfg;
        private ConfigEntry<bool>? allowDuplicatesCfg;
        private ConfigEntry<bool>? autoAdvanceCfg;
        private ConfigEntry<int>? titleProbeTimeoutSecCfg;
        private ConfigEntry<int>? playlistProbeTimeoutSecCfg;
        private ConfigEntry<bool>? globalAudioCfg;
        private ConfigEntry<bool>? bypassEnvCfg;
        private ConfigEntry<bool>? publicEnqueueCfg;
        private ConfigEntry<float>? maxHearingDistanceCfg;
        private ConfigEntry<float>? uiScaleCfg;
        private ConfigEntry<bool>? showAdvancedCfg;

        private bool hostPublicEnqueue = false;
        private bool hostGlobalAudio = false;
        private bool hostBypassEnv = false;
        private bool hostAllowDuplicates = false;
        private bool hostAutoAdvance = true;
        private float hostMaxHearingDistance = 35f;
        private int hostMaxQueueSize = 20;
        private int hostPerUserQueueLimit = 5;

        private int MaxAudioSizeMb => Mathf.Max(1, maxAudioSizeMbCfg?.Value ?? 100);
        private int MaxTempFiles => Mathf.Max(1, maxTempFilesCfg?.Value ?? 5);
        private int MaxQueueSize => Mathf.Max(1, ActAsHost() ? (maxQueueSizeCfg?.Value ?? 20) : (hostMaxQueueSize > 0 ? hostMaxQueueSize : 20));
        private int PerUserQueueLimit => Mathf.Max(1, ActAsHost() ? (perUserQueueLimitCfg?.Value ?? 5) : (hostPerUserQueueLimit > 0 ? hostPerUserQueueLimit : 5));
        private bool AllowDuplicates => ActAsHost() ? (allowDuplicatesCfg?.Value ?? false) : hostAllowDuplicates;
        private bool AutoAdvance => ActAsHost() ? (autoAdvanceCfg?.Value ?? true) : hostAutoAdvance;
        private int TitleProbeTimeoutSec => Mathf.Max(5, titleProbeTimeoutSecCfg?.Value ?? 20);
        private int PlaylistProbeTimeoutSec => Mathf.Max(5, playlistProbeTimeoutSecCfg?.Value ?? 30);
        private bool GlobalAudio => ActAsHost() ? (globalAudioCfg?.Value ?? false) : hostGlobalAudio;
        private bool BypassEnv => ActAsHost() ? (bypassEnvCfg?.Value ?? false) : hostBypassEnv;
        private bool PublicEnqueue => ActAsHost() ? (publicEnqueueCfg?.Value ?? false) : hostPublicEnqueue;
        private float MaxHearingDistance => Mathf.Clamp(ActAsHost() ? (maxHearingDistanceCfg?.Value ?? 35f) : (hostMaxHearingDistance <= 0f ? 35f : hostMaxHearingDistance), 5f, 10000f);
        private float UiScale => Mathf.Clamp(uiScaleCfg?.Value ?? 1.0f, 0.75f, 2.0f);
        private bool ShowAdvanced => showAdvancedCfg?.Value ?? false;

        private float syncInterval = 5f;
        private float lastSyncTime = 0f;
        private float lastConfigBroadcast = -999f;
        private bool sentInitialConfig = false;

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

        [Serializable] private class SongMetadata { public string? title; }

        [Serializable]
        private class SongRequest
        {
            public string url = "";
            public string hash = "";
            public string requestedBy = "";
            public int requestedByActor = -1;
            public string title = "";
        }

        [Serializable] private class SongQueueWrapper { public List<SongRequest> items = new(); }

        [Serializable]
        private class HostConfigPayload
        {
            public bool publicEnqueue;
            public bool globalAudio;
            public bool bypassEnv;
            public bool allowDuplicates;
            public bool autoAdvance;
            public float maxHearingDistance;
            public int maxQueueSize;
            public int perUserQueueLimit;
            public string v;
        }

        private readonly List<SongRequest> queue = new();
        private Vector2 queueScroll = Vector2.zero;
        private Vector2 panelScroll = Vector2.zero;

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
            maxQueueSizeCfg = Config.Bind("Limits", "MaxQueueSize", 20, "Max items allowed in queue. (Host only)");
            perUserQueueLimitCfg = Config.Bind("Limits", "PerUserQueueLimit", 5, "Max items one user can have pending in the queue. (Host only)");
            allowDuplicatesCfg = Config.Bind("Queue", "AllowDuplicates", false, "Allow the same URL to be enqueued multiple times. (Host only)");
            autoAdvanceCfg = Config.Bind("Queue", "AutoAdvance", true, "Automatically advance to next track when one finishes. (Host only)");
            titleProbeTimeoutSecCfg = Config.Bind("Advanced", "TitleProbeTimeoutSec", 20, "Timeout for title probes (seconds).");
            playlistProbeTimeoutSecCfg = Config.Bind("Advanced", "PlaylistProbeTimeoutSec", 30, "Timeout for playlist expansion (seconds).");

            globalAudioCfg = Config.Bind("Audio", "GlobalAudio", false, "2D audio heard anywhere. (Host only)");
            bypassEnvCfg = Config.Bind("Audio", "BypassEnvironmentEffects", false, "Bypass reverb/occlusion. (Host only)");
            maxHearingDistanceCfg = Config.Bind("Audio", "MaxHearingDistance", 35f, "3D audio max distance. (Host only)");
            publicEnqueueCfg = Config.Bind("Queue", "PublicEnqueue", false, "Non-holders may add and control playback. (Host only)");

            uiScaleCfg = Config.Bind("UI", "UiScale", 1.0f, "Scale UI text/vertical only (client)");
            showAdvancedCfg = Config.Bind("UI", "ShowAdvanced", false, "Show Advanced section (client)");

            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.spatialBlend = 1f;
            audioSource.volume = currentVolume;
            audioSource.loop = false;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.minDistance = 1f;
            audioSource.maxDistance = MaxHearingDistance;
            audioSource.bypassEffects = BypassEnv;
            audioSource.bypassListenerEffects = BypassEnv;
            audioSource.bypassReverbZones = BypassEnv;

            new Harmony("com.damianmqr.bingbongplayer").PatchAll();
        }

        private void OnDestroy()
        {
            if (currentPlaybackCoroutine != null) StopCoroutine(currentPlaybackCoroutine);
            if (debouncePlayCoroutine != null) StopCoroutine(debouncePlayCoroutine);
            if (downloadCoroutine != null) StopCoroutine(downloadCoroutine);
        }

        private float spectrumTimer = 0f;
        private float spectrumInterval = 0.012f;

        private void LateUpdate()
        {
            var connectedToGameServer = PhotonNetwork.Server == ServerConnection.GameServer;
            if (lastConnectedState && !connectedToGameServer)
            {
                StopAndResetSong();
                queue.Clear();
                sentInitialConfig = false;
            }
            lastConnectedState = connectedToGameServer;

            if (audioSource == null || !PhotonNetwork.IsConnectedAndReady || photonView == null) return;

            if (ActAsHost())
            {
                if (!sentInitialConfig) { BroadcastHostConfig(); sentInitialConfig = true; }
                if (Time.time - lastConfigBroadcast > 15f) { BroadcastHostConfig(); }
            }

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

            if (GlobalAudio)
            {
                audioSource.spatialBlend = 0f;
                audioSource.volume = currentVolume;
            }
            else
            {
                audioSource.spatialBlend = playingCinematic ? 0f : 1f;
                audioSource.maxDistance = MaxHearingDistance;
                audioSource.volume = position.HasValue ? currentVolume : 0f;
                if (position.HasValue) audioSource.transform.position = position.Value;
            }

            audioSource.bypassEffects = BypassEnv;
            audioSource.bypassListenerEffects = BypassEnv;
            audioSource.bypassReverbZones = BypassEnv;

            if (ActAsHost() && AutoAdvance && !manualStop && audioSource.clip != null && !audioSource.isPlaying && !string.IsNullOrEmpty(currentSongHash) && songLoadingState == SongLoadingState.Loaded)
            {
                PlayNextImpl();
            }

            if (audioSource != null && bingBongMouth != null && mouthOpenParam != null)
            {
                spectrumTimer += Time.deltaTime;
                if (spectrumTimer >= spectrumInterval)
                {
                    spectrumTimer = 0f;

                    float targetOpen = 0f;
                    bool haveAudio = audioSource.isPlaying && spectrumData != null && audioSource.clip != null;

                    if (haveAudio)
                    {
                        audioSource.GetSpectrumData(spectrumData, 0, fftWindow);

                        float freqResolution = AudioSettings.outputSampleRate / 2f / sampleSize;
                        int minIndex = Mathf.FloorToInt(vocalLow / freqResolution);
                        int maxIndex = Mathf.CeilToInt(vocalHigh / freqResolution);
                        minIndex = Mathf.Clamp(minIndex, 0, sampleSize - 1);
                        maxIndex = Mathf.Clamp(maxIndex, 0, sampleSize - 1);

                        float sum = 0f;
                        int count = 0;
                        for (int i = minIndex; i <= maxIndex; i++) { sum += spectrumData![i]; count++; }
                        float avg = count > 0 ? sum / count : 0f;

                        targetOpen = Mathf.Clamp01(avg * 50f);
                    }

                    try { bingBongMouth.SetFloat(mouthOpenHash, targetOpen); } catch { }
                }
            }
        }

        private void BroadcastHostConfig()
        {
            lastConfigBroadcast = Time.time;
            var payload = new HostConfigPayload
            {
                publicEnqueue = PublicEnqueue,
                globalAudio = GlobalAudio,
                bypassEnv = BypassEnv,
                allowDuplicates = AllowDuplicates,
                autoAdvance = AutoAdvance,
                maxHearingDistance = MaxHearingDistance,
                maxQueueSize = MaxQueueSize,
                perUserQueueLimit = PerUserQueueLimit,
                v = "1.2.0"
            };
            photonView?.RPC(nameof(RPC_ConfigSync), RpcTarget.Others, JsonUtility.ToJson(payload));
        }

        [PunRPC]
        private void RPC_ConfigSync(string json)
        {
            try
            {
                var p = JsonUtility.FromJson<HostConfigPayload>(json);
                if (p == null) return;
                hostPublicEnqueue = p.publicEnqueue;
                hostGlobalAudio = p.globalAudio;
                hostBypassEnv = p.bypassEnv;
                hostAllowDuplicates = p.allowDuplicates;
                hostAutoAdvance = p.autoAdvance;
                hostMaxHearingDistance = p.maxHearingDistance;
                hostMaxQueueSize = p.maxQueueSize;
                hostPerUserQueueLimit = p.perUserQueueLimit;
            }
            catch { }
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

        private GUIStyle? boxStyle, labelStyle, buttonStyle, sliderStyle, sliderThumbStyle, headerStyle, smallLabelStyle, toggleStyle, foldoutStyle;
        private Texture2D? boxBgTex;
        private Texture2D? sliderBgTex;
        private Texture2D? sliderThumbTex;
        private Texture2D? buttonNormalBgTex;
        private Texture2D? buttonHoverBgTex;
        private bool initializedStyles;
        private float appliedUiScale = -1f;

        private Texture2D MakeSolidColorTex(Color col)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, col);
            tex.Apply();
            return tex;
        }

        private void InitStyles()
        {
            if (initializedStyles && Mathf.Abs(appliedUiScale - UiScale) < 0.001f) return;
            appliedUiScale = UiScale;

            Font? labelFont = null;
            foreach (var font in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (font.name == "DarumaDropOne-Regular") { labelFont = font; break; }
            }

            if (labelFont == null && !PhotonNetwork.IsConnectedAndReady)
                return;

            boxBgTex = MakeSolidColorTex(new Color(0f, 0f, 0f, 0.80f));

            int padTB = Mathf.RoundToInt(12 * UiScale);
            boxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(12, 12, padTB, padTB),
                margin = new RectOffset(10, 10, 10, 10),
                normal = { background = boxBgTex }
            };

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(14 * UiScale),
                normal = { textColor = Color.white },
                richText = true,
            };

            smallLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(12 * UiScale),
                normal = { textColor = Color.white },
                richText = true,
            };

            headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(22 * UiScale),
                normal = { textColor = Color.white },
                richText = true,
            };

            toggleStyle = new GUIStyle(GUI.skin.toggle)
            {
                fontSize = Mathf.RoundToInt(13 * UiScale),
            };

            foldoutStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.RoundToInt(13 * UiScale),
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 8, Mathf.RoundToInt(6 * UiScale), Mathf.RoundToInt(6 * UiScale)),
            };

            buttonNormalBgTex = MakeSolidColorTex(new Color(0.2f, 0.2f, 0.2f, 0.95f));
            buttonHoverBgTex = MakeSolidColorTex(new Color(0.3f, 0.3f, 0.3f, 0.95f));
            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.RoundToInt(13 * UiScale),
                margin = new RectOffset(5, 5, 5, 5),
                padding = new RectOffset(8, 8, Mathf.RoundToInt(6 * UiScale), Mathf.RoundToInt(6 * UiScale)),
                normal = { background = buttonNormalBgTex },
                hover = { background = buttonHoverBgTex },
            };

            sliderBgTex = MakeSolidColorTex(new Color(0.15f, 0.15f, 0.15f, 1f));
            sliderThumbTex = MakeSolidColorTex(new Color(1f, 0.6f, 0f, 1f));
            sliderStyle = new GUIStyle(GUI.skin.horizontalSlider)
            {
                normal = { background = sliderBgTex },
                fixedHeight = Mathf.RoundToInt(12 * UiScale),
            };

            sliderThumbStyle = new GUIStyle(GUI.skin.horizontalSliderThumb)
            {
                normal = { background = sliderThumbTex },
                fixedWidth = Mathf.RoundToInt(16 * UiScale),
                fixedHeight = Mathf.RoundToInt(16 * UiScale),
            };

            if (labelFont != null)
            {
                labelStyle.font = labelFont;
                smallLabelStyle.font = labelFont;
                headerStyle.font = labelFont;
                buttonStyle.font = labelFont;
                toggleStyle.font = labelFont;
                foldoutStyle.font = labelFont;
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
                    GUILayout.BeginArea(new Rect(10, 10, 480, Mathf.RoundToInt(100 * UiScale)));
                    GUILayout.Label("<b>Press [ESC] to access Music Player</b>", labelStyle);
                    GUILayout.EndArea();
                }
                return;
            }

            float margin = 10f;
            float maxWidth = Mathf.Min(720f, Screen.width - 2 * margin);
            float maxH = Screen.height - 2 * margin;
            Rect area = new Rect(margin, margin, maxWidth, maxH);

            GUILayout.BeginArea(area, boxStyle);
            panelScroll = GUILayout.BeginScrollView(panelScroll, GUILayout.Width(maxWidth - 8f), GUILayout.Height(maxH - 8f));

            GUILayout.Label("【BingBong Player】", headerStyle);

            GUILayout.Label(ActAsHost()
                ? "※ You are the acting host."
                : "※ Waiting for host…", smallLabelStyle);

            bool isLocalHolder = Player.localPlayer.HasInAnySlot(BingBongItemID);
            bool mayEnqueue = isLocalHolder || PublicEnqueue;

            if (mayEnqueue)
            {
                GUILayout.Space(4 * UiScale);
                GUILayout.Label("URL:", labelStyle);
                GUILayout.BeginHorizontal();
                urlInput = GUILayout.TextField(urlInput, GUILayout.Width(maxWidth - 180f), GUILayout.Height(26 * UiScale));
                if (GUILayout.Button("Clear", buttonStyle, GUILayout.Width(70f), GUILayout.Height(26 * UiScale))) urlInput = "";
                if (GUILayout.Button("Add »", buttonStyle, GUILayout.Width(70f), GUILayout.Height(26 * UiScale))) RequestEnqueue(urlInput);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                bool controlsEnabled = isLocalHolder || PublicEnqueue;
                GUI.enabled = controlsEnabled;
                if (GUILayout.Button("Stop", buttonStyle, GUILayout.Height(26 * UiScale))) photonView.RPC(nameof(RPC_RequestStop), RpcTarget.All);
                if (GUILayout.Button("〈〈 10s", buttonStyle, GUILayout.Height(26 * UiScale))) photonView.RPC(nameof(RPC_RequestSeek), RpcTarget.All, -10f);
                if (GUILayout.Button("10s 〉〉", buttonStyle, GUILayout.Height(26 * UiScale))) photonView.RPC(nameof(RPC_RequestSeek), RpcTarget.All, 10f);
                if (GUILayout.Button("Next »", buttonStyle, GUILayout.Height(26 * UiScale))) photonView.RPC(nameof(RPC_RequestNext), RpcTarget.All);
                GUI.enabled = true;
                if (!isLocalHolder && PublicEnqueue)
                {
                    GUILayout.Label("※ Public enqueue is enabled.", smallLabelStyle);
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(8 * UiScale); GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1)); GUILayout.Space(8 * UiScale);
            GUILayout.Label($"Local Volume: {Mathf.RoundToInt(currentVolume * 100)}%", labelStyle);
            float newVolume = GUILayout.HorizontalSlider(currentVolume, 0f, 1f, sliderStyle, sliderThumbStyle, GUILayout.Width(maxWidth - 40f));
            if (Mathf.Abs(newVolume - currentVolume) > 0.001f)
            {
                currentVolume = newVolume;
                if (volumeSetting != null) volumeSetting.Value = newVolume;
                if (audioSource != null) audioSource.volume = currentVolume;
            }

            GUILayout.Space(8 * UiScale); GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1)); GUILayout.Space(8 * UiScale);
            bool showAdv = ShowAdvanced;
            string advTitle = showAdv ? "▼ Advanced" : "► Advanced";
            if (GUILayout.Button(advTitle, foldoutStyle, GUILayout.Height(26 * UiScale)))
            {
                showAdv = !showAdv;
                if (showAdvancedCfg != null) showAdvancedCfg.Value = showAdv;
            }

            if (showAdv)
            {
                bool canEdit = ActAsHost();
                GUI.enabled = canEdit;
                bool newGlobal = GUILayout.Toggle(GlobalAudio, " Global Audio (2D / Hear Anywhere)", toggleStyle);
                bool newBypass = GUILayout.Toggle(BypassEnv, " Bypass Environment Effects", toggleStyle);
                bool newPublicEnqueue = GUILayout.Toggle(PublicEnqueue, " Allow Public Enqueue (add/controls)", toggleStyle);

                if (newGlobal != GlobalAudio) { globalAudioCfg!.Value = newGlobal; BroadcastHostConfig(); }
                if (newBypass != BypassEnv) { bypassEnvCfg!.Value = newBypass; BroadcastHostConfig(); }
                if (newPublicEnqueue != PublicEnqueue) { publicEnqueueCfg!.Value = newPublicEnqueue; BroadcastHostConfig(); }

                if (!GlobalAudio)
                {
                    GUILayout.Label($"Max Hearing Distance: {Mathf.RoundToInt(MaxHearingDistance)}m", labelStyle);
                    float newMax = GUILayout.HorizontalSlider(MaxHearingDistance, 5f, 300f, sliderStyle, sliderThumbStyle, GUILayout.Width(maxWidth - 40f));
                    if (Mathf.Abs(newMax - MaxHearingDistance) > 0.001f) { maxHearingDistanceCfg!.Value = newMax; BroadcastHostConfig(); }
                }

                GUILayout.Label($"Max Queue Size: {MaxQueueSize}", smallLabelStyle);
                int newMaxQ = Mathf.RoundToInt(GUILayout.HorizontalSlider(MaxQueueSize, 5, 50, sliderStyle, sliderThumbStyle, GUILayout.Width(maxWidth - 40f)));
                if (newMaxQ != MaxQueueSize) { maxQueueSizeCfg!.Value = newMaxQ; BroadcastHostConfig(); }

                GUILayout.Label($"Per-User Queue Limit: {PerUserQueueLimit}", smallLabelStyle);
                int newPerUser = Mathf.RoundToInt(GUILayout.HorizontalSlider(PerUserQueueLimit, 1, 15, sliderStyle, sliderThumbStyle, GUILayout.Width(maxWidth - 40f)));
                if (newPerUser != PerUserQueueLimit) { perUserQueueLimitCfg!.Value = newPerUser; BroadcastHostConfig(); }

                bool newDup = GUILayout.Toggle(AllowDuplicates, " Allow Duplicate URLs", toggleStyle);
                if (newDup != AllowDuplicates) { allowDuplicatesCfg!.Value = newDup; BroadcastHostConfig(); }

                bool newAuto = GUILayout.Toggle(AutoAdvance, " Auto-Advance", toggleStyle);
                if (newAuto != AutoAdvance) { autoAdvanceCfg!.Value = newAuto; BroadcastHostConfig(); }
                GUI.enabled = true;

                GUILayout.Label($"UI Scale: {UiScale:0.00}x", smallLabelStyle);
                float newUi = GUILayout.HorizontalSlider(UiScale, 0.75f, 2.0f, sliderStyle, sliderThumbStyle, GUILayout.Width(maxWidth - 40f));
                if (Mathf.Abs(newUi - UiScale) > 0.001f) uiScaleCfg!.Value = newUi;
            }

            if (songLoadingState == SongLoadingState.Loading)
            {
                GUILayout.Space(8 * UiScale); GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1)); GUILayout.Space(8 * UiScale);
                int idx = Mathf.FloorToInt(Time.time) % LoadingProgress.Length;
                GUILayout.Label(LoadingProgress[idx], labelStyle);
            }
            else if (songLoadingState == SongLoadingState.Error)
            {
                GUILayout.Space(8 * UiScale); GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1)); GUILayout.Space(8 * UiScale);
                GUILayout.Label("ERROR! Couldn't load audio.", labelStyle);
            }
            else if (!string.IsNullOrEmpty(currentSongTitle) && audioSource?.clip != null && (audioSource.isPlaying || audioSource.time > 0f))
            {
                GUILayout.Space(8 * UiScale); GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1)); GUILayout.Space(8 * UiScale);
                GUILayout.Label($"Now Playing: 「{currentSongTitle}」", labelStyle);
                int currentMin = Mathf.FloorToInt(audioSource.time / 60f);
                int currentSec = Mathf.FloorToInt(audioSource.time % 60f);
                int totalMin = Mathf.FloorToInt(audioSource.clip.length / 60f);
                int totalSec = Mathf.FloorToInt(audioSource.clip.length % 60f);
                GUILayout.Label($"{currentMin:D2}:{currentSec:D2} / {totalMin:D2}:{totalSec:D2}", labelStyle);
            }

            GUILayout.Space(8 * UiScale); GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1)); GUILayout.Space(8 * UiScale);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"【Queue】 ({queue.Count}/{MaxQueueSize})", labelStyle);
            GUILayout.FlexibleSpace();
            if (isLocalHolder || PublicEnqueue)
            {
                if (GUILayout.Button("〜 Shuffle", buttonStyle, GUILayout.Height(24 * UiScale)))
                    photonView.RPC(nameof(RPC_RequestShuffle), RpcTarget.All);
                if (GUILayout.Button("[ ] Clear", buttonStyle, GUILayout.Height(24 * UiScale)))
                    photonView.RPC(nameof(RPC_RequestClearQueue), RpcTarget.All);
            }
            GUILayout.EndHorizontal();

            float minQueue = 140f * UiScale;
            float maxQueue = Mathf.Clamp(Screen.height * 0.65f, 160f, Screen.height - 260f);
            float estRow = 22f * UiScale;
            float desiredQueue = Mathf.Clamp(queue.Count * estRow + 8f, minQueue, maxQueue);

            queueScroll = GUILayout.BeginScrollView(queueScroll, GUILayout.Height(desiredQueue), GUILayout.Width(maxWidth - 20f));
            for (int i = 0; i < queue.Count; i++)
            {
                var it = queue[i];
                string display = string.IsNullOrEmpty(it.title) ? "Fetching title…" : it.title;
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{i + 1}. • {display} ({ShortId(it.requestedBy)})", smallLabelStyle, GUILayout.Width(maxWidth - 180f));
                bool canRemove = isLocalHolder || PublicEnqueue || it.requestedByActor == LocalActor();
                GUI.enabled = canRemove;
                if (GUILayout.Button("Remove", buttonStyle, GUILayout.Width(90f)))
                    photonView.RPC(nameof(RPC_RequestRemoveAt), RpcTarget.All, i);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private string ShortId(string id) => string.IsNullOrEmpty(id) ? "??" : (id.Length <= 6 ? id : id.Substring(0, 6));

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
        private void RPC_RequestEnqueue(string url, string requestorId, PhotonMessageInfo info)
        {
            if (!IsSupportedMediaUrl(url)) return;
            if (!ActAsHost()) return;

            int senderActor = info.Sender != null ? info.Sender.ActorNumber : -1;

            if (IsYouTubePlaylistUrl(url))
            {
                StartCoroutine(EnqueueYouTubePlaylist(url, requestorId, senderActor));
                return;
            }

            var added = TryEnqueueInternal(url, requestorId, senderActor, out var addedItem, knownTitle: null);
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
        private void RPC_RequestRemoveAt(int index, PhotonMessageInfo info)
        {
            if (!ActAsHost()) return;
            if (index < 0 || index >= queue.Count) return;

            int senderActor = info.Sender != null ? info.Sender.ActorNumber : -1;

            var item = queue[index];
            bool allowed = (item.requestedByActor == senderActor) || IsRequestorHolder(senderActor) || PublicEnqueue;
            if (!allowed) return;

            queue.RemoveAt(index);
            BroadcastQueue();
        }

        [PunRPC]
        private void RPC_RequestClearQueue(PhotonMessageInfo info)
        {
            if (!ActAsHost()) return;
            int senderActor = info.Sender != null ? info.Sender.ActorNumber : -1;
            if (!(PublicEnqueue || IsRequestorHolder(senderActor))) return;
            queue.Clear();
            BroadcastQueue();
        }

        [PunRPC]
        private void RPC_RequestNext(PhotonMessageInfo info)
        {
            if (!ActAsHost()) return;
            int senderActor = info.Sender != null ? info.Sender.ActorNumber : -1;
            if (!(PublicEnqueue || IsRequestorHolder(senderActor))) return;
            PlayNextImpl();
        }

        [PunRPC]
        private void RPC_RequestShuffle(PhotonMessageInfo info)
        {
            if (!ActAsHost()) return;
            int senderActor = info.Sender != null ? info.Sender.ActorNumber : -1;
            if (!(PublicEnqueue || IsRequestorHolder(senderActor))) return;

            var rng = new System.Random();
            for (int i = queue.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (queue[i], queue[j]) = (queue[j], queue[i]);
            }
            BroadcastQueue();
        }

        [PunRPC]
        private void RPC_RequestStop(PhotonMessageInfo info)
        {
            if (!ActAsHost()) return;
            int senderActor = info.Sender != null ? info.Sender.ActorNumber : -1;
            if (!(PublicEnqueue || IsRequestorHolder(senderActor))) return;

            photonView.RPC(nameof(RPC_StopAudio), RpcTarget.All);
        }

        [PunRPC]
        private void RPC_RequestSeek(float seconds, PhotonMessageInfo info)
        {
            if (!ActAsHost()) return;
            int senderActor = info.Sender != null ? info.Sender.ActorNumber : -1;
            if (!(PublicEnqueue || IsRequestorHolder(senderActor))) return;

            if (audioSource == null || audioSource.clip == null) return;
            float t = Mathf.Clamp(audioSource.time + seconds, 0f, audioSource.clip.length);
            audioSource.time = t;
            photonView.RPC(nameof(RPC_SyncTime), RpcTarget.Others, currentSongHash, t);
        }

        private bool IsYouTubePlaylistUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            var host = uri.Host.ToLowerInvariant();
            if (!(host.Contains("youtube.com") || host.Contains("youtu.be") || host.Contains("music.youtube.com"))) return false;

            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            bool hasList = !string.IsNullOrEmpty(query["list"]);

            bool hasVideoId = false;
            if (host.Contains("youtu.be"))
            {
                var id = uri.AbsolutePath.Trim('/');
                hasVideoId = !string.IsNullOrEmpty(id);
            }
            else
            {
                var v = query["v"];
                if (!string.IsNullOrEmpty(v)) hasVideoId = true;
                var path = uri.AbsolutePath.ToLowerInvariant();
                if (path.StartsWith("/shorts/")) hasVideoId = true;
            }

            return hasList && !hasVideoId;
        }

        private IEnumerator EnqueueYouTubePlaylist(string playlistUrl, string requestorId, int requestorActor)
        {
            if (string.IsNullOrEmpty(ytDlpPath) || !File.Exists(ytDlpPath))
            {
                yield break;
            }

            int remainingQueueSlots = Math.Max(0, MaxQueueSize - queue.Count);
            int userCount = queue.Count(it => it.requestedByActor == requestorActor);
            int remainingUserSlots = Math.Max(0, PerUserQueueLimit - userCount);
            int maxToAdd = Math.Min(remainingQueueSlots, remainingUserSlots);
            if (maxToAdd <= 0)
            {
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
            proc.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) lines.Add(e.Data); };
            proc.ErrorDataReceived += (s, e) => { };

            bool started = false;
            try { started = proc.Start(); }
            catch (Exception) { }
            if (!started) yield break;

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            float startTime = Time.realtimeSinceStartup;
            while (!proc.HasExited)
            {
                if (Time.realtimeSinceStartup - startTime > PlaylistProbeTimeoutSec)
                {
                    try { proc.Kill(); } catch { }
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
                int uCount = queue.Count(it => it.requestedByActor == requestorActor);
                if (uCount >= PerUserQueueLimit) break;

                if (!AllowDuplicates && queue.Any(it => it.url.Equals(url, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var ok = TryEnqueueInternal(url, requestorId, requestorActor, out var addedItem, knownTitle: title);
                if (ok && addedItem != null) added++;
            }

            if (added > 0) BroadcastQueue();

            if (added > 0 && !audioSource!.isPlaying && songLoadingState != SongLoadingState.Loading)
            {
                PlayNextImpl();
            }
        }

        private bool TryEnqueueInternal(string url, string requestorId, int requestorActor, out SongRequest? addedItem, string? knownTitle)
        {
            addedItem = null;
            if (queue.Count >= MaxQueueSize) return false;
            int userCount = queue.Count(it => it.requestedByActor == requestorActor);
            if (userCount >= PerUserQueueLimit) return false;
            if (!AllowDuplicates && queue.Any(it => it.url.Equals(url, StringComparison.OrdinalIgnoreCase))) return false;

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
                requestedByActor = requestorActor,
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
            catch { }
        }

        private IEnumerator DebouncePlayCoroutine(float delay, string url, string userId)
        {
            yield return new WaitForSecondsRealtime(delay);
            PlayAudio(url, userId);
        }

        [PunRPC]
        private void RPC_PlayAudio(string url, string userId, PhotonMessageInfo info)
        {
            if (info.Sender != null && !info.Sender.IsMasterClient && hostHasMod) return;
            if (string.IsNullOrWhiteSpace(url) || !IsSupportedMediaUrl(url)) return;
            if (debouncePlayCoroutine != null)
            {
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
                photonView?.RPC(nameof(RPC_PlayAudio), RpcTarget.Others, url, userId);
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
                    if (e.Data.StartsWith("title-current:"))
                        titleFromPrint = e.Data.Substring("title-current:".Length).Trim();
                }
            };
            proc.ErrorDataReceived += (s, e) => { };

            try { proc.Start(); }
            catch (Exception) { songLoadingState = SongLoadingState.Error; downloadCoroutine = null; yield break; }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            float startTime = Time.realtimeSinceStartup;
            while (!proc.HasExited)
            {
                if (Time.realtimeSinceStartup - startTime > TitleProbeTimeoutSec + 60)
                {
                    try { proc.Kill(); } catch { }
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
                    if (!string.IsNullOrEmpty(id)) return id;
                }
                if (host.Contains("youtube.com") || host.Contains("music.youtube.com"))
                {
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    var v = query["v"];
                    if (!string.IsNullOrEmpty(v)) return v;
                    var path = uri.AbsolutePath.Trim('/');
                    if (path.StartsWith("shorts/", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = path.Split('/');
                        if (parts.Length >= 2 && parts[1].Length > 0) return parts[1];
                    }
                }
            }
            catch { }

            using var sha = System.Security.Cryptography.SHA1.Create();
            var hashBytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url));
            return BitConverter.ToString(hashBytes, 0, 6).Replace("-", "");
        }

        private IEnumerator LoadAndPlay(string path)
        {
            if (audioSource == null)
            {
                songLoadingState = SongLoadingState.Error;
                yield break;
            }
            songLoadingState = SongLoadingState.Loaded;
            using var www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.WAV);
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                songLoadingState = SongLoadingState.Error;
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
            manualStop = true;
            if (audioSource != null)
            {
                audioSource.Stop();
                audioSource.clip = null;
            }
        }

        [PunRPC]
        private void RPC_SyncTime(string songHash, float hostTime)
        {
            if (!ActAsHost() && audioSource != null && audioSource.clip != null && songHash == currentSongHash)
            {
                if (Mathf.Abs(audioSource.time - hostTime) > 0.5f && hostTime >= 0f && hostTime <= audioSource.clip.length)
                {
                    audioSource.time = hostTime;
                }
            }
        }

        [PunRPC]
        private void RPC_SyncPlaying(string songHash, bool hostIsPlaying, bool isOwner, string hostTitle)
        {
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
                try { wav.Delete(); } catch { }

                var jsonPath = Path.ChangeExtension(wav.FullName, ".json");
                if (File.Exists(jsonPath))
                {
                    try { File.Delete(jsonPath); } catch { }
                }
            }
        }

        private static readonly string[] AllowedHosts =
        {
            "youtube.com","www.youtube.com","m.youtube.com","music.youtube.com","www.music.youtube.com","youtu.be","www.youtu.be",
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
            proc.ErrorDataReceived += (s, e) => { };

            bool started = false;
            try { started = proc.Start(); }
            catch (Exception) { }
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
                (it.UIData?.itemName?.IndexOf("BingBong", StringComparison.OrdinalIgnoreCase) >= 0) ||
                (it.name?.IndexOf("BingBong", StringComparison.OrdinalIgnoreCase) >= 0);

            [HarmonyPostfix, HarmonyPatch("Awake")]
            static void Awake_Post(Item __instance)
            {
                if (IsBingBong(__instance))
                {
                    BingBongPlayerPlugin.bingBong = __instance.transform;

                    Transform root = __instance.transform;

                    BingBongPlayerPlugin.bingBongMouth = null;
                    var animators = root.GetComponentsInChildren<Animator>(true);
                    foreach (var anim in animators)
                    {
                        try
                        {
                            var pars = anim.parameters;
                            AnimatorControllerParameter? mouthParam = null;
                            foreach (var p in pars)
                            {
                                if (p.type == AnimatorControllerParameterType.Float &&
                                    p.name.IndexOf("mouth", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    mouthParam = p;
                                    break;
                                }
                            }
                            if (mouthParam != null)
                            {
                                BingBongPlayerPlugin.mouthOpenParam = mouthParam.name;
                                BingBongPlayerPlugin.mouthOpenHash = mouthParam.nameHash;
                                BingBongPlayerPlugin.bingBongMouth = anim;
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }

            [HarmonyPrefix, HarmonyPatch("OnDestroy")]
            static void OnDestroy_Pre(Item __instance)
            {
                if (BingBongPlayerPlugin.bingBong == __instance.transform)
                {
                    BingBongPlayerPlugin.mouthOpenParam = null;
                    BingBongPlayerPlugin.mouthOpenHash = 0;
                    BingBongPlayerPlugin.bingBongMouth = null;
                    BingBongPlayerPlugin.bingBong = null;
                }
            }
        }
    }
}
