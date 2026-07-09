using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sdk;
using StudentAge.QQAIMoments.Ai;
using StudentAge.QQAIMoments.Config;
using StudentAge.QQAIMoments.Models;
using StudentAge.QQAIMoments.Util;
using TheEntity;
using UnityEngine;
using UnityEngine.Networking;

namespace StudentAge.QQAIMoments.Runtime
{
    internal sealed class UsageTelemetryService
    {
        private const int SchemaVersion = 1;
        private readonly PluginConfig config;
        private readonly MonoBehaviour runner;
        private readonly Action<string> log;
        private string salt;
        private string installId;

        internal UsageTelemetryService(PluginConfig config, MonoBehaviour runner, Action<string> log)
        {
            this.config = config;
            this.runner = runner;
            this.log = log;
        }

        internal void RecordAiResult(AiJob job, AiPrompt prompt, AiResult result, int rewriteAttempts, string finalState, string qualityIssue)
        {
            if (!IsEnabled() || job == null)
            {
                return;
            }

            try
            {
                EnsureIdentity();
                JObject record = BuildRecord(job, prompt, result, rewriteAttempts, finalState, qualityIssue);
                AppendLocal(record);
                TryUpload(record);
            }
            catch (Exception ex)
            {
                DebugLog("使用数据记录失败：" + ex.Message);
            }
        }

        private bool IsEnabled()
        {
            return config != null
                   && config.ShareUsageData != null
                   && config.ShareUsageData.Value;
        }

        private JObject BuildRecord(AiJob job, AiPrompt prompt, AiResult result, int rewriteAttempts, string finalState, string qualityIssue)
        {
            string sourceText = prompt != null ? prompt.SourceText : job.SourceText;
            string parentText = prompt != null ? prompt.ParentCommentText : job.ParentCommentText;
            string aiText = result != null ? result.Text : "";

            JObject record = new JObject();
            record["schema"] = SchemaVersion;
            record["created_at_utc"] = DateTime.UtcNow.ToString("O");
            record["plugin_version"] = StudentAge.QQAIMoments.QqAiMomentsPlugin.PluginVersion;
            record["install_id"] = installId ?? "";
            record["event"] = "ai_generation_result";
            record["raw_text_included"] = ShouldIncludeRawText();
            record["raw_text_notice"] = ShouldIncludeRawText() ? "player/ai text is included after obvious-secret masking and length limiting" : "raw player/ai text is not included";
            record["model"] = config.Model != null ? config.Model.Value : "";

            record["job"] = new JObject
            {
                ["type"] = job.Type.ToString(),
                ["content_id_hash"] = HashValue("content:" + job.ContentId),
                ["parent_comment_id_hash"] = job.ParentCommentId > 0 ? HashValue("comment:" + job.ParentCommentId) : "",
                ["author_role_id"] = job.AuthorRoleId,
                ["post_author_role_id"] = job.PostAuthorRoleId,
                ["target_role_id"] = job.TargetRoleId,
                ["author_relation"] = RoleSnapshot(job.AuthorRoleId),
                ["target_relation"] = RoleSnapshot(job.TargetRoleId),
                ["post_author_is_player"] = job.PostAuthorRoleId == 0,
                ["target_is_player"] = job.TargetRoleId == 0,
                ["intent"] = job.Intent ?? "",
                ["reply_strategy"] = job.ReplyStrategy ?? ""
            };

            record["generation"] = new JObject
            {
                ["success"] = result != null && result.Success,
                ["final_state"] = finalState ?? "",
                ["quality_issue"] = qualityIssue ?? "",
                ["rewrite_attempts"] = rewriteAttempts,
                ["has_should_reply"] = result != null && result.HasShouldReply,
                ["should_reply"] = result == null || result.ShouldReply,
                ["http_status"] = result != null ? result.HttpStatus : 0,
                ["request_attempts"] = result != null ? result.RequestAttempts : 0,
                ["endpoint_kind"] = EndpointKind(result != null ? result.ApiEndpoint : ""),
                ["error_kind"] = ErrorKind(result != null ? result.Error : ""),
                ["looks_template_like"] = !string.IsNullOrEmpty(aiText) && TextSanitizer.LooksLikeTemplatePhrase(aiText),
                ["looks_assistant_leak"] = !string.IsNullOrEmpty(aiText) && TextSanitizer.LooksLikeAssistantLeak(aiText)
            };

            record["text_metrics"] = new JObject
            {
                ["source_len_bucket"] = LengthBucket(sourceText),
                ["parent_len_bucket"] = LengthBucket(parentText),
                ["ai_len_bucket"] = LengthBucket(aiText),
                ["source_hash"] = HashText(sourceText),
                ["parent_hash"] = HashText(parentText),
                ["ai_hash"] = HashText(aiText)
            };

            record["actions"] = ActionSnapshot(result != null ? result.Actions : null);
            if (ShouldIncludeRawText())
            {
                record["texts"] = BuildRawTextBlock(job, sourceText, parentText, aiText);
            }
            return record;
        }

        private JObject BuildRawTextBlock(AiJob job, string sourceText, string parentText, string aiText)
        {
            JObject texts = new JObject();
            if (job.PostAuthorRoleId == 0 || job.Type == AiJobType.PlayerPostComment)
            {
                texts["player_post_text"] = ProtectText(sourceText);
            }
            if (job.Type == AiJobType.PlayerCommentReply || job.TargetRoleId == 0)
            {
                texts["player_comment_text"] = ProtectText(parentText);
            }
            texts["ai_result_text"] = ProtectText(aiText);
            return texts;
        }

        private JObject ActionSnapshot(AiActionSet actions)
        {
            if (actions == null)
            {
                return new JObject
                {
                    ["has_any"] = false
                };
            }
            return new JObject
            {
                ["has_any"] = actions.HasAny,
                ["has_like"] = actions.HasLike,
                ["like"] = actions.HasLike && actions.Like,
                ["has_favor_delta"] = actions.HasFavorDelta,
                ["favor_delta_bucket"] = actions.HasFavorDelta ? SignedBucket(actions.FavorDelta) : "",
                ["has_relation_delta"] = actions.HasRelationDelta,
                ["relation_delta"] = actions.HasRelationDelta ? actions.RelationDelta : 0,
                ["has_relation_set"] = actions.HasRelationSet,
                ["relation_set"] = actions.HasRelationSet ? actions.RelationSet : 0,
                ["main_attr_changes"] = actions.MainAttrDeltas != null ? actions.MainAttrDeltas.Count : 0,
                ["npc_attr_changes"] = actions.NpcAttrDeltas != null ? actions.NpcAttrDeltas.Count : 0
            };
        }

        private JObject RoleSnapshot(int roleId)
        {
            if (roleId <= 0)
            {
                return new JObject
                {
                    ["role_id"] = roleId,
                    ["is_player"] = roleId == 0
                };
            }

            try
            {
                Role role = Singleton<RoleMgr>.Ins.GetRole(roleId);
                if (role == null)
                {
                    return new JObject { ["role_id"] = roleId };
                }

                return new JObject
                {
                    ["role_id"] = roleId,
                    ["relation_level"] = role.Relation,
                    ["favor_bucket"] = NumberBucket(role.Favor)
                };
            }
            catch
            {
                return new JObject { ["role_id"] = roleId };
            }
        }

        private bool ShouldIncludeRawText()
        {
            return IsEnabled()
                   && config.ShareUsageRawText != null
                   && config.ShareUsageRawText.Value;
        }

        private void AppendLocal(JObject record)
        {
            string dir = PathUtil.ConfigRelative("QQAIMoments/telemetry");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string path = Path.Combine(dir, "usage-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".jsonl");
            File.AppendAllText(path, record.ToString(Formatting.None) + Environment.NewLine, Encoding.UTF8);
            PruneLocal(path);
        }

        private void TryUpload(JObject record)
        {
            if (runner == null || config.ShareUsageDataEndpoint == null)
            {
                return;
            }

            string endpoint = (config.ShareUsageDataEndpoint.Value ?? "").Trim();
            if (endpoint.Length == 0)
            {
                return;
            }

            runner.StartCoroutine(Upload(record, endpoint));
        }

        private System.Collections.IEnumerator Upload(JObject record, string endpoint)
        {
            byte[] payload = Encoding.UTF8.GetBytes(record.ToString(Formatting.None));
            using (UnityWebRequest request = new UnityWebRequest(endpoint, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(payload);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = Math.Max(1, config.ShareUsageDataUploadTimeoutSeconds != null ? config.ShareUsageDataUploadTimeoutSeconds.Value : 8);
                request.SetRequestHeader("Content-Type", "application/json");
                yield return request.SendWebRequest();

                bool error = request.result == UnityWebRequest.Result.ConnectionError
                             || request.result == UnityWebRequest.Result.ProtocolError
                             || request.result == UnityWebRequest.Result.DataProcessingError;
                if (error)
                {
                    DebugLog("使用数据上传失败，已保留本地记录：" + request.responseCode + " " + request.error);
                }
            }
        }

        private void PruneLocal(string path)
        {
            int keep = config.ShareUsageDataMaxLocalRecords != null ? Math.Max(1, config.ShareUsageDataMaxLocalRecords.Value) : 500;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length <= keep)
            {
                return;
            }

            string[] tail = new string[keep];
            Array.Copy(lines, lines.Length - keep, tail, 0, keep);
            File.WriteAllLines(path, tail, Encoding.UTF8);
        }

        private void EnsureIdentity()
        {
            if (!string.IsNullOrEmpty(salt) && !string.IsNullOrEmpty(installId))
            {
                return;
            }

            string path = PathUtil.ConfigRelative("QQAIMoments/telemetry/install.json");
            try
            {
                if (File.Exists(path))
                {
                    JObject stored = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                    salt = (string)stored["salt"];
                }
            }
            catch
            {
                salt = null;
            }

            if (string.IsNullOrEmpty(salt))
            {
                salt = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                PathUtil.EnsureParent(path);
                JObject root = new JObject
                {
                    ["schema"] = 1,
                    ["salt"] = salt,
                    ["created_at_utc"] = DateTime.UtcNow.ToString("O")
                };
                File.WriteAllText(path, root.ToString(Formatting.Indented), Encoding.UTF8);
            }

            installId = HashValue("install:" + salt);
        }

        private string ProtectText(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            string text = value.Replace("\r", " ").Replace("\n", " ").Trim();
            text = Regex.Replace(text, @"[A-Za-z]:\\[^\\/:*?""<>|\r\n]+(?:\\[^\\/:*?""<>|\r\n]+)+", "[local_path]");
            text = Regex.Replace(text, @"https?://[^\s]+", "[url]", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", "[email]", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"(?i)\b(bearer\s+[A-Za-z0-9._\-]{8,}|sk-[A-Za-z0-9_\-]{8,}|api[_\- ]?key\s*[:=]\s*[A-Za-z0-9._\-]{8,}|token\s*[:=]\s*[A-Za-z0-9._\-]{8,})", "[secret]");
            text = Regex.Replace(text, @"\b\+?\d[\d\s\-]{7,}\d\b", "[long_number]");

            int max = config.ShareUsageRawTextMaxChars != null ? Math.Max(20, config.ShareUsageRawTextMaxChars.Value) : 500;
            if (text.Length > max)
            {
                text = text.Substring(0, max) + "…[truncated]";
            }
            return text;
        }

        private string HashText(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }
            return HashValue("text:" + NormalizeForHash(value));
        }

        private string HashValue(string value)
        {
            EnsureSaltOnly();
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes((salt ?? "") + "\n" + (value ?? "")));
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < bytes.Length && i < 8; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }
                return builder.ToString();
            }
        }

        private void EnsureSaltOnly()
        {
            if (!string.IsNullOrEmpty(salt))
            {
                return;
            }
            EnsureIdentity();
        }

        private static string NormalizeForHash(string value)
        {
            return (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static string EndpointKind(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint))
            {
                return "";
            }
            string lower = endpoint.ToLowerInvariant();
            if (lower.Contains("/responses"))
            {
                return "responses";
            }
            if (lower.Contains("/chat/completions"))
            {
                return "chat_completions";
            }
            return "unknown";
        }

        private static string ErrorKind(string error)
        {
            if (string.IsNullOrEmpty(error))
            {
                return "";
            }
            string lower = error.ToLowerInvariant();
            if (lower.Contains("timeout") || lower.Contains("timed out"))
            {
                return "timeout";
            }
            if (lower.Contains("401") || lower.Contains("403") || lower.Contains("auth"))
            {
                return "auth";
            }
            if (lower.Contains("429") || lower.Contains("rate"))
            {
                return "rate_limit";
            }
            if (lower.Contains("400") || lower.Contains("404") || lower.Contains("405") || lower.Contains("415") || lower.Contains("422"))
            {
                return "endpoint_or_payload";
            }
            if (lower.Contains("empty"))
            {
                return "empty";
            }
            if (lower.Contains("parse"))
            {
                return "parse";
            }
            return "other";
        }

        private static string LengthBucket(string value)
        {
            int len = string.IsNullOrEmpty(value) ? 0 : value.Length;
            if (len <= 0) return "0";
            if (len <= 4) return "1-4";
            if (len <= 8) return "5-8";
            if (len <= 16) return "9-16";
            if (len <= 32) return "17-32";
            if (len <= 64) return "33-64";
            if (len <= 128) return "65-128";
            return "129+";
        }

        private static string NumberBucket(float value)
        {
            if (value < 0f) return "<0";
            if (value < 20f) return "0-19";
            if (value < 50f) return "20-49";
            if (value < 80f) return "50-79";
            if (value < 120f) return "80-119";
            return "120+";
        }

        private static string SignedBucket(float value)
        {
            if (value <= -3f) return "<=-3";
            if (value < 0f) return "-0~-3";
            if (value == 0f) return "0";
            if (value < 3f) return "+0~+3";
            return ">=+3";
        }

        private void DebugLog(string message)
        {
            try
            {
                if (config != null && config.DebugLog != null && config.DebugLog.Value && log != null)
                {
                    log(message);
                }
            }
            catch
            {
            }
        }
    }
}
