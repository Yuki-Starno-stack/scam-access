using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Kolkata.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace ScamAccess
{
    // Keyboard and speech access to the in-game computer (Kolkata OS).
    // Up/Down move through the current view, Enter activates, Tab switches between open windows and the desktop.
    internal class OsNavigator
    {
        private static readonly FieldInfo AppOrderField = AccessTools.Field(typeof(KolkataOsController), "m_appOrder");
        private static readonly FieldInfo ActiveAppField = AccessTools.Field(typeof(KolkataOsController), "m_activeApp");
        private static readonly FieldInfo CallStateUiField = AccessTools.Field(typeof(KolkataOsController), "m_callUiState");
        private static readonly MethodInfo DesktopIconClicked = AccessTools.Method(typeof(KolkataOsController), "OnDesktopIconClicked");
        private static readonly MethodInfo AwaitingAnswer = AccessTools.Method(typeof(KolkataOsController), "IsAwaitingIncomingCallAnswer");
        private static readonly MethodInfo AnswerCall = AccessTools.Method(typeof(KolkataOsController), "OnIncomingCallAnswerClicked");
        private static readonly MethodInfo DeclineCall = AccessTools.Method(typeof(KolkataOsController), "OnIncomingCallDeclineClicked");
        private static readonly FieldInfo ClickedField = AccessTools.Field(typeof(Clickable), "clicked");
        private static readonly FieldInfo ClickedInfoField = AccessTools.Field(typeof(Clickable), "clickedWithEventInfo");

        private enum ItemKind { App, Element, Answer, Decline }

        private class Item
        {
            public ItemKind Kind;
            public object App;
            public VisualElement Element;
            public string Text;
            // Scamazon product cards: one item per product, Element is its buy button.
            public bool Card;
            public bool Buyable;
            public string Name;
            public string Action;
        }

        private VisualElement holdButton;   // Scamazon "hold to buy" button being held for the player
        private float holdDownAt, holdUpAt;
        private bool holdIsDown;
        private string followName;          // product to return to after a purchase rebuilt the shop
        private float followAt;

        private bool active;
        private object viewApp;          // null = desktop
        private bool viewIsCall;
        private List<Item> items = new List<Item>();
        private int index;
        private bool wasRinging;
        private float nextRingCheck;
        private float nextRefresh;
        private string lastViewSignature;
        private object lastSeenActive;

        private static KolkataOsController Controller => KolkataOsSingleton.Instance != null ? KolkataOsSingleton.Instance.Controller : null;

        public static bool EditingText;

        public static bool AtComputer => KolkataMonitorInputState.IsMonitorInputCaptured && Controller != null;

        public void Update()
        {
            KolkataOsController os = Controller;
            CheckRinging(os);
            bool now = AtComputer;
            if (now && !active) Enter(os);
            if (!now) { active = false; vipShown = false; return; }

            Keyboard kb = Keyboard.current;
            if (kb == null) return;
            bool editing = IsEditingText(os);
            EditingText = editing;
            bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

            if (Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + 0.4f;
                FollowActiveWindow(os);
            }

            TickShopping(os);
            // The "Verify Discord VIP" pop-up is its own panel on top of the computer; take it over while it is up.
            bool vip = VipRoot() != null;
            if (vip != vipShown)
            {
                vipShown = vip;
                Rebuild(os);
                index = 0;
                if (vip) Speech.Say("Verify Discord VIP window, " + items.Count + " items. Up and down to browse, Enter to use. Close is the last button. " + (items.Count > 0 ? items[0].Text : ""), true);
                else FollowActiveWindow(os, true);
            }
            if (kb.f1Key.wasPressedThisFrame) Help();
            if (kb.wKey.wasPressedThisFrame && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed) && !vipShown) { CloseCurrent(os); return; }
            if (kb.tabKey.wasPressedThisFrame && !vipShown) { SwitchView(os, shift ? -1 : 1); return; }
            if (editing)
            {
                // While typing in a box, Up/Down leave the box; everything else goes to the game.
                if (kb.upArrowKey.wasPressedThisFrame || kb.downArrowKey.wasPressedThisFrame)
                {
                    Blur(os);
                    Move(os, kb.upArrowKey.wasPressedThisFrame ? -1 : 1);
                }
                return;
            }
            if (kb.downArrowKey.wasPressedThisFrame) Move(os, 1);
            if (kb.upArrowKey.wasPressedThisFrame) Move(os, -1);
            if (kb.homeKey.wasPressedThisFrame) Jump(os, 0);
            if (kb.endKey.wasPressedThisFrame) Jump(os, int.MaxValue);
            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) Activate(os);
            if (kb.leftArrowKey.wasPressedThisFrame) Adjust(-1);
            if (kb.rightArrowKey.wasPressedThisFrame) Adjust(1);
            if (kb.gKey.wasPressedThisFrame) GlobeGuess(os, false);
            if (kb.bKey.wasPressedThisFrame) NextBuyable(os, shift ? -1 : 1);
            if (vipShown) return;
            if (kb.backquoteKey.wasPressedThisFrame) ListWindows(os);
            if (kb.digit0Key.wasPressedThisFrame) GoToWindow(os, 0);
            for (int n = 1; n <= 9; n++)
                if (kb[Key.Digit1 + (n - 1)].wasPressedThisFrame) GoToWindow(os, n);
        }

        // ---------- Close window, Discord pop-up, button tooltips ----------

        private bool vipShown;
        private static readonly FieldInfo VipCurrentField = AccessTools.Field(typeof(DiscordVipVerificationUi), "current");
        private static readonly FieldInfo VipRootField = AccessTools.Field(typeof(DiscordVipVerificationUi), "root");
        private static readonly MethodInfo CloseAppMethod = AccessTools.Method(typeof(KolkataOsController), "CloseApp");

        private static VisualElement VipRoot()
        {
            try
            {
                MonoBehaviour cur = VipCurrentField?.GetValue(null) as MonoBehaviour;
                if (cur == null) return null;
                VisualElement r = VipRootField?.GetValue(cur) as VisualElement;
                return r != null && r.panel != null ? r : null;
            }
            catch { return null; }
        }

        // Icon-only buttons (the camera's take-selfie, copy, delete...) get their name from the tooltip the game registers for them.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VisualElement, Func<string>> Tips =
            new System.Runtime.CompilerServices.ConditionalWeakTable<VisualElement, Func<string>>();

        internal static string TipOf(VisualElement e)
        {
            if (e == null) return "";
            try { if (Tips.TryGetValue(e, out Func<string> f)) return f() ?? ""; } catch { }
            return "";
        }

        public static void Patch(Harmony harmony)
        {
            try
            {
                Type t = AccessTools.TypeByName("Kolkata.UI.KolkataOsTooltip");
                MethodInfo m = t == null ? null : AccessTools.Method(t, "Register", new[] { typeof(VisualElement), typeof(Func<string>) });
                if (m != null) harmony.Patch(m, postfix: new HarmonyMethod(typeof(OsNavigator), nameof(TooltipRegistered)));
                else Plugin.Log.LogWarning("Tooltip Register not found; icon buttons stay unnamed");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Tooltip patch failed: " + e.Message); }
        }

        private static void TooltipRegistered(VisualElement target, Func<string> textProvider)
        {
            if (target == null || textProvider == null) return;
            Tips.Remove(target);
            Tips.Add(target, textProvider);
        }

        private void CloseCurrent(KolkataOsController os)
        {
            if (viewApp == null) { Speech.Say("This is the desktop. There is no window to close. Tab or a number key picks a window.", true); return; }
            object app = viewApp;
            string title = AppTitle(app);
            Invoke(CloseAppMethod, os, app);
            if (IsOpenVisible(app) || F<object>(app, "State")?.ToString() == "OpenMinimized")
            {
                Speech.Say(title + " can't be closed right now.", true);
                return;
            }
            // Land on the window that is now on top, or the desktop when nothing else is open.
            object next = Get(ActiveAppField, os);
            if (next == null || !IsOpenVisible(next))
            {
                List<object> open = OpenWindows(os);
                next = open.Count > 0 ? open[open.Count - 1] : null;
            }
            viewIsCall = false;
            viewApp = next;
            lastSeenActive = Get(ActiveAppField, os);
            if (viewApp != null) Invoke(DesktopIconClicked, os, viewApp);
            Rebuild(os);
            index = 0;
            Speech.Say("Closed " + title + ". " + ViewName(os) + ", " + items.Count + " items" + (items.Count > 0 ? ". " + items[0].Text : ""), true);
        }

        private static bool IsRinging(KolkataOsController os)
        {
            try { return os != null && AwaitingAnswer != null && (bool)AwaitingAnswer.Invoke(os, null); }
            catch { return false; }
        }

        // ---------- Scamazon shopping ----------

        private static bool IsInside(VisualElement e, VisualElement ancestor)
        {
            for (VisualElement v = e; v != null; v = v.parent) if (v == ancestor) return true;
            return false;
        }

        // The text on a buy button. "Hold to buy" buttons keep it in a child label.
        private static string ActionText(Button action)
        {
            string a = UiText.Clean(action.text);
            if (a.Length == 0) a = UiText.Clean(action.Q<Label>()?.text);
            return a;
        }

        // One spoken item for a whole product card: name, what the button does, then price and description.
        private static Item CardItem(VisualElement card)
        {
            Button action = card.Q<Button>(null, "scamazon__buy-button");
            if (action == null) return null;
            string name = "";
            var rest = new List<string>();
            card.Query<TextElement>().ForEach(te =>
            {
                if (IsInside(te, action) || !UiText.IsShown(te)) return;
                string s = UiText.Clean(te.text);
                if (s.Length == 0 || (s.Length == 1 && !char.IsLetterOrDigit(s[0]))) return;
                if (te.ClassListContains("scamazon__product-name")) name = s;
                else if (te.ClassListContains("scamazon__product-price--original")) rest.Add("was " + s);
                else if (!rest.Contains(s)) rest.Add(s);
            });
            string act = ActionText(action);
            var parts = new List<string> { name.Length > 0 ? name : "Product" };
            if (act.Length > 0) parts.Add(act);
            parts.AddRange(rest);
            return new Item
            {
                Kind = ItemKind.Element, Element = action, Card = true, Name = name, Action = act,
                Buyable = action.enabledInHierarchy && !act.Equals("OPEN APP", StringComparison.OrdinalIgnoreCase),
                Text = UiText.JoinSentences(parts)
            };
        }

        private void NextBuyable(KolkataOsController os, int step)
        {
            Rebuild(os);
            if (!items.Exists(i => i.Card)) { Speech.Say("B jumps between things you can buy. It works in the Scamazon window.", true); return; }
            for (int i = index + step; i >= 0 && i < items.Count; i += step)
            {
                if (!items[i].Card) continue; // every product, whether it is for sale, free or just opens its app
                index = i;
                Speech.Say(items[i].Text, true);
                return;
            }
            Speech.Say(step > 0 ? "No more products below. Shift B goes back up." : "No more products above.", true);
        }

        private void ActivateCard(Item it)
        {
            VisualElement el = it.Element;
            if (!el.enabledInHierarchy) { Speech.Say("Not available. " + it.Action, true); return; }
            followName = it.Name;
            if (el.ClassListContains("scamazon__buy-button--hold"))
            {
                // These buttons want the mouse held down for about a second. Scroll it into view, then hold it for the player.
                el.GetFirstAncestorOfType<ScrollView>()?.ScrollTo(el);
                holdButton = el;
                holdIsDown = false;
                holdDownAt = Time.unscaledTime + 0.15f;
                followAt = 0f;
                Speech.Say("Buying " + it.Name + ". Wait a moment.", true);
                return;
            }
            Click(el);
            followAt = Time.unscaledTime + 0.7f;
            Speech.Say(it.Action + ". " + it.Name, true);
        }

        private static void SendMouse(VisualElement el, EventType type)
        {
            var sys = new Event { type = type, button = 0, mousePosition = el.worldBound.center };
            if (type == EventType.MouseDown)
                using (PointerDownEvent e = PointerDownEvent.GetPooled(sys)) { e.target = el; el.SendEvent(e); }
            else
                using (PointerUpEvent e = PointerUpEvent.GetPooled(sys)) { e.target = el; el.SendEvent(e); }
        }

        // Runs the held purchase and, after any purchase, puts the player back on the same product.
        private void TickShopping(KolkataOsController os)
        {
            float now = Time.unscaledTime;
            if (holdButton != null)
            {
                try
                {
                    if (!holdIsDown && now >= holdDownAt)
                    {
                        SendMouse(holdButton, EventType.MouseDown);
                        holdIsDown = true;
                        holdUpAt = now + 1.2f; // the game asks for 0.86 seconds
                    }
                    else if (holdIsDown && now >= holdUpAt)
                    {
                        string after = ActionText((Button)holdButton);
                        SendMouse(holdButton, EventType.MouseUp);
                        Plugin.Log.LogInfo("Scamazon hold purchase, button now says: " + after);
                        if (after.StartsWith("HOLD TO", StringComparison.OrdinalIgnoreCase))
                            Speech.Say("The purchase did not go through.", true);
                        holdButton = null;
                        followAt = now + 0.2f;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Scamazon hold purchase failed: " + e);
                    holdButton = null;
                    Speech.Say("Buying failed", true);
                }
            }
            if (followAt > 0f && now >= followAt)
            {
                followAt = 0f;
                Rebuild(os);
                if (items.Count > 0 && !items.Exists(i => i.Card))
                {
                    // A confirmation question replaced the shop.
                    var texts = new List<string>();
                    foreach (Item it in items) texts.Add(it.Text);
                    index = 0;
                    Speech.Say(UiText.JoinSentences(texts) + ". Up and down to choose, Enter to confirm.", false);
                    return;
                }
                int found = string.IsNullOrEmpty(followName) ? -1 : items.FindIndex(i => i.Card && i.Name == followName);
                if (found >= 0) index = found;
            }
        }

        // ---------- Windows by number ----------

        private List<object> OpenWindows(KolkataOsController os)
        {
            var list = new List<object>();
            foreach (object app in Apps(os)) if (IsOpenVisible(app)) list.Add(app);
            return list;
        }

        private void ListWindows(KolkataOsController os)
        {
            List<object> open = OpenWindows(os);
            if (open.Count == 0) { Speech.Say("No open windows. 0 goes to the desktop.", true); return; }
            var names = new List<string>();
            for (int i = 0; i < open.Count && i < 9; i++) names.Add((i + 1) + " " + AppTitle(open[i]));
            Speech.Say(open.Count + " open windows. " + string.Join(", ", names) + ". 0 is the desktop.", true);
        }

        private void GoToWindow(KolkataOsController os, int number)
        {
            List<object> open = OpenWindows(os);
            if (number > open.Count)
            {
                Speech.Say("No window " + number + ". " + (open.Count == 0 ? "Nothing is open." : open.Count + " open."), true);
                return;
            }
            ShowView(os, number == 0 ? null : open[number - 1]);
        }

        private void ShowView(KolkataOsController os, object app)
        {
            viewIsCall = false;
            viewApp = app;
            if (viewApp != null) Invoke(DesktopIconClicked, os, viewApp); // brings the window to the front
            Rebuild(os);
            index = 0;
            List<object> open = OpenWindows(os);
            string place = viewApp != null && open.Count > 1 ? ", " + (open.IndexOf(viewApp) + 1) + " of " + open.Count : "";
            Speech.Say(ViewName(os) + place + ", " + items.Count + " items" + (items.Count > 0 ? ". " + items[0].Text : ""), true);
        }

        // ---------- IP Geolocation globe ----------

        private static readonly FieldInfo GlobeStateField = AccessTools.Field(typeof(Kolkata.IPGeolocation.TraceGlobe), "state");
        private static readonly FieldInfo GlobePossibleField = AccessTools.Field(typeof(Kolkata.IPGeolocation.TraceGlobe), "possible");
        private static readonly MethodInfo GlobeSetGuess = AccessTools.Method(typeof(Kolkata.IPGeolocation.TraceGlobe), "SetGuess");

        private Kolkata.IPGeolocation.TraceGlobe FindGlobe(KolkataOsController os)
        {
            VisualElement root = viewApp != null ? F<VisualElement>(viewApp, "WindowContent") : null;
            Kolkata.IPGeolocation.TraceGlobe g = root?.Q<Kolkata.IPGeolocation.TraceGlobe>();
            if (g != null) return g;
            foreach (object app in Apps(os))
            {
                if (!IsOpenVisible(app)) continue;
                g = F<VisualElement>(app, "WindowContent")?.Q<Kolkata.IPGeolocation.TraceGlobe>();
                if (g != null) return g;
            }
            return null;
        }

        // The globe highlights the land that fits the trace readings, and a sighted player clicks inside it.
        // This places the guess in the middle of that highlighted land.
        private void GlobeGuess(KolkataOsController os, bool fromItem)
        {
            Kolkata.IPGeolocation.TraceGlobe globe = FindGlobe(os);
            if (globe == null) { if (fromItem) Speech.Say("The globe is not open", true); return; }
            try
            {
                var state = GlobeStateField?.GetValue(globe) as Kolkata.IPGeolocation.TraceSnapshot;
                if (state == null || GlobeSetGuess == null) { Speech.Say("Can't reach the globe in this game version", true); return; }
                if (state.locked) { Speech.Say("The location is already locked", true); return; }
                if (state.failed) { Speech.Say("The trace failed", true); return; }
                if (!state.started) { Speech.Say("Start the trace first, then wait for it to finish.", true); return; }
                if (!state.ready) { Speech.Say("The trace is still running. Wait for it to finish, then press G.", true); return; }

                var tiles = new List<Vector2>();
                if (GlobePossibleField?.GetValue(globe) is List<Kolkata.IPGeolocation.TraceSurface> possible)
                    foreach (Kolkata.IPGeolocation.TraceSurface t in possible) tiles.Add(t.coordinate);
                if (tiles.Count == 0)
                    foreach (Kolkata.IPGeolocation.TraceSurface t in Kolkata.IPGeolocation.TraceSurface.Land)
                        if (Kolkata.IPGeolocation.TraceRound.Possible(t.coordinate, state.readings, 160f)) tiles.Add(t.coordinate);
                if (tiles.Count == 0) { Speech.Say("No highlighted area on the globe yet", true); return; }

                // Middle of the highlighted land: average direction, then the highlighted tile closest to it.
                Vector3 sum = Vector3.zero;
                foreach (Vector2 c in tiles) sum += Kolkata.IPGeolocation.TraceRound.Unit(c);
                Vector3 mid = sum.sqrMagnitude > 1e-6f ? sum.normalized : Kolkata.IPGeolocation.TraceRound.Unit(tiles[0]);
                Vector2 best = tiles[0];
                float bestDot = -2f;
                foreach (Vector2 c in tiles)
                {
                    float d = Vector3.Dot(mid, Kolkata.IPGeolocation.TraceRound.Unit(c));
                    if (d > bestDot) { bestDot = d; best = c; }
                }
                float reach = 0f;
                foreach (Vector2 c in tiles) reach = Mathf.Max(reach, Kolkata.IPGeolocation.TraceRound.Distance(best, c));

                GlobeSetGuess.Invoke(globe, new object[] { best, null });
                if (!globe.Guess.HasValue) { Speech.Say("The globe did not accept a guess. The call may have ended.", true); return; }

                Kolkata.IPGeolocation.TraceGeography geo = Kolkata.IPGeolocation.TraceGeography.Load();
                string country = geo.CountryAt(best)?.name;
                string city = null;
                float cityKm = float.MaxValue;
                foreach (Kolkata.IPGeolocation.TraceCity tc in geo.cities)
                {
                    float d = Kolkata.IPGeolocation.TraceRound.Distance(best, new Vector2(tc.latitude, tc.longitude));
                    if (d < cityKm) { cityKm = d; city = string.IsNullOrWhiteSpace(tc.name) ? tc.id : tc.name; }
                }
                string where = (string.IsNullOrWhiteSpace(country) ? "the highlighted area" : country)
                    + (city != null ? ", " + Mathf.RoundToInt(cityKm / 10f) * 10 + " kilometers from " + city : "");
                Plugin.Log.LogInfo("Globe guess " + best.x.ToString("0.0") + ", " + best.y.ToString("0.0") + " from " + tiles.Count + " tiles, reach " + Mathf.RoundToInt(reach) + " km");
                Rebuild(os);
                int lockIndex = items.FindIndex(i => i.Element is Button b && b.name == "lock");
                if (lockIndex >= 0) index = lockIndex;
                Speech.Say("Guess placed in " + where + ". The highlighted area reaches " + Mathf.RoundToInt(reach / 50f) * 50 + " kilometers from it. "
                    + (lockIndex >= 0 ? "You are on the lock button, press Enter to lock it in." : "Find the lock button and press Enter."), true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Globe guess failed: " + (e.InnerException ?? e));
                Speech.Say("Placing the guess failed", true);
            }
        }

        public void ReadView()
        {
            KolkataOsController os = Controller;
            if (os == null) return;
            Rebuild(os);
            var texts = new List<string>();
            foreach (Item it in items) texts.Add(it.Text);
            Speech.Say(ViewName(os) + ". " + UiText.JoinSentences(texts), true);
        }

        private void Enter(KolkataOsController os)
        {
            active = true;
            viewApp = Get(ActiveAppField, os);
            if (viewApp != null && !IsOpenVisible(viewApp)) viewApp = null;
            lastSeenActive = viewApp;
            Rebuild(os);
            index = 0;
            if (IsRinging(os) && VipRoot() == null) { viewIsCall = true; wasRinging = true; Rebuild(os); index = 0; Speech.Say("Computer. Incoming call from " + CallerName(os) + ". Press Enter to answer, or down arrow then Enter to decline.", true); return; }
            Speech.Say("Computer. " + ViewName(os) + ", " + items.Count + " items. Up and down to browse, Enter to use, Tab to switch windows, F1 for help.", true);
        }

        private static void Help()
        {
            Speech.Say("Computer keys. Up and down arrows move. Enter uses the item. Left and right change a dropdown or slider. " +
                "Tab and shift tab switch between open windows and the desktop. Number keys 1 to 9 jump straight to an open window, 0 goes to the desktop, and the key left of 1 lists the open windows with their numbers. " +
                "In Scamazon, each product is one item, Enter buys it or gets it free, and B jumps to the next product, shift B to the previous. Control W closes the current window. While a call is ringing, answer and decline are at the top of the desktop and of the Phone window. " +
                "During a call, Control+K copies the newest number the caller gave you and fills it into its app; press it again for older numbers, and Control+V pastes in a text box. " +
                "Home and end jump to first and last. F9 reads everything in this window. In IP Geolocation, G places your guess on the globe once the trace has finished. Escape leaves the computer.", true);
        }

        // ---------- Phone ----------

        private void CheckRinging(KolkataOsController os)
        {
            if (os == null || Time.unscaledTime < nextRingCheck) return;
            nextRingCheck = Time.unscaledTime + 0.25f;
            bool ringing = false;
            try { ringing = AwaitingAnswer != null && (bool)AwaitingAnswer.Invoke(os, null); } catch { }
            if (ringing && !wasRinging)
            {
                string caller = CallerName(os);
                if (AtComputer)
                {
                    viewIsCall = true;
                    Rebuild(os);
                    index = 0;
                    Speech.Say("Incoming call from " + caller + ". Press Enter to answer, or down arrow then Enter to decline.", true);
                }
                else
                {
                    Speech.Say("Your phone is ringing. " + caller + " is calling. Sit at your desk and use the monitor to answer.", true);
                }
            }
            if (!ringing && wasRinging && viewIsCall)
            {
                viewIsCall = false;
                if (AtComputer) { FollowActiveWindow(os, true); }
            }
            wasRinging = ringing;
        }

        private static string CallerName(KolkataOsController os)
        {
            object ui = Get(CallStateUiField, os);
            string name = ui != null ? AccessTools.Field(ui.GetType(), "callerName")?.GetValue(ui) as string : null;
            return string.IsNullOrWhiteSpace(name) ? "Unknown caller" : name;
        }

        // ---------- Views ----------

        private IList Apps(KolkataOsController os) => Get(AppOrderField, os) as IList ?? new ArrayList();

        private static object Get(FieldInfo f, object o) => f == null || o == null ? null : f.GetValue(o);
        private static T F<T>(object o, string name) => o == null ? default : (T)(AccessTools.Field(o.GetType(), name)?.GetValue(o) ?? default(T));
        private static bool IsOpenVisible(object app) => F<object>(app, "State")?.ToString() == "OpenVisible";
        private static string AppTitle(object app)
        {
            object def = F<object>(app, "Definition");
            string t = def != null ? AccessTools.Field(def.GetType(), "title")?.GetValue(def) as string : null;
            return string.IsNullOrWhiteSpace(t) ? "App" : t;
        }

        private string ViewName(KolkataOsController os)
        {
            if (VipRoot() != null) return "Verify Discord VIP window";
            if (viewIsCall) return "Incoming call";
            return viewApp == null ? "Desktop" : AppTitle(viewApp) + " window";
        }

        private void FollowActiveWindow(KolkataOsController os, bool force = false)
        {
            if (viewIsCall) return;
            object activeApp = Get(ActiveAppField, os);
            if (activeApp != null && !IsOpenVisible(activeApp)) activeApp = null;
            if (viewApp != null && !IsOpenVisible(viewApp)) { viewApp = activeApp; force = true; }
            // Only follow the game when a different window becomes active (an app opened, a call arrived).
            // If the player chose a view with Tab, stay there while the same window stays active.
            if (!ReferenceEquals(activeApp, lastSeenActive))
            {
                lastSeenActive = activeApp;
                if (activeApp != null && !ReferenceEquals(activeApp, viewApp)) { viewApp = activeApp; force = true; }
            }
            string sig = ViewSignature(os);
            if (force || sig != lastViewSignature)
            {
                VisualElement keep = index >= 0 && index < items.Count ? items[index].Element : null;
                Rebuild(os);
                int found = keep != null ? items.FindIndex(i => i.Element == keep) : -1;
                if (force)
                {
                    index = 0;
                    Speech.Say(ViewName(os) + ", " + items.Count + " items" + (items.Count > 0 ? ". " + items[0].Text : ""), true);
                }
                else index = found >= 0 ? found : Mathf.Clamp(index, 0, Math.Max(0, items.Count - 1));
            }
        }

        private string ViewSignature(KolkataOsController os)
        {
            // Cheap change detector: count of shown text elements and buttons in the view.
            VisualElement root = viewApp != null ? F<VisualElement>(viewApp, "WindowContent") : null;
            if (root == null) return "desktop:" + Apps(os).Count;
            int n = 0;
            root.Query<VisualElement>().ForEach(e => { if ((e is TextElement || e is TextField) && UiText.IsShown(e)) n++; });
            return root.GetHashCode() + ":" + n;
        }

        private void Rebuild(KolkataOsController os)
        {
            items = new List<Item>();
            VisualElement vipRoot = VipRoot();
            if (vipRoot != null)
            {
                Collect(vipRoot, items);
                lastViewSignature = ViewSignature(os);
                index = Mathf.Clamp(index, 0, Math.Max(0, items.Count - 1));
                return;
            }
            if (viewIsCall)
            {
                items.Add(new Item { Kind = ItemKind.Answer, Text = "Answer call from " + CallerName(os) + ", button" });
                items.Add(new Item { Kind = ItemKind.Decline, Text = "Decline call, button" });
            }
            else if (viewApp == null)
            {
                foreach (object app in Apps(os))
                {
                    if (!F<bool>(app, "IsAvailable") || F<bool>(app, "IsDesktopHidden")) continue;
                    Button b = F<Button>(app, "DesktopButton");
                    if (b == null || !UiText.IsShown(b)) continue;
                    string state = F<object>(app, "State")?.ToString();
                    string suffix = state == "OpenVisible" ? ", open" : state == "OpenMinimized" ? ", minimized" : "";
                    items.Add(new Item { Kind = ItemKind.App, App = app, Element = b, Text = AppTitle(app) + suffix });
                }
            }
            else
            {
                VisualElement root = F<VisualElement>(viewApp, "WindowContent") ?? F<VisualElement>(viewApp, "Window");
                // A Scamazon "are you sure" question covers the shop, so only it is listed while it is up.
                VisualElement confirm = root?.Q(null, "scamazon-confirmation-overlay");
                if (confirm != null && UiText.IsShown(confirm)) root = confirm;
                if (root != null) Collect(root, items);
            }
            // A call that is ringing can be answered from the desktop and from the Phone window too.
            if (!viewIsCall && IsRinging(os) && (viewApp == null || AppTitle(viewApp).IndexOf("phone", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                items.Insert(0, new Item { Kind = ItemKind.Answer, Text = "Incoming call. Answer call from " + CallerName(os) + ", button" });
                items.Insert(1, new Item { Kind = ItemKind.Decline, Text = "Decline call, button" });
            }
            lastViewSignature = ViewSignature(os);
            index = Mathf.Clamp(index, 0, Math.Max(0, items.Count - 1));
        }

        private static void Collect(VisualElement e, List<Item> list)
        {
            foreach (VisualElement child in e.Children())
            {
                if (!UiText.IsShown(child)) continue;
                if (child.ClassListContains("scamazon__product-card"))
                {
                    Item card = CardItem(child);
                    if (card != null) { list.Add(card); continue; }
                }
                if (child is Kolkata.IPGeolocation.TraceGlobe)
                {
                    list.Add(new Item { Kind = ItemKind.Element, Element = child, Text = "Globe. When the trace has finished, press Enter here, or G anywhere in this window, to place your guess in the middle of the highlighted area." });
                    continue;
                }
                if (child is Button || child is TextField || child is Toggle || child is DropdownField || child is Slider || child is SliderInt)
                {
                    string d = UiText.Describe(child);
                    if (d.Length > 0) list.Add(new Item { Kind = ItemKind.Element, Element = child, Text = d });
                    continue;
                }
                if (child is TextElement te)
                {
                    string s = UiText.Clean(te.text);
                    if (s.Length > 0 && !IsDuplicate(list, s)) list.Add(new Item { Kind = ItemKind.Element, Element = child, Text = s });
                }
                Collect(child, list);
            }
        }

        private static bool IsDuplicate(List<Item> list, string s) => list.Count > 0 && list[list.Count - 1].Text == s;

        // ---------- Movement ----------

        private void Move(KolkataOsController os, int step)
        {
            Rebuild(os);
            if (items.Count == 0) { Speech.Say(ViewName(os) + " is empty", true); return; }
            int next = index + step;
            if (next < 0 || next >= items.Count)
            {
                Speech.Say((next < 0 ? "Top. " : "Bottom. ") + items[index].Text, true);
                return;
            }
            index = next;
            Speech.Say(items[index].Text, true);
        }

        private void Jump(KolkataOsController os, int to)
        {
            Rebuild(os);
            if (items.Count == 0) return;
            index = Mathf.Clamp(to, 0, items.Count - 1);
            Speech.Say(items[index].Text, true);
        }

        private void SwitchView(KolkataOsController os, int step)
        {
            var views = new List<object> { null };
            foreach (object app in Apps(os)) if (IsOpenVisible(app)) views.Add(app);
            int cur = views.IndexOf(viewApp);
            int next = ((cur + step) % views.Count + views.Count) % views.Count;
            ShowView(os, views[next]);
        }

        // ---------- Activation ----------

        private void Activate(KolkataOsController os)
        {
            Rebuild(os);
            if (items.Count == 0) return;
            Item it = items[index];
            switch (it.Kind)
            {
                case ItemKind.Answer:
                    Invoke(AnswerCall, os);
                    viewIsCall = false;
                    Speech.Say("Answered. Talk to the caller with your microphone.", true);
                    return;
                case ItemKind.Decline:
                    Invoke(DeclineCall, os);
                    viewIsCall = false;
                    Speech.Say("Declined", true);
                    return;
                case ItemKind.App:
                    Invoke(DesktopIconClicked, os, it.App);
                    viewApp = it.App;
                    Rebuild(os);
                    index = 0;
                    Speech.Say("Opened " + AppTitle(it.App) + ", " + items.Count + " items" + (items.Count > 0 ? ". " + items[0].Text : ""), true);
                    return;
            }
            VisualElement el = it.Element;
            if (it.Card) { ActivateCard(it); return; }
            if (el is Kolkata.IPGeolocation.TraceGlobe) { GlobeGuess(os, true); return; }
            switch (el)
            {
                case TextField tf:
                    tf.Focus();
                    Speech.Say("Editing " + UiText.Describe(tf) + ". Type, then press Enter. Up or down arrow to leave.", true);
                    return;
                case Toggle t:
                    t.value = !t.value;
                    Speech.Say(UiText.Describe(t), true);
                    return;
                case DropdownField _:
                case Slider _:
                case SliderInt _:
                    Adjust(1);
                    return;
            }
            if (!el.enabledInHierarchy) { Speech.Say("Unavailable", true); return; }
            Click(el);
            Speech.Say("Pressed " + it.Text, true);
        }

        private void Adjust(int dir)
        {
            if (index < 0 || index >= items.Count) return;
            VisualElement el = items[index].Element;
            switch (el)
            {
                case DropdownField d when d.choices != null && d.choices.Count > 0:
                    d.index = ((d.index + dir) % d.choices.Count + d.choices.Count) % d.choices.Count;
                    break;
                case Slider s:
                    s.value = Mathf.Clamp(s.value + dir * Mathf.Max(0.01f, (s.highValue - s.lowValue) / 20f), s.lowValue, s.highValue);
                    break;
                case SliderInt si:
                    si.value = Mathf.Clamp(si.value + dir * Math.Max(1, (si.highValue - si.lowValue) / 20), si.lowValue, si.highValue);
                    break;
                default:
                    return;
            }
            items[index].Text = UiText.Describe(el);
            Speech.Say(items[index].Text, true);
        }

        // Buttons wired with "clicked" respond to a submit; everything else in the OS listens for ClickEvent.
        internal static void Click(VisualElement el)
        {
            if (el is Button b && b.clickable != null && (ClickedField?.GetValue(b.clickable) != null || ClickedInfoField?.GetValue(b.clickable) != null))
            {
                using (NavigationSubmitEvent e = NavigationSubmitEvent.GetPooled())
                {
                    e.target = b;
                    b.SendEvent(e);
                }
                return;
            }
            using (ClickEvent e = ClickEvent.GetPooled())
            {
                e.target = el;
                el.SendEvent(e);
            }
        }

        private static bool IsEditingText(KolkataOsController os)
        {
            UIDocument doc = os.GetComponentInParent<UIDocument>() ?? os.GetComponentInChildren<UIDocument>(true);
            Focusable f = doc?.rootVisualElement?.panel?.focusController?.focusedElement;
            for (VisualElement v = f as VisualElement; v != null; v = v.parent)
                if (v is TextField) return true;
            return false;
        }

        private static void Blur(KolkataOsController os)
        {
            UIDocument doc = os.GetComponentInParent<UIDocument>() ?? os.GetComponentInChildren<UIDocument>(true);
            (doc?.rootVisualElement?.panel?.focusController?.focusedElement as VisualElement)?.Blur();
        }

        private static void Invoke(MethodInfo m, object target, params object[] args)
        {
            if (m == null) { Plugin.Log.LogWarning("Missing OS method"); return; }
            try { m.Invoke(target, args.Length == 0 ? null : args); }
            catch (Exception e) { Plugin.Log.LogWarning("OS call " + m.Name + " failed: " + (e.InnerException ?? e).Message); }
        }
    }
}
