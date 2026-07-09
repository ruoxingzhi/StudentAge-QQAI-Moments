using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using StudentAge.QQAIMoments.Config;
using StudentAge.QQAIMoments.Models;
using StudentAge.QQAIMoments.Util;
using TheEntity;
using Sdk;

namespace StudentAge.QQAIMoments.Runtime
{
    internal sealed class PersistentStore
    {
        private readonly PluginConfig config;
        private readonly Action<string> log;
        private string currentPath;

        internal AiStoreData Data { get; private set; } = new AiStoreData();
        internal string CurrentPath { get { return currentPath; } }

        internal string CurrentRuntimeIdentity
        {
            get
            {
                return CurrentRoleShortGuid() + "|round=" + CurrentRound();
            }
        }

        internal PersistentStore(PluginConfig config, Action<string> log)
        {
            this.config = config;
            this.log = log;
        }

        internal void LoadForCurrentRole()
        {
            currentPath = ResolveSavePath();
            LoadFromCurrentPath();
        }

        internal bool EnsureLoadedForCurrentRole()
        {
            string path = ResolveSavePath();
            if (string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            currentPath = path;
            LoadFromCurrentPath();
            return true;
        }

        internal bool IsCurrentRolePathChanged()
        {
            string path = ResolveSavePath();
            return !string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase);
        }

        private void LoadFromCurrentPath()
        {
            try
            {
                if (File.Exists(currentPath))
                {
                    Data = JsonConvert.DeserializeObject<AiStoreData>(File.ReadAllText(currentPath)) ?? new AiStoreData();
                    Normalize();
                    int cleanupCount = CleanupOnLoad();
                    if (cleanupCount > 0)
                    {
                        Save();
                        log("[INFO][Store] 已清理 AI 空间测试/重复数据：" + cleanupCount + " 项，path=" + currentPath + ", " + DataSummary());
                    }
                    log("[INFO][Store] 已加载 AI 空间存档：" + currentPath + ", " + FileSummary(currentPath) + ", " + DataSummary());
                }
                else
                {
                    string legacyPath = ResolveLegacySavePath();
                    if (!string.Equals(legacyPath, currentPath, StringComparison.OrdinalIgnoreCase) && File.Exists(legacyPath))
                    {
                        Data = JsonConvert.DeserializeObject<AiStoreData>(File.ReadAllText(legacyPath)) ?? new AiStoreData();
                        Normalize();
                        Save();
                        log("[INFO][Store] 已迁移旧 AI 空间存档到当前游戏存档槽：legacy=" + legacyPath
                            + ", current=" + currentPath + ", " + DataSummary());
                    }
                    else
                    {
                        Data = new AiStoreData();
                        log("[INFO][Store] 当前存档槽暂无 AI 空间 sidecar，创建新数据：path=" + currentPath);
                    }
                    Save();
                }
            }
            catch (Exception ex)
            {
                log("[ERROR][Store] 加载 AI 空间存档失败，使用空数据：path=" + currentPath
                    + ", file=" + FileSummary(currentPath)
                    + " " + ex);
                Data = new AiStoreData();
            }
        }

        internal void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(currentPath))
                {
                    currentPath = ResolveSavePath();
                }
                PathUtil.EnsureParent(currentPath);
                File.WriteAllText(currentPath, JsonConvert.SerializeObject(Data, Formatting.Indented));
            }
            catch (Exception ex)
            {
                log("[ERROR][Store] 保存 AI 空间存档失败：path=" + currentPath + ", " + DataSummary() + " " + ex);
            }
        }

        internal bool HasDedupe(string key)
        {
            return !string.IsNullOrEmpty(key) && Data.Dedupe.Contains(key);
        }

        internal void AddDedupe(string key)
        {
            if (!string.IsNullOrEmpty(key) && !Data.Dedupe.Contains(key))
            {
                Data.Dedupe.Add(key);
            }
        }

        internal int NextContentId()
        {
            int id = Data.NextContentId;
            Data.NextContentId++;
            return id;
        }

        internal int NextCommentId()
        {
            int id = Data.NextCommentId;
            Data.NextCommentId++;
            return id;
        }

        internal AiMomentRecord FindMoment(int contentId)
        {
            return Data.Moments.Find(m => m.ContentId == contentId);
        }

        internal AiCommentRecord FindComment(int commentId)
        {
            return Data.Comments.Find(c => c.CommentId == commentId);
        }

        internal bool HasCommentForPurpose(int contentId, int authorRoleId, string purpose)
        {
            return Data.Comments.Exists(c => c.ContentId == contentId && c.AuthorRoleId == authorRoleId && c.Purpose == purpose);
        }

        internal bool HasThumb(int contentId, int roleId)
        {
            return Data.Thumbs.Exists(t => t.ContentId == contentId && t.RoleId == roleId);
        }

        internal void AddThumb(int contentId, int roleId)
        {
            if (!HasThumb(contentId, roleId))
            {
                Data.Thumbs.Add(new AiThumbRecord { ContentId = contentId, RoleId = roleId, RoundNumber = CurrentRound() });
            }
        }

        internal void CopyCurrentDataToSaveSlot(string saveName)
        {
            if (string.IsNullOrEmpty(saveName) || Data == null)
            {
                return;
            }
            try
            {
                Save();
                string path = ResolveSavePathForSlot(Path.GetFileName(saveName));
                PathUtil.EnsureParent(path);
                File.WriteAllText(path, JsonConvert.SerializeObject(Data, Formatting.Indented));
                log("[INFO][Store] 已复制 AI 空间 sidecar 到新游戏存档槽：from=" + currentPath
                    + ", to=" + path + ", " + DataSummary());
                currentPath = path;
            }
            catch (Exception ex)
            {
                log("[ERROR][Store] 复制 AI 空间存档到新游戏存档槽失败：saveName=" + saveName
                    + ", current=" + currentPath + ", " + DataSummary() + " " + ex);
            }
        }

        private string ResolveSavePath()
        {
            return ResolveSavePathForSlot(CurrentSaveSlot());
        }

        private string ResolveSavePathForSlot(string saveSlot)
        {
            string root = PathUtil.ConfigRelative(config.StoreDirectory.Value);
            root = Path.Combine(root, PathUtil.SafeFileName(saveSlot));
            return Path.Combine(root, PathUtil.SafeFileName(CurrentRoleShortGuid()) + ".json");
        }

        private string ResolveLegacySavePath()
        {
            string root = PathUtil.ConfigRelative(config.StoreDirectory.Value);
            return Path.Combine(root, PathUtil.SafeFileName(CurrentRoleShortGuid()) + ".json");
        }

        private static string CurrentRoleShortGuid()
        {
            string guid = "default";
            try
            {
                Role role = Singleton<RoleMgr>.Ins.GetRole();
                if (role != null && !string.IsNullOrEmpty(role.guid))
                {
                    guid = role.GetShortGuid();
                }
            }
            catch
            {
                guid = "default";
            }
            return guid;
        }

        private static string CurrentSaveSlot()
        {
            try
            {
                string saveName = Game.GetLatestSaveName();
                if (!string.IsNullOrEmpty(saveName))
                {
                    return Path.GetFileName(saveName);
                }
            }
            catch
            {
            }

            return "unsaved";
        }

        internal static int CurrentRound()
        {
            try
            {
                return Singleton<RoundMgr>.Ins.GetRound();
            }
            catch
            {
                return 0;
            }
        }

        internal static bool IsRecordVisibleInCurrentRound(int roundNumber)
        {
            int currentRound = CurrentRound();
            return roundNumber <= 0 || currentRound <= 0 || roundNumber <= currentRound;
        }

        private string DataSummary()
        {
            try
            {
                if (Data == null)
                {
                    return "data=null";
                }
                return "moments=" + (Data.Moments != null ? Data.Moments.Count : 0)
                    + ", comments=" + (Data.Comments != null ? Data.Comments.Count : 0)
                    + ", thumbs=" + (Data.Thumbs != null ? Data.Thumbs.Count : 0)
                    + ", dedupe=" + (Data.Dedupe != null ? Data.Dedupe.Count : 0)
                    + ", nextContentId=" + Data.NextContentId
                    + ", nextCommentId=" + Data.NextCommentId
                    + ", runtimeIdentity=" + CurrentRuntimeIdentity;
            }
            catch
            {
                return "dataSummary=error";
            }
        }

        private static string FileSummary(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    return "filePath=empty";
                }
                FileInfo info = new FileInfo(path);
                if (!info.Exists)
                {
                    return "fileExists=false";
                }
                return "fileExists=true, length=" + info.Length + ", lastWriteUtc=" + info.LastWriteTimeUtc.ToString("o");
            }
            catch (Exception ex)
            {
                return "fileSummaryError=" + ex.Message;
            }
        }

        private void Normalize()
        {
            if (Data.Moments == null) Data.Moments = new List<AiMomentRecord>();
            if (Data.Comments == null) Data.Comments = new List<AiCommentRecord>();
            if (Data.Thumbs == null) Data.Thumbs = new List<AiThumbRecord>();
            if (Data.Dedupe == null) Data.Dedupe = new List<string>();
            if (Data.NextContentId < DynamicKZoneRegistry.ContentBase || Data.NextContentId >= 21000000)
            {
                Data.NextContentId = DynamicKZoneRegistry.ContentBase;
            }
            if (Data.NextCommentId < DynamicKZoneRegistry.CommentBase)
            {
                Data.NextCommentId = DynamicKZoneRegistry.CommentBase;
            }
        }

        private int CleanupOnLoad()
        {
            int removed = 0;
            if (Data == null)
            {
                return 0;
            }

            bool cleanupTests = config.CleanupHotkeyTestDataOnLoad != null && config.CleanupHotkeyTestDataOnLoad.Value;
            bool cleanupDuplicates = config.CleanupDuplicateAiCommentsOnLoad != null && config.CleanupDuplicateAiCommentsOnLoad.Value;
            if (!cleanupTests && !cleanupDuplicates)
            {
                return 0;
            }

            if (cleanupTests && Data.Dedupe != null)
            {
                removed += Data.Dedupe.RemoveAll(IsTestDedupeKey);
            }

            if (Data.Comments == null || Data.Comments.Count == 0)
            {
                return removed;
            }

            Dictionary<string, int> seen = cleanupDuplicates ? new Dictionary<string, int>() : null;
            for (int i = Data.Comments.Count - 1; i >= 0; i--)
            {
                AiCommentRecord comment = Data.Comments[i];
                if (comment == null)
                {
                    Data.Comments.RemoveAt(i);
                    removed++;
                    continue;
                }

                if (cleanupTests && IsTestComment(comment))
                {
                    Data.Comments.RemoveAt(i);
                    removed++;
                    continue;
                }

                if (cleanupDuplicates && IsGeneratedAiComment(comment))
                {
                    if (HasChildComments(comment.CommentId))
                    {
                        continue;
                    }
                    string normalized = NormalizeForCompare(comment.Content);
                    if (normalized.Length == 0)
                    {
                        continue;
                    }
                    string key = comment.ContentId + ":" + normalized;
                    if (seen.ContainsKey(key))
                    {
                        Data.Comments.RemoveAt(i);
                        removed++;
                    }
                    else
                    {
                        seen[key] = comment.CommentId;
                    }
                }
            }
            return removed;
        }

        private bool HasChildComments(int commentId)
        {
            if (commentId <= 0 || Data == null || Data.Comments == null)
            {
                return false;
            }
            for (int i = 0; i < Data.Comments.Count; i++)
            {
                AiCommentRecord candidate = Data.Comments[i];
                if (candidate != null && candidate.ParentCommentId == commentId)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsTestDedupeKey(string key)
        {
            return !string.IsNullOrEmpty(key)
                && key.StartsWith("alt-c-test", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTestComment(AiCommentRecord comment)
        {
            return comment != null
                && !string.IsNullOrEmpty(comment.Purpose)
                && comment.Purpose.StartsWith("test-", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGeneratedAiComment(AiCommentRecord comment)
        {
            return comment != null
                && comment.AuthorRoleId > 0
                && !IsPlayerWrittenPurpose(comment.Purpose);
        }

        private static bool IsPlayerWrittenPurpose(string purpose)
        {
            return !string.IsNullOrEmpty(purpose)
                && (purpose.StartsWith("player-free-reply", StringComparison.OrdinalIgnoreCase)
                    || purpose.StartsWith("player-option", StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeForCompare(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }
            char[] chars = value.ToCharArray();
            List<char> kept = new List<char>(chars.Length);
            for (int i = 0; i < chars.Length; i++)
            {
                char ch = chars[i];
                if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch) && !char.IsSymbol(ch))
                {
                    kept.Add(ch);
                }
            }
            return new string(kept.ToArray());
        }
    }
}
