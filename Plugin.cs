using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace ScamAccess
{
    [BepInPlugin("scamaccess.screenreader", "Scam Access", Updater.Version)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private static readonly FieldInfo CurrentInteractableField = AccessTools.Field(typeof(PlayerInteractor), "currentInteractable");

        private UIDocument[] documents = new UIDocument[0];
        private float nextDocumentScan;
        private Focusable lastFocused;
        private string lastFocusText;
        private object lastInteractable;
        private string lastInteractText;
        private float nextInteractPoll;
        private object lastSpokenTarget;
        private string lastSpokenHint;
        private float lastSpokenAt = -10f;
        private Navigation nav;
        private OsNavigator osNav;
        private BepInEx.Configuration.ConfigEntry<string> attackKey, blockKey;
        private BepInEx.Configuration.ConfigEntry<bool> arrowsTurn;
        private InputActionAsset boundAsset;

        // Log every binding once (so keys can be chosen without clashes) and add keyboard keys for mouse-only actions.
        private void SetUpBindings()
        {
            InputActionAsset asset = GameInputBindings.Asset;
            if (asset == null || ReferenceEquals(asset, boundAsset)) return;
            boundAsset = asset;
            var sb = new StringBuilder("Bindings:");
            foreach (InputActionMap map in asset.actionMaps)
                foreach (InputAction a in map.actions)
                {
                    var keys = new List<string>();
                    foreach (InputBinding b in a.bindings)
                        if (!string.IsNullOrEmpty(b.effectivePath) && (b.effectivePath.Contains("Keyboard") || b.effectivePath.Contains("Mouse")))
                            keys.Add(b.effectivePath);
                    if (keys.Count > 0) sb.Append("\n  ").Append(map.name).Append('/').Append(a.name).Append(" = ").Append(string.Join(", ", keys));
                }
            Log.LogInfo(sb.ToString());
            AddBinding(asset, "Player", "Attack", attackKey.Value);
            AddBinding(asset, "Player", "Block", blockKey.Value);
            if (arrowsTurn.Value) FreeArrowsFromStrafe(asset);
            RemoveKey(asset, "Shortcuts/TutorialSkip", "<Keyboard>/" + nav.AutoWalkKey.ToString().ToLowerInvariant());
            RemoveKey(asset, "Shortcuts/Scoreboard", "<Keyboard>/tab"); // Tab switches computer windows
        }

        private static void RemoveKey(InputActionAsset asset, string action, string path)
        {
            InputAction a = asset.FindAction(action);
            if (a == null) return;
            bool wasEnabled = a.enabled;
            if (wasEnabled) a.Disable();
            for (int i = 0; i < a.bindings.Count; i++)
                if (string.Equals(a.bindings[i].path, path, StringComparison.OrdinalIgnoreCase))
                {
                    a.ApplyBindingOverride(i, new InputBinding { overridePath = "" });
                    Log.LogInfo("Removed " + path + " from " + action);
                }
            if (wasEnabled) a.Enable();
        }

        // The pause menu sometimes ignores a second Escape. If Escape is pressed while it was open
        // and it is still open at the end of the frame, close it ourselves.
        private bool pauseOpenLastFrame;

        private void LateUpdate()
        {
            try
            {
                SettingsUi s = SettingsUi.Instance;
                bool open = s != null && s.IsOpen;
                bool inGame = Mirror.NetworkClient.active || Mirror.NetworkServer.active;
                Keyboard kb = Keyboard.current;
                if (inGame && open && pauseOpenLastFrame && kb != null && kb.escapeKey.wasPressedThisFrame && !DropdownOpen())
                {
                    Log.LogInfo("Escape did not close the pause menu; closing it");
                    if (MetaCursorNetDebug.Instance != null) MetaCursorNetDebug.Instance.CloseEscapeMenu();
                    else s.IsOpen = false;
                    open = false;
                    Speech.Say("Back to game", true);
                }
                if (open && !pauseOpenLastFrame && inGame) Speech.Say("Pause menu. Tab and Shift Tab move between every control.", false);
                pauseOpenLastFrame = open;
            }
            catch (Exception e)
            {
                Log.LogWarning("LateUpdate error: " + e.Message);
            }
        }

        private bool DropdownOpen()
        {
            foreach (UIDocument doc in documents)
            {
                if (doc == null || doc.rootVisualElement?.panel == null) continue;
                var root = doc.rootVisualElement.panel.visualTree;
                if (root.Q(className: "unity-base-dropdown") != null) return true;
            }
            return false;
        }

        // Left/Right arrows strafe by default; the mod uses them for turning, so remove them from walking.
        private static void FreeArrowsFromStrafe(InputActionAsset asset)
        {
            InputAction move = asset.FindAction("Player/Move");
            if (move == null) return;
            bool wasEnabled = move.enabled;
            if (wasEnabled) move.Disable();
            for (int i = 0; i < move.bindings.Count; i++)
            {
                string p = move.bindings[i].path;
                if (p == "<Keyboard>/leftArrow" || p == "<Keyboard>/rightArrow")
                    move.ApplyBindingOverride(i, new InputBinding { overridePath = "" });
            }
            if (wasEnabled) move.Enable();
            Log.LogInfo("Left/Right arrows removed from Player/Move (used for turning)");
        }

        private static void AddBinding(InputActionAsset asset, string map, string action, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            InputAction a = asset.FindAction(map + "/" + action);
            if (a == null) { Log.LogWarning("No action " + map + "/" + action); return; }
            foreach (InputBinding b in a.bindings) if (b.effectivePath == path) return;
            bool wasEnabled = a.enabled;
            if (wasEnabled) a.Disable();
            a.AddBinding(path);
            if (wasEnabled) a.Enable();
            Log.LogInfo("Added " + path + " to " + map + "/" + action);
        }

        private void Awake()
        {
            Log = Logger;
            Speech.Init(Path.GetDirectoryName(Info.Location));
            nav = new Navigation(Config, gameObject);
            osNav = new OsNavigator();
            attackKey = Config.Bind("Keys", "AttackBinding", "<Keyboard>/semicolon", "Keyboard key added to the game's left-click (Attack) action. Input System path.");
            arrowsTurn = Config.Bind("Navigation", "ArrowsTurn", true, "Left/Right arrows turn instead of stepping sideways (A/D still step sideways).");
            blockKey = Config.Bind("Keys", "BlockBinding", "<Keyboard>/quote", "Keyboard key added to the game's right-click (Block) action. Input System path.");
            var harmony = new Harmony("scamaccess.screenreader");
            harmony.PatchAll(typeof(Patches));
            Announcements.Patch(harmony);
            OsNavigator.Patch(harmony);
            MicMute.Init(Config);
            InviteCode.Init(Config);
            Updater.Init(Config, this);
            QuitGame.Init(Config);
            ResetSave.Init(Config);
            CallValues.Init(Config, harmony);
            BossFight.Init(Config, harmony);
            SceneManager.activeSceneChanged += (from, to) =>
            {
                lastFocused = null;
                lastInteractable = null;
                nextDocumentScan = 0f;
                Log.LogInfo("Scene: " + to.name);
            };
            Log.LogInfo("Scam Access loaded");
            Speech.Say("Scam Access loaded. Arrow keys move through menus, Enter selects. F9 reads the screen, F10 repeats.", true);
        }

        private void Update()
        {
            try
            {
                HandleKeys();
                TrackUiFocus();
                TrackInteraction();
                nav.Update();
                osNav.Update();
                Announcements.Update();
                SetUpBindings();
            }
            catch (Exception e)
            {
                Log.LogWarning("Update error: " + e);
            }
        }

        private void HandleKeys()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null) return;
            if (kb.f9Key.wasPressedThisFrame) { if (OsNavigator.AtComputer) osNav.ReadView(); else ReadScreen(); }
            if (kb.f10Key.wasPressedThisFrame) Speech.Repeat();
            bool typing = (OsNavigator.AtComputer && OsNavigator.EditingText) || Normalize(lastFocused as VisualElement) is TextField;
            CallValues.Update(kb); // Control+K, also while typing in a box
            BossFight.Update(kb);
            if (!typing) { MicMute.Update(kb); InviteCode.Update(kb); QuitGame.Update(kb); ResetSave.Update(kb); } // letter keys, so not while typing
            if (kb.f5Key.wasPressedThisFrame ||(kb.minusKey.wasPressedThisFrame && !typing)) Announcements.SayStatus();
            if (kb.f11Key.wasPressedThisFrame)
            {
                LanguageManager.SelectLanguage("en");
                Speech.Say("Game and call language set to English", true);
            }
            // Menus: Left/Right change a focused dropdown instead of moving away from it.
            if (!OsNavigator.AtComputer && lastFocused is DropdownField d && d.choices != null && d.choices.Count > 0
                && (kb.leftArrowKey.wasPressedThisFrame || kb.rightArrowKey.wasPressedThisFrame))
            {
                int dir = kb.rightArrowKey.wasPressedThisFrame ? 1 : -1;
                d.index = ((d.index + dir) % d.choices.Count + d.choices.Count) % d.choices.Count;
                refocus = d;
                lastFocusText = null;
            }
        }


        // ---------- UI Toolkit focus ----------

        private void RefreshDocuments()
        {
            if (Time.unscaledTime < nextDocumentScan) return;
            nextDocumentScan = Time.unscaledTime + 0.5f;
            documents = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None);
        }

        private readonly Dictionary<UIDocument, Focusable> focusByDoc = new Dictionary<UIDocument, Focusable>();
        private float nextAutoFocus;
        private VisualElement refocus;

        private void TrackUiFocus()
        {
            if (OsNavigator.AtComputer) { lastFocused = null; return; }
            if (refocus != null)
            {
                VisualElement r = refocus;
                refocus = null;
                if (r.panel != null) { r.Focus(); lastFocused = r; Speech.Say(UiText.Describe(r), true); lastFocusText = UiText.Describe(r); return; }
            }
            RefreshDocuments();
            // Several documents can each hold a focused element (a menu behind another menu).
            // Report the one whose focus changed most recently; otherwise keep the current one.
            Focusable focused = null;
            Focusable anyFocused = null;
            foreach (UIDocument doc in documents)
            {
                if (doc == null || !doc.isActiveAndEnabled) continue;
                Focusable f = doc.rootVisualElement?.panel?.focusController?.focusedElement;
                focusByDoc.TryGetValue(doc, out Focusable before);
                focusByDoc[doc] = f;
                if (f == null) continue;
                if (f is VisualElement fv && !UiText.IsShown(fv)) continue;
                if (anyFocused == null || ReferenceEquals(f, lastFocused)) anyFocused = f;
                if (!ReferenceEquals(f, before)) focused = f;
            }
            if (focused == null) focused = anyFocused;
            if (focused == null)
            {
                lastFocused = null;
                AutoFocusOpenMenu();
                return;
            }
            VisualElement ve = Normalize(focused as VisualElement);
            string text = UiText.Describe(ve);
            if (!ReferenceEquals(focused, lastFocused) || text != lastFocusText)
            {
                bool moved = !ReferenceEquals(focused, lastFocused);
                lastFocused = focused;
                lastFocusText = text;
                if (!string.IsNullOrEmpty(text)) Speech.Say(text, moved);
            }
        }

        // Menus such as the pause menu open without focusing anything, so keyboard users hear nothing.
        // When a menu is open (cursor free) and nothing has focus, focus the first button of the topmost menu.
        private void AutoFocusOpenMenu()
        {
            if (UnityEngine.Cursor.lockState == CursorLockMode.Locked || Time.unscaledTime < nextAutoFocus) return;
            nextAutoFocus = Time.unscaledTime + 0.3f;
            var docs = new List<UIDocument>(documents);
            docs.Sort((a, b) => b.sortingOrder.CompareTo(a.sortingOrder));
            foreach (UIDocument doc in docs)
            {
                if (doc == null || !doc.isActiveAndEnabled || doc.rootVisualElement == null || IsWorldPanel(doc)) continue;
                VisualElement first = null, firstButton = null, play = null;
                doc.rootVisualElement.Query<VisualElement>().ForEach(e =>
                {
                    if (!(e is Button || e is Toggle || e is DropdownField || e is Slider || e is TextField)) return;
                    if (!(e.focusable && e.enabledInHierarchy && e.canGrabFocus && UiText.IsShown(e))) return;
                    if (first == null) first = e;
                    if (firstButton == null && e is Button) firstButton = e;
                    if (play == null && (e.name == "play-button" || e.name.StartsWith("resume"))) play = e;
                });
                first = play ?? firstButton ?? first;
                if (first == null) continue;
                Log.LogInfo("Auto-focusing " + first.name + " in " + doc.name);
                first.Focus();
                return;
            }
        }

        // A focused inner text input belongs to its field; report the field.
        private static VisualElement Normalize(VisualElement ve)
        {
            VisualElement cur = ve;
            for (int i = 0; i < 4 && cur != null; i++)
            {
                if (cur is TextField || cur is Toggle || cur is DropdownField || cur is Slider || cur is SliderInt || cur is Button || cur is ScrollView)
                    return cur;
                cur = cur.parent;
            }
            return ve;
        }

        // ---------- What the player is looking at ----------

        private void TrackInteraction()
        {
            if (Time.unscaledTime < nextInteractPoll) return;
            nextInteractPoll = Time.unscaledTime + 0.15f;
            PlayerInteractor pi = PlayerInteractor.LocalPlayerInstance;
            if (pi == null) { lastInteractable = null; return; }
            object target = CurrentInteractableField?.GetValue(pi);
            string hint = pi.GetInteractTutorialText();
            if (ReferenceEquals(target, lastInteractable) && hint == lastInteractText) return;
            lastInteractable = target;
            lastInteractText = hint;
            if (target is Interactable it && it != null)
            {
                string name = ObjectName(it);
                string msg = string.IsNullOrEmpty(hint) ? name : name + ", " + hint;
                // The target flickers off and on while moving; don't repeat the same thing within 2 seconds.
                bool sameAsRecent = ReferenceEquals(target, lastSpokenTarget) && Time.unscaledTime - lastSpokenAt < 2f
                    && (string.IsNullOrEmpty(hint) || hint == lastSpokenHint);
                if (!sameAsRecent) Speech.Say(msg, true);
                lastSpokenTarget = target;
                lastSpokenAt = Time.unscaledTime;
                if (!string.IsNullOrEmpty(hint)) lastSpokenHint = hint;
            }
        }

        internal static string ObjectName(Interactable it)
        {
            if (it is MonitorInteractable mon)
            {
                if (mon.UsesStandingInteraction) return "Laptop";
                if (!mon.SupportsPhoneCalls) return "Lobby computer, no calls";
                var ch = Patches.ChairOf(mon);
                bool npc = ch != null && ch.Networksitter is NpcInteractor;
                bool taken = ch != null && ch.Networksitter != null && !npc && !ReferenceEquals(ch.Networksitter, PlayerInteractor.LocalPlayerInstance);
                return "Work computer" + (taken ? ", taken" : npc ? ", coworker sitting, you can take it" : "");
            }
            if (it is ChairInteractable chair) return Patches.IsDeskChair(chair) ? "Desk chair" : "Chair";
            if (!string.IsNullOrWhiteSpace(it.ItemName)) return it.ItemName;
            if (!string.IsNullOrWhiteSpace(it.displayName) && it.displayName != "Interact") return it.displayName;
            string n = Regex.Replace(it.gameObject.name, @"\(Clone\)|\(\d+\)|\d+$", "").Replace('_', ' ').Trim();
            return Regex.Replace(n, "([a-z])([A-Z])", "$1 $2");
        }

        // ---------- Read whole screen ----------

        private void ReadScreen()
        {
            RefreshDocuments();
            var parts = new List<string>();
            var seen = new HashSet<string>();
            // Prefer the topmost open overlay of each document: read documents by sorting order, highest first.
            var docs = new List<UIDocument>(documents);
            docs.Sort((a, b) => b.sortingOrder.CompareTo(a.sortingOrder));
            foreach (UIDocument doc in docs)
            {
                if (doc == null || !doc.isActiveAndEnabled || doc.rootVisualElement == null) continue;
                if (IsWorldPanel(doc)) continue;
                UiText.CollectVisible(doc.rootVisualElement, parts, seen);
            }
            foreach (TMP_Text t in UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsSortMode.None))
            {
                if (t == null || !t.isActiveAndEnabled || !(t is TextMeshProUGUI)) continue;
                if (t.canvasRenderer != null && t.canvasRenderer.GetAlpha() <= 0.01f) continue;
                string s = UiText.Clean(t.text);
                if (s.Length > 0 && seen.Add(s)) parts.Add(s);
            }
            Speech.Say(parts.Count == 0 ? "No text on screen" : UiText.JoinSentences(parts), true);
        }

        // Panels drawn onto 3D objects (office screens, quota board) render into a texture.
        private static bool IsWorldPanel(UIDocument doc)
        {
            PanelSettings ps = doc.panelSettings;
            if (ps == null) return false;
            if (ps.targetTexture != null) return true;
            PropertyInfo mode = typeof(PanelSettings).GetProperty("renderMode");
            return mode != null && mode.GetValue(ps)?.ToString() == "WorldSpace";
        }
    }

    internal static class UiText
    {
        private static readonly Regex Tags = new Regex("<[^>]+>");

        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Tags.Replace(s, " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        public static bool IsShown(VisualElement e)
        {
            for (VisualElement cur = e; cur != null; cur = cur.parent)
            {
                IResolvedStyle st = cur.resolvedStyle;
                if (st.display == DisplayStyle.None || st.visibility == Visibility.Hidden || st.opacity <= 0.01f) return false;
            }
            return true;
        }

        public static string Describe(VisualElement e)
        {
            if (e == null) return "";
            string role, main;
            switch (e)
            {
                case Toggle t:
                    main = First(t.label, t.text, InnerText(t));
                    role = (t.value ? "checked" : "not checked") + " checkbox";
                    break;
                case DropdownField d:
                    main = First(d.label, "") + " " + Clean(d.value != null && d.formatSelectedValueCallback != null ? d.formatSelectedValueCallback(d.value) : d.value);
                    role = "dropdown, press Enter to open";
                    break;
                case Slider s:
                    main = First(s.label, "") + " " + Math.Round(s.value, 2);
                    role = "slider";
                    break;
                case SliderInt si:
                    main = First(si.label, "") + " " + si.value;
                    role = "slider";
                    break;
                case TextField tf:
                    main = First(tf.label, "") + " " + (tf.isPasswordField ? "" : tf.value);
                    role = "edit";
                    break;
                case Button b:
                    main = First(b.text, InnerText(b), b.tooltip, OsNavigator.TipOf(b), b.name);
                    role = "button";
                    break;
                case ScrollView sv:
                    main = InnerText(sv);
                    role = "";
                    break;
                default:
                    main = First(InnerText(e), e.tooltip, e.name);
                    role = "";
                    break;
            }
            string result = Clean(main);
            // Settings rows put the name in a separate label next to the control.
            if (!(e is Button) && !(e is ScrollView))
            {
                string rowLabel = RowLabel(e);
                if (rowLabel.Length > 0 && !result.Contains(rowLabel)) result = rowLabel + " " + result;
            }
            if (role.Length > 0) result = (result + ", " + role).Trim(' ', ',');
            if (!e.enabledInHierarchy) result += ", unavailable";
            return result;
        }

        private static string RowLabel(VisualElement e)
        {
            if (e is BaseField<bool> bf && !string.IsNullOrWhiteSpace(bf.label)) return "";
            VisualElement cur = e;
            for (int depth = 0; depth < 3 && cur.parent != null; depth++)
            {
                VisualElement row = cur.parent;
                foreach (VisualElement sibling in row.Children())
                {
                    if (sibling == cur) continue;
                    Label l = sibling as Label ?? sibling.Q<Label>();
                    if (l == null || sibling.Q<Button>() != null || !IsShown(l)) continue;
                    string s = Clean(l.text);
                    if (s.Length > 0) return s;
                }
                cur = row;
            }
            return "";
        }

        private static string First(params string[] options)
        {
            foreach (string o in options)
                if (!string.IsNullOrWhiteSpace(o)) return o;
            return "";
        }

        public static string InnerText(VisualElement e)
        {
            var parts = new List<string>();
            var seen = new HashSet<string>();
            e.Query<TextElement>().ForEach(te =>
            {
                if (!IsShown(te)) return;
                string s = Clean(te.text);
                if (s.Length > 0 && seen.Add(s)) parts.Add(s);
            });
            return JoinSentences(parts);
        }

        public static string JoinSentences(List<string> parts)
        {
            var sb = new StringBuilder();
            foreach (string p in parts)
            {
                string s = p.TrimEnd();
                if (s.Length == 0) continue;
                sb.Append(s);
                char c = s[s.Length - 1];
                sb.Append(c == '.' || c == '!' || c == '?' || c == ':' ? " " : ". ");
            }
            return sb.ToString().Trim();
        }

        public static void CollectVisible(VisualElement root, List<string> parts, HashSet<string> seen)
        {
            root.Query<VisualElement>().ForEach(e =>
            {
                if (!(e is TextElement || e is TextField || e is Toggle || e is DropdownField || e is Slider)) return;
                if (e.parent is TextField || e.parent is Toggle || e.parent is DropdownField) return;
                if (!e.enabledInHierarchy || !IsShown(e)) return;
                string s = e is TextElement te && !(e is Button) ? Clean(te.text) : Describe(e);
                if (s.Length > 0 && seen.Add(s)) parts.Add(s);
            });
        }
    }

    internal static class Speech
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("nvdaControllerClient.dll", CharSet = CharSet.Unicode)]
        private static extern int nvdaController_speakText(string text);

        [DllImport("nvdaControllerClient.dll")]
        private static extern int nvdaController_cancelSpeech();

        [DllImport("nvdaControllerClient.dll")]
        private static extern int nvdaController_testIfRunning();

        private static bool loaded;
        private static string last = "";

        public static void Init(string folder)
        {
            string dll = Path.Combine(folder, "nvdaControllerClient.dll");
            loaded = File.Exists(dll) && LoadLibrary(dll) != IntPtr.Zero;
            Plugin.Log.LogInfo("NVDA controller loaded: " + loaded + (loaded ? ", NVDA running: " + (nvdaController_testIfRunning() == 0) : ""));
        }

        public static void Say(string text, bool interrupt)
        {
            text = UiText.Clean(text);
            if (text.Length == 0) return;
            last = text;
            Plugin.Log.LogInfo("SAY: " + text);
            if (!loaded) return;
            try
            {
                if (interrupt) nvdaController_cancelSpeech();
                nvdaController_speakText(text);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Speech failed: " + e.Message);
            }
        }

        public static void Repeat() => Say(last, true);
    }

    internal static class Patches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(TutorialPopupUi), nameof(TutorialPopupUi.Show))]
        private static void TutorialShown(string title, string body)
        {
            Speech.Say(string.IsNullOrWhiteSpace(title) ? body : title + ". " + body, true);
        }

        private static readonly FieldInfo MonitorChairField = AccessTools.Field(typeof(MonitorInteractable), "chairInteractable");

        internal static ChairInteractable ChairOf(MonitorInteractable m) => m == null ? null : MonitorChairField?.GetValue(m) as ChairInteractable;

        internal static bool IsDeskChair(ChairInteractable chair)
        {
            foreach (MonitorInteractable m in UnityEngine.Object.FindObjectsByType<MonitorInteractable>(FindObjectsSortMode.None))
                if (!m.UsesStandingInteraction && ReferenceEquals(ChairOf(m), chair)) return true;
            return false;
        }

        // Sitting at a desk, E opens that desk's monitor no matter where the camera points.
        [HarmonyPrefix, HarmonyPatch(typeof(PlayerInteractor), "HandleInteractionInput")]
        private static bool SeatedMonitor(PlayerInteractor __instance)
        {
            try
            {
                if (!__instance.isLocalPlayer || !__instance.isSitting || KolkataMonitorInputState.IsMonitorInputCaptured) return true;
                foreach (MonitorInteractable m in UnityEngine.Object.FindObjectsByType<MonitorInteractable>(FindObjectsSortMode.None))
                {
                    if (m == null || m.UsesStandingInteraction) continue;
                    var chair = MonitorChairField?.GetValue(m) as ChairInteractable;
                    if (chair == null || !ReferenceEquals(chair.Networksitter, __instance)) continue;
                    if (!m.CanInteract(__instance)) { Speech.Say("This monitor can't be used right now", true); return false; }
                    m.LocalPredictInteraction();
                    m.ClientInteract(__instance);
                    Plugin.Log.LogInfo("Opened desk monitor from chair");
                    return false;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Seated monitor failed: " + e.Message); }
            return true;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(LanguageManager), nameof(LanguageManager.SelectLanguage))]
        private static void LanguageChanged(string value)
        {
            string name = value == "auto" ? "automatic" : value == "en" ? "English" : "code " + value;
            Speech.Say("Language changed to " + name + ". Press F11 to switch back to English.", false);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(MainMenuToolkitController), "ShowAnnouncement")]
        private static void MenuAnnouncement(string message)
        {
            Speech.Say(message, false);
        }
    }
}
