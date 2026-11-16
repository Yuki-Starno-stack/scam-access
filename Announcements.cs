using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamAccess
{
    // Speaks text the game only shows on screen: notifications, announcements, training steps and HUD status.
    internal static class Announcements
    {
        private static readonly Dictionary<string, float> recent = new Dictionary<string, float>();

        // Type name, method name, index of the message argument (-1 = special handling).
        private static readonly (string type, string method, int arg)[] Hooks =
        {
            ("Kolkata.UI.KolkataOsNotificationCenter", "ShowSvg", 0),
            ("Kolkata.UI.KolkataOsNotificationCenter", "ShowImage", 0),
            ("LocalAnnouncementText", "DisplayForSeconds", 0),
            ("LocalAnnouncementText", "DisplayPersistent", 1),
            ("LocalAnnouncementUi", "Show", 0),
            ("LocalAnnouncementUi", "ShowPersistent", 0),
            ("LocalAnnouncementUi", "ShowAboveSettings", 0),
            ("FrequentAnnouncementText", "Display", 0),
            ("MainMenuAnnouncement", "DisplayForSeconds", 0),
            ("PointsPopupUi", "Show", 0),
        };

        public static void Patch(Harmony harmony)
        {
            var postfix = new HarmonyMethod(typeof(Announcements), nameof(SpeakArgument));
            foreach (var h in Hooks)
            {
                Type t = AccessTools.TypeByName(h.type);
                if (t == null) { Plugin.Log.LogWarning("Announce hook: no type " + h.type); continue; }
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t))
                {
                    if (m.Name != h.method) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length <= h.arg || ps[h.arg].ParameterType != typeof(string)) continue;
                    try
                    {
                        harmony.Patch(m, postfix: postfix);
                        argIndex[m] = h.arg;
                    }
                    catch (Exception e) { Plugin.Log.LogWarning("Announce hook failed " + h.type + "." + h.method + ": " + e.Message); }
                }
            }
            TryPatch(harmony, "ScammerGameHudController", "SetDayZeroStep", nameof(DayZeroStep));
            TryPatch(harmony, "PointsPopupUi", "ShowPoints", nameof(Points));
        }

        private static readonly Dictionary<MethodBase, int> argIndex = new Dictionary<MethodBase, int>();

        private static void TryPatch(Harmony harmony, string type, string method, string postfixName)
        {
            MethodInfo m = AccessTools.Method(AccessTools.TypeByName(type), method);
            if (m == null) { Plugin.Log.LogWarning("Announce hook: no " + type + "." + method); return; }
            try { harmony.Patch(m, postfix: new HarmonyMethod(typeof(Announcements), postfixName)); }
            catch (Exception e) { Plugin.Log.LogWarning("Announce hook failed " + type + "." + method + ": " + e.Message); }
        }

        private static void SpeakArgument(MethodBase __originalMethod, object[] __args)
        {
            if (!argIndex.TryGetValue(__originalMethod, out int i) || __args == null || i >= __args.Length) return;
            Say(__args[i] as string);
        }

        private static void Points(int points) => Say((points >= 0 ? "Plus " : "Minus ") + Math.Abs(points) + " points");

        private static void DayZeroStep(object __instance)
        {
            string step = LabelText(__instance, "dayZeroStepLabel");
            string title = LabelText(__instance, "dayZeroTitle");
            string body = LabelText(__instance, "dayZeroBody");
            Say(UiText.JoinSentences(new List<string> { step, title, body }), true);
        }

        public static void Say(string text, bool interrupt = false)
        {
            text = UiText.Clean(text);
            if (text.Length == 0) return;
            float now = Time.unscaledTime;
            if (recent.TryGetValue(text, out float at) && now - at < 3f) return;
            recent[text] = now;
            Speech.Say(text, interrupt);
        }

        internal static string LabelText(object owner, string field)
        {
            if (owner == null) return "";
            Label l = AccessTools.Field(owner.GetType(), field)?.GetValue(owner) as Label;
            return l == null || !UiText.IsShown(l) ? "" : UiText.Clean(l.text);
        }

        // ---------- HUD watcher and status key ----------

        private static object hud;
        private static float nextHudFind, nextWatch;
        private static readonly Dictionary<string, string> watched = new Dictionary<string, string>();
        private static readonly string[] WatchedLabels = { "clockOutRequest", "finalBattleStatus", "dayZeroLoop" };

        private static object Hud()
        {
            if ((hud == null || (hud is UnityEngine.Object o && o == null)) && Time.unscaledTime >= nextHudFind)
            {
                nextHudFind = Time.unscaledTime + 2f;
                Type t = AccessTools.TypeByName("ScammerGameHudController");
                hud = t != null ? UnityEngine.Object.FindFirstObjectByType(t) : null;
            }
            return hud;
        }

        public static void Update()
        {
            if (Time.unscaledTime < nextWatch) return;
            nextWatch = Time.unscaledTime + 0.5f;
            object h = Hud();
            if (h == null) return;
            foreach (string f in WatchedLabels)
            {
                string now = LabelText(h, f);
                watched.TryGetValue(f, out string before);
                if (now != before)
                {
                    watched[f] = now;
                    if (now.Length > 0) Say(now);
                }
            }
        }

        private static string Duration(float seconds)
        {
            int s = Mathf.Max(0, Mathf.CeilToInt(seconds));
            int m = s / 60;
            s %= 60;
            if (m == 0) return s + (s == 1 ? " second" : " seconds");
            return m + (m == 1 ? " minute" : " minutes") + (s > 0 ? " " + s + (s == 1 ? " second" : " seconds") : "");
        }

        // Money made, the target, and time left, read from the game itself so it works even when the
        // on-screen cards are hidden (for example while sitting at the computer).
        public static void SayStatus()
        {
            object h = Hud();
            ScammerGameManager gm = ScammerGameManager.Instance;
            if (gm != null && gm.ScammerGameStarted)
            {
                try
                {
                    var say = new List<string>();
                    string mine = h != null ? UiText.Clean((AccessTools.Field(h.GetType(), "moneyValue")?.GetValue(h) as Label)?.text) : "";
                    if (mine.Length > 0) say.Add("You have made " + mine + " today");
                    if (gm.IsFrenzyMode) say.Add("Scams complete: " + gm.FrenzyScamCount + " of " + gm.Quota);
                    else say.Add("Target: " + gm.MoneyPool.ToString("N0") + " of " + gm.Quota.ToString("N0") + " dollars"
                        + (gm.MoneyRemaining > 0 ? ", " + gm.MoneyRemaining.ToString("N0") + " to go" : ", target reached"));
                    if (gm.IsReviewPhase) say.Add("The day is over. Review ends in " + Duration(gm.ReviewTimeRemaining));
                    else if (gm.WaitingForDayStartDoor) say.Add("Day " + gm.CurrentDay + " has not started yet. Press N to go to the day door");
                    else say.Add("Time left in day " + gm.CurrentDay + ": " + Duration(gm.DayTimeRemaining));
                    Speech.Say(UiText.JoinSentences(say), true);
                    return;
                }
                catch (Exception e) { Plugin.Log.LogWarning("Status failed: " + e.Message); }
            }
            if (h == null) { Speech.Say("No status right now. The work day has not started.", true); return; }
            var parts = new List<string>();
            string money = LabelText(h, "moneyValue");
            string quota = LabelText(h, "quotaText");
            string timer = LabelText(h, "timerText");
            if (money.Length > 0) parts.Add("Your money " + money);
            if (quota.Length > 0) parts.Add("Quota " + quota);
            if (timer.Length > 0) parts.Add("Day ends in " + timer);
            string step = LabelText(h, "dayZeroTitle");
            if (step.Length > 0) parts.Add("Current step: " + step + ". " + LabelText(h, "dayZeroBody"));
            Speech.Say(parts.Count == 0 ? "No status right now" : UiText.JoinSentences(parts), true);
        }
    }
}
