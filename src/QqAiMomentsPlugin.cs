using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Config;
using HarmonyLib;
using Sdk;
using Sdk.PlatformAPI;
using StudentAge.QQAIMoments.Ai;
using StudentAge.QQAIMoments.Config;
using StudentAge.QQAIMoments.Runtime;
using StudentAge.QQAIMoments.Social;
using StudentAge.QQAIMoments.Util;
using TheEntity;
using UnityEngine;
using View.Hint;
using View.Main;

namespace StudentAge.QQAIMoments
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class QqAiMomentsPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "studentage.qqai.moments";
        public const string PluginName = "StudentAge QQ AI Moments";
        public const string PluginVersion = "1.1.13";
        private const int RequiredUsageConsentVersion = 1;
        private const int UsageConsentWindowId = 1599010101;
        private const int RawTextConsentWindowId = 1599010102;
        private const int NativeConsentDialogGraceFrames = 120;
        private const int NativeConsentMaxSilentAttempts = 2;

        private static QqAiMomentsPlugin instance;
        private ManualLogSource logSource;
        private Harmony harmony;
        private PluginConfig config;
        private PersistentStore store;
        private DynamicKZoneRegistry registry;
        private RuntimeSaveSanitizer saveSanitizer;
        private KZoneMutationService mutation;
        private ReactionScheduler scheduler;
        private PersonaService personaService;
        private UsageTelemetryService telemetry;
        private bool initialized;
        private readonly System.Random random = new System.Random();
        private readonly object hotReloadLock = new object();
        private FileSystemWatcher configWatcher;
        private FileSystemWatcher personaWatcher;
        private string watchedConfigPath;
        private string watchedPersonaPath;
        private bool configReloadPending;
        private bool personaReloadPending;
        private DateTime configReloadDueUtc;
        private DateTime personaReloadDueUtc;
        private DateTime suppressConfigWatcherUntilUtc;
        private DateTime suppressPersonaWatcherUntilUtc;
        private bool usageConsentShowing;
        private bool rawTextConsentShowing;
        private int usageConsentDelayFrames = 120;
        private int rawTextConsentDelayFrames = 20;
        private int savePathRefreshFrames = 1800;
        private bool consentUiLikelyReady;
        private string lastSavePathRuntimeMessage;
        private string lastSavePathNoChangeDebugMessage;
        private string lastDirectoryRewriteMessage;
        private bool loggedConsentWaitingForUi;
        private bool loggedConsentUiReady;
        private bool loggedConsentCountdown;
        private bool updateFailureLogged;
        private bool guiConsentFailureLogged;
        private bool usageConsentGuiLogged;
        private bool rawTextConsentGuiLogged;
        private bool consentGuiFallbackEnabled;
        private int usageConsentShownFrame = -1;
        private int rawTextConsentShownFrame = -1;
        private int usageConsentNativeAttempts;
        private int rawTextConsentNativeAttempts;
        private float consentGuiEarliestRealtime;
        private int lastRuntimeTickFrame = -1;
        private bool controlLoopFallbackLogged;
        private bool freeReplyOptionsReadyLogged;
        private bool freeReplyOptionsFailureLogged;

        internal static void InstanceSafeCall(Action<QqAiMomentsPlugin> action)
        {
            try
            {
                if (instance != null)
                {
                    action(instance);
                }
            }
            catch (Exception ex)
            {
                if (instance != null && instance.logSource != null)
                {
                    instance.logSource.LogError(ex);
                }
            }
        }

        internal static void RewriteGameSaveDirectorySafe(ref string directory, string reason)
        {
            try
            {
                if (instance != null)
                {
                    instance.RewriteGameSaveDirectory(ref directory, reason);
                }
            }
            catch (Exception ex)
            {
                if (instance != null && instance.logSource != null)
                {
                    instance.logSource.LogError(ex);
                }
            }
        }

        private void Awake()
        {
            instance = this;
            logSource = Logger;
            RuntimeLog("[INFO][Startup] 插件启动：version=" + PluginVersion);
            config = PluginConfig.Bind(Config);
            Config.Save();
            RuntimeLog("[INFO][Config] 配置已绑定：cfg=" + Config.ConfigFilePath
                + ", aiEnabled=" + SafeBool(config.UseAi)
                + ", baseUrlConfigured=" + (!string.IsNullOrEmpty(SafeString(config.BaseUrl)))
                + ", retryAlternateEndpoint=" + SafeBool(config.RetryAlternateEndpoint)
                + ", model=" + SafeString(config.Model)
                + ", personaFile=" + SafeString(config.PersonaFile)
                + ", debug=" + SafeBool(config.DebugLog));

            store = new PersistentStore(config, DebugLog);
            registry = new DynamicKZoneRegistry(store, DebugLog);
            saveSanitizer = new RuntimeSaveSanitizer(store, DebugLog);
            mutation = new KZoneMutationService(store, registry, DebugLog, random);
            personaService = new PersonaService(config, DebugLog);
            personaService.LoadOrCreate();
            RuntimeLog("[INFO][Persona] 默认/用户人设文件已检查：" + SafeString(config.PersonaFile));
            telemetry = new UsageTelemetryService(config, this, RuntimeLog);
            consentGuiEarliestRealtime = Time.realtimeSinceStartup + 2f;

            NpcSelector selector = new NpcSelector(random);
            IAiClient aiClient = new OpenAiCompatibleClient(config);
            IAiClient fallback = new TemplateFallbackClient(random);
            PromptBuilder promptBuilder = new PromptBuilder(config, personaService);
            scheduler = new ReactionScheduler(config, aiClient, fallback, promptBuilder, selector, personaService, mutation, store, telemetry, this, RuntimeLog, random);

            harmony = new Harmony(PluginGuid + ".harmony");
            harmony.PatchAll();
            RuntimeLog("[INFO][Hooks] Harmony 补丁已安装；存档路径兼容修复已启用：Main/ModCtrl/SaveMgrEx/Game/EntryView + SaveMgr 低层访问兜底");
            SetupHotReloadWatchers();
            logSource.LogInfo(PluginName + " " + PluginVersion + " loaded.");
            logSource.LogInfo("AI BaseUrl 可在 BepInEx/config/studentage.qqai.moments.cfg 中配置；NPC 人设可编辑 " + config.PersonaFile.Value);
        }

        private void OnDestroy()
        {
            RuntimeLog("[INFO][Shutdown] 插件卸载，正在保存 sidecar 数据并释放 watcher/hook。");
            DisposeHotReloadWatchers();
            if (harmony != null)
            {
                harmony.UnpatchSelf();
            }
            if (store != null)
            {
                store.Save();
            }
            if (instance == this)
            {
                instance = null;
            }
        }

        private void Update()
        {
            RuntimeTick("plugin-update", false);
        }

        internal void RuntimeTickFromGameLoop()
        {
            RuntimeTick("game-control-update", true);
        }

        private void RuntimeTick(string source, bool fallbackLoop)
        {
            int frame = Time.frameCount;
            if (lastRuntimeTickFrame == frame)
            {
                return;
            }
            lastRuntimeTickFrame = frame;

            try
            {
                if (fallbackLoop && !controlLoopFallbackLogged)
                {
                    controlLoopFallbackLogged = true;
                    RuntimeLog("游戏主循环兜底检测已启用，QQAI 运行时逻辑会随游戏 Control.Update 推进。");
                }

                RefreshGameSavePathDuringStartup();
                ProcessHotReloads();
                EnsureFreeReplyOptionsAvailable();
                MaybeShowUsageShareConsent();
            }
            catch (Exception ex)
            {
                if (!updateFailureLogged)
                {
                    updateFailureLogged = true;
                    RuntimeLog("运行时循环异常，已保护后续帧不被刷屏；请把日志发给作者：source=" + source + " " + ex);
                }
            }
        }

        private void OnGUI()
        {
            if (!consentGuiFallbackEnabled)
            {
                return;
            }

            try
            {
                DrawConsentGuiFallback();
            }
            catch (Exception ex)
            {
                if (!guiConsentFailureLogged)
                {
                    guiConsentFailureLogged = true;
                    RuntimeLog("内置数据共享弹窗绘制失败：" + ex);
                }
            }
        }

        internal void RefreshGameSavePath(string reason)
        {
            try
            {
                string platformUserId = SafePlatformUserId();
                string userId = ResolveCurrentSaveUserId();
                if (string.IsNullOrEmpty(userId))
                {
                    RuntimeLogOnce(ref lastSavePathRuntimeMessage,
                        "[WARN][SavePath] 暂未识别真实存档用户，暂不改写存档路径：reason=" + reason
                        + ", platformUserId=" + SafeForLog(platformUserId)
                        + ", persistentDataPath=" + Application.persistentDataPath
                        + ", currentSavePath=" + SafeForLog(PathDefine.SAVE_PATH));
                    return;
                }

                string savePath = BuildSavePath(userId);
                string staleUserPath = Path.Combine(Application.persistentDataPath, "Saves", "user");
                bool targetExists = Directory.Exists(savePath);
                bool staleHasSave = Directory.Exists(staleUserPath) && HasAnySaveFile(staleUserPath);
                if (!targetExists && staleHasSave)
                {
                    RuntimeLogOnce(ref lastSavePathRuntimeMessage,
                        "[WARN][SavePath] 检测到仅 user 存档目录存在，暂不强制切换路径，避免隐藏玩家现有存档：reason=" + reason
                        + ", platformUserId=" + SafeForLog(platformUserId)
                        + ", resolvedUserId=" + SafeForLog(userId)
                        + ", target=" + savePath
                        + ", stale=" + staleUserPath);
                    return;
                }

                string oldSavePath = PathDefine.SAVE_PATH;
                string testSavePath = Path.Combine(Application.persistentDataPath, "Saves_Test", userId);
                string imgPath = Path.Combine(savePath, "Images");
                string musicPath = Path.Combine(savePath, "Musics");
                bool changed = !string.Equals(PathDefine.SAVE_PATH, savePath, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(PathDefine.TEST_SAVE_PATH, testSavePath, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(PathDefine.IMG_PATH, imgPath, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(PathDefine.MUSIC_PATH, musicPath, StringComparison.OrdinalIgnoreCase);

                PathDefine.SAVE_PATH = savePath;
                PathDefine.TEST_SAVE_PATH = testSavePath;
                PathDefine.IMG_PATH = imgPath;
                PathDefine.MUSIC_PATH = musicPath;

                if (changed)
                {
                    RuntimeLog("[INFO][SavePath] 已校正游戏存档目录：reason=" + reason
                        + ", platformUserId=" + SafeForLog(platformUserId)
                        + ", resolvedUserId=" + SafeForLog(userId)
                        + ", oldSavePath=" + SafeForLog(oldSavePath)
                        + ", newSavePath=" + savePath
                        + ", targetExists=" + targetExists
                        + ", staleUserHasSave=" + staleHasSave
                        + ", persistentDataPath=" + Application.persistentDataPath);
                }
                else
                {
                    DebugLogOnce(ref lastSavePathNoChangeDebugMessage, "[DEBUG][SavePath] 存档目录无需校正：reason=" + reason
                        + ", resolvedUserId=" + SafeForLog(userId)
                        + ", savePath=" + savePath);
                }
            }
            catch (Exception ex)
            {
                RuntimeLog("[ERROR][SavePath] 校正游戏存档目录失败：" + ex);
            }
        }

        internal void RewriteGameSaveDirectory(ref string directory, string reason)
        {
            try
            {
                if (string.IsNullOrEmpty(directory) || !IsUserSavePath(directory))
                {
                    return;
                }

                string userId = ResolveCurrentSaveUserId();
                if (string.IsNullOrEmpty(userId))
                {
                    return;
                }

                string corrected = BuildSavePath(userId);
                if (string.Equals(directory, corrected, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!Directory.Exists(corrected) && Directory.Exists(directory) && HasAnySaveFile(directory))
                {
                    RuntimeLogOnce(ref lastDirectoryRewriteMessage, "保留当前 user 存档目录，避免隐藏该目录中的现有存档。(" + reason + ")");
                    return;
                }

                directory = corrected;
                RefreshGameSavePath(reason);
                RuntimeLogOnce(ref lastDirectoryRewriteMessage, "已将本次存档访问改到真实 Steam 存档目录：" + corrected + " (" + reason + ")");
            }
            catch (Exception ex)
            {
                RuntimeLog("[ERROR][SavePath] 改写存档访问目录失败：" + ex);
            }
        }

        internal void MarkConsentUiLikelyReady()
        {
            consentUiLikelyReady = true;
            if (usageConsentDelayFrames > 20)
            {
                usageConsentDelayFrames = 20;
            }
        }

        private void RefreshGameSavePathDuringStartup()
        {
            if (savePathRefreshFrames <= 0)
            {
                return;
            }
            savePathRefreshFrames--;
            RefreshGameSavePath("startup-watch");
        }

        private string ResolveCurrentSaveUserId()
        {
            try
            {
                string userId = SafePlatformUserId();
                if (IsRealUserId(userId))
                {
                    return userId;
                }
            }
            catch
            {
            }

            return FindLikelySaveUserId();
        }

        private static string SafePlatformUserId()
        {
            try
            {
                BasePlatform platform = Platform.Current;
                return platform != null ? platform.GetUserId() : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsRealUserId(string userId)
        {
            return !string.IsNullOrEmpty(userId)
                && !string.Equals(userId, "user", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildSavePath(string userId)
        {
            return Path.Combine(Application.persistentDataPath, "Saves", userId);
        }

        private static bool IsUserSavePath(string path)
        {
            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string stale = Path.GetFullPath(Path.Combine(Application.persistentDataPath, "Saves", "user")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(fullPath, stale, StringComparison.OrdinalIgnoreCase);
        }

        private static string FindLikelySaveUserId()
        {
            try
            {
                string root = Path.Combine(Application.persistentDataPath, "Saves");
                if (!Directory.Exists(root))
                {
                    return null;
                }

                string best = null;
                DateTime bestTime = DateTime.MinValue;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (!IsRealUserId(name) || !HasAnySaveFile(dir))
                    {
                        continue;
                    }

                    DateTime time = Directory.GetLastWriteTimeUtc(dir);
                    string global = Path.Combine(dir, "_global");
                    if (File.Exists(global))
                    {
                        time = File.GetLastWriteTimeUtc(global);
                    }

                    if (best == null || time > bestTime)
                    {
                        best = name;
                        bestTime = time;
                    }
                }
                return best;
            }
            catch
            {
                return null;
            }
        }

        private static bool HasAnySaveFile(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return false;
                }
                if (File.Exists(Path.Combine(directory, "_global")))
                {
                    return true;
                }
                return Directory.GetFiles(directory, "*.save").Length > 0
                    || Directory.GetFiles(directory, "*.autosave").Length > 0
                    || Directory.GetFiles(directory, "*.quicksave").Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private void RuntimeLogOnce(ref string lastMessage, string message)
        {
            if (string.Equals(lastMessage, message, StringComparison.Ordinal))
            {
                return;
            }
            lastMessage = message;
            RuntimeLog(message);
        }

        private void EnsureFreeReplyOptionsAvailable()
        {
            try
            {
                if (Cfg.KZoneCommentCfgMap == null)
                {
                    return;
                }

                KZoneFreeReplyBridge.InjectFreeReplyOptions();
                if (!freeReplyOptionsReadyLogged)
                {
                    freeReplyOptionsReadyLogged = true;
                    RuntimeLog("QQ空间自由回复入口配置已就绪。");
                }
            }
            catch (Exception ex)
            {
                if (!freeReplyOptionsFailureLogged)
                {
                    freeReplyOptionsFailureLogged = true;
                    RuntimeLog("QQ空间自由回复入口配置失败：" + ex);
                }
            }
        }

        private void DrawConsentGuiFallback()
        {
            if (config == null || Time.realtimeSinceStartup < consentGuiEarliestRealtime)
            {
                return;
            }

            if (ShouldAskUsageShareConsent())
            {
                if (!usageConsentGuiLogged)
                {
                    usageConsentGuiLogged = true;
                    RuntimeLog("正在显示内置大字版数据共享选择弹窗。");
                }
                Rect rect = CenteredConsentRect(760f, 420f);
                GUI.ModalWindow(UsageConsentWindowId, rect, DrawUsageConsentWindow, "QQ空间AI数据共享");
                return;
            }

            if (config.ShareUsageData != null
                && config.ShareUsageData.Value
                && config.ShareUsageRawTextPrompted != null
                && !config.ShareUsageRawTextPrompted.Value)
            {
                if (!rawTextConsentGuiLogged)
                {
                    rawTextConsentGuiLogged = true;
                    RuntimeLog("正在显示内置大字版原文共享选择弹窗。");
                }
                Rect rect = CenteredConsentRect(760f, 390f);
                GUI.ModalWindow(RawTextConsentWindowId, rect, DrawRawTextConsentWindow, "是否共享玩家原文");
            }
        }

        private static Rect CenteredConsentRect(float preferredWidth, float preferredHeight)
        {
            float margin = 40f;
            float width = Mathf.Min(preferredWidth, Mathf.Max(360f, Screen.width - margin * 2f));
            float height = Mathf.Min(preferredHeight, Mathf.Max(300f, Screen.height - margin * 2f));
            return new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
        }

        private void DrawUsageConsentWindow(int id)
        {
            DrawConsentContent(
                "为了改进 NPC 回复质量，是否共享 QQ 空间 AI 互动数据？",
                "会上传：触发类型、NPC、好感/关系区间、成功或失败、回复长度、模板感判断、重写次数、点赞/好感动作摘要。",
                "不会上传：API Key、BaseUrl、系统用户名/目录、机器名、存档名。",
                "默认关闭；不同意不影响游戏。之后可在配置 [Privacy] 中随时关闭。",
                "同意共享",
                "不共享",
                AcceptUsageShare,
                DeclineUsageShare);
        }

        private void DrawRawTextConsentWindow(int id)
        {
            DrawConsentContent(
                "是否额外共享玩家原文和 AI 回复原文？",
                "用途：判断 NPC 是否答非所问、是否太像模板，方便后续改进提示词和人设。",
                "同意后会先遮罩邮箱、网址、手机号/长数字、密钥样式和本机路径，并限制单段长度。",
                "不同意也可以继续只共享统计数据；之后可在 [Privacy] 中修改。",
                "共享原文",
                "只共享统计",
                AcceptRawTextShare,
                DeclineRawTextShare);
        }

        private static void DrawConsentContent(
            string title,
            string paragraph1,
            string paragraph2,
            string paragraph3,
            string acceptText,
            string declineText,
            Action accept,
            Action decline)
        {
            int bodySize = Mathf.Clamp(Screen.height / 38, 18, 26);
            int titleSize = Mathf.Clamp(bodySize + 4, 22, 32);
            GUIStyle titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = titleSize,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
                alignment = TextAnchor.UpperLeft
            };
            GUIStyle bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = bodySize,
                wordWrap = true,
                alignment = TextAnchor.UpperLeft
            };
            GUIStyle buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.Clamp(bodySize, 18, 26),
                fontStyle = FontStyle.Bold
            };

            GUILayout.BeginVertical();
            GUILayout.Space(12);
            GUILayout.Label(title, titleStyle);
            GUILayout.Space(14);
            GUILayout.Label(paragraph1, bodyStyle);
            GUILayout.Space(8);
            GUILayout.Label(paragraph2, bodyStyle);
            GUILayout.Space(8);
            GUILayout.Label(paragraph3, bodyStyle);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(acceptText, buttonStyle, GUILayout.Height(Mathf.Clamp(Screen.height / 13, 54, 72))))
            {
                accept();
            }
            GUILayout.Space(18);
            if (GUILayout.Button(declineText, buttonStyle, GUILayout.Height(Mathf.Clamp(Screen.height / 13, 54, 72))))
            {
                decline();
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            GUILayout.EndVertical();
        }


        private void MaybeShowUsageShareConsent()
        {
            if (config == null || config.ShareUsageDataPrompted == null || config.ShareUsageData == null)
            {
                return;
            }

            if (!consentUiLikelyReady)
            {
                if (!IsConsentUiReady())
                {
                    if (ShouldAskUsageShareConsent() && !loggedConsentWaitingForUi)
                    {
                        loggedConsentWaitingForUi = true;
                        RuntimeLog("数据共享选择等待主菜单 UI 就绪。");
                    }
                    return;
                }
                consentUiLikelyReady = true;
                if (usageConsentDelayFrames > 20)
                {
                    usageConsentDelayFrames = 20;
                }
                if (!loggedConsentUiReady)
                {
                    loggedConsentUiReady = true;
                    RuntimeLog("主菜单 UI 已就绪，准备显示数据共享选择。");
                }
            }

            if (ShouldAskUsageShareConsent())
            {
                if (usageConsentShowing)
                {
                    CheckUsageConsentDialogLiveness();
                    return;
                }
                if (usageConsentDelayFrames > 0)
                {
                    if (!loggedConsentCountdown)
                    {
                        loggedConsentCountdown = true;
                        RuntimeLog("数据共享选择将在主菜单稳定后弹出。");
                    }
                    usageConsentDelayFrames--;
                    return;
                }
                RuntimeLog("正在显示数据共享选择弹窗。");
                ShowUsageShareDialog();
                return;
            }

            if (config.ShareUsageData.Value
                && config.ShareUsageRawTextPrompted != null
                && !config.ShareUsageRawTextPrompted.Value)
            {
                if (rawTextConsentShowing)
                {
                    CheckRawTextConsentDialogLiveness();
                    return;
                }
                if (rawTextConsentDelayFrames > 0)
                {
                    rawTextConsentDelayFrames--;
                    return;
                }
                ShowRawTextShareDialog();
            }
        }

        private void ShowUsageShareDialog()
        {
            usageConsentShowing = true;
            try
            {
                if (UIMgr.IsViewOpeningOrOpened<CommonComfirmView>())
                {
                    usageConsentShowing = false;
                    usageConsentDelayFrames = 30;
                    DebugLog("[DEBUG][Consent] 官方确认框已有实例打开，延后显示数据共享选择。");
                    return;
                }

                HintHelper.ShowConfirm(
                    UsageConsentDescription(),
                    new Action(AcceptUsageShare),
                    new Action(DeclineUsageShare),
                    true,
                    "QQ空间AI数据共享",
                    "同意共享",
                    "不共享",
                    "默认关闭；不同意不会影响游戏和插件功能。",
                    false);
                consentGuiFallbackEnabled = false;
                usageConsentShownFrame = Time.frameCount;
                usageConsentNativeAttempts++;
                RuntimeLog("[INFO][Consent] 使用官方原生确认框显示数据共享选择。attempt=" + usageConsentNativeAttempts);
            }
            catch (Exception ex)
            {
                EnableConsentGuiFallback("数据共享选择", ex);
            }
        }

        private bool ShouldAskUsageShareConsent()
        {
            if (config == null || config.ShareUsageDataPrompted == null)
            {
                return false;
            }

            if (!config.ShareUsageDataPrompted.Value)
            {
                return true;
            }

            return config.ShareUsageDataPromptVersion != null
                && config.ShareUsageDataPromptVersion.Value < RequiredUsageConsentVersion;
        }

        private bool IsConsentUiReady()
        {
            try
            {
                return UIMgr.IsViewOpened<EntryView>() || UIMgr.IsViewOpened<MainView>();
            }
            catch
            {
                return false;
            }
        }

        private void ShowRawTextShareDialog()
        {
            rawTextConsentShowing = true;
            try
            {
                if (UIMgr.IsViewOpeningOrOpened<CommonComfirmView>())
                {
                    rawTextConsentShowing = false;
                    rawTextConsentDelayFrames = 30;
                    DebugLog("[DEBUG][Consent] 官方确认框已有实例打开，延后显示原文共享选择。");
                    return;
                }

                HintHelper.ShowConfirm(
                    RawTextConsentDescription(),
                    new Action(AcceptRawTextShare),
                    new Action(DeclineRawTextShare),
                    true,
                    "是否共享玩家原文",
                    "共享原文",
                    "只共享统计",
                    "不同意也可以继续只共享统计数据；之后可在 [Privacy] 中修改。",
                    false);
                consentGuiFallbackEnabled = false;
                rawTextConsentShownFrame = Time.frameCount;
                rawTextConsentNativeAttempts++;
                RuntimeLog("[INFO][Consent] 使用官方原生确认框显示原文共享选择。attempt=" + rawTextConsentNativeAttempts);
            }
            catch (Exception ex)
            {
                EnableConsentGuiFallback("原文共享选择", ex);
            }
        }

        private void CheckUsageConsentDialogLiveness()
        {
            if (!IsNativeConsentDialogLost(usageConsentShownFrame))
            {
                return;
            }

            usageConsentShowing = false;
            usageConsentShownFrame = -1;
            usageConsentDelayFrames = 45;
            RuntimeLog("[WARN][Consent] 数据共享确认框已关闭但没有收到选择回调，将重新显示。attempt=" + usageConsentNativeAttempts);
            if (usageConsentNativeAttempts >= NativeConsentMaxSilentAttempts)
            {
                EnableConsentGuiFallback("数据共享选择无回调", null);
            }
        }

        private void CheckRawTextConsentDialogLiveness()
        {
            if (!IsNativeConsentDialogLost(rawTextConsentShownFrame))
            {
                return;
            }

            rawTextConsentShowing = false;
            rawTextConsentShownFrame = -1;
            rawTextConsentDelayFrames = 45;
            RuntimeLog("[WARN][Consent] 原文共享确认框已关闭但没有收到选择回调，将重新显示。attempt=" + rawTextConsentNativeAttempts);
            if (rawTextConsentNativeAttempts >= NativeConsentMaxSilentAttempts)
            {
                EnableConsentGuiFallback("原文共享选择无回调", null);
            }
        }

        private bool IsNativeConsentDialogLost(int shownFrame)
        {
            if (shownFrame < 0 || Time.frameCount - shownFrame <= NativeConsentDialogGraceFrames)
            {
                return false;
            }

            try
            {
                return !UIMgr.IsViewOpeningOrOpened<CommonComfirmView>();
            }
            catch
            {
                return true;
            }
        }

        private void EnableConsentGuiFallback(string dialogName, Exception ex)
        {
            usageConsentShowing = false;
            rawTextConsentShowing = false;
            consentGuiFallbackEnabled = true;
            consentGuiEarliestRealtime = Mathf.Min(consentGuiEarliestRealtime, Time.realtimeSinceStartup);
            string error = ex != null ? ex.ToString() : "native dialog closed without callback";
            RuntimeLog("[WARN][Consent] 官方原生确认框不可用，已切换到内置大字兜底弹窗：" + dialogName + "，错误=" + error);
        }

        private static string UsageConsentDescription()
        {
            return "这个插件可以收集 QQ 空间 AI 互动的运行数据，帮助作者判断回复是否自然、是否重复、是否经常失败。"
                + "\n\n会共享：角色编号、互动类型、是否成功、失败原因、回复长度、重写次数、是否命中去重、基础评分等统计信息。"
                + "\n\n不会共享：API Key、BaseUrl、系统用户名、机器名、本地目录、存档名。"
                + "\n\n默认不开启。你选择“不共享”后，游戏和插件功能仍会正常使用。";
        }

        private static string RawTextConsentDescription()
        {
            return "如果你同意共享原文，插件会额外上传玩家发出的 QQ 空间文字和 AI 回复文字，用来分析“答非所问”“模板味”“角色不像本人”等问题。"
                + "\n\n上传前会做基础遮罩：邮箱、网址、手机号/长数字、疑似密钥、本机路径会被替换，并限制单段长度。"
                + "\n\n如果你选择“只共享统计”，插件仍只共享统计指标，不会上传玩家原文。";
        }

        private void AcceptUsageShare()
        {
            try
            {
                config.ShareUsageData.Value = true;
                config.ShareUsageDataPrompted.Value = true;
                if (config.ShareUsageDataPromptVersion != null)
                {
                    config.ShareUsageDataPromptVersion.Value = RequiredUsageConsentVersion;
                }
                Config.Save();
                RuntimeLog("已启用 QQ AI 使用数据共享；原文共享将单独询问。");
                usageConsentNativeAttempts = 0;
                usageConsentShownFrame = -1;
            }
            catch (Exception ex)
            {
                RuntimeLog("保存数据共享选择失败：" + ex.Message);
            }
            usageConsentShowing = false;
            rawTextConsentDelayFrames = 20;
        }

        private void DeclineUsageShare()
        {
            try
            {
                config.ShareUsageData.Value = false;
                config.ShareUsageDataPrompted.Value = true;
                if (config.ShareUsageDataPromptVersion != null)
                {
                    config.ShareUsageDataPromptVersion.Value = RequiredUsageConsentVersion;
                }
                if (config.ShareUsageRawText != null)
                {
                    config.ShareUsageRawText.Value = false;
                }
                if (config.ShareUsageRawTextPrompted != null)
                {
                    config.ShareUsageRawTextPrompted.Value = true;
                }
                Config.Save();
                RuntimeLog("已关闭 QQ AI 使用数据共享。");
                usageConsentNativeAttempts = 0;
                rawTextConsentNativeAttempts = 0;
                usageConsentShownFrame = -1;
                rawTextConsentShownFrame = -1;
            }
            catch (Exception ex)
            {
                RuntimeLog("保存数据共享选择失败：" + ex.Message);
            }
            usageConsentShowing = false;
            rawTextConsentShowing = false;
        }

        private void AcceptRawTextShare()
        {
            try
            {
                config.ShareUsageRawText.Value = true;
                config.ShareUsageRawTextPrompted.Value = true;
                Config.Save();
                RuntimeLog("已启用 QQ AI 原文共享：玩家输入和 AI 回复会经基础遮罩后进入共享数据。");
                rawTextConsentNativeAttempts = 0;
                rawTextConsentShownFrame = -1;
            }
            catch (Exception ex)
            {
                RuntimeLog("保存原文共享选择失败：" + ex.Message);
            }
            rawTextConsentShowing = false;
        }

        private void DeclineRawTextShare()
        {
            try
            {
                config.ShareUsageRawText.Value = false;
                config.ShareUsageRawTextPrompted.Value = true;
                Config.Save();
                RuntimeLog("已关闭 QQ AI 原文共享，仅共享统计指标。");
                rawTextConsentNativeAttempts = 0;
                rawTextConsentShownFrame = -1;
            }
            catch (Exception ex)
            {
                RuntimeLog("保存原文共享选择失败：" + ex.Message);
            }
            rawTextConsentShowing = false;
        }

        internal void OnKZoneInit(KZoneData kzone)
        {
            EnsureRuntime(kzone);
            registry.InjectAll(kzone);
        }

        internal void OnBeforeKZoneDataAccess(KZoneData kzone)
        {
            if (EnsureRuntime(kzone))
            {
                registry.InjectAll(kzone);
            }
        }

        internal void OnPostCustom(KZoneContentData result)
        {
            if (!EnsureRuntime(Singleton<RoleMgr>.Ins.GetKZoneData()) || mutation.IsApplyingAiMutation)
            {
                return;
            }
            scheduler.OnPlayerPosted(Singleton<RoleMgr>.Ins.GetKZoneData(), result);
        }

        internal void OnPost(KZoneData kzone, int id)
        {
            if (!EnsureRuntime(kzone) || mutation.IsApplyingAiMutation || kzone == null || kzone.datas == null)
            {
                return;
            }
            KZoneContentData content;
            if (kzone.datas.TryGetValue(id, out content) && content != null && content.RoleId == 0)
            {
                scheduler.OnPlayerPosted(kzone, content);
            }
        }

        internal void OnComment(KZoneData kzone, int commentId, int targetId)
        {
            if (KZoneFreeReplyBridge.IsFreeReplyOption(commentId) || !EnsureRuntime(kzone) || mutation.IsApplyingAiMutation)
            {
                return;
            }
            mutation.PersistExistingCommentIfMissing(kzone, commentId, "player-option-comment");
            scheduler.OnPlayerCommented(kzone, commentId);
        }

        internal bool OpenFreeReplyInput(KZoneData kzone, int targetId)
        {
            if (!EnsureRuntime(kzone))
            {
                return false;
            }
            bool opened = KZoneFreeReplyBridge.TryOpenFreeReplyInput(kzone, targetId, mutation, scheduler, DebugLog);
            if (!opened)
            {
                DebugLog("自由回复入口未打开，targetId=" + targetId);
            }
            return opened;
        }

        internal bool OpenFreeReplyInputForContent(KZoneData kzone, int contentId)
        {
            if (!EnsureRuntime(kzone))
            {
                return false;
            }
            bool opened = KZoneFreeReplyBridge.TryOpenFreeReplyInputForContent(kzone, contentId, mutation, scheduler, DebugLog);
            if (!opened)
            {
                DebugLog("自由回复内容入口未打开，contentId=" + contentId);
            }
            return opened;
        }

        internal bool OpenFreeReplyInputForComment(KZoneData kzone, int contentId, int commentId)
        {
            if (!EnsureRuntime(kzone))
            {
                return false;
            }
            bool opened = KZoneFreeReplyBridge.TryOpenFreeReplyInputForComment(kzone, contentId, commentId, mutation, scheduler, DebugLog);
            if (!opened)
            {
                DebugLog("自由回复评论入口未打开，contentId=" + contentId + ", commentId=" + commentId);
            }
            return opened;
        }

        internal void OnBeforeRoleSave()
        {
            if (!initialized || saveSanitizer == null)
            {
                return;
            }
            KZoneData kzone = SafeGetKZoneData();
            saveSanitizer.BeforeGameSave(kzone);
        }

        internal void OnAfterRoleSave()
        {
            if (!initialized || saveSanitizer == null)
            {
                return;
            }
            KZoneData kzone = SafeGetKZoneData();
            saveSanitizer.AfterGameSave(kzone);
            if (store != null)
            {
                store.Save();
            }
        }

        internal void OnNewRound(KZoneData kzone)
        {
            if (!EnsureRuntime(kzone))
            {
                return;
            }
            registry.InjectAll(kzone);
            scheduler.OnNewRound(kzone);
        }

        internal void OnAltCTestHotkey()
        {
            KZoneData kzone = SafeGetKZoneData();
            if (!EnsureRuntime(kzone))
            {
                logSource.LogWarning("[QQAI] Alt+C 测试跳过：运行时尚未就绪。");
                return;
            }

            registry.InjectAll(kzone);
            if (scheduler.TriggerTestReplyToLatestPlayerMoment(kzone))
            {
                logSource.LogInfo("[QQAI] Alt+C 测试入口已触发：NPC AI 将回复主角最近动态。");
            }
            else
            {
                logSource.LogWarning("[QQAI] Alt+C 测试入口未触发，请确认已读档、QQ 空间有主角动态且存在可用 NPC。");
            }
        }

        internal void OnGameLoadStarting(string saveName)
        {
            if (scheduler != null)
            {
                scheduler.InvalidateRuntimeJobs("开始读档 " + (saveName ?? ""));
            }
        }

        internal void OnGameSaveStarting(string saveName)
        {
            if (!initialized || store == null || string.IsNullOrEmpty(saveName))
            {
                return;
            }
            store.CopyCurrentDataToSaveSlot(saveName);
        }

        private bool EnsureRuntime(KZoneData kzone)
        {
            if (kzone == null || Cfg.PersonCfgMap == null || Cfg.KZoneContentCfgMap == null || Cfg.KZoneCommentCfgMap == null)
            {
                DebugLog("[DEBUG][Runtime] QQ 空间运行时未就绪：kzone=" + (kzone != null)
                    + ", personCfg=" + (Cfg.PersonCfgMap != null)
                    + ", contentCfg=" + (Cfg.KZoneContentCfgMap != null)
                    + ", commentCfg=" + (Cfg.KZoneCommentCfgMap != null));
                return false;
            }
            if (!initialized)
            {
                RuntimeLog("[INFO][Runtime] 首次初始化 QQAI 运行时：contentCount=" + SafeKZoneContentCount(kzone));
                store.LoadForCurrentRole();
                personaService.LoadOrCreate();
                SetupPersonaWatcher();
                registry.InjectAll(kzone);
                registry.RemoveUnknownDynamicArtifacts(kzone);
                initialized = true;
                RuntimeLog("[INFO][Runtime] QQAI 运行时初始化完成：store=" + SafeForLog(store.CurrentPath)
                    + ", runtimeIdentity=" + SafeForLog(store.CurrentRuntimeIdentity)
                    + ", contentCount=" + SafeKZoneContentCount(kzone));
            }
            else if (store.IsCurrentRolePathChanged())
            {
                RuntimeLog("[INFO][Runtime] 检测到游戏存档槽变化，重新加载 QQAI sidecar：oldStore=" + SafeForLog(store.CurrentPath));
                saveSanitizer.RemoveStoreArtifacts(kzone);
                store.LoadForCurrentRole();
                registry.InjectAll(kzone);
                registry.RemoveUnknownDynamicArtifacts(kzone);
                RuntimeLog("[INFO][Runtime] QQAI sidecar 已切换：newStore=" + SafeForLog(store.CurrentPath)
                    + ", runtimeIdentity=" + SafeForLog(store.CurrentRuntimeIdentity));
            }
            return true;
        }

        private void SetupHotReloadWatchers()
        {
            SetupConfigWatcher();
            SetupPersonaWatcher();
        }

        private void SetupConfigWatcher()
        {
            try
            {
                string path = Config != null ? Config.ConfigFilePath : null;
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }

                path = Path.GetFullPath(path);
                if (configWatcher != null && string.Equals(path, watchedConfigPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                DisposeWatcher(ref configWatcher);
                watchedConfigPath = path;
                configWatcher = CreateSingleFileWatcher(path, QueueConfigReload, "config");
                DebugLog("[DEBUG][HotReload] 已启用配置热加载监听：" + path);
            }
            catch (Exception ex)
            {
                RuntimeLog("[WARN][HotReload] 配置热加载监听启动失败：" + ex);
            }
        }

        private void SetupPersonaWatcher()
        {
            try
            {
                if (config == null || config.HotReloadPersonas == null || !config.HotReloadPersonas.Value)
                {
                    DisposeWatcher(ref personaWatcher);
                    watchedPersonaPath = null;
                    return;
                }

                string path = Path.GetFullPath(PathUtil.ConfigRelative(config.PersonaFile.Value));
                if (personaWatcher != null && string.Equals(path, watchedPersonaPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                DisposeWatcher(ref personaWatcher);
                watchedPersonaPath = path;
                personaWatcher = CreateSingleFileWatcher(path, QueuePersonaReload, "persona");
                DebugLog("[DEBUG][HotReload] 已启用人设热加载监听：" + path);
            }
            catch (Exception ex)
            {
                RuntimeLog("[WARN][HotReload] 人设热加载监听启动失败：" + ex);
            }
        }

        private FileSystemWatcher CreateSingleFileWatcher(string filePath, Action queueReload, string label)
        {
            string directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory))
            {
                directory = ".";
            }
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            FileSystemWatcher watcher = new FileSystemWatcher(directory, Path.GetFileName(filePath));
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;
            FileSystemEventHandler changed = delegate { queueReload(); };
            RenamedEventHandler renamed = delegate { queueReload(); };
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += renamed;
            watcher.Error += delegate(object sender, ErrorEventArgs args)
            {
                RuntimeLog("[WARN][HotReload] 文件监听器异常：type=" + SafeForLog(label)
                    + ", path=" + SafeForLog(filePath)
                    + ", error=" + args.GetException());
            };
            watcher.EnableRaisingEvents = true;
            return watcher;
        }

        private void QueueConfigReload()
        {
            if (DateTime.UtcNow < suppressConfigWatcherUntilUtc)
            {
                return;
            }
            QueueHotReload(ref configReloadPending, ref configReloadDueUtc);
        }

        private void QueuePersonaReload()
        {
            if (DateTime.UtcNow < suppressPersonaWatcherUntilUtc)
            {
                return;
            }
            QueueHotReload(ref personaReloadPending, ref personaReloadDueUtc);
        }

        private void QueueHotReload(ref bool pending, ref DateTime dueUtc)
        {
            int debounceMs = 800;
            try
            {
                if (config != null && config.HotReloadDebounceMs != null)
                {
                    debounceMs = Math.Max(100, Math.Min(5000, config.HotReloadDebounceMs.Value));
                }
            }
            catch
            {
            }

            lock (hotReloadLock)
            {
                pending = true;
                dueUtc = DateTime.UtcNow.AddMilliseconds(debounceMs);
            }
        }

        private void ProcessHotReloads()
        {
            bool reloadConfig = false;
            bool reloadPersona = false;
            DateTime now = DateTime.UtcNow;
            lock (hotReloadLock)
            {
                if (configReloadPending && now >= configReloadDueUtc)
                {
                    configReloadPending = false;
                    reloadConfig = true;
                }
                if (personaReloadPending && now >= personaReloadDueUtc)
                {
                    personaReloadPending = false;
                    reloadPersona = true;
                }
            }

            if (reloadConfig)
            {
                ReloadConfigFromDisk();
            }
            if (reloadPersona)
            {
                ReloadPersonasFromDisk("人设文件热加载");
            }
        }

        private void ReloadConfigFromDisk()
        {
            if (config == null || config.HotReloadConfig == null || !config.HotReloadConfig.Value)
            {
                DebugLog("配置文件发生变化，但 HotReloadConfig 已关闭，忽略本次热加载。");
                return;
            }

            string oldPersonaPath = null;
            try
            {
                oldPersonaPath = Path.GetFullPath(PathUtil.ConfigRelative(config.PersonaFile.Value));
            }
            catch
            {
            }

            try
            {
                suppressConfigWatcherUntilUtc = DateTime.UtcNow.AddSeconds(1);
                Config.Reload();
                Config.Save();
                suppressConfigWatcherUntilUtc = DateTime.UtcNow.AddSeconds(1);
                RuntimeLog("[INFO][HotReload] 配置文件已热加载：cfg=" + SafeForLog(Config.ConfigFilePath)
                    + ", aiEnabled=" + SafeBool(config.UseAi)
                    + ", model=" + SafeString(config.Model)
                    + ", retryAlternateEndpoint=" + SafeBool(config.RetryAlternateEndpoint)
                    + ", personaFile=" + SafeString(config.PersonaFile)
                    + "；旧请求不会被强制中断，但旧队列结果会被丢弃。");
                if (scheduler != null)
                {
                    scheduler.InvalidateRuntimeJobs("配置文件热加载");
                }

                SetupHotReloadWatchers();

                string newPersonaPath = null;
                try
                {
                    newPersonaPath = Path.GetFullPath(PathUtil.ConfigRelative(config.PersonaFile.Value));
                }
                catch
                {
                }
                if (initialized && !string.Equals(oldPersonaPath, newPersonaPath, StringComparison.OrdinalIgnoreCase))
                {
                    ReloadPersonasFromDisk("配置热加载后切换人设文件");
                }
            }
            catch (Exception ex)
            {
                RuntimeLog("[ERROR][HotReload] 配置文件热加载失败：" + ex);
            }
        }

        private void ReloadPersonasFromDisk(string reason)
        {
            if (config == null || config.HotReloadPersonas == null || !config.HotReloadPersonas.Value)
            {
                DebugLog("人设文件发生变化，但 HotReloadPersonas 已关闭，忽略本次热加载。");
                return;
            }
            if (!initialized || personaService == null)
            {
                DebugLog("人设文件发生变化，将在 QQ 空间运行时初始化后加载。");
                return;
            }

            try
            {
                suppressPersonaWatcherUntilUtc = DateTime.UtcNow.AddSeconds(1);
                personaService.LoadOrCreate();
                suppressPersonaWatcherUntilUtc = DateTime.UtcNow.AddSeconds(1);
                if (scheduler != null)
                {
                    scheduler.InvalidateRuntimeJobs(reason);
                }
                SetupPersonaWatcher();
                RuntimeLog("[INFO][HotReload] " + reason + "完成：personaFile=" + SafeString(config.PersonaFile)
                    + "；旧请求不会被强制中断，但旧队列结果会被丢弃。");
            }
            catch (Exception ex)
            {
                RuntimeLog("[ERROR][HotReload] " + reason + "失败：" + ex);
            }
        }

        private void DisposeHotReloadWatchers()
        {
            DisposeWatcher(ref configWatcher);
            DisposeWatcher(ref personaWatcher);
        }

        private static void DisposeWatcher(ref FileSystemWatcher watcher)
        {
            if (watcher == null)
            {
                return;
            }
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
            }
            watcher = null;
        }

        private static KZoneData SafeGetKZoneData()
        {
            try
            {
                return Singleton<RoleMgr>.Ins.GetKZoneData();
            }
            catch
            {
                return null;
            }
        }

        private void DebugLog(string message)
        {
            if (config != null && config.DebugLog.Value)
            {
                logSource.LogInfo("[QQAI] " + message);
            }
        }

        private void DebugLogOnce(ref string lastMessage, string message)
        {
            if (string.Equals(lastMessage, message, StringComparison.Ordinal))
            {
                return;
            }
            lastMessage = message;
            DebugLog(message);
        }

        internal void HookDebugLog(string hook, string message)
        {
            DebugLog("[DEBUG][Hook/" + hook + "] " + message);
        }

        private void RuntimeLog(string message)
        {
            if (logSource != null)
            {
                logSource.LogInfo("[QQAI] " + message);
            }
        }

        private static string SafeBool(ConfigEntry<bool> entry)
        {
            try
            {
                return entry != null ? entry.Value.ToString() : "null";
            }
            catch
            {
                return "error";
            }
        }

        private static string SafeString(ConfigEntry<string> entry)
        {
            try
            {
                return entry != null ? SafeForLog(entry.Value) : "null";
            }
            catch
            {
                return "error";
            }
        }

        private static string SafeForLog(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }
            return value.Replace("\r", " ").Replace("\n", " ");
        }

        private static int SafeKZoneContentCount(KZoneData kzone)
        {
            try
            {
                return kzone != null && kzone.datas != null ? kzone.datas.Count : -1;
            }
            catch
            {
                return -1;
            }
        }
    }
}
