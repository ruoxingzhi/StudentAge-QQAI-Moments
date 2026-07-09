using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace StudentAge.QQAIMoments.Models
{
    internal enum AiJobType
    {
        PlayerPostComment,
        NpcActivePost,
        PlayerCommentReply,
        NpcNpcComment,
        NpcNpcReply
    }

    internal sealed class AiJob
    {
        internal AiJobType Type;
        internal int ContentId;
        internal int AuthorRoleId;
        internal int PostAuthorRoleId;
        internal int TargetRoleId;
        internal int ParentCommentId;
        internal string SourceText;
        internal string ParentCommentText;
        internal string ExistingText;
        internal string ThreadSummary;
        internal string RecentSimilarTexts;
        internal string RecentSelfTurns;
        internal string Intent;
        internal string ReplyStrategy;
        internal string ExtraInstruction;
        internal string DedupeKey;
        internal string PurposeOverride;
        internal string RuntimeIdentity;
    }

    internal sealed class AiPrompt
    {
        internal AiJobType Type;
        internal int ContentId;
        internal int AuthorRoleId;
        internal int PostAuthorRoleId;
        internal int TargetRoleId;
        internal int ParentCommentId;
        internal string System;
        internal string User;
        internal string SourceText;
        internal string ParentCommentText;
        internal string ExistingText;
        internal string ThreadSummary;
        internal string RecentSimilarTexts;
        internal string RecentSelfTurns;
        internal string Intent;
        internal string ReplyStrategy;
        internal string ExtraInstruction;
        internal int MaxTokens;
        internal float Temperature;
    }

    internal sealed class AiResult
    {
        internal bool Success;
        internal string Text;
        internal string Error;
        internal string RawText;
        internal string RawResponse;
        internal long HttpStatus;
        internal string ApiEndpoint;
        internal int RequestAttempts;
        internal AiActionSet Actions;
        internal bool HasShouldReply;
        internal bool ShouldReply = true;

        internal static AiResult Ok(string text)
        {
            return new AiResult { Success = true, Text = text };
        }

        internal static AiResult Ok(string text, AiActionSet actions)
        {
            return new AiResult { Success = true, Text = text, Actions = actions };
        }

        internal static AiResult Ok(string text, AiActionSet actions, string rawText, string rawResponse)
        {
            return new AiResult { Success = true, Text = text, Actions = actions, RawText = rawText, RawResponse = rawResponse };
        }

        internal static AiResult Fail(string error)
        {
            return new AiResult { Success = false, Error = error };
        }

        internal static AiResult Fail(string error, string rawResponse)
        {
            return new AiResult { Success = false, Error = error, RawResponse = rawResponse };
        }
    }

    internal sealed class AiActionSet
    {
        internal bool HasLike;
        internal bool Like;
        internal bool HasFavorDelta;
        internal float FavorDelta;
        internal bool HasRelationDelta;
        internal int RelationDelta;
        internal bool HasRelationSet;
        internal int RelationSet;
        internal List<AiAttrDelta> MainAttrDeltas = new List<AiAttrDelta>();
        internal List<AiAttrDelta> NpcAttrDeltas = new List<AiAttrDelta>();

        internal bool HasAny
        {
            get
            {
                return HasLike
                    || HasFavorDelta
                    || HasRelationDelta
                    || HasRelationSet
                    || (MainAttrDeltas != null && MainAttrDeltas.Count > 0)
                    || (NpcAttrDeltas != null && NpcAttrDeltas.Count > 0);
            }
        }
    }

    internal sealed class AiAttrDelta
    {
        internal int RoleId;
        internal int AttrId;
        internal float Delta;
    }

    internal static class AiStructuredOutputParser
    {
        internal static AiResult Parse(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return AiResult.Ok(string.Empty);
            }

            string candidate = StripCodeFence(raw.Trim());
            JObject root;
            if (!TryParseObject(candidate, out root))
            {
                return AiResult.Ok(raw);
            }

            string text = FirstString(root, "text", "content", "reply", "post", "message");
            JToken actionsToken = root["actions"] ?? root["action"] ?? root;
            AiActionSet actions = ParseActions(actionsToken);
            AiResult result = AiResult.Ok(text ?? string.Empty, actions != null && actions.HasAny ? actions : null);
            bool shouldReply;
            if (TryBool(FirstToken(root, "should_reply", "shouldReply", "reply_visible", "should_post", "shouldPost"), out shouldReply))
            {
                result.HasShouldReply = true;
                result.ShouldReply = shouldReply;
            }
            return result;
        }

        private static AiActionSet ParseActions(JToken token)
        {
            if (token == null || token.Type != JTokenType.Object)
            {
                return null;
            }

            AiActionSet actions = new AiActionSet();
            JToken like = FirstToken(token, "like", "liked", "thumb", "thumb_up", "should_like");
            bool likeValue;
            if (TryBool(like, out likeValue))
            {
                actions.HasLike = true;
                actions.Like = likeValue;
            }

            float favorDelta;
            if (TryFloat(FirstToken(token, "favor_delta", "favor", "favorChange", "favor_change"), out favorDelta))
            {
                actions.HasFavorDelta = true;
                actions.FavorDelta = favorDelta;
            }

            int relationDelta;
            if (TryInt(FirstToken(token, "relation_delta", "relationDelta", "relationship_delta", "relation_change"), out relationDelta))
            {
                actions.HasRelationDelta = true;
                actions.RelationDelta = relationDelta;
            }

            int relationSet;
            if (TryInt(FirstToken(token, "relation_set", "relation", "relationship", "relation_level"), out relationSet))
            {
                actions.HasRelationSet = true;
                actions.RelationSet = relationSet;
            }

            AppendAttrDeltas(actions.MainAttrDeltas, FirstToken(token, "main_attr_changes", "main_attrs", "player_attr_changes", "player_attrs"), 0);
            AppendAttrDeltas(actions.NpcAttrDeltas, FirstToken(token, "npc_attr_changes", "npc_attrs", "author_attr_changes", "author_attrs"), -1);
            return actions;
        }

        private static void AppendAttrDeltas(List<AiAttrDelta> target, JToken token, int defaultRoleId)
        {
            if (target == null || token == null)
            {
                return;
            }

            if (token.Type == JTokenType.Object)
            {
                foreach (JProperty prop in ((JObject)token).Properties())
                {
                    int attrId;
                    float delta;
                    if (int.TryParse(prop.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out attrId)
                        && TryFloat(prop.Value, out delta))
                    {
                        target.Add(new AiAttrDelta { RoleId = defaultRoleId, AttrId = attrId, Delta = delta });
                    }
                }
                return;
            }

            if (token.Type != JTokenType.Array)
            {
                return;
            }

            foreach (JToken item in token)
            {
                if (item == null || item.Type != JTokenType.Object)
                {
                    continue;
                }
                int attrId;
                float delta;
                if (!TryInt(FirstToken(item, "attr_id", "attr", "id"), out attrId)
                    || !TryFloat(FirstToken(item, "delta", "value", "change"), out delta))
                {
                    continue;
                }
                int roleId = defaultRoleId;
                int parsedRoleId;
                if (TryInt(FirstToken(item, "role_id", "role", "npc_id"), out parsedRoleId))
                {
                    roleId = parsedRoleId;
                }
                target.Add(new AiAttrDelta { RoleId = roleId, AttrId = attrId, Delta = delta });
            }
        }

        private static bool TryParseObject(string text, out JObject root)
        {
            root = null;
            try
            {
                root = JObject.Parse(text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string StripCodeFence(string text)
        {
            if (!text.StartsWith("```"))
            {
                return text;
            }
            int firstLine = text.IndexOf('\n');
            int lastFence = text.LastIndexOf("```");
            if (firstLine >= 0 && lastFence > firstLine)
            {
                return text.Substring(firstLine + 1, lastFence - firstLine - 1).Trim();
            }
            return text;
        }

        private static string FirstString(JObject root, params string[] names)
        {
            JToken token = FirstToken(root, names);
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        private static JToken FirstToken(JToken root, params string[] names)
        {
            if (root == null || names == null)
            {
                return null;
            }
            foreach (string name in names)
            {
                JToken token = root[name];
                if (token != null && token.Type != JTokenType.Null)
                {
                    return token;
                }
            }
            return null;
        }

        private static bool TryBool(JToken token, out bool value)
        {
            value = false;
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }
            if (token.Type == JTokenType.Boolean)
            {
                value = token.Value<bool>();
                return true;
            }
            string text = token.ToString().Trim().ToLowerInvariant();
            if (text == "1" || text == "true" || text == "yes" || text == "like")
            {
                value = true;
                return true;
            }
            if (text == "0" || text == "false" || text == "no" || text == "none")
            {
                value = false;
                return true;
            }
            return false;
        }

        private static bool TryInt(JToken token, out int value)
        {
            value = 0;
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }
            if (token.Type == JTokenType.Integer)
            {
                value = token.Value<int>();
                return true;
            }
            return int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryFloat(JToken token, out float value)
        {
            value = 0f;
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }
            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                value = token.Value<float>();
                return true;
            }
            return float.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }

    internal sealed class AiStoreData
    {
        public int Version = 1;
        public int NextContentId = 17000000;
        public int NextCommentId = 1600000000;
        public List<AiMomentRecord> Moments = new List<AiMomentRecord>();
        public List<AiCommentRecord> Comments = new List<AiCommentRecord>();
        public List<AiThumbRecord> Thumbs = new List<AiThumbRecord>();
        public List<string> Dedupe = new List<string>();
    }

    internal sealed class AiMomentRecord
    {
        public int ContentId;
        public int AuthorRoleId;
        public string Content;
        public long PostTimeTicks;
        public int SeasonYear;
        public int Season;
        public int VisitCnt;
        public bool IsActivePost;
        public int RoundNumber;
    }

    internal sealed class AiCommentRecord
    {
        public int CommentId;
        public int ContentId;
        public int AuthorRoleId;
        public int TargetRoleId;
        public int ParentCommentId;
        public string Content;
        public long PostTimeTicks;
        public bool IsOptionOnly;
        public string Purpose;
        public int RoundNumber;
    }

    internal sealed class AiThumbRecord
    {
        public int ContentId;
        public int RoleId;
        public int RoundNumber;
    }
}
