using System;
using System.Collections;
using System.Collections.Generic;
using Config;
using Sdk;
using StudentAge.QQAIMoments.Ai;
using StudentAge.QQAIMoments.Config;
using StudentAge.QQAIMoments.Models;
using StudentAge.QQAIMoments.Social;
using TheEntity;
using UnityEngine;

namespace StudentAge.QQAIMoments.Runtime
{
    internal sealed class ReactionScheduler
    {
        private const int DuplicateAiRewriteAttempts = 2;
        private readonly PluginConfig config;
        private readonly IAiClient aiClient;
        private readonly IAiClient fallbackClient;
        private readonly PromptBuilder promptBuilder;
        private readonly PromptDebugRecorder promptDebugRecorder;
        private readonly NpcSelector npcSelector;
        private readonly PersonaService personaService;
        private readonly KZoneMutationService mutation;
        private readonly PersistentStore store;
        private readonly UsageTelemetryService telemetry;
        private readonly MonoBehaviour runner;
        private readonly Action<string> log;
        private readonly System.Random random;
        private readonly Queue<AiJob> queue = new Queue<AiJob>();
        private readonly HashSet<string> pendingDedupe = new HashSet<string>();
        private bool running;
        private int requestsThisRound;
        private int runtimeEpoch;

        internal ReactionScheduler(
            PluginConfig config,
            IAiClient aiClient,
            IAiClient fallbackClient,
            PromptBuilder promptBuilder,
            NpcSelector npcSelector,
            PersonaService personaService,
            KZoneMutationService mutation,
            PersistentStore store,
            UsageTelemetryService telemetry,
            MonoBehaviour runner,
            Action<string> log,
            System.Random random)
        {
            this.config = config;
            this.aiClient = aiClient;
            this.fallbackClient = fallbackClient;
            this.promptBuilder = promptBuilder;
            this.promptDebugRecorder = new PromptDebugRecorder(config, log);
            this.npcSelector = npcSelector;
            this.personaService = personaService;
            this.mutation = mutation;
            this.store = store;
            this.telemetry = telemetry;
            this.runner = runner;
            this.log = log;
            this.random = random ?? new System.Random();
        }

        internal void ResetRoundBudget()
        {
            requestsThisRound = 0;
        }

        internal void InvalidateRuntimeJobs(string reason)
        {
            runtimeEpoch++;
            queue.Clear();
            pendingDedupe.Clear();
            log("已终止过期 QQ 空间 AI 任务：" + (string.IsNullOrEmpty(reason) ? "运行环境变化" : reason));
        }

        internal void OnPlayerPosted(KZoneData kzone, KZoneContentData content)
        {
            if (!CanRun(kzone, content) || content.RoleId != 0)
            {
                return;
            }

            List<int> known = npcSelector.GetKnownNpcs();
            List<int> likes = npcSelector.PickWeighted(known, config.MaxLikesPerPlayerPost.Value, config.PlayerPostLikeChance.Value);
            foreach (int npcId in likes)
            {
                mutation.AddThumb(kzone, content.id, npcId);
            }

            List<int> commenters = npcSelector.PickWeighted(known, config.MaxCommentsPerPlayerPost.Value, config.PlayerPostCommentChance.Value);
            foreach (int npcId in commenters)
            {
                if (!personaService.IsPersonaEnabled(npcId))
                {
                    continue;
                }
                string key = Dedupe("player-post-comment", content.id, npcId, 0, 0);
                if (store.HasDedupe(key) || store.HasCommentForPurpose(content.id, npcId, "player-post-comment"))
                {
                    continue;
                }
                Enqueue(new AiJob
                {
                    Type = AiJobType.PlayerPostComment,
                    ContentId = content.id,
                    AuthorRoleId = npcId,
                    PostAuthorRoleId = 0,
                    TargetRoleId = 0,
                    ParentCommentId = 0,
                    SourceText = content.Content,
                    ExistingText = mutation.ExistingCommentsSummary(content),
                    DedupeKey = key
                });
            }
        }

        internal bool TriggerTestReplyToLatestPlayerMoment(KZoneData kzone)
        {
            if (kzone == null || !config.Enabled.Value || Cfg.PersonCfgMap == null)
            {
                log("Alt+C 测试跳过：QQ 空间数据或配置尚未就绪。");
                return false;
            }

            KZoneContentData content = FindLatestPlayerMoment(kzone);
            if (content == null)
            {
                log("Alt+C 测试跳过：未找到主角最近动态。");
                return false;
            }

            List<int> known = npcSelector.GetKnownNpcs();
            int npcId = PickEnabledPersona(known);
            if (npcId <= 0)
            {
                log("Alt+C 测试跳过：未找到可用 NPC。");
                return false;
            }
            if (!personaService.IsPersonaEnabled(npcId))
            {
                log("Alt+C 测试跳过：NPC 人设未启用：" + npcId);
                return false;
            }
            if (requestsThisRound >= config.MaxAiRequestsPerRound.Value)
            {
                log("Alt+C 测试跳过：本回合 AI 请求数已达到上限。");
                return false;
            }

            string key = Dedupe("alt-c-test-player-post-comment", content.id, npcId, 0, DateTime.Now.Ticks.GetHashCode());
            Enqueue(new AiJob
            {
                Type = AiJobType.PlayerPostComment,
                ContentId = content.id,
                AuthorRoleId = npcId,
                PostAuthorRoleId = 0,
                TargetRoleId = 0,
                ParentCommentId = 0,
                SourceText = content.Content,
                ExistingText = mutation.ExistingCommentsSummary(content),
                DedupeKey = key,
                PurposeOverride = "test-alt-c-player-post-comment"
            });
            log("Alt+C 测试已触发：NPC " + npcId + " 回复主角动态 " + content.id);
            return true;
        }

        internal void OnNewRound(KZoneData kzone)
        {
            if (kzone == null || !config.Enabled.Value)
            {
                return;
            }
            ResetRoundBudget();
            MaybeNpcActivePosts(kzone);
            MaybeNpcNpcInteractions(kzone);
        }

        internal void OnPlayerCommented(KZoneData kzone, int commentId)
        {
            if (kzone == null || !config.Enabled.Value)
            {
                return;
            }
            int contentId = mutation.FindContentIdForComment(kzone, commentId);
            if (contentId < 0 || kzone.datas == null || !kzone.datas.ContainsKey(contentId))
            {
                return;
            }
            KZoneCommentCfg commentCfg;
            if (!Cfg.KZoneCommentCfgMap.TryGetValue(commentId, out commentCfg) || commentCfg.roles == null || commentCfg.roles.Count == 0)
            {
                return;
            }
            if (commentCfg.roles[0] != 0)
            {
                return;
            }

            KZoneContentData content = kzone.datas[contentId];
            int replyNpc = ResolveReplyNpc(content, commentCfg);
            if (!npcSelector.IsUsableNpc(replyNpc, true) || !personaService.IsPersonaEnabled(replyNpc))
            {
                return;
            }

            Role role = Singleton<RoleMgr>.Ins.GetRole(replyNpc);
            if (random.NextDouble() > RelationScorer.ReplyChance(role, config.PlayerCommentReplyBaseChance.Value))
            {
                return;
            }

            string key = Dedupe("player-comment-reply", contentId, replyNpc, 0, commentId);
            if (store.HasDedupe(key))
            {
                return;
            }

            Enqueue(new AiJob
            {
                Type = AiJobType.PlayerCommentReply,
                ContentId = contentId,
                AuthorRoleId = replyNpc,
                PostAuthorRoleId = SafeContentRoleId(content),
                TargetRoleId = 0,
                ParentCommentId = commentId,
                SourceText = content.Content,
                ParentCommentText = KZoneData.FormatContent(commentCfg.content),
                ExistingText = mutation.ExistingCommentsSummary(content),
                DedupeKey = key
            });
        }

        private void MaybeNpcActivePosts(KZoneData kzone)
        {
            if (random.NextDouble() > config.NpcActivePostChance.Value)
            {
                return;
            }

            List<int> candidates = npcSelector.GetActivePostCandidates();
            int count = Math.Max(0, Math.Min(config.MaxNpcActivePostsPerRound.Value, candidates.Count));
            for (int i = 0; i < count; i++)
            {
                int npcId = npcSelector.PickOneWeighted(candidates);
                if (npcId <= 0)
                {
                    break;
                }
                candidates.Remove(npcId);
                if (!personaService.IsPersonaEnabled(npcId))
                {
                    continue;
                }
                ValueTuple<int, int> season = Singleton<RoundMgr>.Ins.NowSeason();
                string key = Dedupe("npc-active-post-" + season.Item1 + "-" + season.Item2, 0, npcId, 0, 0);
                if (store.HasDedupe(key))
                {
                    continue;
                }
                Enqueue(new AiJob
                {
                    Type = AiJobType.NpcActivePost,
                    ContentId = 0,
                    AuthorRoleId = npcId,
                    PostAuthorRoleId = npcId,
                    TargetRoleId = -1,
                    ParentCommentId = 0,
                    SourceText = BuildActivePostContext(npcId, season),
                    ExistingText = RecentNpcPosts(npcId),
                    ThreadSummary = RecentNpcPosts(npcId),
                    RecentSimilarTexts = RecentNpcPosts(npcId),
                    Intent = "self_status",
                    ReplyStrategy = "casual_update_with_one_concrete_detail",
                    DedupeKey = key
                });
            }
        }

        private void MaybeNpcNpcInteractions(KZoneData kzone)
        {
            if (kzone.datas == null || random.NextDouble() > config.NpcNpcInteractionChance.Value)
            {
                return;
            }

            int commentsLeft = config.MaxNpcNpcCommentsPerPost.Value;
            int repliesLeft = config.MaxNpcNpcRepliesPerPost.Value;
            List<int> known = npcSelector.GetKnownNpcs();
            foreach (KeyValuePair<int, KZoneContentData> pair in kzone.datas)
            {
                if (commentsLeft <= 0 && repliesLeft <= 0)
                {
                    break;
                }
                KZoneContentData content = pair.Value;
                if (content.RoleId <= 0)
                {
                    continue;
                }

                List<int> candidates = new List<int>(known);
                candidates.Remove(content.RoleId);
                int npcId = npcSelector.PickOneWeighted(candidates);
                if (npcId > 0 && commentsLeft > 0)
                {
                    mutation.AddThumb(kzone, content.id, npcId);
                    string key = Dedupe("npc-npc-comment", content.id, npcId, content.RoleId, 0);
                    if (!store.HasDedupe(key) && !store.HasCommentForPurpose(content.id, npcId, "npc-npc-comment"))
                    {
                        Enqueue(new AiJob
                        {
                            Type = AiJobType.NpcNpcComment,
                            ContentId = content.id,
                            AuthorRoleId = npcId,
                            PostAuthorRoleId = SafeContentRoleId(content),
                            TargetRoleId = content.RoleId,
                            ParentCommentId = 0,
                            SourceText = content.Content,
                            ExistingText = mutation.ExistingCommentsSummary(content),
                            DedupeKey = key
                        });
                        commentsLeft--;
                    }
                }

                if (repliesLeft > 0)
                {
                    int parentId;
                    int targetRoleId;
                    if (TryPickNpcComment(content, out parentId, out targetRoleId))
                    {
                        List<int> replyCandidates = new List<int>(known);
                        replyCandidates.Remove(targetRoleId);
                        int replyNpc = npcSelector.PickOneWeighted(replyCandidates);
                        string key = Dedupe("npc-npc-reply", content.id, replyNpc, targetRoleId, parentId);
                        if (replyNpc > 0 && !store.HasDedupe(key))
                        {
                            Enqueue(new AiJob
                            {
                                Type = AiJobType.NpcNpcReply,
                                ContentId = content.id,
                                AuthorRoleId = replyNpc,
                                PostAuthorRoleId = SafeContentRoleId(content),
                                TargetRoleId = targetRoleId,
                                ParentCommentId = parentId,
                                SourceText = content.Content,
                                ParentCommentText = ResolveCommentText(content, parentId),
                                ExistingText = mutation.ExistingCommentsSummary(content),
                                DedupeKey = key
                            });
                            repliesLeft--;
                        }
                    }
                }
            }
        }

        private bool CanRun(KZoneData kzone, KZoneContentData content)
        {
            return config.Enabled.Value && kzone != null && content != null && Cfg.PersonCfgMap != null;
        }

        private static KZoneContentData FindLatestPlayerMoment(KZoneData kzone)
        {
            if (kzone == null || kzone.datas == null)
            {
                return null;
            }

            KZoneContentData latest = null;
            foreach (KeyValuePair<int, KZoneContentData> pair in kzone.datas)
            {
                KZoneContentData content = pair.Value;
                if (content == null)
                {
                    continue;
                }
                try
                {
                    if (content.RoleId != 0)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }
                if (latest == null || content.postTime > latest.postTime)
                {
                    latest = content;
                }
            }
            return latest;
        }

        private int PickEnabledPersona(List<int> candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return -1;
            }

            List<int> remaining = new List<int>(candidates);
            while (remaining.Count > 0)
            {
                int id = npcSelector.PickOneWeighted(remaining);
                if (id <= 0)
                {
                    return -1;
                }
                if (personaService.IsPersonaEnabled(id))
                {
                    return id;
                }
                remaining.Remove(id);
            }
            return -1;
        }

        private void Enqueue(AiJob job)
        {
            if (job == null || store.HasDedupe(job.DedupeKey) || (!string.IsNullOrEmpty(job.DedupeKey) && pendingDedupe.Contains(job.DedupeKey)))
            {
                if (job != null && config != null && config.DebugLog != null && config.DebugLog.Value)
                {
                    log("[DEBUG][AIQueue] 跳过重复/已完成任务：" + JobSummary(job));
                }
                return;
            }
            job.RuntimeIdentity = CurrentJobRuntimeIdentity();
            if (!string.IsNullOrEmpty(job.DedupeKey))
            {
                pendingDedupe.Add(job.DedupeKey);
            }
            queue.Enqueue(job);
            if (config != null && config.DebugLog != null && config.DebugLog.Value)
            {
                log("[DEBUG][AIQueue] 入队：" + JobSummary(job) + ", queue=" + queue.Count + ", running=" + running);
            }
            if (!running)
            {
                runner.StartCoroutine(ProcessQueue());
            }
        }

        private IEnumerator ProcessQueue()
        {
            running = true;
            AiJob activeJob = null;
            bool completedNormally = false;
            try
            {
                while (queue.Count > 0)
                {
                    if (requestsThisRound >= config.MaxAiRequestsPerRound.Value)
                    {
                        log("[WARN][AIQueue] 达到本回合 AI 请求上限，清空剩余任务：requestsThisRound=" + requestsThisRound
                            + ", limit=" + config.MaxAiRequestsPerRound.Value
                            + ", dropped=" + QueuePreview(5));
                        queue.Clear();
                        pendingDedupe.Clear();
                        break;
                    }

                    AiJob job = queue.Dequeue();
                    activeJob = job;
                    if (!IsJobStillCurrent(job))
                    {
                        ReleasePending(job);
                        activeJob = null;
                        log("[INFO][AIQueue] 跳过过期 AI 任务：" + JobSummary(job));
                        continue;
                    }

                    AiPrompt prompt;
                    try
                    {
                        RefreshJobContext(job);
                        prompt = promptBuilder.Build(job);
                    }
                    catch (Exception ex)
                    {
                        log("[ERROR][AIQueue] 构建 AI 提示词失败，跳过任务：" + JobSummary(job) + " " + ex);
                        ReleasePending(job);
                        activeJob = null;
                        continue;
                    }

                    AiResult result = null;
                    yield return GenerateSafely(aiClient, prompt, r => result = r, job, "initial");
                    CountAiRequests(result);
                    SafePromptDebugSave(prompt, result, 0, "initial", job);

                    if (!IsJobStillCurrent(job))
                    {
                        ReleasePending(job);
                        activeJob = null;
                        log("[INFO][AIQueue] 丢弃过期 AI 结果：" + JobSummary(job));
                        continue;
                    }

                    if ((result == null || !result.Success) && config.FallbackWhenFailed.Value)
                    {
                        if (result != null && !string.IsNullOrEmpty(result.Error))
                        {
                            log("[WARN][AIQueue] AI 生成失败，使用模板：" + result.Error + " job=" + JobSummary(job));
                        }
                        yield return GenerateSafely(fallbackClient, prompt, r => result = r, job, "fallback");
                        SafePromptDebugSave(prompt, result, 0, "fallback", job);
                        if (result == null || !result.Success)
                        {
                            log("[ERROR][AIQueue] AI 失败后本地兜底也未写入：" + (result != null ? result.Error : "no result")
                                + " job=" + JobSummary(job));
                        }
                    }

                    int rewriteAttempt = 0;
                    string rewriteIssue;
                    while (ShouldRewriteResult(job, result, out rewriteIssue) && rewriteAttempt < DuplicateAiRewriteAttempts)
                    {
                        if (requestsThisRound >= config.MaxAiRequestsPerRound.Value)
                        {
                            log("[WARN][AIQueue] AI 去重重写跳过：本回合请求数已达上限。job=" + JobSummary(job));
                            break;
                        }
                        rewriteAttempt++;
                        string rejectedText = result != null ? result.Text : "";
                        log("[WARN][AIQuality] AI 生成内容质量不合格，要求模型重写(" + rewriteAttempt + "/" + DuplicateAiRewriteAttempts + ")："
                            + rewriteIssue + ", rejected=" + TextSummaryForLog(rejectedText)
                            + " job=" + JobSummary(job));
                        job.ExtraInstruction = AppendContext(job.ExtraInstruction, BuildRewriteInstruction(job, rejectedText, rewriteIssue, rewriteAttempt));
                        try
                        {
                            RefreshJobContext(job);
                            prompt = promptBuilder.Build(job);
                        }
                        catch (Exception ex)
                        {
                            log("[ERROR][AIQueue] 重写提示词构建失败，跳过任务：" + JobSummary(job) + " " + ex);
                            result = AiResult.Fail("rewrite prompt build failed: " + ex.Message);
                            break;
                        }
                        prompt.ExistingText = AppendContext(prompt.ExistingText, rejectedText);
                        prompt.User += "\n[重写提醒]\n上一版不可用，原因：" + rewriteIssue + "\n"
                                       + "上一版和附近评论的角度已经用过：\n" + BuildForbiddenTextBlock(job, rejectedText)
                                       + "\n请换一个真实反应角度；不要只替换同义词，不要出现助手/客服/生成任务口吻。\n";
                        prompt.Temperature = Math.Min(0.85f, prompt.Temperature + 0.08f * rewriteAttempt);
                        result = null;
                        yield return GenerateSafely(aiClient, prompt, r => result = r, job, "rewrite");
                        CountAiRequests(result);
                        SafePromptDebugSave(prompt, result, rewriteAttempt, "rewrite", job);
                        if (!IsJobStillCurrent(job))
                        {
                            ReleasePending(job);
                            activeJob = null;
                            log("[INFO][AIQueue] 丢弃过期 AI 重写结果：" + JobSummary(job));
                            result = null;
                            break;
                        }
                        if (result == null || !result.Success)
                        {
                            log("[WARN][AIQueue] AI 去重重写失败，不使用模板替代重复内容：" + (result != null ? result.Error : "no result")
                                + " job=" + JobSummary(job));
                            break;
                        }
                    }

                    if (ShouldRewriteResult(job, result, out rewriteIssue))
                    {
                        log("[WARN][AIQuality] 跳过质量不合格 AI 内容：" + rewriteIssue
                            + ", result=" + TextSummaryForLog(result != null ? result.Text : null)
                            + " job=" + JobSummary(job));
                        RecordTelemetry(job, prompt, result, rewriteAttempt, "rejected", rewriteIssue);
                        ReleasePending(job);
                        activeJob = null;
                        continue;
                    }
                    string finalState;
                    try
                    {
                        finalState = ApplyResult(job, result);
                    }
                    catch (Exception ex)
                    {
                        finalState = "apply_error";
                        log("[ERROR][AIQueue] AI 结果应用失败，已跳过该任务：" + JobSummary(job) + " " + ex);
                    }
                    RecordTelemetry(job, prompt, result, rewriteAttempt, finalState, null);
                    ReleasePending(job);
                    activeJob = null;
                    yield return null;
                }
                completedNormally = true;
            }
            finally
            {
                if (!completedNormally)
                {
                    ReleasePending(activeJob);
                    log("[ERROR][AIQueue] 队列处理被异常中断，已释放当前任务并尝试恢复：job=" + JobSummary(activeJob)
                        + ", remaining=" + queue.Count);
                }
                running = false;
                if (!completedNormally && queue.Count > 0 && runner != null)
                {
                    runner.StartCoroutine(ProcessQueue());
                }
                if (queue.Count == 0 && config != null && config.DebugLog != null && config.DebugLog.Value)
                {
                    log("[DEBUG][AIQueue] 队列处理结束：requestsThisRound=" + requestsThisRound);
                }
            }
        }

        private IEnumerator GenerateSafely(IAiClient client, AiPrompt prompt, Action<AiResult> callback, AiJob job, string phase)
        {
            if (client == null)
            {
                callback(AiResult.Fail("AI client is null"));
                yield break;
            }

            IEnumerator routine = null;
            try
            {
                routine = client.Generate(prompt, callback);
            }
            catch (Exception ex)
            {
                callback(AiResult.Fail(phase + " generate start exception: " + ex.Message));
                log("[ERROR][AIQueue] AI 请求启动异常：" + phase + " job=" + JobSummary(job) + " " + ex);
                yield break;
            }

            if (routine == null)
            {
                callback(AiResult.Fail(phase + " generate returned null coroutine"));
                log("[ERROR][AIQueue] AI 请求返回空协程：" + phase + " job=" + JobSummary(job));
                yield break;
            }

            while (true)
            {
                object current = null;
                bool moved;
                try
                {
                    moved = routine.MoveNext();
                    if (moved)
                    {
                        current = routine.Current;
                    }
                }
                catch (Exception ex)
                {
                    callback(AiResult.Fail(phase + " generate runtime exception: " + ex.Message));
                    log("[ERROR][AIQueue] AI 请求执行异常：" + phase + " job=" + JobSummary(job) + " " + ex);
                    yield break;
                }

                if (!moved)
                {
                    break;
                }
                yield return current;
            }
        }

        private void SafePromptDebugSave(AiPrompt prompt, AiResult result, int rewriteAttempt, string phase, AiJob job)
        {
            try
            {
                promptDebugRecorder.Save(prompt, result, rewriteAttempt, phase);
            }
            catch (Exception ex)
            {
                log("[WARN][AIDebug] 保存提示词调试记录失败：" + phase + " job=" + JobSummary(job) + " " + ex.Message);
            }
        }

        private string CurrentJobRuntimeIdentity()
        {
            return store.CurrentRuntimeIdentity + "|epoch=" + runtimeEpoch;
        }

        private bool IsJobStillCurrent(AiJob job)
        {
            return job != null && string.Equals(job.RuntimeIdentity, CurrentJobRuntimeIdentity(), StringComparison.Ordinal);
        }

        private void ReleasePending(AiJob job)
        {
            if (job != null && !string.IsNullOrEmpty(job.DedupeKey))
            {
                pendingDedupe.Remove(job.DedupeKey);
            }
        }

        private string QueuePreview(int max)
        {
            try
            {
                if (queue.Count == 0)
                {
                    return "0";
                }
                List<string> items = new List<string>();
                foreach (AiJob item in queue)
                {
                    items.Add(JobSummary(item));
                    if (items.Count >= max)
                    {
                        break;
                    }
                }
                return "count=" + queue.Count + ", first=" + string.Join(" | ", items.ToArray());
            }
            catch
            {
                return "count=" + queue.Count;
            }
        }

        private static string JobSummary(AiJob job)
        {
            if (job == null)
            {
                return "null";
            }
            return "type=" + job.Type
                + ", contentId=" + job.ContentId
                + ", author=" + job.AuthorRoleId
                + ", postAuthor=" + job.PostAuthorRoleId
                + ", target=" + job.TargetRoleId
                + ", parent=" + job.ParentCommentId
                + ", purpose=" + SafePart(job.PurposeOverride)
                + ", dedupe=" + SafePart(job.DedupeKey)
                + ", runtime=" + SafePart(job.RuntimeIdentity);
        }

        private static string TextSummaryForLog(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "empty";
            }
            return "len=" + value.Length + ",hash=" + StableHashForLog(value);
        }

        private static string StableHashForLog(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 16777619;
                }
                return hash.ToString("x8");
            }
        }

        private static string SafePart(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }
            string sanitized = value.Replace("\r", " ").Replace("\n", " ");
            return sanitized.Length <= 96 ? sanitized : sanitized.Substring(0, 96) + "...";
        }

        private void CountAiRequests(AiResult result)
        {
            int attempts = result != null ? result.RequestAttempts : 0;
            if (attempts <= 0)
            {
                return;
            }
            requestsThisRound += attempts;
        }

        private bool ShouldRewriteResult(AiJob job, AiResult result, out string issue)
        {
            issue = null;
            if (job == null || result == null || !result.Success)
            {
                return false;
            }

            if (IsSilentReply(result))
            {
                if (ShouldForceVisibleReply(job))
                {
                    issue = "本次互动已由游戏调度为可见发言，不能返回 should_reply=false";
                    return true;
                }
                return false;
            }

            if (string.IsNullOrEmpty(result.Text))
            {
                return false;
            }

            if (TextSanitizer.LooksLikeAssistantLeak(result.Text))
            {
                issue = "出现助手/客服/生成任务口吻";
                return true;
            }

            if (TextSanitizer.LooksLikeTemplatePhrase(result.Text))
            {
                issue = "模板感过重，像预设范文或 AI 安慰句";
                return true;
            }

            if (IsDuplicateGeneratedText(job, result.Text))
            {
                issue = "与已有评论或近期生成内容重复";
                return true;
            }

            return false;
        }

        private static bool ShouldForceVisibleReply(AiJob job)
        {
            if (job == null)
            {
                return false;
            }
            return job.Type == AiJobType.NpcActivePost
                || job.Type == AiJobType.PlayerCommentReply
                || job.Type == AiJobType.NpcNpcComment
                || job.Type == AiJobType.NpcNpcReply;
        }

        private static bool IsSilentReply(AiResult result)
        {
            return result != null && result.HasShouldReply && !result.ShouldReply;
        }

        private void RecordTelemetry(AiJob job, AiPrompt prompt, AiResult result, int rewriteAttempts, string finalState, string qualityIssue)
        {
            if (telemetry == null)
            {
                return;
            }
            telemetry.RecordAiResult(job, prompt, result, rewriteAttempts, finalState, qualityIssue);
        }

        private string ApplyResult(AiJob job, AiResult result)
        {
            if (result == null || !result.Success)
            {
                return "failed";
            }
            if (!IsJobStillCurrent(job))
            {
                log("应用前丢弃过期 AI 结果：" + job.Type + "/" + job.ContentId);
                return "stale";
            }

            KZoneData kzone = Singleton<RoleMgr>.Ins.GetKZoneData();
            if (IsSilentReply(result))
            {
                if (!string.IsNullOrEmpty(job.DedupeKey))
                {
                    store.AddDedupe(job.DedupeKey);
                }
                if (job.Type != AiJobType.NpcActivePost)
                {
                    ApplyActions(job, result.Actions, kzone, job.ContentId);
                }
                store.Save();
                log("AI 选择不公开回复：" + job.Type + "/" + job.ContentId);
                return "silent";
            }

            if (string.IsNullOrEmpty(result.Text))
            {
                return "empty";
            }

            bool applied = false;
            if (job.Type == AiJobType.NpcActivePost)
            {
                AiMomentRecord moment = mutation.AddNpcMoment(kzone, job.AuthorRoleId, result.Text, true);
                if (moment != null)
                {
                    store.AddDedupe(job.DedupeKey);
                    ApplyActions(job, result.Actions, kzone, moment.ContentId);
                    AddImmediateNpcInteractionsForNewPost(kzone, moment.ContentId, job.AuthorRoleId);
                    applied = true;
                }
            }
            else
            {
                AiCommentRecord comment = mutation.AddComment(kzone, job.ContentId, job.AuthorRoleId, job.TargetRoleId, job.ParentCommentId, result.Text, Purpose(job));
                if (comment != null)
                {
                    store.AddDedupe(job.DedupeKey);
                    ApplyActions(job, result.Actions, kzone, job.ContentId);
                    applied = true;
                }
            }
            store.Save();
            return applied ? "applied" : "not_applied";
        }

        private void RefreshJobContext(AiJob job)
        {
            if (job == null || job.ContentId <= 0)
            {
                return;
            }

            try
            {
                KZoneData kzone = Singleton<RoleMgr>.Ins.GetKZoneData();
                KZoneContentData content;
                if (kzone == null || kzone.datas == null || !kzone.datas.TryGetValue(job.ContentId, out content) || content == null)
                {
                    return;
                }

                if (string.IsNullOrEmpty(job.SourceText))
                {
                    job.SourceText = content.Content;
                }
                if (job.PostAuthorRoleId == 0 && SafeContentRoleId(content) > 0)
                {
                    job.PostAuthorRoleId = SafeContentRoleId(content);
                }
                if (job.ParentCommentId > 0)
                {
                    job.ParentCommentText = ResolveCommentText(content, job.ParentCommentId);
                }
                job.ExistingText = mutation.ExistingCommentsSummary(content);
                job.ThreadSummary = mutation.ThreadSummary(content);
                job.RecentSimilarTexts = mutation.RecentSimilarTexts(content);
                job.RecentSelfTurns = mutation.RecentSelfTurns(content, job.AuthorRoleId);
                if (string.IsNullOrEmpty(job.Intent))
                {
                    job.Intent = InferIntent(job);
                }
                if (string.IsNullOrEmpty(job.ReplyStrategy))
                {
                    job.ReplyStrategy = InferReplyStrategy(job);
                }
            }
            catch
            {
            }
        }

        private bool IsDuplicateGeneratedText(AiJob job, string text)
        {
            if (job == null || string.IsNullOrEmpty(text) || job.ContentId <= 0)
            {
                return false;
            }

            try
            {
                KZoneData kzone = Singleton<RoleMgr>.Ins.GetKZoneData();
                KZoneContentData content;
                if (kzone == null || kzone.datas == null || !kzone.datas.TryGetValue(job.ContentId, out content) || content == null || content.comments == null)
                {
                    return false;
                }

                foreach (KeyValuePair<int, KZoneCommentData> pair in content.comments)
                {
                    string existing = null;
                    KZoneCommentCfg cfg;
                    if (Cfg.KZoneCommentCfgMap != null && Cfg.KZoneCommentCfgMap.TryGetValue(pair.Key, out cfg))
                    {
                        existing = cfg.content;
                    }
                    if (string.IsNullOrEmpty(existing) && pair.Value != null)
                    {
                        existing = pair.Value.content;
                    }
                    if (IsSameOrTooSimilar(existing, text))
                    {
                        return true;
                    }
                }

                if (IsDuplicateInGeneratedStore(job, text))
                {
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private bool IsDuplicateInGeneratedStore(AiJob job, string text)
        {
            if (store == null || store.Data == null || store.Data.Comments == null || string.IsNullOrEmpty(text))
            {
                return false;
            }
            for (int i = store.Data.Comments.Count - 1; i >= 0; i--)
            {
                AiCommentRecord record = store.Data.Comments[i];
                if (record == null || record.AuthorRoleId <= 0 || record.CommentId == job.ParentCommentId)
                {
                    continue;
                }
                if (IsPlayerWrittenPurpose(record.Purpose))
                {
                    continue;
                }
                if (record.ContentId == job.ContentId || record.AuthorRoleId == job.AuthorRoleId)
                {
                    if (IsSameOrTooSimilar(record.Content, text))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private string BuildForbiddenTextBlock(AiJob job, string candidate)
        {
            List<string> values = new List<string>();
            if (!string.IsNullOrEmpty(candidate))
            {
                values.Add(SummarizeTextAngle(candidate));
            }

            try
            {
                KZoneData kzone = Singleton<RoleMgr>.Ins.GetKZoneData();
                KZoneContentData content;
                if (kzone != null && kzone.datas != null && kzone.datas.TryGetValue(job.ContentId, out content) && content != null && content.comments != null)
                {
                    foreach (KeyValuePair<int, KZoneCommentData> pair in content.comments)
                    {
                        string existing = null;
                        KZoneCommentCfg cfg;
                        if (Cfg.KZoneCommentCfgMap != null && Cfg.KZoneCommentCfgMap.TryGetValue(pair.Key, out cfg))
                        {
                            existing = cfg.content;
                        }
                        if (string.IsNullOrEmpty(existing) && pair.Value != null)
                        {
                            existing = pair.Value.content;
                        }
                        if (!string.IsNullOrEmpty(existing) && values.Count < 10)
                        {
                            string angle = SummarizeTextAngle(KZoneData.FormatContent(existing));
                            if (!string.IsNullOrEmpty(angle) && !values.Contains(angle))
                            {
                                values.Add(angle);
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            return string.Join("\n", values.ToArray());
        }

        private static string BuildRewriteInstruction(AiJob job, string rejectedText, string issue, int attempt)
        {
            string axis = RewriteAxis(job, attempt);
            string intent = job != null && !string.IsNullOrEmpty(job.Intent) ? job.Intent : InferIntent(job);
            string angle = string.IsNullOrEmpty(rejectedText) ? "空回复或拒绝发言" : SummarizeTextAngle(rejectedText);
            return "刚才那版不能使用，原因：" + (string.IsNullOrEmpty(issue) ? "质量不合格" : issue) + "。"
                   + "上一版角度：" + angle + "。"
                   + "保持同一意图(" + intent + ")和同一 NPC 性格，但从另一个角度重写：" + axis + "。"
                   + "不要只换同义词；要像角色临时换了一个真实反应。"
                   + "禁止写“我可以帮你/请提供/作为NPC/生成回复/人设语气场景”等助手话。";
        }

        private static string SummarizeTextAngle(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "泛泛回应";
            }
            string s = text.Trim();
            if (ContainsAny(s, "证明", "结论", "逻辑", "条件", "确定"))
            {
                return "理性确认/要求证据";
            }
            if (ContainsAny(s, "在意", "记", "看出来", "认真看"))
            {
                return "表示已读且在意";
            }
            if (ContainsAny(s, "别勉强", "休息", "累", "压力", "难受"))
            {
                return "克制安慰/提醒休息";
            }
            if (ContainsAny(s, "哈哈", "笑", "火钳", "你小子"))
            {
                return "轻松吐槽/玩笑接梗";
            }
            if (ContainsAny(s, "真的吗", "为什么", "？", "?"))
            {
                return "追问原因或真假";
            }
            if (ContainsAny(s, "喜欢", "告白", "发错", "公开"))
            {
                return "处理公开告白边界";
            }
            if (ContainsAny(s, "不错", "可以", "厉害", "进步"))
            {
                return "认可结果/提醒别飘";
            }
            return "普通附和/概括式回应";
        }

        private static string RewriteAxis(AiJob job, int attempt)
        {
            int index = Math.Abs(attempt);
            if (job != null)
            {
                index += ((int)job.Type) + Math.Abs(job.AuthorRoleId);
            }
            switch (index % 5)
            {
                case 0:
                    return "respond through a concrete detail in the source text";
                case 1:
                    return "respond through the NPC's value judgment or standard";
                case 2:
                    return "respond through a short follow-up question";
                case 3:
                    return "respond through restrained concern or support";
                default:
                    return "respond through light teasing that still answers the source";
            }
        }

        private static bool IsSameOrTooSimilar(string left, string right)
        {
            left = NormalizeForCompare(left);
            right = NormalizeForCompare(right);
            if (left.Length == 0 || right.Length == 0)
            {
                return false;
            }
            if (left == right || left.Contains(right) || right.Contains(left))
            {
                return true;
            }
            int common = LongestCommonSubstringLength(left, right);
            int min = Math.Min(left.Length, right.Length);
            return min >= 10 && common * 100 / min >= 84;
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

        private static int LongestCommonSubstringLength(string a, string b)
        {
            int[,] dp = new int[a.Length + 1, b.Length + 1];
            int best = 0;
            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    if (a[i - 1] == b[j - 1])
                    {
                        dp[i, j] = dp[i - 1, j - 1] + 1;
                        if (dp[i, j] > best)
                        {
                            best = dp[i, j];
                        }
                    }
                }
            }
            return best;
        }

        private static string AppendContext(string existing, string addition)
        {
            if (string.IsNullOrEmpty(addition))
            {
                return existing ?? "";
            }
            if (string.IsNullOrEmpty(existing))
            {
                return addition;
            }
            return existing + "；" + addition;
        }

        private static string InferIntent(AiJob job)
        {
            if (job == null)
            {
                return "general";
            }
            switch (job.Type)
            {
                case AiJobType.PlayerPostComment:
                    return ClassifyPlayerText(job.SourceText);
                case AiJobType.PlayerCommentReply:
                    return ClassifyPlayerText(!string.IsNullOrEmpty(job.ParentCommentText) ? job.ParentCommentText : job.SourceText);
                case AiJobType.NpcActivePost:
                    return "self_status";
                case AiJobType.NpcNpcComment:
                    return "npc_public_comment";
                case AiJobType.NpcNpcReply:
                    return "npc_reply";
                default:
                    return "general";
            }
        }

        private static string InferReplyStrategy(AiJob job)
        {
            string intent = InferIntent(job);
            switch (intent)
            {
                case "confession":
                    return "choose one of: gentle_tease, cautious_accept, public_boundary";
                case "follow_up_question":
                    return "choose one of: clarify_previous_reply, answer_really_or_why, continue_previous_stance";
                case "help":
                    return "choose one of: direct_help, concise_advice, ask_for_more_detail";
                case "complaint":
                    return "choose one of: empathize_then_judge, practical_fix, dry_tease";
                case "invitation":
                    return "choose one of: accept_if_plausible, decline_politely, redirect";
                case "joke":
                    return "choose one of: catch_the_joke, echo_lightly, counter_joke";
                case "daily_note":
                    return "choose one of: acknowledge_detail, extend_topic, ask_followup";
                case "emotional_vent":
                    return "choose one of: comfort, stabilize, practical_support";
                case "brag":
                    return "choose one of: congratulate, tease, ask_for_proof";
                case "study_growth":
                    return "choose one of: recognize_effort, practical_push, short_encouragement";
                case "question":
                    return "choose one of: answer_directly, add_context, ask_clarify";
                case "self_status":
                    return "choose one of: casual_update, concrete_detail, mild_mood";
                default:
                    return "choose one of: direct_reply, tease, support";
            }
        }

        private static string ClassifyPlayerText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "general";
            }
            string s = text.Trim();
            if (IsShortFollowUp(s))
            {
                return "follow_up_question";
            }
            if (s.Contains("？") || s.Contains("?"))
            {
                return "question";
            }
            if (ContainsAny(s, new[] { "帮", "教", "怎么", "请问", "求", "求助" }))
            {
                return "help";
            }
            if (ContainsAny(s, new[] { "学习", "刷题", "复习", "考试", "作业", "卷子", "成绩", "进步", "努力", "补课", "排名", "错题", "上岸" }))
            {
                return "study_growth";
            }
            if (ContainsAny(s, new[] { "喜欢你", "爱你", "告白", "在一起", "喜欢我吗" }))
            {
                return "confession";
            }
            if (ContainsAny(s, new[] { "哈哈", "笑死", "乐死", "好好笑", "整活" }))
            {
                return "joke";
            }
            if (ContainsAny(s, new[] { "烦", "难受", "委屈", "生气", "崩溃", "想哭" }))
            {
                return "emotional_vent";
            }
            if (ContainsAny(s, new[] { "约", "一起", "去", "出来", "见面" }))
            {
                return "invitation";
            }
            if (ContainsAny(s, new[] { "厉害", "牛", "拿下", "赢了", "过了" }))
            {
                return "brag";
            }
            if (ContainsAny(s, new[] { "今天", "刚刚", "昨晚", "日常", "记录", "随手" }))
            {
                return "daily_note";
            }
            return "general";
        }

        private static bool IsShortFollowUp(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            string s = text.Trim();
            if (s.Length > 8)
            {
                return false;
            }
            return ContainsAny(s, new[] { "真的吗", "真的？", "真的假的", "为什么", "为啥", "然后呢", "所以呢", "怎么说", "啥意思", "什么意思", "你确定", "是吗", "对吗", "呢", "吗" });
        }

        private static bool ContainsAny(string text, params string[] keywords)
        {
            if (string.IsNullOrEmpty(text) || keywords == null)
            {
                return false;
            }
            for (int i = 0; i < keywords.Length; i++)
            {
                if (!string.IsNullOrEmpty(keywords[i]) && text.IndexOf(keywords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private void ApplyActions(AiJob job, AiActionSet actions, KZoneData kzone, int contentId)
        {
            if (!config.ApplyStructuredActions.Value || actions == null || !actions.HasAny || job == null || kzone == null)
            {
                return;
            }

            try
            {
                List<string> actionMessages = new List<string>();
                if (actions.HasLike && actions.Like && CanApplyLike(job, kzone, contentId))
                {
                    mutation.AddThumb(kzone, contentId, job.AuthorRoleId);
                    actionMessages.Add(RoleName(job.AuthorRoleId) + "点赞");
                }

                if (IsMainParticipant(job, kzone, contentId))
                {
                    string favorMessage = ApplyFavorDelta(job, actions);
                    if (!string.IsNullOrEmpty(favorMessage))
                    {
                        actionMessages.Add(favorMessage);
                    }
                    string relationMessage = ApplyRelationChange(job, actions);
                    if (!string.IsNullOrEmpty(relationMessage))
                    {
                        actionMessages.Add(relationMessage);
                    }
                }

                int appliedAttrChanges = 0;
                ApplyAttrDeltas(job, actions.MainAttrDeltas, true, kzone, contentId, ref appliedAttrChanges, actionMessages);
                ApplyAttrDeltas(job, actions.NpcAttrDeltas, false, kzone, contentId, ref appliedAttrChanges, actionMessages);
                ShowActionSummary(actionMessages);
            }
            catch (Exception ex)
            {
                log("应用 AI 结构化动作失败：" + ex.Message);
            }
        }

        private bool CanApplyLike(AiJob job, KZoneData kzone, int contentId)
        {
            if (job.AuthorRoleId <= 0 || contentId <= 0 || kzone.datas == null)
            {
                return false;
            }
            KZoneContentData content;
            if (!kzone.datas.TryGetValue(contentId, out content) || content == null)
            {
                return false;
            }
            return SafeContentRoleId(content) != job.AuthorRoleId;
        }

        private bool IsMainParticipant(AiJob job, KZoneData kzone, int contentId)
        {
            if (job.Type == AiJobType.PlayerPostComment || job.Type == AiJobType.PlayerCommentReply || job.TargetRoleId == 0)
            {
                return true;
            }
            if (kzone != null && kzone.datas != null)
            {
                KZoneContentData content;
                if (kzone.datas.TryGetValue(contentId, out content) && SafeContentRoleId(content) == 0)
                {
                    return true;
                }
            }
            return false;
        }

        private string ApplyFavorDelta(AiJob job, AiActionSet actions)
        {
            if (!actions.HasFavorDelta || job.AuthorRoleId <= 0)
            {
                return null;
            }
            float delta = Clamp(actions.FavorDelta, -Math.Abs(config.MaxAiFavorDelta.Value), Math.Abs(config.MaxAiFavorDelta.Value));
            if (Math.Abs(delta) < 0.001f)
            {
                return null;
            }
            Role role = SafeRole(job.AuthorRoleId);
            if (role == null)
            {
                return null;
            }
            role.UpdateFavor(delta, 1f, "QQ空间AI互动");
            return RoleName(job.AuthorRoleId) + "好感" + FormatSigned(delta);
        }

        private string ApplyRelationChange(AiJob job, AiActionSet actions)
        {
            if (job.AuthorRoleId <= 0 || (!actions.HasRelationDelta && !actions.HasRelationSet))
            {
                return null;
            }
            int maxDelta = Math.Max(0, config.MaxAiRelationDelta.Value);
            if (maxDelta <= 0)
            {
                return null;
            }

            Role role = SafeRole(job.AuthorRoleId);
            if (role == null || role.Relation <= 0)
            {
                return null;
            }

            int targetRelation = role.Relation;
            if (actions.HasRelationSet)
            {
                targetRelation = actions.RelationSet;
            }
            else if (actions.HasRelationDelta)
            {
                targetRelation = role.Relation + ClampInt(actions.RelationDelta, -maxDelta, maxDelta);
            }

            int diff = targetRelation - role.Relation;
            diff = ClampInt(diff, -maxDelta, maxDelta);
            if (diff == 0)
            {
                return null;
            }
            targetRelation = role.Relation + diff;

            if (targetRelation < 1 || targetRelation > 6 || Cfg.RelationCfgMap == null || !Cfg.RelationCfgMap.ContainsKey(targetRelation))
            {
                return null;
            }

            int oldRelation = role.Relation;
            Singleton<RoleMgr>.Ins.GetRelationData(true).ChangeRelation(job.AuthorRoleId, targetRelation, "QQ空间AI互动", false);
            return RoleName(job.AuthorRoleId) + "关系" + oldRelation + "→" + targetRelation;
        }

        private void ApplyAttrDeltas(AiJob job, List<AiAttrDelta> deltas, bool mainRole, KZoneData kzone, int contentId, ref int appliedCount, List<string> actionMessages)
        {
            if (deltas == null || deltas.Count == 0)
            {
                return;
            }
            int maxChanges = Math.Max(0, config.MaxAiAttrChanges.Value);
            if (maxChanges <= 0)
            {
                return;
            }

            for (int i = 0; i < deltas.Count && appliedCount < maxChanges; i++)
            {
                AiAttrDelta delta = deltas[i];
                if (delta == null || Cfg.PersonAttrCfgMap == null || !Cfg.PersonAttrCfgMap.ContainsKey(delta.AttrId))
                {
                    continue;
                }

                int roleId = mainRole ? 0 : ResolveNpcAttrRole(job, delta.RoleId, kzone, contentId);
                if (!mainRole && roleId <= 0)
                {
                    continue;
                }

                Role role = roleId == 0 ? Singleton<RoleMgr>.Ins.GetRole() : SafeRole(roleId);
                if (role == null)
                {
                    continue;
                }

                float value = Clamp(delta.Delta, -Math.Abs(config.MaxAiAttrDelta.Value), Math.Abs(config.MaxAiAttrDelta.Value));
                if (Math.Abs(value) < 0.001f)
                {
                    continue;
                }

                role.UpdateAttr(delta.AttrId, value, 1f, "QQ空间AI互动", 2);
                if (actionMessages != null)
                {
                    actionMessages.Add((roleId == 0 ? "主角" : RoleName(roleId)) + AttrName(delta.AttrId) + FormatSigned(value));
                }
                appliedCount++;
            }
        }

        private void ShowActionSummary(List<string> actionMessages)
        {
            if (actionMessages == null || actionMessages.Count == 0)
            {
                return;
            }
            string message = "QQ空间AI互动：" + string.Join("，", actionMessages.ToArray());
            log(message);
            if (!config.ShowActionToast.Value)
            {
                return;
            }
            try
            {
                ToastHelper.Toast(message, null, ToastUIType.Normal);
            }
            catch
            {
            }
        }

        private static string FormatSigned(float value)
        {
            return value >= 0f ? "+" + value.ToString("0.#") : value.ToString("0.#");
        }

        private static string RoleName(int roleId)
        {
            try
            {
                PersonCfg cfg;
                if (Cfg.PersonCfgMap != null && Cfg.PersonCfgMap.TryGetValue(roleId, out cfg) && cfg != null && !string.IsNullOrEmpty(cfg.name))
                {
                    return cfg.name;
                }
            }
            catch
            {
            }
            return "NPC" + roleId;
        }

        private static string AttrName(int attrId)
        {
            try
            {
                PersonAttrCfg cfg;
                if (Cfg.PersonAttrCfgMap != null && Cfg.PersonAttrCfgMap.TryGetValue(attrId, out cfg) && cfg != null)
                {
                    string name = cfg.Name(0, 0);
                    if (!string.IsNullOrEmpty(name))
                    {
                        return name;
                    }
                }
            }
            catch
            {
            }
            return "属性" + attrId;
        }

        private int ResolveNpcAttrRole(AiJob job, int requestedRoleId, KZoneData kzone, int contentId)
        {
            if (requestedRoleId <= 0)
            {
                return job.AuthorRoleId;
            }
            if (requestedRoleId == job.AuthorRoleId || requestedRoleId == job.TargetRoleId)
            {
                return requestedRoleId;
            }
            if (kzone != null && kzone.datas != null)
            {
                KZoneContentData content;
                if (kzone.datas.TryGetValue(contentId, out content) && SafeContentRoleId(content) == requestedRoleId)
                {
                    return requestedRoleId;
                }
            }
            return -1;
        }

        private static Role SafeRole(int roleId)
        {
            try
            {
                return Singleton<RoleMgr>.Ins.GetRole(roleId);
            }
            catch
            {
                return null;
            }
        }

        private static int SafeContentRoleId(KZoneContentData content)
        {
            try
            {
                return content != null ? content.RoleId : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static int ClampInt(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private void AddImmediateNpcInteractionsForNewPost(KZoneData kzone, int contentId, int authorRoleId)
        {
            List<int> known = npcSelector.GetKnownNpcs();
            known.Remove(authorRoleId);
            List<int> likes = npcSelector.PickWeighted(known, Math.Min(3, config.MaxLikesPerPlayerPost.Value), 0.55f);
            foreach (int id in likes)
            {
                mutation.AddThumb(kzone, contentId, id);
            }
        }

        private int ResolveReplyNpc(KZoneContentData content, KZoneCommentCfg commentCfg)
        {
            if (commentCfg.parent > 0)
            {
                KZoneCommentCfg parentCfg;
                if (Cfg.KZoneCommentCfgMap.TryGetValue(commentCfg.parent, out parentCfg)
                    && parentCfg.roles != null
                    && parentCfg.roles.Count > 0
                    && parentCfg.roles[0] > 0)
                {
                    return parentCfg.roles[0];
                }
            }
            return content.RoleId;
        }

        private bool TryPickNpcComment(KZoneContentData content, out int commentId, out int authorRoleId)
        {
            commentId = 0;
            authorRoleId = 0;
            if (content.comments == null || content.comments.Count == 0)
            {
                return false;
            }
            List<int> ids = new List<int>(content.comments.Keys);
            for (int i = ids.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                int tmp = ids[i];
                ids[i] = ids[j];
                ids[j] = tmp;
            }
            for (int i = 0; i < ids.Count; i++)
            {
                int id = ids[i];
                KZoneCommentCfg cfg;
                if (Cfg.KZoneCommentCfgMap.TryGetValue(id, out cfg)
                    && cfg.parent == 0
                    && cfg.roles != null
                    && cfg.roles.Count > 0
                    && cfg.roles[0] > 0)
                {
                    commentId = id;
                    authorRoleId = cfg.roles[0];
                    return true;
                }
            }
            return false;
        }

        private static string ResolveCommentText(KZoneContentData content, int commentId)
        {
            if (content == null || commentId <= 0)
            {
                return "";
            }

            KZoneCommentCfg cfg;
            if (Cfg.KZoneCommentCfgMap != null && Cfg.KZoneCommentCfgMap.TryGetValue(commentId, out cfg) && !string.IsNullOrEmpty(cfg.content))
            {
                return KZoneData.FormatContent(cfg.content);
            }

            KZoneCommentData data;
            if (content.comments != null && content.comments.TryGetValue(commentId, out data) && data != null)
            {
                return data.content ?? "";
            }
            return "";
        }

        private string RecentNpcPosts(int npcId)
        {
            List<string> items = new List<string>();
            foreach (AiMomentRecord record in store.Data.Moments)
            {
                if (record.AuthorRoleId == npcId && !string.IsNullOrEmpty(record.Content))
                {
                    items.Add(record.Content);
                    if (items.Count >= 3)
                    {
                        break;
                    }
                }
            }
            return string.Join("；", items.ToArray());
        }

        private string BuildActivePostContext(int npcId, ValueTuple<int, int> season)
        {
            string topicSeed = PickActivePostSeed(npcId);
            string recent = RecentNpcPosts(npcId);
            return "season=" + RoundMgr.ToSeasonYearStr(season)
                   + "; topic_seed=" + topicSeed
                   + "; recent_own_posts=" + (string.IsNullOrEmpty(recent) ? "none" : recent)
                   + "; instruction=Write one concrete casual QQ Zone status from this seed. Avoid repeating recent_own_posts.";
        }

        private string PickActivePostSeed(int npcId)
        {
            PersonaContext ctx = personaService.GetContext(npcId);
            List<string> candidates = new List<string>();
            AddCandidates(candidates, ctx != null ? ctx.ActivePostTriggers : null, 8);
            AddCandidates(candidates, ctx != null ? ctx.PostTopics : null, 8);
            AddCandidates(candidates, ctx != null ? ctx.Likes : null, 5);
            if (candidates.Count == 0)
            {
                candidates.Add("campus daily life");
                candidates.Add("study pressure");
                candidates.Add("club or hobby");
                candidates.Add("weather and season");
                candidates.Add("small mood");
            }
            return candidates[random.Next(candidates.Count)];
        }

        private static void AddCandidates(List<string> target, List<string> source, int max)
        {
            if (target == null || source == null || max <= 0)
            {
                return;
            }
            int count = 0;
            foreach (string item in source)
            {
                if (string.IsNullOrEmpty(item))
                {
                    continue;
                }
                target.Add(item.Replace("\r", " ").Replace("\n", " "));
                count++;
                if (count >= max)
                {
                    break;
                }
            }
        }

        private static string Purpose(AiJob job)
        {
            if (job != null && !string.IsNullOrEmpty(job.PurposeOverride))
            {
                return job.PurposeOverride;
            }
            return Purpose(job != null ? job.Type : AiJobType.PlayerPostComment);
        }

        private static string Purpose(AiJobType type)
        {
            switch (type)
            {
                case AiJobType.NpcNpcComment:
                    return "npc-npc-comment";
                case AiJobType.NpcNpcReply:
                    return "npc-npc-reply";
                case AiJobType.PlayerCommentReply:
                    return "player-comment-reply";
                default:
                    return "player-post-comment";
            }
        }

        private static bool IsPlayerWrittenPurpose(string purpose)
        {
            return !string.IsNullOrEmpty(purpose)
                && (purpose.StartsWith("player-free-reply", StringComparison.OrdinalIgnoreCase)
                    || purpose.StartsWith("player-option", StringComparison.OrdinalIgnoreCase));
        }

        private static string Dedupe(string purpose, int contentId, int authorRoleId, int targetRoleId, int parentCommentId)
        {
            return purpose + ":" + contentId + ":" + authorRoleId + ":" + targetRoleId + ":" + parentCommentId;
        }
    }
}
