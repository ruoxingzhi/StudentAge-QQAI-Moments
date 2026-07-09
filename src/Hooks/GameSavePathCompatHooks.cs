using HarmonyLib;
using View.Main;

namespace StudentAge.QQAIMoments.Hooks
{
    [HarmonyPatch]
    internal static class GameSavePathCompatHooks
    {
        [HarmonyPatch(typeof(Main), "OnInit")]
        [HarmonyPostfix]
        private static void Main_OnInit_Postfix()
        {
            Refresh("Main.OnInit");
        }

        [HarmonyPatch(typeof(Main), "StartGame")]
        [HarmonyPrefix]
        private static void Main_StartGame_Prefix()
        {
            Refresh("Main.StartGame");
        }

        [HarmonyPatch(typeof(ModCtrl), "Load")]
        [HarmonyPrefix]
        private static void ModCtrl_Load_Prefix()
        {
            Refresh("ModCtrl.Load");
        }

        [HarmonyPatch(typeof(SaveMgrEx), "LoadGlobal")]
        [HarmonyPrefix]
        private static void SaveMgrEx_LoadGlobal_Prefix()
        {
            Refresh("SaveMgrEx.LoadGlobal");
        }

        [HarmonyPatch(typeof(Game), "CanContinue")]
        [HarmonyPrefix]
        private static void Game_CanContinue_Prefix()
        {
            Refresh("Game.CanContinue");
        }

        [HarmonyPatch(typeof(EntryView), "Refresh")]
        [HarmonyPrefix]
        private static void EntryView_Refresh_Prefix()
        {
            Refresh("EntryView.Refresh");
        }

        [HarmonyPatch(typeof(EntryView), "Refresh")]
        [HarmonyPostfix]
        private static void EntryView_Refresh_Postfix()
        {
            QqAiMomentsPlugin.InstanceSafeCall(plugin => plugin.MarkConsentUiLikelyReady());
        }

        private static void Refresh(string reason)
        {
            QqAiMomentsPlugin.InstanceSafeCall(plugin => plugin.RefreshGameSavePath(reason));
        }
    }
}
