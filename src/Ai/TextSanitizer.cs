using System;
using System.Text.RegularExpressions;

namespace StudentAge.QQAIMoments.Ai
{
    internal static class TextSanitizer
    {
        private static readonly string[] AssistantLeakPhrases =
        {
            "请提供更多",
            "请提供具体",
            "告诉我你希望",
            "你希望的人设",
            "人设、语气和场景",
            "你想让我回复",
            "我来帮你生成",
            "帮你生成合适",
            "生成合适的回复",
            "作为NPC",
            "群聊里的NPC",
            "作为AI",
            "语言模型",
            "模型无法",
            "插件",
            "提示词"
        };

        private static readonly string[] TemplateLikePhrases =
        {
            "我看得出来",
            "不是随便说说",
            "认真看完",
            "所以才会有点",
            "有点在意",
            "你已经很棒",
            "别想太多",
            "进步不是运气",
            "不是白熬",
            "该见效了",
            "见效了。继续",
            "给你整个",
            "能交差的框架",
            "主题、流程、分工",
            "半小时给你盘顺",
            "你小子又想云参团",
            "算你有功",
            "今天不算白坐"
        };

        internal static string Clean(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            text = text.Trim();
            text = text.Replace("\r", " ").Replace("\n", " ");
            text = Regex.Replace(text, "\\s+", " ");
            text = StripFence(text);
            text = StripQuotes(text);
            text = Regex.Replace(text, "^(AI|NPC|角色|回复|评论|说说|动态)\\s*[:：]\\s*", "", RegexOptions.IgnoreCase).Trim();
            text = text.Replace("我是一个AI", "").Replace("作为AI", "").Replace("作为一个AI", "");

            if (maxChars > 0 && text.Length > maxChars)
            {
                text = text.Substring(0, maxChars).TrimEnd('，', '。', '、', '！', '？', ',', '.', '!', '?') + "…";
            }
            return text.Trim();
        }

        internal static bool LooksLikeAssistantLeak(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string compact = Regex.Replace(text, "\\s+", "");
            for (int i = 0; i < AssistantLeakPhrases.Length; i++)
            {
                if (compact.IndexOf(AssistantLeakPhrases[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool LooksLikeTemplatePhrase(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string compact = Regex.Replace(text, "\\s+", "");
            for (int i = 0; i < TemplateLikePhrases.Length; i++)
            {
                string phrase = Regex.Replace(TemplateLikePhrases[i], "\\s+", "");
                if (!string.IsNullOrEmpty(phrase) && compact.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static string StripFence(string text)
        {
            if (text.StartsWith("```"))
            {
                int first = text.IndexOf('\n');
                int last = text.LastIndexOf("```", StringComparison.Ordinal);
                if (first >= 0 && last > first)
                {
                    return text.Substring(first + 1, last - first - 1).Trim();
                }
            }
            return text;
        }

        private static string StripQuotes(string text)
        {
            char[] quotes = { '"', '\'', '“', '”', '‘', '’', '「', '」', '『', '』' };
            bool changed = true;
            while (changed && text.Length >= 2)
            {
                changed = false;
                for (int i = 0; i < quotes.Length; i++)
                {
                    for (int j = 0; j < quotes.Length; j++)
                    {
                        if (text.Length >= 2 && text[0] == quotes[i] && text[text.Length - 1] == quotes[j])
                        {
                            text = text.Substring(1, text.Length - 2).Trim();
                            changed = true;
                        }
                    }
                }
            }
            return text;
        }
    }
}
