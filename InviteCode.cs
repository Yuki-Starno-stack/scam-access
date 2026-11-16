using System;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ScamAccess
{
    // Control+C (outside text boxes) copies the lobby invite code to the clipboard, the same code as the
    // game's "copy code" button in the pause menu. Friends paste it under Play, Join by code.
    internal static class InviteCode
    {
        private static ConfigEntry<Key> key;

        public static void Init(ConfigFile cfg)
        {
            key = cfg.Bind("Keys", "CopyInviteCode", Key.C, "Hold Control and press this key to copy the lobby invite code. Ignored while typing in a text box.");
        }

        public static void Update(Keyboard kb)
        {
            if (key.Value == Key.None || !kb[key.Value].wasPressedThisFrame) return;
            if (!kb.leftCtrlKey.isPressed && !kb.rightCtrlKey.isPressed) return;
            try { Copy(); }
            catch (Exception e) { Plugin.Log.LogWarning("Copy invite code failed: " + e); }
        }

        private static void Copy()
        {
            ulong id = SettingsUi.CurrentSteamLobbyId;
            if (id == 0)
            {
                Speech.Say("No invite code yet. Host or join a game first.", true);
                return;
            }
            GUIUtility.systemCopyBuffer = id.ToString();
            Plugin.Log.LogInfo("Invite code copied");
            Speech.Say("Invite code copied. Send it to your friend. They join from the main menu: Play, Join by code, paste, then Join.", true);
        }
    }
}
