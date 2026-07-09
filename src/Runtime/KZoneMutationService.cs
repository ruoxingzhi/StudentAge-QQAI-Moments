using System;
using System.Collections.Generic;
using Config;
using Sdk;
using StudentAge.QQAIMoments.Ai;
using StudentAge.QQAIMoments.Models;

namespace StudentAge.QQAIMoments.Runtime
{
    internal sealed class KZoneMutationService
    {
        private readonly PersistentStore store;
        private readonly DynamicKZoneRegistry registry;
        private readonly Action<string> log;
        private readonly System.Random random;

        internal bool IsApplyingAiMutation { get; private set; }

        internal KZoneMutationService(PersistentStore store, DynamicKZoneRegistry registry, Action<string> log, System.Random random)
        {
            this.store = store;
            this.registry = registry;
            this.log = log;
            this.random = random ?? new System.Random();
        }

        internal AiMomentRecord AddNpcMoment(KZoneData kzone, int authorRoleId, string content, bool isActivePost)
        {
            if (kzone == null || !registry.EnsureMaps())
            {
                return null;
            }

            content = TextSanitizer.Clean(content, 90);
            if (string.IsNullOrEmpty(content))
            {
                return null;
            }

            int contentId = AllocateContentId();
            ValueTuple<int, int> season = Singleton<RoundMgr>.Ins.NowSeason();
            AiMomentRecord record = new AiMomentRecord
            {
                ContentId = contentId,
                AuthorRoleId = authorRoleId,
                Content = content,
                PostTimeTicks = DateTime.Now.Ticks,
                SeasonYear = season.Item1,
                Season = season.Item2,
                VisitCnt = random.Next(8, 46),
                IsActivePost = isActivePost,
                RoundNumber = PersistentStore.CurrentRound()
            };

            store.Data.Moments.Add(record);
            registry.InjectMomentCfg(record);

            if (kzone.datas == null)
            {
                kzone.datas = new Dictionary<int, KZoneContentData>();
            }

            IsApplyingAiMutation = true;
            try
            {
                kzone.Post(contentId);
            }
            finally
            {
                IsApplyingAiMutation = false;
            }

            KZoneContentData data;
            if (kzone.datas.TryGetValue(contentId, out data))
            {
                data.postTime = record.PostTimeTicks;
                data.visitCnt = record.VisitCnt;
                data.seasonYear = season;
            }

            store.Save();
            EventMgr.Send(13001);
            return record;
        }

        internal AiCommentRecord AddComment(KZoneData kzone, int contentId, int authorRoleId, int targetRoleId, int parentCommentId, string text, string purpose)
        {
            if (kzone == null || kzone.datas == null || !kzone.datas.ContainsKey(contentId) || !registry.EnsureMaps())
            {
                return null;
            }

            text = TextSanitizer.Clean(text, 80);
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            int commentId = AllocateCommentId();
            AiCommentRecord record = new AiCommentRecord
            {
                CommentId = commentId,
                ContentId = contentId,
                AuthorRoleId = authorRoleId,
                TargetRoleId = targetRoleId,
                ParentCommentId = parentCommentId,
                Content = text,
                PostTimeTicks = DateTime.Now.Ticks,
                IsOptionOnly = false,
                Purpose = purpose ?? "",
                RoundNumber = PersistentStore.CurrentRound()
            };

            store.Data.Comments.Add(record);
            registry.InjectCommentCfg(record);

            bool added = false;
            IsApplyingAiMutation = true;
            try
            {
                added = kzone.datas[contentId].AddComment(commentId, 0.0);
            }
            finally
            {
                IsApplyingAiMutation = false;
            }

            if (!added)
            {
                log("AI 评论未能添加：" + commentId);
                store.Data.Comments.Remove(record);
                store.Save();
                return null;
            }

            store.Save();
            EventMgr.Send(13001);
            return record;
        }

        internal void AddThumb(KZoneData kzone, int contentId, int roleId)
        {
            if (kzone == null || kzone.datas == null)
            {
                return;
            }
            KZoneContentData content;
            if (!kzone.datas.TryGetValue(contentId, out content))
            {
                return;
            }
            if (content.AddThumb(roleId))
            {
                store.AddThumb(contentId, roleId);
                store.Save();
                EventMgr.Send(13001);
            }
        }

        internal AiCommentRecord PersistExistingCommentIfMissing(KZoneData kzone, int commentId, string purpose)
        {
            if (kzone == null || kzone.datas == null || store.FindComment(commentId) != null)
            {
                return null;
            }
            if (!DynamicKZoneRegistry.IsDynamicCommentId(commentId))
            {
                return null;
            }

            int contentId = FindContentIdForComment(kzone, commentId);
            if (contentId < 0)
            {
                return null;
            }

            KZoneContentData content;
            KZoneCommentData commentData;
            KZoneCommentCfg commentCfg;
            if (!kzone.datas.TryGetValue(contentId, out content)
                || content == null
                || content.comments == null
                || !content.comments.TryGetValue(commentId, out commentData)
                || !Cfg.KZoneCommentCfgMap.TryGetValue(commentId, out commentCfg))
            {
                return null;
            }

            int authorRoleId = commentCfg.roles != null && commentCfg.roles.Count > 0 ? commentCfg.roles[0] : 0;
            int targetRoleId = commentCfg.roles != null && commentCfg.roles.Count > 1 ? commentCfg.roles[1] : content.RoleId;
            AiCommentRecord record = new AiCommentRecord
            {
                CommentId = commentId,
                ContentId = contentId,
                AuthorRoleId = authorRoleId,
                TargetRoleId = targetRoleId,
                ParentCommentId = commentCfg.parent,
                Content = commentCfg.content ?? commentData.content ?? "",
                PostTimeTicks = commentData.postTime,
                IsOptionOnly = false,
                Purpose = purpose ?? "",
                RoundNumber = PersistentStore.CurrentRound()
            };

            store.Data.Comments.Add(record);
            store.Save();
            return record;
        }

        internal int FindContentIdForComment(KZoneData kzone, int commentId)
        {
            if (kzone == null || kzone.datas == null)
            {
                return -1;
            }
            foreach (KeyValuePair<int, KZoneContentData> pair in kzone.datas)
            {
                if (pair.Value != null && pair.Value.comments != null && pair.Value.comments.ContainsKey(commentId))
                {
                    return pair.Key;
                }
            }
            return -1;
        }

        internal string ExistingCommentsSummary(KZoneContentData content, int max = 6)
        {
            if (content == null || content.comments == null || content.comments.Count == 0)
            {
                return "";
            }
            List<string> items = new List<string>();
            foreach (KeyValuePair<int, KZoneCommentData> pair in content.comments)
            {
                KZoneCommentCfg cfg;
                string text = pair.Value != null ? pair.Value.content : null;
                if (Cfg.KZoneCommentCfgMap.TryGetValue(pair.Key, out cfg))
                {
                    int role = cfg.roles != null && cfg.roles.Count > 0 ? cfg.roles[0] : -1;
                    if (string.IsNullOrEmpty(text))
                    {
                        text = cfg.content;
                    }
                    items.Add(role + "：" + KZoneData.FormatContent(text ?? ""));
                    if (items.Count >= max)
                    {
                        break;
                    }
                }
            }
            return string.Join("；", items.ToArray());
        }

        internal string ThreadSummary(KZoneContentData content, int max = 8)
        {
            List<CommentLine> lines = CollectCommentLines(content);
            if (lines.Count == 0 || max <= 0)
            {
                return "";
            }

            lines.Sort(delegate(CommentLine a, CommentLine b)
            {
                return a.PostTimeTicks.CompareTo(b.PostTimeTicks);
            });

            int start = Math.Max(0, lines.Count - max);
            List<string> items = new List<string>();
            for (int i = start; i < lines.Count; i++)
            {
                CommentLine line = lines[i];
                string prefix = "role " + line.AuthorRoleId;
                if (line.TargetRoleId >= 0 && line.TargetRoleId != line.AuthorRoleId)
                {
                    prefix += " to role " + line.TargetRoleId;
                }
                if (line.ParentCommentId > 0)
                {
                    prefix += " reply_to_comment " + line.ParentCommentId;
                }
                items.Add(prefix + ": " + line.Text);
            }
            return string.Join(" | ", items.ToArray());
        }

        internal string RecentSelfTurns(KZoneContentData content, int roleId, int max = 3)
        {
            if (roleId < 0 || max <= 0)
            {
                return "";
            }

            List<CommentLine> lines = CollectCommentLines(content);
            if (lines.Count == 0)
            {
                return "";
            }

            lines.Sort(delegate(CommentLine a, CommentLine b)
            {
                return b.PostTimeTicks.CompareTo(a.PostTimeTicks);
            });

            List<string> items = new List<string>();
            foreach (CommentLine line in lines)
            {
                if (line.AuthorRoleId != roleId)
                {
                    continue;
                }
                items.Add("comment " + line.CommentId + ": " + line.Text);
                if (items.Count >= max)
                {
                    break;
                }
            }
            return string.Join(" | ", items.ToArray());
        }

        internal string RecentSimilarTexts(KZoneContentData content, int max = 10)
        {
            List<CommentLine> lines = CollectCommentLines(content);
            if (lines.Count == 0 || max <= 0)
            {
                return "";
            }

            lines.Sort(delegate(CommentLine a, CommentLine b)
            {
                return b.PostTimeTicks.CompareTo(a.PostTimeTicks);
            });

            List<string> items = new List<string>();
            foreach (CommentLine line in lines)
            {
                if (string.IsNullOrEmpty(line.Text) || items.Contains(line.Text))
                {
                    continue;
                }
                items.Add(line.Text);
                if (items.Count >= max)
                {
                    break;
                }
            }
            return string.Join(" | ", items.ToArray());
        }

        private List<CommentLine> CollectCommentLines(KZoneContentData content)
        {
            List<CommentLine> result = new List<CommentLine>();
            if (content == null || content.comments == null)
            {
                return result;
            }

            foreach (KeyValuePair<int, KZoneCommentData> pair in content.comments)
            {
                CommentLine line = BuildCommentLine(pair.Key, pair.Value);
                if (line != null && !string.IsNullOrEmpty(line.Text))
                {
                    result.Add(line);
                }
            }
            return result;
        }

        private static CommentLine BuildCommentLine(int commentId, KZoneCommentData data)
        {
            KZoneCommentCfg cfg;
            string text = data != null ? data.content : null;
            int authorRoleId = -1;
            int targetRoleId = -1;
            int parentCommentId = 0;

            if (Cfg.KZoneCommentCfgMap != null && Cfg.KZoneCommentCfgMap.TryGetValue(commentId, out cfg))
            {
                if (string.IsNullOrEmpty(text))
                {
                    text = cfg.content;
                }
                if (cfg.roles != null && cfg.roles.Count > 0)
                {
                    authorRoleId = cfg.roles[0];
                    if (cfg.roles.Count > 1)
                    {
                        targetRoleId = cfg.roles[1];
                    }
                }
                parentCommentId = cfg.parent;
            }

            text = KZoneData.FormatContent(text ?? "");
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            return new CommentLine
            {
                CommentId = commentId,
                AuthorRoleId = authorRoleId,
                TargetRoleId = targetRoleId,
                ParentCommentId = parentCommentId,
                Text = text,
                PostTimeTicks = data != null ? data.postTime : 0L
            };
        }

        private sealed class CommentLine
        {
            internal int CommentId;
            internal int AuthorRoleId;
            internal int TargetRoleId;
            internal int ParentCommentId;
            internal string Text;
            internal long PostTimeTicks;
        }

        private int AllocateContentId()
        {
            int id;
            do
            {
                id = store.NextContentId();
                if (id < DynamicKZoneRegistry.ContentBase || id >= 21000000)
                {
                    id = DynamicKZoneRegistry.ContentBase;
                    store.Data.NextContentId = id + 1;
                }
            }
            while (Cfg.KZoneContentCfgMap.ContainsKey(id) || (long)id * 100L + 3L > int.MaxValue);
            return id;
        }

        private int AllocateCommentId()
        {
            int id;
            do
            {
                id = store.NextCommentId();
                if (id < DynamicKZoneRegistry.CommentBase)
                {
                    id = DynamicKZoneRegistry.CommentBase;
                    store.Data.NextCommentId = id + 1;
                }
            }
            while (Cfg.KZoneCommentCfgMap.ContainsKey(id));
            return id;
        }
    }
}
