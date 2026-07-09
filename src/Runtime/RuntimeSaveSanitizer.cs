using System;
using System.Collections.Generic;
using StudentAge.QQAIMoments.Models;

namespace StudentAge.QQAIMoments.Runtime
{
    internal sealed class RuntimeSaveSanitizer
    {
        private readonly PersistentStore store;
        private readonly Action<string> log;
        private readonly Dictionary<int, KZoneContentData> removedContents = new Dictionary<int, KZoneContentData>();
        private readonly Dictionary<int, List<KeyValuePair<int, KZoneCommentData>>> removedComments = new Dictionary<int, List<KeyValuePair<int, KZoneCommentData>>>();
        private readonly Dictionary<int, List<int>> removedThumbs = new Dictionary<int, List<int>>();
        private bool stripped;

        internal RuntimeSaveSanitizer(PersistentStore store, Action<string> log)
        {
            this.store = store;
            this.log = log;
        }

        internal void BeforeGameSave(KZoneData kzone)
        {
            if (stripped || kzone == null || kzone.datas == null || store == null || store.Data == null)
            {
                return;
            }

            removedContents.Clear();
            removedComments.Clear();
            removedThumbs.Clear();

            List<int> dynamicContentIds = new List<int>();
            foreach (KeyValuePair<int, KZoneContentData> pair in kzone.datas)
            {
                if (DynamicKZoneRegistry.IsDynamicContentId(pair.Key))
                {
                    dynamicContentIds.Add(pair.Key);
                }
            }

            foreach (int contentId in dynamicContentIds)
            {
                removedContents[contentId] = kzone.datas[contentId];
                kzone.datas.Remove(contentId);
            }

            foreach (KeyValuePair<int, KZoneContentData> pair in kzone.datas)
            {
                StripDynamicComments(pair.Key, pair.Value);
                StripStoredThumbs(pair.Key, pair.Value);
            }

            stripped = true;
            if (removedContents.Count > 0 || removedComments.Count > 0 || removedThumbs.Count > 0)
            {
                log("保存原游戏存档前临时剥离 AI 空间运行时数据。");
            }
        }

        internal void AfterGameSave(KZoneData kzone)
        {
            if (!stripped)
            {
                return;
            }
            try
            {
                if (kzone != null && kzone.datas != null)
                {
                    foreach (KeyValuePair<int, KZoneContentData> pair in removedContents)
                    {
                        if (!kzone.datas.ContainsKey(pair.Key))
                        {
                            kzone.datas[pair.Key] = pair.Value;
                        }
                    }

                    foreach (KeyValuePair<int, List<KeyValuePair<int, KZoneCommentData>>> pair in removedComments)
                    {
                        KZoneContentData content;
                        if (!kzone.datas.TryGetValue(pair.Key, out content) || content == null)
                        {
                            continue;
                        }
                        EnsureCommentMap(content);
                        foreach (KeyValuePair<int, KZoneCommentData> comment in pair.Value)
                        {
                            if (!content.comments.ContainsKey(comment.Key))
                            {
                                content.comments[comment.Key] = comment.Value;
                            }
                        }
                    }

                    foreach (KeyValuePair<int, List<int>> pair in removedThumbs)
                    {
                        KZoneContentData content;
                        if (!kzone.datas.TryGetValue(pair.Key, out content) || content == null)
                        {
                            continue;
                        }
                        EnsureThumbList(content);
                        foreach (int roleId in pair.Value)
                        {
                            if (!content.thumbs.Contains(roleId))
                            {
                                content.thumbs.Add(roleId);
                            }
                        }
                    }
                }
            }
            finally
            {
                removedContents.Clear();
                removedComments.Clear();
                removedThumbs.Clear();
                stripped = false;
            }
        }

        internal void RemoveStoreArtifacts(KZoneData kzone)
        {
            if (kzone == null || kzone.datas == null || store == null || store.Data == null)
            {
                return;
            }

            foreach (AiMomentRecord moment in store.Data.Moments)
            {
                if (moment != null)
                {
                    kzone.datas.Remove(moment.ContentId);
                }
            }

            foreach (AiCommentRecord comment in store.Data.Comments)
            {
                if (comment != null && kzone.datas.ContainsKey(comment.ContentId))
                {
                    KZoneContentData content = kzone.datas[comment.ContentId];
                    if (content != null && content.comments != null)
                    {
                        content.comments.Remove(comment.CommentId);
                    }
                }
            }

            foreach (AiThumbRecord thumb in store.Data.Thumbs)
            {
                if (thumb != null && kzone.datas.ContainsKey(thumb.ContentId))
                {
                    KZoneContentData content = kzone.datas[thumb.ContentId];
                    if (content != null && content.thumbs != null)
                    {
                        content.thumbs.Remove(thumb.RoleId);
                    }
                }
            }
        }

        private void StripDynamicComments(int contentId, KZoneContentData content)
        {
            if (content == null || content.comments == null || content.comments.Count == 0)
            {
                return;
            }

            List<int> ids = new List<int>();
            foreach (KeyValuePair<int, KZoneCommentData> pair in content.comments)
            {
                if (DynamicKZoneRegistry.IsDynamicCommentId(pair.Key))
                {
                    ids.Add(pair.Key);
                }
            }

            if (ids.Count == 0)
            {
                return;
            }

            List<KeyValuePair<int, KZoneCommentData>> snapshot = new List<KeyValuePair<int, KZoneCommentData>>();
            foreach (int id in ids)
            {
                snapshot.Add(new KeyValuePair<int, KZoneCommentData>(id, content.comments[id]));
                content.comments.Remove(id);
                if (content.optionTargets != null)
                {
                    content.optionTargets.Remove(id);
                    content.optionTargets.Remove(contentId);
                }
            }
            removedComments[contentId] = snapshot;
        }

        private void StripStoredThumbs(int contentId, KZoneContentData content)
        {
            if (content == null || content.thumbs == null || content.thumbs.Count == 0 || store.Data.Thumbs == null)
            {
                return;
            }

            List<int> snapshot = null;
            foreach (AiThumbRecord thumb in store.Data.Thumbs)
            {
                if (thumb == null || thumb.ContentId != contentId || !content.thumbs.Contains(thumb.RoleId))
                {
                    continue;
                }
                if (snapshot == null)
                {
                    snapshot = new List<int>();
                }
                snapshot.Add(thumb.RoleId);
            }

            if (snapshot == null || snapshot.Count == 0)
            {
                return;
            }

            foreach (int roleId in snapshot)
            {
                content.thumbs.Remove(roleId);
            }
            removedThumbs[contentId] = snapshot;
        }

        private static void EnsureCommentMap(KZoneContentData content)
        {
            if (content.comments == null)
            {
                content.comments = new Dictionary<int, KZoneCommentData>();
            }
        }

        private static void EnsureThumbList(KZoneContentData content)
        {
            if (content.thumbs == null)
            {
                content.thumbs = new List<int>();
            }
        }
    }
}
