using System;
using System.Collections;
using System.Collections.Generic;
using StudentAge.QQAIMoments.Models;

namespace StudentAge.QQAIMoments.Ai
{
    internal sealed class TemplateFallbackClient : IAiClient
    {
        private readonly System.Random random;

        internal TemplateFallbackClient(System.Random random)
        {
            this.random = random ?? new System.Random();
        }

        public IEnumerator Generate(AiPrompt prompt, Action<AiResult> callback)
        {
            List<string> candidates = BuildCandidates(prompt);
            string text = PickDifferent(candidates, BuildBlacklist(prompt));
            if (string.IsNullOrEmpty(text))
            {
                callback(AiResult.Fail("local fallback skipped to avoid repeating existing QQ Zone text."));
            }
            else
            {
                callback(AiResult.Ok(text));
            }
            yield break;
        }

        private List<string> BuildCandidates(AiPrompt prompt)
        {
            List<string> list = new List<string>();
            if (prompt == null)
            {
                list.Add("我看到了，先记一下。");
                return list;
            }

            string topic = Snippet(!string.IsNullOrEmpty(prompt.ParentCommentText) ? prompt.ParentCommentText : prompt.SourceText, 16);
            string source = Snippet(prompt.SourceText, 18);
            string prefix = RolePrefix(prompt.AuthorRoleId);

            switch (prompt.Type)
            {
                case AiJobType.NpcActivePost:
                    list.Add(prefix + "今天突然想记一件小事。");
                    list.Add(prefix + "有些事不说出来，好像就会被这天带过去。");
                    list.Add(prefix + "今天的状态还行，先留个痕迹。");
                    list.Add(prefix + "路过这里，顺手发一条。");
                    break;
                case AiJobType.PlayerCommentReply:
                    if (string.Equals(prompt.Intent, "follow_up_question", StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(prefix + "你是在追问刚才那句吧，我的意思没有变。");
                        list.Add(prefix + "不是随口说的，重点还在「" + source + "」。");
                        list.Add(prefix + "如果你问真假，我只能说我刚才是认真回的。");
                    }
                    else
                    {
                        list.Add(prefix + "你说的「" + topic + "」我看到了。");
                        list.Add(prefix + "这句我会记住，不算随便一回。");
                        list.Add(prefix + "按你这个说法，我大概明白你的意思了。");
                        list.Add(prefix + "别只说一半，下次继续讲。");
                    }
                    break;
                case AiJobType.NpcNpcComment:
                    list.Add(prefix + "这条说得有点像你。");
                    list.Add(prefix + "看到「" + topic + "」这里，我停了一下。");
                    list.Add(prefix + "我倒是挺在意这句的。");
                    list.Add(prefix + "这事可以，先给你记一笔。");
                    break;
                case AiJobType.NpcNpcReply:
                    list.Add(prefix + "你刚才这句「" + topic + "」我不同意一半。");
                    list.Add(prefix + "这话先别说满，我再看看。");
                    list.Add(prefix + "懂你的意思，不过我想法不完全一样。");
                    list.Add(prefix + "行，这句我接住了。");
                    break;
                default:
                    list.Add(prefix + "这条我认真看了，不是随便点进来的。");
                    list.Add(prefix + "你说的「" + topic + "」还挺有意思。");
                    list.Add(prefix + "这不像随手发的，我先记一下。");
                    list.Add(prefix + "看得出来你挺在意这件事。");
                    break;
            }

            AddRoleSpecific(list, prompt.AuthorRoleId, topic);
            return list;
        }

        private void AddRoleSpecific(List<string> list, int roleId, string topic)
        {
            switch (roleId)
            {
                case 3:
                    list.Add("你小子，发这个是想让我接梗吧。");
                    list.Add("火钳刘明，这条我先占个位置。");
                    break;
                case 101:
                    list.Add("看到这条，感觉你今天心情有一点不一样。");
                    list.Add("我认真看完了，想法比表面更细一点。");
                    break;
                case 102:
                    list.Add("嗯哼，这个结论暂时可以接受。");
                    list.Add("先把条件列清楚，这条不是随便说说。");
                    break;
                case 103:
                    list.Add("这句话里有一点没说完的部分。");
                    list.Add("我看到了，暂时先不多评价。");
                    break;
                case 104:
                    list.Add("可以，至少这次你是真的在往前走。");
                    list.Add("别停在嘴上，下一步也要做出来。");
                    break;
                case 105:
                    list.Add("这句我有点在意，先悄悄记一下。");
                    list.Add("感觉你不是随便发的，我看出来了。");
                    break;
                case 201:
                    list.Add("这条像一颗小石子，咚地掉进水里了。");
                    list.Add("我脑子里已经开始自动配画面了。");
                    break;
                case 202:
                    list.Add("信息量不大，但重点还算清楚。");
                    list.Add("我更想知道你为什么会突然想到这个。");
                    break;
                case 203:
                    list.Add("这事别急着下结论，慢慢来。");
                    list.Add("如果这是你的真实想法，那就认真对待。");
                    break;
                case 204:
                    list.Add("这条有点意思，我先收下。");
                    list.Add("你把话放在这里，应该不是无缘无故。");
                    break;
            }
        }

        private string PickDifferent(List<string> values, string existingText)
        {
            if (values == null || values.Count == 0)
            {
                return null;
            }

            int start = random.Next(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                string value = values[(start + i) % values.Count];
                if (!IsBlacklisted(existingText, value))
                {
                    return value;
                }
            }
            return null;
        }

        private static string BuildBlacklist(AiPrompt prompt)
        {
            if (prompt == null)
            {
                return "";
            }
            return (prompt.ExistingText ?? "") + "\n" + (prompt.RecentSimilarTexts ?? "") + "\n" + (prompt.ThreadSummary ?? "");
        }

        private static bool IsBlacklisted(string blacklist, string candidate)
        {
            if (string.IsNullOrEmpty(candidate))
            {
                return true;
            }
            if (string.IsNullOrEmpty(blacklist))
            {
                return false;
            }
            string normalizedCandidate = NormalizeForCompare(candidate);
            if (normalizedCandidate.Length == 0)
            {
                return true;
            }

            string[] parts = blacklist.Replace('\n', '；').Replace('|', '；').Split(new char[] { '；', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string normalized = NormalizeForCompare(parts[i]);
                if (normalized.Length == 0)
                {
                    continue;
                }
                if (normalized == normalizedCandidate || normalized.Contains(normalizedCandidate) || normalizedCandidate.Contains(normalized))
                {
                    return true;
                }
                int min = Math.Min(normalized.Length, normalizedCandidate.Length);
                if (min >= 10 && LongestCommonSubstringLength(normalized, normalizedCandidate) * 100 / min >= 86)
                {
                    return true;
                }
            }
            return false;
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

        private static string RolePrefix(int roleId)
        {
            switch (roleId)
            {
                case 3:
                    return "";
                case 102:
                    return "嗯哼，";
                case 104:
                    return "";
                case 202:
                    return "";
                default:
                    return "";
            }
        }

        private static string Snippet(string value, int max)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "这件事";
            }
            value = value.Replace("\r", " ").Replace("\n", " ").Trim();
            if (value.Length <= max)
            {
                return value;
            }
            return value.Substring(0, max);
        }
    }
}
