using System;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ScamAccess
{
    // Control+Shift+R twice wipes the current campaign save (progress, cosmetics, permanent apps, money items),
    // so everyone can start again at the same level. It drives the game's own "Reset save" dialog from the pause
    // menu, so the result is identical to pressing that button: it leaves the lobby and returns to the main menu.
    // Each player has their own save, so each player resets their own. The second press is the confirmation.
    internal static class ResetSave
    {
        private static ConfigEntry<Key> key;
        private static float armedUntil;
        private static SaveResetDialog dialog;
        private static readonly FieldInfo DialogField = typeof(PauseMenuToolkitController).GetField("saveReset", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo ResetMethod = typeof(SaveResetDialog).GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Init(ConfigFile cfg)
        {
            key = cfg.Bind("Keys", "ResetSave", Key.R, "Hold Control and Shift and press this key twice to erase the current save and start over. Ignored while typing in a text box.");
        }

        public static void Update(Keyboard kb)
        {
            if (key.Value == Key.None || !kb[key.Value].wasPressedThisFrame) return;
            if (!kb.leftCtrlKey.isPressed && !kb.rightCtrlKey.isPressed) return;
            if (!kb.leftShiftKey.isPressed && !kb.rightShiftKey.isPressed) return;
            try { Press(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Reset save failed: " + e);
                Speech.Say("Could not reset the save.", true);
            }
        }

        private static void Press()
        {
            if (Time.unscaledTime <= armedUntil && dialog != null && dialog.IsOpen)
            {
                armedUntil = 0f;
                Plugin.Log.LogInfo("Resetting the save");
                Speech.Say("Resetting the save. Returning to the main menu.", true);
                ResetMethod.Invoke(dialog, null);
                return;
            }

            var menu = UnityEngine.Object.FindFirstObjectByType<PauseMenuToolkitController>(FindObjectsInactive.Include);
            dialog = menu != null ? DialogField.GetValue(menu) as SaveResetDialog : null;
            if (dialog == null) { Speech.Say("Reset save is not available here. Open it from the main menu or the pause menu.", true); return; }
            if (dialog.IsOpen) dialog.Cancel();
            dialog.Show();
            if (!dialog.IsOpen) { Speech.Say("Reset save is not available right now.", true); return; }
            armedUntil = Time.unscaledTime + 10f;
            Speech.Say("Reset the save? All progress, cosmetics and permanent apps will be erased and you will go back to the main menu. "
                + "Press Control+Shift+R again within 10 seconds to confirm, or Escape to cancel.", true);
        }
    }
}
