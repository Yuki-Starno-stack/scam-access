using System;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ScamAccess
{
    // One key to mute or unmute the game's microphone, so you can talk elsewhere (Discord, for example)
    // without the callers or other players hearing you. Uses the game's own mute state, the one behind
    // the microphone button in the pause menu, so voice chat and caller speech recognition both stop.
    internal static class MicMute
    {
        private static ConfigEntry<Key> key;
        private static MuteAndDeafen state;

        public static void Init(ConfigFile cfg)
        {
            key = cfg.Bind("Keys", "ToggleMicrophone", Key.V, "Mute or unmute your microphone in the game. Ignored while typing in a text box.");
        }

        public static void Update(Keyboard kb)
        {
            if (key.Value == Key.None || !kb[key.Value].wasPressedThisFrame) return;
            try { Toggle(); }
            catch (Exception e) { Plugin.Log.LogWarning("Mic mute failed: " + e); }
        }

        private static void Toggle()
        {
            if (state == null) state = UnityEngine.Object.FindFirstObjectByType<MuteAndDeafen>(FindObjectsInactive.Include);
            if (state == null) { Speech.Say("Microphone mute is not available here. Join or host a game first.", true); return; }
            if (state.IsMicMissing) { Speech.Say("No microphone found.", true); return; }

            // Same rule as the game's own button: any muted or deafened state goes back to normal.
            bool mute = state.State == MuteAndDeafenState.None;
            state.State = mute ? MuteAndDeafenState.Muted : MuteAndDeafenState.None;
            Plugin.Log.LogInfo("Microphone state: " + state.State);
            Speech.Say(mute ? "Microphone muted" : "Microphone on", true);
        }
    }
}
