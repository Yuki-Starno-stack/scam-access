using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ScamAccess
{
    // The numbers a caller gives you (gift card code, card number, remote desktop code and so on).
    // The game shows them highlighted in the phone transcript once the caller has said them, and a sighted
    // player clicks one to copy it and have it filled into the scam app. This does the same from the keyboard:
    // Control+K copies the newest one and fills it in; pressing it again steps back to older ones.
    internal static class CallValues
    {
        private class Entry
        {
            public string Tag, Value, Label;
            public Action<string, string> Autofill;
        }

        private static ConfigEntry<Key> key;
        private static MethodInfo getSpans;
        private static FieldInfo spanValue, spanTag;
        private static readonly List<Entry> entries = new List<Entry>();          // newest first
        private static readonly Dictionary<string, int> seen = new Dictionary<string, int>(); // transcript text -> shown length already handled
        private static int cursor;
        private static float lastPress;

        public static void Init(ConfigFile cfg, Harmony harmony)
        {
            key = cfg.Bind("Keys", "CopyCallerNumber", Key.K, "Hold Control and press this key to copy the newest number the caller gave you and fill it into its app. Press again for older ones.");
            try
            {
                Type builder = AccessTools.TypeByName("Kolkata.UI.KolkataOsContentBuilder");
                MethodInfo set = builder != null ? AccessTools.Method(builder, "SetPhoneSentinelText") : null;
                getSpans = builder != null ? AccessTools.Method(builder, "GetCompletedPhoneSentinels") : null;
                if (set == null || getSpans == null) { Plugin.Log.LogWarning("Caller numbers: game methods not found"); return; }
                harmony.Patch(set, postfix: new HarmonyMethod(typeof(CallValues), nameof(AfterSetText)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Caller numbers patch failed: " + e); }
        }

        // Runs whenever the game puts transcript text on screen. "visible" grows as the caller speaks, so a
        // number only counts once it has been fully said.
        private static void AfterSetText(string visible, string authoritative, Action<string, string> autofill)
        {
            try
            {
                if (string.IsNullOrEmpty(visible) || string.IsNullOrEmpty(authoritative) || authoritative.IndexOf("sentinel=", StringComparison.Ordinal) < 0) return;
                int done;
                if (seen.TryGetValue(authoritative, out done) && done == visible.Length) return;
                if (seen.Count > 300) seen.Clear();
                seen[authoritative] = visible.Length;

                IEnumerable spans = getSpans.Invoke(null, new object[] { visible, authoritative }) as IEnumerable;
                if (spans == null) return;
                foreach (object span in spans)
                {
                    if (spanValue == null) { spanValue = AccessTools.Field(span.GetType(), "Value"); spanTag = AccessTools.Field(span.GetType(), "Tag"); }
                    string value = spanValue?.GetValue(span) as string;
                    string tag = spanTag?.GetValue(span) as string;
                    if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(tag)) continue;
                    Entry known = entries.Find(e => e.Tag == tag && e.Value == value);
                    if (known != null) { if (autofill != null) known.Autofill = autofill; continue; }
                    var entry = new Entry { Tag = tag, Value = value, Label = LabelFor(tag), Autofill = autofill };
                    entries.Insert(0, entry);
                    if (entries.Count > 20) entries.RemoveAt(entries.Count - 1);
                    cursor = 0;
                    Plugin.Log.LogInfo("Caller gave: " + entry.Label);
                    Speech.Say(entry.Label + " received. Control+K copies it.", false);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Caller numbers read failed: " + (e.InnerException ?? e).Message); }
        }

        private static string LabelFor(string tag)
        {
            string label = null;
            try { label = SentinelValueFactory.GetDefinition(tag)?.label; } catch { }
            if (!string.IsNullOrWhiteSpace(label)) return label.Trim();
            string t = tag.StartsWith("FICTIONAL_", StringComparison.OrdinalIgnoreCase) ? tag.Substring(10) : tag;
            return t.Replace('_', ' ').ToLowerInvariant();
        }

        public static void Update(Keyboard kb)
        {
            if (key.Value == Key.None || !kb[key.Value].wasPressedThisFrame) return;
            if (!kb.leftCtrlKey.isPressed && !kb.rightCtrlKey.isPressed) return;
            if (entries.Count == 0) { Speech.Say("The caller has not given any numbers yet.", true); return; }

            // A second press soon after the first steps back to the number before.
            float now = Time.unscaledTime;
            cursor = now - lastPress < 4f ? (cursor + 1) % entries.Count : 0;
            lastPress = now;
            Entry e = entries[cursor];
            GUIUtility.systemCopyBuffer = e.Value;
            try { e.Autofill?.Invoke(e.Tag, e.Value); }
            catch (Exception ex) { Plugin.Log.LogInfo("Autofill failed: " + ex.Message); }
            Plugin.Log.LogInfo("Copied caller value: " + e.Label);
            Speech.Say("Copied " + e.Label + ": " + e.Value + (entries.Count > 1 ? ". " + (cursor + 1) + " of " + entries.Count : ""), true);
        }
    }
}
