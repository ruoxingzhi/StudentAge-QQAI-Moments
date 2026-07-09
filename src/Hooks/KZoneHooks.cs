using System;
using System.Collections.Generic;
using Config;
using GenUI.Common;
using HarmonyLib;
using Sdk;
using StudentAge.QQAIMoments.Runtime;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using View.Common;
using View.TheAction;

namespace StudentAge.QQAIMoments.Hooks
{
    [HarmonyPatch]
    internal static class KZoneHooks
    {
        private static readonly HashSet<string> LoggedHookFailures = new HashSet<string>();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(KZoneData), "Init")]
        private static void KZoneData_Init_Postfix(KZoneData __instance)
        {
            QqAiMomentsPlugin.InstanceSafeCall(p => p.OnKZoneInit(__instance));
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(KZoneData), "Init")]
        private static void KZoneData_Init_Prefix(KZoneData __instance)
        {
            QqAiMomentsPlugin.InstanceSafeCall(p => p.OnBeforeKZoneDataAccess(__instance));
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(KZoneData), "Refresh")]
        private static void KZoneData_Refresh_Prefix(KZoneData __instance)
        {
            QqAiMomentsPlugin.InstanceSafeCall(p => p.OnBeforeKZoneDataAccess(__instance));
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(KZoneData), "CheckWrongId")]
        private static void KZoneData_CheckWrongId_Prefix(KZoneData __instance)
        {
            QqAiMomentsPlugin.InstanceSafeCall(p => p.OnBeforeKZoneDataAccess(__instance));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(KZoneData), "PostCustom")]
        private static void KZoneData_PostCustom_Postfix(KZoneContentData __result)
        {
            QqAiMomentsPlugin.InstanceSafeCall(p => p.OnPostCustom(__result));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(KZoneData), "Post")]
        private static void KZoneData_Post_Postfix(KZoneData __instance, int _id)
        {
            QqAiMomentsPlugin.InstanceSafeCall(p => p.OnPost(__instance, _id));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(KZoneData), "Comment")]
        private static void KZoneData_Comment_Postfix(KZoneData __instance, int _commentId, int _targetId)
        {
            QqAiMomentsPlugin.InstanceSafeCall(p => p.OnComment(__instance, _commentId, _targetId));
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(KZoneData), "Comment")]
        private static bool KZoneData_Comment_Prefix(KZoneData __instance, int _commentId, int _targetId)
        {
            if (!KZoneFreeReplyBridge.IsFreeReplyOption(_commentId))
            {
                return true;
            }

            QqAiMomentsPlugin.InstanceSafeCall(p => p.OpenFreeReplyInput(__instance, _targetId));
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(KZoneCommon), "OnRenderItem", new Type[] { typeof(Cell_KZoneItemUI), typeof(bool) })]
        private static void KZoneCommon_OnRenderItem_Postfix(Cell_KZoneItemUI _cell)
        {
            try
            {
                if (_cell == null || _cell.data == null)
                {
                    return;
                }

                KZoneContentData data = _cell.data as KZoneContentData;
                if (data == null)
                {
                    return;
                }

                KZoneData kzone = Singleton<RoleMgr>.Ins.GetKZoneData();
                if (KZoneFreeReplyBridge.CanFreeReplyToContent(kzone, data.id))
                {
                    _cell.btn_comment.interactable = true;
                    _cell.img_option.gameObject.SetActive(true);
                }
            }
            catch (Exception ex)
            {
                LogHookFailure("OnRenderItem", ex, _cell != null && _cell.data != null ? _cell.data.ToString() : "");
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(KZoneCommon), "ShowItemOption")]
        private static bool KZoneCommon_ShowItemOption_Prefix(Cell_KZoneItemUI _cell, int contentId, List<int> options)
        {
            try
            {
                KZoneData kzone = Singleton<RoleMgr>.Ins.GetKZoneData();
                if (!KZoneFreeReplyBridge.ShouldOpenFreeReplyForContent(kzone, contentId, options))
                {
                    return true;
                }

                bool opened = false;
                QqAiMomentsPlugin.InstanceSafeCall(p => opened = p.OpenFreeReplyInputForContent(kzone, contentId));
                return !opened;
            }
            catch (Exception ex)
            {
                LogHookFailure("ShowItemOption", ex, "contentId=" + contentId);
                return true;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(KZoneCommon), "OnRenderComment")]
        private static void KZoneCommon_OnRenderComment_Postfix(UICell _cell)
        {
            try
            {
                Cell_KZoneCommentUI cell = _cell as Cell_KZoneCommentUI;
                if (cell == null || cell.data == null)
                {
                    return;
                }

                int commentId = (int)cell.data;
                KZoneData kzone = Singleton<RoleMgr>.Ins.GetKZoneData();
                if (KZoneFreeReplyBridge.CanFreeReplyToComment(kzone, commentId))
                {
                    cell.btn_comment.interactable = true;
                }
            }
            catch (Exception ex)
            {
                LogHookFailure("OnRenderComment", ex, _cell != null && _cell.data != null ? _cell.data.ToString() : "");
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(KZonePageHomeView), "OnComment")]
        private static bool KZonePageHomeView_OnComment_Prefix(int _commentId, int _contentId)
        {
            try
            {
                KZoneData kzone = Singleton<RoleMgr>.Ins.GetKZoneData();
                KZoneCommentCfg cfg;
                List<int> options = null;
                if (Cfg.KZoneCommentCfgMap != null && Cfg.KZoneCommentCfgMap.TryGetValue(_commentId, out cfg))
                {
                    options = cfg.options;
                }

                if (!KZoneFreeReplyBridge.ShouldOpenFreeReplyForComment(kzone, _contentId, _commentId, options))
                {
                    return true;
                }

                bool opened = false;
                QqAiMomentsPlugin.InstanceSafeCall(p => opened = p.OpenFreeReplyInputForComment(kzone, _contentId, _commentId));
                return !opened;
            }
            catch (Exception ex)
            {
                LogHookFailure("Home.OnComment", ex, "contentId=" + _contentId + ", commentId=" + _commentId);
                return true;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(KZonePageBlogView), "OnComment")]
        private static bool KZonePageBlogView_OnComment_Prefix(int _commentId, int _contentId)
        {
            try
            {
                KZoneData kzone = Singleton<RoleMgr>.Ins.GetKZoneData();
                KZoneCommentCfg cfg;
                List<int> options = null;
                if (Cfg.KZoneCommentCfgMap != null && Cfg.KZoneCommentCfgMap.TryGetValue(_commentId, out cfg))
                {
                    options = cfg.options;
                }

                if (!KZoneFreeReplyBridge.ShouldOpenFreeReplyForComment(kzone, _contentId, _commentId, options))
                {
                    return true;
                }

                bool opened = false;
                QqAiMomentsPlugin.InstanceSafeCall(p => opened = p.OpenFreeReplyInputForComment(kzone, _contentId, _commentId));
                return !opened;
            }
            catch (Exception ex)
            {
                LogHookFailure("Blog.OnComment", ex, "contentId=" + _contentId + ", commentId=" + _commentId);
                return true;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(KZoneData), "NewRound")]
        private static void KZoneData_NewRound_Postfix(KZoneData __instance)
        {
            QqAiMomentsPlugin.InstanceSafeCall(p => p.OnNewRound(__instance));
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Game), "LoadGame")]
        private static void Game_LoadGame_Prefix(string _saveName)
        {
            QqAiMomentsPlugin.InstanceSafeCall(p => p.OnGameLoadStarting(_saveName));
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Game), "SaveGameAsync")]
        private static void Game_SaveGameAsync_Prefix(string _saveName)
        {
            QqAiMomentsPlugin.InstanceSafeCall(p => p.OnGameSaveStarting(_saveName));
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(RoleMgr), "Save")]
        private static void RoleMgr_Save_Prefix()
        {
            QqAiMomentsPlugin.InstanceSafeCall(p => p.OnBeforeRoleSave());
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(RoleMgr), "Save")]
        private static Exception RoleMgr_Save_Finalizer(Exception __exception)
        {
            QqAiMomentsPlugin.InstanceSafeCall(p => p.OnAfterRoleSave());
            return __exception;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Control), "Update")]
        private static void Control_Update_Postfix()
        {
            QqAiMomentsPlugin.InstanceSafeCall(p => p.RuntimeTickFromGameLoop());

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || IsTextInputActive())
            {
                return;
            }

            bool altPressed = keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed;
            if (altPressed && keyboard.cKey.wasPressedThisFrame)
            {
                QqAiMomentsPlugin.InstanceSafeCall(p => p.OnAltCTestHotkey());
            }
        }

        private static bool IsTextInputActive()
        {
            try
            {
                if (DebugMgr.IsShowing())
                {
                    return true;
                }

                EventSystem eventSystem = EventSystem.current;
                if (eventSystem == null || eventSystem.currentSelectedGameObject == null)
                {
                    return false;
                }

                GameObject selected = eventSystem.currentSelectedGameObject;
                return selected.GetComponent("TMP_InputField") != null
                    || selected.GetComponent("InputField") != null;
            }
            catch (Exception ex)
            {
                LogHookFailure("IsTextInputActive", ex, "");
                return false;
            }
        }

        private static void LogHookFailure(string hook, Exception ex, string context)
        {
            if (ex == null)
            {
                return;
            }
            string key = hook + "|" + ex.GetType().FullName + "|" + context;
            if (!LoggedHookFailures.Add(key))
            {
                return;
            }
            QqAiMomentsPlugin.InstanceSafeCall(p => p.HookDebugLog(hook, "异常：" + context + " " + ex));
        }
    }
}
