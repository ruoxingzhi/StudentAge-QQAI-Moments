using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using View.Hint;

namespace StudentAge.QQAIMoments.Hooks
{
    [HarmonyPatch(typeof(CommonComfirmView), "OnOpen")]
    internal static class UsageConsentDialogHooks
    {
        private const string UsageTitle = "QQ\u7a7a\u95f4AI\u6570\u636e\u5171\u4eab";
        private const string RawTextTitle = "\u662f\u5426\u5171\u4eab\u73a9\u5bb6\u539f\u6587";

        private static void Postfix(CommonComfirmView __instance)
        {
            try
            {
                if (__instance == null || __instance.txt_title == null)
                {
                    return;
                }

                string title = __instance.txt_title.text ?? "";
                if (title != UsageTitle && title != RawTextTitle)
                {
                    return;
                }

                FitDialog(__instance);
                FitDesc(__instance.txtex_desc);
                FitButtonText(__instance.txt_title, 30);
                FitButtonText(__instance.txt_ok, 24);
                FitButtonText(__instance.txt_cancel, 24);
                FitButtonText(__instance.txt_bot, 20);
            }
            catch
            {
            }
        }

        private static void FitDialog(CommonComfirmView view)
        {
            RectTransform bg = view.img_bg != null ? view.img_bg.rectTransform : null;
            if (bg != null)
            {
                bg.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, Mathf.Max(bg.rect.width, 900f));
                bg.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, Mathf.Max(bg.rect.height, 560f));
            }

            RectTransform buttons = view.group_btn;
            if (buttons != null)
            {
                Vector2 pos = buttons.anchoredPosition;
                if (pos.y > -205f)
                {
                    buttons.anchoredPosition = new Vector2(pos.x, -205f);
                }
            }
        }

        private static void FitDesc(TextMeshProUGUI text)
        {
            if (text == null)
            {
                return;
            }

            text.richText = true;
            text.enableAutoSizing = false;
            text.fontSize = 23f;
            text.lineSpacing = 2f;
            text.paragraphSpacing = 7f;
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Overflow;
            text.alignment = TextAlignmentOptions.TopLeft;

            RectTransform rect = text.rectTransform;
            if (rect != null)
            {
                rect.anchorMin = new Vector2(0f, 0f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.offsetMin = new Vector2(70f, 120f);
                rect.offsetMax = new Vector2(-70f, -110f);
            }

            text.ForceMeshUpdate(false, false);
        }

        private static void FitButtonText(Text text, int size)
        {
            if (text == null)
            {
                return;
            }

            text.supportRichText = true;
            text.resizeTextForBestFit = false;
            text.fontSize = Math.Max(text.fontSize, size);
        }
    }
}
