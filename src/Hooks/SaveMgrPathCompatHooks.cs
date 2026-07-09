using HarmonyLib;
using Sdk;

namespace StudentAge.QQAIMoments.Hooks
{
    [HarmonyPatch]
    internal static class SaveMgrPathCompatHooks
    {
        [HarmonyPatch(typeof(SaveMgr), "Load")]
        [HarmonyPrefix]
        private static void SaveMgr_Load_Prefix(ref string _directory, string _filename)
        {
            Rewrite(ref _directory, _filename, "SaveMgr.Load/" + _filename);
        }

        [HarmonyPatch(typeof(SaveMgr), "LoadAsync")]
        [HarmonyPrefix]
        private static void SaveMgr_LoadAsync_Prefix(ref string _directory, string _filename)
        {
            Rewrite(ref _directory, _filename, "SaveMgr.LoadAsync/" + _filename);
        }

        [HarmonyPatch(typeof(SaveMgr), "LoadInfoAsync")]
        [HarmonyPrefix]
        private static void SaveMgr_LoadInfoAsync_Prefix(ref string _directory, string _filename)
        {
            Rewrite(ref _directory, _filename, "SaveMgr.LoadInfoAsync/" + _filename);
        }

        [HarmonyPatch(typeof(SaveMgr), "Save")]
        [HarmonyPrefix]
        private static void SaveMgr_Save_Prefix(ref string _directory, string _filename)
        {
            Rewrite(ref _directory, _filename, "SaveMgr.Save/" + _filename);
        }

        [HarmonyPatch(typeof(SaveMgr), "SaveAsync")]
        [HarmonyPrefix]
        private static void SaveMgr_SaveAsync_Prefix(ref string _directory, string _filename)
        {
            Rewrite(ref _directory, _filename, "SaveMgr.SaveAsync/" + _filename);
        }

        private static void Rewrite(ref string directory, string filename, string reason)
        {
            QqAiMomentsPlugin.RewriteGameSaveDirectorySafe(ref directory, filename, reason);
        }
    }
}
