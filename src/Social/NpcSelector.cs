using System;
using System.Collections.Generic;
using Config;
using Sdk;
using TheEntity;

namespace StudentAge.QQAIMoments.Social
{
    internal sealed class NpcSelector
    {
        private readonly System.Random random;

        internal NpcSelector(System.Random random)
        {
            this.random = random ?? new System.Random();
        }

        internal List<int> GetKnownNpcs()
        {
            List<int> result = new List<int>();
            try
            {
                List<int> ids = Singleton<RoleMgr>.Ins.GetRelationData(true).GetOtherRelation(-3);
                AddFiltered(ids, result, true);
            }
            catch
            {
            }
            return result;
        }

        internal List<int> GetActivePostCandidates()
        {
            List<int> result = new List<int>();
            try
            {
                List<int> ids = Singleton<RoleMgr>.Ins.GetRelationData(true).GetAllSocialNpcs(true);
                AddFiltered(ids, result, true);
                result.RemoveAll(id =>
                {
                    Role role = Singleton<RoleMgr>.Ins.GetRole(id);
                    return role == null || role.Relation <= 0;
                });
            }
            catch
            {
            }
            return result;
        }

        internal List<int> PickWeighted(List<int> candidates, int maxCount, float baseChance)
        {
            List<int> result = new List<int>();
            if (candidates == null || candidates.Count == 0 || maxCount <= 0)
            {
                return result;
            }

            List<int> shuffled = new List<int>(candidates);
            Shuffle(shuffled);
            foreach (int id in shuffled)
            {
                Role role = Singleton<RoleMgr>.Ins.GetRole(id);
                if (role == null)
                {
                    continue;
                }
                if (random.NextDouble() <= RelationScorer.Chance(role, baseChance))
                {
                    result.Add(id);
                    if (result.Count >= maxCount)
                    {
                        break;
                    }
                }
            }
            return result;
        }

        internal int PickOneWeighted(List<int> candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return -1;
            }

            float total = 0f;
            foreach (int id in candidates)
            {
                total += RelationScorer.Score(Singleton<RoleMgr>.Ins.GetRole(id));
            }
            if (total <= 0f)
            {
                return candidates[random.Next(candidates.Count)];
            }
            double roll = random.NextDouble() * total;
            float acc = 0f;
            foreach (int id in candidates)
            {
                acc += RelationScorer.Score(Singleton<RoleMgr>.Ins.GetRole(id));
                if (roll <= acc)
                {
                    return id;
                }
            }
            return candidates[candidates.Count - 1];
        }

        private void AddFiltered(List<int> ids, List<int> result, bool requireKnown)
        {
            if (ids == null)
            {
                return;
            }
            foreach (int id in ids)
            {
                if (IsUsableNpc(id, requireKnown) && !result.Contains(id))
                {
                    result.Add(id);
                }
            }
        }

        internal bool IsUsableNpc(int id, bool requireKnown)
        {
            if (id <= 0)
            {
                return false;
            }
            if (!Cfg.PersonCfgMap.ContainsKey(id))
            {
                return false;
            }
            Role role = Singleton<RoleMgr>.Ins.GetRole(id);
            if (role == null || role.isLeave)
            {
                return false;
            }
            if (requireKnown && role.Relation <= 0)
            {
                return false;
            }
            if ((id == 103 || id == 204) && !IsDlcRoleEnabled(id))
            {
                return false;
            }
            return true;
        }

        private static bool IsDlcRoleEnabled(int id)
        {
            try
            {
                return Singleton<DLCCtrl>.Ins.IsDLCLoaded(DLC_IDX.Chuyang)
                    && Singleton<RoleMgr>.Ins.GetRole().IncCtrl.GetValue(RoleIncType.ToggleRole, id) != 0f;
            }
            catch
            {
                return false;
            }
        }

        private void Shuffle(List<int> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                int tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }
    }
}
