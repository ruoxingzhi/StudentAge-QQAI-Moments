using System;
using System.Collections.Generic;
using Config;
using MiniGame.TalkInput;
using Sdk;
using StudentAge.QQAIMoments.Models;

namespace StudentAge.QQAIMoments.Runtime
{
    internal static class KZoneFreeReplyBridge
    {
        internal const int FreeReplyOptionId = 1599000000;
        private const string FreeReplyOptionText = "写下自己的回复...";
        private const string Placeholder = "输入回复内容";

        internal static bool IsFreeReplyOption(int optionId)
        {
            return optionId == FreeReplyOptionId;
        }

        internal static void InjectFreeReplyOptions()
        {
            if (Cfg.KZoneCommentCfgMap == null)
            {
                return;
            }

            EnsureFreeReplyOptionCfg();
            AddFreeReplyOptionsToEligibleCfgs();
        }

        internal static bool ShouldOpenFreeReplyForContent(KZoneData kzone, int contentId, List<int> nativeOptions)
        {
            return CanFreeReplyToContent(kzone, contentId)
                && (!HasBlockingNativeOptionsForContent(contentId, nativeOptions) || IsTargetConsumed(kzone, contentId));
        }

        internal static bool ShouldOpenFreeReplyForComment(KZoneData kzone, int contentId, int commentId, List<int> nativeOptions)
        {
            return CanFreeReplyToComment(kzone, commentId)
                && (!HasBlockingNativeOptionsForComment(commentId, nativeOptions) || IsTargetConsumed(kzone, commentId));
        }

        internal static bool TryOpenFreeReplyInputForContent(
            KZoneData kzone,
            int contentId,
            KZoneMutationService mutation,
            ReactionScheduler scheduler,
            Action<string> log)
        {
            KZoneContentData content;
            if (kzone == null
                || kzone.datas == null
                || !kzone.datas.TryGetValue(contentId, out content)
                || content == null
                || content.RoleId <= 0)
            {
                return false;
            }

            return TryOpenResolvedFreeReplyInput(kzone, new FreeReplyTarget
            {
                ContentId = contentId,
                TargetRoleId = content.RoleId,
                ParentCommentId = 0,
                Purpose = "player-free-reply-content"
            }, mutation, scheduler, log);
        }

        internal static bool TryOpenFreeReplyInputForComment(
            KZoneData kzone,
            int contentId,
            int commentId,
            KZoneMutationService mutation,
            ReactionScheduler scheduler,
            Action<string> log)
        {
            KZoneContentData content;
            KZoneCommentData comment;
            KZoneCommentCfg cfg;
            if (kzone == null
                || kzone.datas == null
                || !kzone.datas.TryGetValue(contentId, out content)
                || content == null
                || content.comments == null
                || !content.comments.TryGetValue(commentId, out comment)
                || Cfg.KZoneCommentCfgMap == null
                || !Cfg.KZoneCommentCfgMap.TryGetValue(commentId, out cfg)
                || cfg.roles == null
                || cfg.roles.Count == 0
                || cfg.roles[0] <= 0)
            {
                return false;
            }

            return TryOpenResolvedFreeReplyInput(kzone, new FreeReplyTarget
            {
                ContentId = contentId,
                TargetRoleId = cfg.roles[0],
                ParentCommentId = commentId,
                Purpose = "player-free-reply-comment"
            }, mutation, scheduler, log);
        }

        private static void AddFreeReplyOptionsToEligibleCfgs()
        {
            if (Cfg.KZoneContentCfgMap != null)
            {
                foreach (KeyValuePair<int, KZoneContentCfg> pair in Cfg.KZoneContentCfgMap)
                {
                    KZoneContentCfg cfg = pair.Value;
                    if (cfg != null && cfg.role > 0)
                    {
                        if (cfg.options == null)
                        {
                            cfg.options = new List<int>();
                        }
                        AddFreeOption(cfg.options);
                    }
                }
            }

            foreach (KeyValuePair<int, KZoneCommentCfg> pair in Cfg.KZoneCommentCfgMap)
            {
                KZoneCommentCfg cfg = pair.Value;
                if (cfg != null
                    && cfg.id != FreeReplyOptionId
                    && cfg.roles != null
                    && cfg.roles.Count > 0
                    && cfg.roles[0] > 0)
                {
                    if (cfg.options == null)
                    {
                        cfg.options = new List<int>();
                    }
                    AddFreeOption(cfg.options);
                }
            }
        }

        internal static bool HasOnlyFreeReplyOption(List<int> options)
        {
            if (options == null || options.Count == 0)
            {
                return false;
            }
            for (int i = 0; i < options.Count; i++)
            {
                if (!IsFreeReplyOption(options[i]))
                {
                    return false;
                }
            }
            return true;
        }

        internal static bool CanFreeReplyToContent(KZoneData kzone, int contentId)
        {
            KZoneContentData content;
            return kzone != null
                && kzone.datas != null
                && kzone.datas.TryGetValue(contentId, out content)
                && content != null
                && content.RoleId > 0;
        }

        internal static bool CanFreeReplyToComment(KZoneData kzone, int commentId)
        {
            KZoneContentData content;
            KZoneCommentData comment;
            KZoneCommentCfg cfg;
            return TryResolveComment(kzone, commentId, out content, out comment)
                && Cfg.KZoneCommentCfgMap != null
                && Cfg.KZoneCommentCfgMap.TryGetValue(commentId, out cfg)
                && cfg.roles != null
                && cfg.roles.Count > 0
                && cfg.roles[0] > 0;
        }

        internal static bool IsTargetConsumed(KZoneData kzone, int targetId)
        {
            if (kzone == null || kzone.datas == null)
            {
                return false;
            }
            KZoneContentData content;
            if (kzone.datas.TryGetValue(targetId, out content))
            {
                return content.optionTargets != null && content.optionTargets.Contains(targetId);
            }
            foreach (KeyValuePair<int, KZoneContentData> pair in kzone.datas)
            {
                content = pair.Value;
                if (content != null
                    && content.comments != null
                    && content.comments.ContainsKey(targetId)
                    && content.optionTargets != null
                    && content.optionTargets.Contains(targetId))
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool TryOpenFreeReplyInput(
            KZoneData kzone,
            int targetId,
            KZoneMutationService mutation,
            ReactionScheduler scheduler,
            Action<string> log)
        {
            if (kzone == null || mutation == null)
            {
                return false;
            }

            FreeReplyTarget target;
            if (!TryResolveTarget(kzone, targetId, out target))
            {
                return false;
            }

            return TryOpenResolvedFreeReplyInput(kzone, target, mutation, scheduler, log);
        }

        private static bool TryOpenResolvedFreeReplyInput(
            KZoneData kzone,
            FreeReplyTarget target,
            KZoneMutationService mutation,
            ReactionScheduler scheduler,
            Action<string> log)
        {
            if (kzone == null || mutation == null || target.ContentId <= 0)
            {
                return false;
            }

            UIMgr.OpenView<TalkInputCommonView>(UILayerType.None, null, new object[]
            {
                Placeholder,
                TalkInputContentType.Default,
                (Action<string>)delegate(string text)
                {
                    int textLength = text != null ? text.Length : 0;
                    try
                    {
                        if (log != null)
                        {
                            log("[INFO][FreeReply] 玩家自由回复提交：contentId=" + target.ContentId
                                + ", parentCommentId=" + target.ParentCommentId
                                + ", targetRoleId=" + target.TargetRoleId
                                + ", purpose=" + target.Purpose
                                + ", textLength=" + textLength);
                        }

                        AiCommentRecord record = mutation.AddComment(
                            kzone,
                            target.ContentId,
                            0,
                            target.TargetRoleId,
                            target.ParentCommentId,
                            text,
                            target.Purpose);
                        if (record != null && scheduler != null)
                        {
                            scheduler.OnPlayerCommented(kzone, record.CommentId);
                        }
                        else if (record == null && log != null)
                        {
                            log("[WARN][FreeReply] 自由回复为空或未能写入：contentId=" + target.ContentId
                                + ", parentCommentId=" + target.ParentCommentId
                                + ", textLength=" + textLength);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (log != null)
                        {
                            log("[ERROR][FreeReply] 自由回复提交失败：contentId=" + target.ContentId
                                + ", parentCommentId=" + target.ParentCommentId
                                + ", targetRoleId=" + target.TargetRoleId
                                + ", purpose=" + target.Purpose
                                + ", textLength=" + textLength
                                + " " + ex);
                        }
                    }
                }
            });
            return true;
        }

        private static void EnsureFreeReplyOptionCfg()
        {
            if (Cfg.KZoneCommentCfgMap.ContainsKey(FreeReplyOptionId))
            {
                return;
            }
            Cfg.KZoneCommentCfgMap[FreeReplyOptionId] = new KZoneCommentCfg
            {
                id = FreeReplyOptionId,
                roles = new List<int> { 0 },
                parent = 0,
                content = FreeReplyOptionText,
                comments = new List<List<int>>(),
                options = new List<int>(),
                effect = new List<List<float>>(),
                condition = new List<List<double>>(),
                personality = new List<int>()
            };
        }

        private static void AddFreeOption(List<int> options)
        {
            if (options == null)
            {
                return;
            }
            if (!options.Contains(FreeReplyOptionId))
            {
                options.Add(FreeReplyOptionId);
            }
        }

        private static bool HasBlockingNativeOptionsForContent(int contentId, List<int> options)
        {
            if (options == null || options.Count == 0)
            {
                return false;
            }
            for (int i = 0; i < options.Count; i++)
            {
                int optionId = options[i];
                if (IsFreeReplyOption(optionId))
                {
                    continue;
                }
                if (DynamicKZoneRegistry.IsDynamicContentId(contentId) && optionId / 100 == contentId)
                {
                    continue;
                }
                if (Cfg.KZoneCommentCfgMap != null && Cfg.KZoneCommentCfgMap.ContainsKey(optionId))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasBlockingNativeOptionsForComment(int commentId, List<int> options)
        {
            if (options == null || options.Count == 0)
            {
                return false;
            }
            for (int i = 0; i < options.Count; i++)
            {
                int optionId = options[i];
                if (IsFreeReplyOption(optionId))
                {
                    continue;
                }
                if (Cfg.KZoneCommentCfgMap != null && Cfg.KZoneCommentCfgMap.ContainsKey(optionId))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryResolveTarget(KZoneData kzone, int targetId, out FreeReplyTarget target)
        {
            target = default(FreeReplyTarget);
            if (kzone == null || kzone.datas == null)
            {
                return false;
            }

            KZoneContentData content;
            if (kzone.datas.TryGetValue(targetId, out content) && content != null && content.RoleId > 0)
            {
                target = new FreeReplyTarget
                {
                    ContentId = targetId,
                    TargetRoleId = content.RoleId,
                    ParentCommentId = 0,
                    Purpose = "player-free-reply-content"
                };
                return true;
            }

            KZoneCommentData comment;
            if (!TryResolveComment(kzone, targetId, out content, out comment) || content == null)
            {
                return false;
            }

            KZoneCommentCfg cfg;
            if (Cfg.KZoneCommentCfgMap == null
                || !Cfg.KZoneCommentCfgMap.TryGetValue(targetId, out cfg)
                || cfg.roles == null
                || cfg.roles.Count == 0
                || cfg.roles[0] <= 0)
            {
                return false;
            }

            target = new FreeReplyTarget
            {
                ContentId = content.id,
                TargetRoleId = cfg.roles[0],
                ParentCommentId = targetId,
                Purpose = "player-free-reply-comment"
            };
            return true;
        }

        private static bool TryResolveComment(KZoneData kzone, int commentId, out KZoneContentData content, out KZoneCommentData comment)
        {
            content = null;
            comment = null;
            if (kzone == null || kzone.datas == null)
            {
                return false;
            }
            foreach (KeyValuePair<int, KZoneContentData> pair in kzone.datas)
            {
                KZoneContentData candidate = pair.Value;
                if (candidate != null
                    && candidate.comments != null
                    && candidate.comments.TryGetValue(commentId, out comment))
                {
                    content = candidate;
                    return true;
                }
            }
            return false;
        }

        private struct FreeReplyTarget
        {
            internal int ContentId;
            internal int TargetRoleId;
            internal int ParentCommentId;
            internal string Purpose;
        }
    }
}
