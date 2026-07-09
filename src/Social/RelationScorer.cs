using System;
using TheEntity;

namespace StudentAge.QQAIMoments.Social
{
    internal static class RelationScorer
    {
        internal static float Score(Role role)
        {
            if (role == null)
            {
                return 0f;
            }
            float relation = Math.Max(0, role.Relation);
            float favor = Math.Max(0f, role.Favor);
            return 1f + relation * 2f + Math.Min(10f, favor / 20f);
        }

        internal static float Chance(Role role, float baseChance)
        {
            float score = Score(role);
            float relationBonus = Math.Min(0.4f, Math.Max(0, role == null ? 0 : role.Relation) * 0.06f);
            float favorBonus = Math.Min(0.3f, Math.Max(0f, role == null ? 0f : role.Favor) / 300f);
            return Clamp(baseChance * 0.45f + relationBonus + favorBonus, 0f, 0.98f);
        }

        internal static float ReplyChance(Role role, float baseChance)
        {
            if (role == null || role.Relation <= 0)
            {
                return 0f;
            }
            float chance = baseChance;
            chance += Math.Min(0.35f, role.Relation * 0.08f);
            chance += Math.Min(0.3f, Math.Max(0f, role.Favor) / 250f);
            return Clamp(chance, 0f, 0.98f);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}

