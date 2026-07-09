using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StudentAge.QQAIMoments.Config;
using StudentAge.QQAIMoments.Models;
using StudentAge.QQAIMoments.Util;

namespace StudentAge.QQAIMoments.Ai
{
    internal sealed class PromptDebugRecorder
    {
        private readonly PluginConfig config;
        private readonly Action<string> log;

        internal PromptDebugRecorder(PluginConfig config, Action<string> log)
        {
            this.config = config;
            this.log = log;
        }

        internal void Save(AiPrompt prompt, AiResult result, int rewriteAttempt, string phase)
        {
            if (config == null || config.DebugPromptLog == null || !config.DebugPromptLog.Value || prompt == null)
            {
                return;
            }

            try
            {
                string dir = PathUtil.ConfigRelative("QQAIMoments/debug/prompts");
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                JObject root = new JObject();
                root["created_at"] = DateTime.Now.ToString("O");
                root["phase"] = phase ?? "unknown";
                root["rewrite_attempt"] = rewriteAttempt;
                root["job"] = new JObject
                {
                    ["type"] = prompt.Type.ToString(),
                    ["content_id"] = prompt.ContentId,
                    ["author_role_id"] = prompt.AuthorRoleId,
                    ["post_author_role_id"] = prompt.PostAuthorRoleId,
                    ["target_role_id"] = prompt.TargetRoleId,
                    ["parent_comment_id"] = prompt.ParentCommentId,
                    ["temperature"] = prompt.Temperature,
                    ["max_tokens"] = prompt.MaxTokens
                };
                root["source_text"] = prompt.SourceText ?? "";
                root["parent_comment_text"] = prompt.ParentCommentText ?? "";
                root["existing_text"] = prompt.ExistingText ?? "";
                root["thread_summary"] = prompt.ThreadSummary ?? "";
                root["recent_similar_texts"] = prompt.RecentSimilarTexts ?? "";
                root["recent_self_turns"] = prompt.RecentSelfTurns ?? "";
                root["intent"] = prompt.Intent ?? "";
                root["reply_strategy"] = prompt.ReplyStrategy ?? "";
                root["extra_instruction"] = prompt.ExtraInstruction ?? "";
                root["system"] = prompt.System ?? "";
                root["user"] = prompt.User ?? "";

                if (result != null)
                {
                    root["result"] = new JObject
                    {
                        ["success"] = result.Success,
                        ["has_should_reply"] = result.HasShouldReply,
                        ["should_reply"] = result.ShouldReply,
                        ["text"] = result.Text ?? "",
                        ["error"] = result.Error ?? "",
                        ["raw_text"] = result.RawText ?? "",
                        ["raw_response"] = Short(result.RawResponse, 6000)
                    };
                }

                string file = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + prompt.Type + "_" + prompt.AuthorRoleId + ".json");
                File.WriteAllText(file, root.ToString(Formatting.Indented));
                Prune(dir, Math.Max(1, config.DebugPromptLogCount.Value));
            }
            catch (Exception ex)
            {
                if (log != null)
                {
                    log("Prompt debug save failed: " + ex.Message);
                }
            }
        }

        private static string Short(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
            {
                return value ?? "";
            }
            return value.Substring(0, max);
        }

        private static void Prune(string dir, int keep)
        {
            FileInfo[] files = new DirectoryInfo(dir).GetFiles("*.json");
            Array.Sort(files, delegate(FileInfo a, FileInfo b) { return b.CreationTimeUtc.CompareTo(a.CreationTimeUtc); });
            for (int i = keep; i < files.Length; i++)
            {
                try
                {
                    files[i].Delete();
                }
                catch
                {
                }
            }
        }
    }
}
