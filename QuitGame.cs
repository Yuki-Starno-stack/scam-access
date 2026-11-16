using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ScamAccess
{
    // Control+Q twice closes the game, so Alt+F4 is not needed. The second press is the confirmation.
    internal static class QuitGame
    {
        private static ConfigEntry<Key> key;
        private static float armedUntil;

        public static void Init(ConfigFile cfg)
        {
            key = cfg.Bind("Keys", "QuitGame", Key.Q, "Hold Control and press this key twice to close the game. Ignored while typing in a text box.");
        }

        public static void Update(Keyboard kb)
        {
            if (key.Value == Key.None || !kb[key.Value].wasPressedThisFrame) return;
            if (!kb.leftCtrlKey.isPressed && !kb.rightCtrlKey.isPressed) return;
            if (Time.unscaledTime <= armedUntil)
            {
                Plugin.Log.LogInfo("Quit requested with the quit key");
                Speech.Say("Closing the game.", true);
                Application.Quit();
                return;
            }
            armedUntil = Time.unscaledTime + 5f;
            Speech.Say("Press Control+Q again to close the game.", true);
        }
    }
}
