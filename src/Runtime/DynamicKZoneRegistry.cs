using System;
using System.Collections.Generic;
using Config;
using Sdk;
using StudentAge.QQAIMoments.Models;

namespace StudentAge.QQAIMoments.Runtime
{
    internal sealed class DynamicKZoneRegistry
    {
        internal const int ContentBase = 17000000;
        internal const int CommentBase = 1600000000;
        private readonly PersistentStore store;
        private readonly Action<string> log;

        internal DynamicKZoneRegistry(PersistentStore store, Action<string> log)
        {
            this.store = store;
            this.log = log;
        }

        internal void InjectAll(KZoneData kzone)
        {
            if (!EnsureMaps())
            {
                return;
            }

            int moments = 0;
            int comments = 0;
            int thumbs = 0;
            int failures = 0;

            foreach (AiMomentRecord moment in store.Data.Moments)
            {
                if (!PersistentStore.IsRecordVisibleInCurrentRound(moment != null ? moment.RoundNumber : 0))
                {
                    continue;
                }
                try
                {
                    InjectMomentCfg(moment);
                    EnsureMomentData(kzone, moment);
                    moments++;
                }
                catch (Exception ex)
                {
                    failures++;
                    log("[ERROR][KZoneInject] 恢复 AI 动态失败：contentId=" + (moment != null ? moment.ContentId : 0)
                        + ", roleId=" + (moment != null ? moment.AuthorRoleId : 0)
                        + ", round=" + (moment != null ? moment.RoundNumber : 0)
                        + ", store=" + store.CurrentPath
                        + " " + ex);
                }
            }

            foreach (AiCommentRecord comment in store.Data.Comments)
            {
                if (!PersistentStore.IsRecordVisibleInCurrentRound(comment != null ? comment.RoundNumber : 0))
                {
                    continue;
                }
                try
                {
                    InjectCommentCfg(comment);
                    if (!comment.IsOptionOnly)
                    {
                        EnsureCommentData(kzone, comment);
                    }
                    comments++;
                }
                catch (Exception ex)
                {
                    failures++;
                    log("[ERROR][KZoneInject] 恢复 AI 评论失败：commentId=" + (comment != null ? comment.CommentId : 0)
                        + ", contentId=" + (comment != null ? comment.ContentId : 0)
                        + ", parent=" + (comment != null ? comment.ParentCommentId : 0)
                        + ", roleId=" + (comment != null ? comment.AuthorRoleId : 0)
                        + ", purpose=" + (comment != null ? comment.Purpose : "")
                        + ", store=" + store.CurrentPath
                        + " " + ex);
                }
            }

            foreach (AiThumbRecord thumb in store.Data.Thumbs)
            {
                if (!PersistentStore.IsRecordVisibleInCurrentRound(thumb != null ? thumb.RoundNumber : 0))
                {
                    continue;
                }
                try
                {
                    EnsureThumbData(kzone, thumb);
                    thumbs++;
                }
                catch (Exception ex)
                {
                    failures++;
                    log("[ERROR][KZoneInject] 恢复 AI 点赞失败：contentId=" + (thumb != null ? thumb.ContentId : 0)
                        + ", roleId=" + (thumb != null ? thumb.RoleId : 0)
                        + ", round=" + (thumb != null ? thumb.RoundNumber : 0)
                        + ", store=" + store.CurrentPath
                        + " " + ex);
                }
            }

            KZoneFreeReplyBridge.InjectFreeReplyOptions();
            if (failures > 0)
            {
                log("[WARN][KZoneInject] AI 空间恢复完成但存在失败项：moments=" + moments
                    + ", comments=" + comments
                    + ", thumbs=" + thumbs
                    + ", failures=" + failures
                    + ", store=" + store.CurrentPath);
            }
            else if (moments + comments + thumbs > 0)
            {
                log("[INFO][KZoneInject] AI 空间恢复完成：moments=" + moments
                    + ", comments=" + comments
                    + ", thumbs=" + thumbs
                    + ", store=" + store.CurrentPath);
            }
        }

        internal bool EnsureMaps()
        {
            return Cfg.KZoneContentCfgMap != null
                && Cfg.KZoneCommentCfgMap != null
                && Cfg.PersonCfgMap != null;
        }

        private static string PreviewIds(List<int> ids, int max)
        {
            if (ids == null || ids.Count == 0)
            {
                return "";
            }
            List<string> parts = new List<string>();
            for (int i = 0; i < ids.Count && i < max; i++)
            {
                parts.Add(ids[i].ToString());
            }
            if (ids.Count > max)
            {
                parts.Add("...");
            }
            return string.Join(",", parts.ToArray());
        }

        internal void RemoveUnknownDynamicArtifacts(KZoneData kzone)
        {
            if (!EnsureMaps() || kzone == null || kzone.datas == null)
            {
                return;
            }

            int removed = 0;
            List<int> contentIds = new List<int>();
            foreach (KeyValuePair<int, KZoneContentData> pair in kzone.datas)
            {
                if (IsDynamicContentId(pair.Key) && !Cfg.KZoneContentCfgMap.ContainsKey(pair.Key))
                {
                    contentIds.Add(pair.Key);
                }
            }
            foreach (int contentId in contentIds)
            {
                if (kzone.datas.Remove(contentId))
                {
                    removed++;
                }
            }

            foreach (KeyValuePair<int, KZoneContentData> pair in kzone.datas)
            {
                KZoneContentData content = pair.Value;
                if (content == null || content.comments == null || content.comments.Count == 0)
                {
                    continue;
                }

                List<int> commentIds = new List<int>();
                foreach (KeyValuePair<int, KZoneCommentData> comment in content.comments)
                {
                    if (IsDynamicCommentId(comment.Key) && !Cfg.KZoneCommentCfgMap.ContainsKey(comment.Key))
                    {
                        commentIds.Add(comment.Key);
                    }
                }

                foreach (int commentId in commentIds)
                {
                    if (content.comments.Remove(commentId))
                    {
                        removed++;
                    }
                    if (content.optionTargets != null)
                    {
                        content.optionTargets.Remove(commentId);
                        if (commentId / 100 == pair.Key)
                        {
                            content.optionTargets.Remove(pair.Key);
                        }
                    }
                }
            }

            if (removed > 0)
            {
                log("[WARN][KZoneInject] 已移除缺少 sidecar/CFG 的 AI 空间遗留项：" + removed
                    + ", contentIds=" + PreviewIds(contentIds, 8));
            }
        }

        internal void InjectMomentCfg(AiMomentRecord record)
        {
            if (!EnsureMaps() || record == null)
            {
                return;
            }

            KZoneContentCfg cfg = new KZoneContentCfg
            {
                id = record.ContentId,
                role = record.AuthorRoleId,
                content = record.Content ?? "",
                imgs = new List<string>(),
                thumbs = new List<List<int>>(),
                comments = new List<List<int>>(),
                options = BuildPlayerOptionIds(record.ContentId),
                cond = new List<List<double>>(),
                thumbCnt = 0,
                title = null,
                visitCnt = record.VisitCnt
            };

            Cfg.KZoneContentCfgMap[record.ContentId] = cfg;
            InjectPlayerOptionCfgs(record.ContentId);
            KZoneFreeReplyBridge.InjectFreeReplyOptions();
        }

        internal void InjectCommentCfg(AiCommentRecord record)
        {
            if (!EnsureMaps() || record == null)
            {
                return;
            }

            List<int> roles = new List<int> { record.AuthorRoleId };
            if (record.TargetRoleId >= 0 && record.TargetRoleId != record.AuthorRoleId)
            {
                roles.Add(record.TargetRoleId);
            }

            KZoneCommentCfg cfg = new KZoneCommentCfg
            {
                id = record.CommentId,
                roles = roles,
                parent = record.ParentCommentId,
                content = record.Content ?? "",
                comments = new List<List<int>>(),
                options = new List<int>(),
                effect = new List<List<float>>(),
                condition = new List<List<double>>(),
                personality = new List<int>()
            };

            Cfg.KZoneCommentCfgMap[record.CommentId] = cfg;
            KZoneFreeReplyBridge.InjectFreeReplyOptions();
        }

        internal void InjectPlayerOptionCfgs(int contentId)
        {
            List<int> optionIds = BuildPlayerOptionIds(contentId);
            string[] texts =
            {
                "说得不错",
                "我也这么觉得",
                "有点在意这条动态"
            };

            for (int i = 0; i < optionIds.Count; i++)
            {
                int id = optionIds[i];
                if (Cfg.KZoneCommentCfgMap.ContainsKey(id))
                {
                    continue;
                }
                Cfg.KZoneCommentCfgMap[id] = new KZoneCommentCfg
                {
                    id = id,
                    roles = new List<int> { 0 },
                    parent = 0,
                    content = texts[i],
                    comments = new List<List<int>>(),
                    options = new List<int>(),
                    effect = new List<List<float>>(),
                    condition = new List<List<double>>(),
                    personality = new List<int>()
                };
            }
        }

        internal static bool IsDynamicContentId(int contentId)
        {
            return contentId >= ContentBase && contentId < 21000000;
        }

        internal static bool IsDynamicCommentId(int commentId)
        {
            return commentId >= CommentBase || (commentId / 100 >= ContentBase && commentId / 100 < 21000000);
        }

        internal static List<int> BuildPlayerOptionIds(int contentId)
        {
            List<int> list = new List<int>();
            long baseId = (long)contentId * 100L;
            if (baseId + 3 <= int.MaxValue)
            {
                list.Add((int)baseId + 1);
                list.Add((int)baseId + 2);
                list.Add((int)baseId + 3);
            }
            return list;
        }

        private void EnsureMomentData(KZoneData kzone, AiMomentRecord record)
        {
            if (kzone == null || record == null)
            {
                return;
            }
            if (kzone.datas == null)
            {
                kzone.datas = new Dictionary<int, KZoneContentData>();
            }
            if (kzone.datas.ContainsKey(record.ContentId))
            {
                return;
            }

            KZoneContentData data = new KZoneContentData();
            data.id = record.ContentId;
            data.type = KZoneContentType.Talk;
            data.postTime = record.PostTimeTicks;
            data.visitCnt = record.VisitCnt;
            data.seasonYear = new ValueTuple<int, int>(record.SeasonYear, record.Season);
            kzone.datas[record.ContentId] = data;
        }

        private void EnsureCommentData(KZoneData kzone, AiCommentRecord record)
        {
            if (kzone == null || kzone.datas == null || record == null)
            {
                return;
            }
            KZoneContentData content;
            if (!kzone.datas.TryGetValue(record.ContentId, out content))
            {
                return;
            }
            if (content.comments == null)
            {
                content.comments = new Dictionary<int, KZoneCommentData>();
            }
            if (record.ParentCommentId > 0 && !content.comments.ContainsKey(record.ParentCommentId))
            {
                log("[WARN][KZoneInject] 跳过恢复 AI 回复，父评论不存在：commentId=" + record.CommentId
                    + ", parent=" + record.ParentCommentId
                    + ", contentId=" + record.ContentId
                    + ", store=" + store.CurrentPath);
                return;
            }
            if (!content.comments.ContainsKey(record.CommentId))
            {
                KZoneCommentData comment = new KZoneCommentData();
                comment.id = record.CommentId;
                comment.content = record.Content;
                comment.postTime = record.PostTimeTicks;
                content.comments[record.CommentId] = comment;
            }
            EnsurePlayerOptionTarget(content, record);
        }

        private void EnsureThumbData(KZoneData kzone, AiThumbRecord record)
        {
            if (kzone == null || kzone.datas == null || record == null)
            {
                return;
            }
            KZoneContentData content;
            if (kzone.datas.TryGetValue(record.ContentId, out content))
            {
                content.AddThumb(record.RoleId);
            }
        }

        private static void EnsurePlayerOptionTarget(KZoneContentData content, AiCommentRecord record)
        {
            if (content == null || record == null || record.AuthorRoleId != 0)
            {
                return;
            }
            if (record.CommentId / 100 != record.ContentId)
            {
                return;
            }
            if (content.optionTargets == null)
            {
                content.optionTargets = new List<int>();
            }
            if (!content.optionTargets.Contains(record.ContentId))
            {
                content.optionTargets.Add(record.ContentId);
            }
        }
    }
}
