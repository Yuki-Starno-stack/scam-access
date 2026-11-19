using System.Collections;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;

namespace ScamAccess
{
    // Updating is done by running Scam Access.exe, which always fetches the newest version from GitHub.
    // In the game this only tells you, about 20 seconds after start, when a newer version is out.
    // A release holds version.txt (just the version number) and the setup files. publish-update.ps1 makes one.
    internal static class Updater
    {
        public const string Version = "1.1.1";
        private const string Repo = "Yuki-Starno-stack/scam-access";
        private const string BaseUrl = "https://github.com/" + Repo + "/releases/latest/download/";

        private static ConfigEntry<bool> check;

        public static void Init(ConfigFile cfg, MonoBehaviour owner)
        {
            check = cfg.Bind("Updates", "CheckForUpdates", true, "Say when a newer Scam Access is out. Run Scam Access.exe again to update.");
            if (check.Value) owner.StartCoroutine(Check());
        }

        private static IEnumerator Check()
        {
            yield return new WaitForSecondsRealtime(20f); // let the start-up speech finish first
            using (UnityWebRequest req = UnityWebRequest.Get(BaseUrl + "version.txt"))
            {
                req.timeout = 15;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) { Plugin.Log.LogInfo("Update check failed: " + req.error); yield break; }
                System.Version latest;
                if (!System.Version.TryParse((req.downloadHandler.text ?? "").Trim(), out latest)) yield break;
                Plugin.Log.LogInfo("Update check: installed " + Version + ", newest " + latest);
                if (latest > new System.Version(Version))
                    Speech.Say("Scam Access " + latest + " is out. To update, close the game and run Scam Access dot exe again.", false);
            }
        }
    }
}
