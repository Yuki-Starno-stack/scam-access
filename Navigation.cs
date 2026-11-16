using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using Metater;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ScamAccess
{
    // Keyboard turning, wall proximity sounds, object beacons and a nearby-object finder.
    internal class Navigation
    {
        private readonly ConfigEntry<Key> turnLeftKey, turnRightKey, nextObjectKey, prevObjectKey, whereKey, levelKey;
        private readonly ConfigEntry<Key> wallToggleKey, beaconToggleKey, autoWalkKey, deskKey, startDoorKey;
        public Key AutoWalkKey => autoWalkKey.Value;

        private bool walking;
        private readonly List<Vector3> path = new List<Vector3>();
        private int pathIndex;
        private Vector3 progressPos;
        private float progressCheckAt;
        private readonly ConfigEntry<float> turnSpeed, wallRange, beaconRange, beaconVolume, wallVolume, walkBoost;
        private readonly ConfigEntry<bool> wallsOn, beaconsOn;

        private AudioSource wallSource;
        private AudioClip wallClip, beaconClip;
        private readonly float[] nextWallPing = new float[3];
        private Vector3 lastPos;
        private float lastYaw;
        private float nextBeacon;

        private List<Interactable> nearby = new List<Interactable>();
        private float nearbyBuiltAt = -100f;
        private int selected = -1;
        private Interactable selectedObject;
        private Interactable[] allInteractables = new Interactable[0];
        private float nextScan;

        public Navigation(ConfigFile cfg, GameObject host)
        {
            turnLeftKey = cfg.Bind("Keys", "TurnLeft", Key.LeftArrow, "Hold to turn left. Shift+key snaps 90 degrees.");
            turnRightKey = cfg.Bind("Keys", "TurnRight", Key.RightArrow, "Hold to turn right. Shift+key snaps 90 degrees.");
            nextObjectKey = cfg.Bind("Keys", "NextObject2", Key.RightBracket, "Select the next nearby object and face it.");
            prevObjectKey = cfg.Bind("Keys", "PreviousObject2", Key.LeftBracket, "Select the previous nearby object and face it.");
            startDoorKey = cfg.Bind("Keys", "GoToStartDoor", Key.N, "Sprint to the door that starts the work day.");
            deskKey = cfg.Bind("Keys", "GoToFreeDesk", Key.M, "Walk to the nearest free desk computer. Then press E to sit and use it.");
            autoWalkKey = cfg.Bind("Keys", "AutoWalk", Key.Backspace, "Walk to the selected object. Press again, or any movement key, to stop.");
            whereKey = cfg.Bind("Keys", "WhereIsSelected", Key.Backslash, "Say where the selected object is and face it again.");
            levelKey = cfg.Bind("Keys", "LevelView", Key.Home, "Look straight ahead (level the camera).");
            wallToggleKey = cfg.Bind("Keys", "ToggleWallSounds", Key.F3, "Turn wall sounds on or off.");
            beaconToggleKey = cfg.Bind("Keys", "ToggleBeacons", Key.F4, "Turn object beacons on or off.");
            turnSpeed = cfg.Bind("Navigation", "TurnSpeed", 120f, "Degrees per second while holding a turn key.");
            walkBoost = cfg.Bind("Navigation", "AutoWalkSpeed", 1.4f, "Speed multiplier on straight stretches of auto walk. 1 is normal sprinting. The game caps how high this can go.");
            wallRange = cfg.Bind("Navigation", "WallRange", 1.6f, "Metres at which walls start sounding.");
            beaconRange = cfg.Bind("Navigation", "BeaconRange", 10f, "Metres within which objects emit beacons.");
            beaconVolume = cfg.Bind("Navigation", "BeaconVolume", 0.6f, "Object beacon volume, 0 to 1.");
            wallVolume = cfg.Bind("Navigation", "WallVolume", 0.5f, "Wall sound volume, 0 to 1.");
            wallsOn = cfg.Bind("Navigation", "WallSounds", true, "Play wall sounds.");
            beaconsOn = cfg.Bind("Navigation", "Beacons", true, "Play object beacons.");

            wallSource = host.AddComponent<AudioSource>();
            wallSource.spatialBlend = 0f;
            wallSource.playOnAwake = false;
            wallSource.ignoreListenerPause = true;
            wallClip = Tone("ScamAccessWall", 0.05f, 520f, 60f, true);
            beaconClip = Tone("ScamAccessBeacon", 0.12f, 880f, 18f, false);
        }

        private static AudioClip Tone(string name, float seconds, float freq, float decay, bool noisy)
        {
            int rate = 44100, n = (int)(rate * seconds);
            var data = new float[n];
            var rng = new System.Random(1);
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                float s = Mathf.Sin(2f * Mathf.PI * freq * t) + 0.5f * Mathf.Sin(4f * Mathf.PI * freq * t);
                if (noisy) s = s * 0.6f + (float)(rng.NextDouble() * 2 - 1) * 0.4f;
                data[i] = s * Mathf.Exp(-decay * t) * 0.5f * Mathf.Min(1f, i / 80f);
            }
            AudioClip clip = AudioClip.Create(name, n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static bool InGameplay => Cursor.lockState == CursorLockMode.Locked && !OsNavigator.AtComputer;

        public void Update()
        {
            Keyboard kb = Keyboard.current;
            PlayerInteractor pi = PlayerInteractor.LocalPlayerInstance;
            if (kb == null || pi == null) return;

            if (kb[wallToggleKey.Value].wasPressedThisFrame)
            {
                wallsOn.Value = !wallsOn.Value;
                Speech.Say("Wall sounds " + (wallsOn.Value ? "on" : "off"), true);
            }
            if (kb[beaconToggleKey.Value].wasPressedThisFrame)
            {
                beaconsOn.Value = !beaconsOn.Value;
                Speech.Say("Object beacons " + (beaconsOn.Value ? "on" : "off"), true);
            }

            ScanInteractables();
            if (InGameplay)
            {
                HandleTurning(kb);
                if (kb[levelKey.Value].wasPressedThisFrame) LevelView();
                if (kb[nextObjectKey.Value].wasPressedThisFrame) Cycle(+1);
                if (kb[prevObjectKey.Value].wasPressedThisFrame) Cycle(-1);
                if (kb[whereKey.Value].wasPressedThisFrame) DescribeSelected(true);
                if (kb[autoWalkKey.Value].wasPressedThisFrame) { if (walking) StopWalking("Auto walk stopped"); else { ClearGoal(); StartWalking(pi); } }
                if (kb[deskKey.Value].wasPressedThisFrame) GoToFreeDesk(pi);
                if (kb[startDoorKey.Value].wasPressedThisFrame) GoToStartDoor(pi);
                if (walkAfterStandAt > 0f && Time.unscaledTime >= walkAfterStandAt)
                {
                    walkAfterStandAt = -1f;
                    if (pi.isSitting) Speech.Say("Still sitting. Press space to stand up, then Backspace again.", true);
                    else StartWalking(pi);
                }
                if (walking) WalkStep(kb, pi);
            }
            else if (walking) StopWalking(null);
            WatchDoor(pi);
            if (wallsOn.Value) Walls(pi);
            if (beaconsOn.Value) Beacons(pi);
        }

        // ---------- Auto walk ----------

        private float walkAfterStandAt = -1f;

        private void GoToFreeDesk(PlayerInteractor pi)
        {
            if (pi.isSitting)
            {
                foreach (MonitorInteractable m in UnityEngine.Object.FindObjectsByType<MonitorInteractable>(FindObjectsSortMode.None))
                {
                    ChairInteractable c = Patches.ChairOf(m);
                    if (c != null && ReferenceEquals(c.Networksitter, pi) && m.SupportsPhoneCalls) { Speech.Say("You are already at a work computer. Press E to use it.", true); return; }
                }
            }
            // Pick the free work computer with the shortest real walking route (same floor only).
            MonitorInteractable best = null;
            float bestDist = float.MaxValue;
            Vector3 from = pi.transform.position;
            var monitors = new List<MonitorInteractable>();
            var spots = new List<Vector3>();
            foreach (MonitorInteractable m in UnityEngine.Object.FindObjectsByType<MonitorInteractable>(FindObjectsSortMode.None))
            {
                if (m == null || !m.isActiveAndEnabled || m.UsesStandingInteraction || !m.SupportsPhoneCalls) continue;
                ChairInteractable chair = Patches.ChairOf(m);
                // Desks held by computer coworkers (NPCs) can be taken over, so only real players count as taken.
                if (chair != null && chair.Networksitter != null && !ReferenceEquals(chair.Networksitter, pi) && !(chair.Networksitter is NpcInteractor)) continue;
                Vector3 spot = chair != null ? chair.transform.position : m.transform.position;
                if (Mathf.Abs(spot.y - from.y) > 2.5f) continue;
                monitors.Add(m);
                spots.Add(spot);
            }
            int pick = BestReachable(from, spots, pi.transform.root, out bestDist);
            if (pick >= 0) best = monitors[pick];
            if (best == null)
            {
                // No work desks loaded here: point the player at the way into the office.
                Interactable door = null;
                float dd = float.MaxValue;
                foreach (Interactable it in allInteractables)
                {
                    if (!Useful(it)) continue;
                    string n = Plugin.ObjectName(it).ToLowerInvariant();
                    if (!(it is FloorEntranceDoor) && !n.Contains("floor entrance") && !n.Contains("elevator")) continue;
                    if (Mathf.Abs(it.transform.position.y - pi.transform.position.y) > 3f) continue;
                    float d2 = (it.transform.position - pi.transform.position).sqrMagnitude;
                    if (d2 < dd) { dd = d2; door = it; }
                }
                if (door == null) { Speech.Say("No free work computer here. Work desks are on the office floor.", true); return; }
                selectedObject = door;
                nearby = new List<Interactable> { door };
                selected = 0;
                nearbyBuiltAt = Time.unscaledTime;
                ClearGoal();
                arriveHint = "Hold E to go through, then press M again to find a work computer.";
                Speech.Say("No work computer on this floor. Taking you to " + Plugin.ObjectName(door), true);
                StartWalking(pi);
                return;
            }
            selectedObject = best;
            nearby = new List<Interactable> { best };
            selected = 0;
            nearbyBuiltAt = Time.unscaledTime;
            MonitorInteractable target = best;
            ChairInteractable seat = Patches.ChairOf(best);
            goalOverride = seat != null ? seat.transform.position : (Vector3?)null;
            arriveRadius = seat != null ? 1.3f : 2.2f;
            onArrive = p =>
            {
                Face(TargetPoint(target));
                if (target.CanInteract(p))
                {
                    target.LocalPredictInteraction();
                    target.ClientInteract(p);
                    Speech.Say("Sitting down at the work computer", true);
                }
                else Speech.Say("Arrived at the work computer. Press E to sit and use it.", true);
            };
            Plugin.Log.LogInfo("Desk finder: route " + (bestDist >= float.MaxValue / 2 ? "unknown" : Mathf.RoundToInt(bestDist) + " m"));
            StartWalking(pi);
        }

        // ---------- Day door ----------
        // Between days the door is a vote: holding it toggles "continue to the next day". Pressing N
        // twice used to take the vote back, and the closed door then blocked every walk to a desk.

        private Door watchDoor;
        private float watchReportAt, watchGiveUpAt;
        private bool watchReported, deskAfterDoor;

        private static bool ReviewVoteOpen(out bool voted, out string tally)
        {
            voted = false;
            tally = "";
            ScammerGameManager gm = ScammerGameManager.Instance;
            if (gm == null || !gm.CanVoteToSkipReview) return false;
            PlayerName me = PlayerName.LocalPlayerInstance;
            voted = me != null && gm.HasVotedToSkipReview(me.netId);
            tally = gm.ReviewSkipVoteCount + " of " + gm.EligibleReviewSkipPlayerCount + " players have voted";
            return true;
        }

        private void WatchDoor(PlayerInteractor pi)
        {
            if (watchDoor == null) return;
            float now = Time.unscaledTime;
            if (watchDoor.IsOpen)
            {
                watchDoor = null;
                bool go = deskAfterDoor && InGameplay;
                deskAfterDoor = false;
                if (go) { Speech.Say("The door is open. Going to a work computer.", true); GoToFreeDesk(pi); }
                else Speech.Say("The door is open. Press M to go to a work computer.", true);
                return;
            }
            if (!watchReported && now >= watchReportAt)
            {
                watchReported = true;
                if (ReviewVoteOpen(out bool voted, out string tally))
                {
                    if (voted) Speech.Say("Your vote to continue is in. " + tally + ". I'll tell you when the door opens.", true);
                    else { Speech.Say("The vote did not register. Press N to try again, and hold still until it finishes.", true); watchDoor = null; }
                }
                else if (!watchDoor.CanInteract(pi)) Speech.Say("Waiting for the door to open.", true);
                else { Speech.Say("The door did not open. Press N to try again, and hold still until it finishes.", true); watchDoor = null; }
            }
            if (watchDoor != null && now >= watchGiveUpAt) { watchDoor = null; deskAfterDoor = false; }
        }

        private void GoToStartDoor(PlayerInteractor pi)
        {
            if (ReviewVoteOpen(out bool already, out string count) && already)
            {
                Speech.Say("You already voted to continue. " + count + ". The door opens when everyone has voted.", true);
                return;
            }
            var doors = new List<Door>();
            var spots = new List<Vector3>();
            foreach (Door d in UnityEngine.Object.FindObjectsByType<Door>(FindObjectsSortMode.None))
            {
                if (d == null || !d.isActiveAndEnabled || !d.startsGame || d.IsOpen) continue;
                doors.Add(d);
                spots.Add(TargetPoint(d));
            }
            int pick = BestReachable(pi.transform.position, spots, pi.transform.root, out float bestDist);
            if (pick < 0) { Speech.Say("No closed start door here. The day may already be running. Press M to find a work computer.", true); return; }
            Door best = doors[pick];
            selectedObject = best;
            nearby = new List<Interactable> { best };
            selected = 0;
            nearbyBuiltAt = Time.unscaledTime;
            ClearGoal();
            onArrive = p =>
            {
                Face(TargetPoint(best));
                if (best.IsOpen) { Speech.Say("The door is already open. Press M to find a work computer.", true); return; }
                if (!best.CanInteract(p)) { Speech.Say("Arrived at the door, but it can't be opened right now. The day may already be running, or only the host can start it.", true); return; }
                bool vote = ReviewVoteOpen(out _, out _);
                best.CmdInteract(p); // completes after its one second hold, as long as the player keeps facing it
                watchDoor = best;
                watchReported = false;
                watchReportAt = Time.unscaledTime + 2.5f;
                watchGiveUpAt = Time.unscaledTime + 120f;
                Speech.Say(vote ? "Hold still. Voting to continue to the next day." : "Hold still. Opening the door to start the day.", true);
            };
            Plugin.Log.LogInfo("Start door: " + doors.Count + " candidates, route " + (bestDist < 1e6f ? Mathf.RoundToInt(bestDist) + " m" : "unknown"));
            StartWalking(pi);
        }

        private string arriveHint;
        private Vector3? goalOverride;
        private float arriveRadius = 2.2f;
        private Action<PlayerInteractor> onArrive;
        private bool sprintWas;

        private void ClearGoal()
        {
            goalOverride = null;
            arriveRadius = 2.2f;
            onArrive = null;
            arriveHint = null;
        }

        private Vector3 Goal() => goalOverride ?? TargetPoint(selectedObject);

        // Of several candidate spots, return the index of the one with the shortest planned route.
        // Only the five nearest in a straight line are planned, to keep it quick.
        private static int BestReachable(Vector3 from, List<Vector3> spots, Transform ignoreRoot, out float routeLen)
        {
            routeLen = float.MaxValue;
            SyncPlayerRadius();
            var order = new List<int>();
            for (int i = 0; i < spots.Count; i++) order.Add(i);
            order.Sort((a, b) => Flat(from, spots[a]).CompareTo(Flat(from, spots[b])));
            int best = order.Count > 0 ? order[0] : -1;
            for (int n = 0; n < order.Count && n < 5; n++)
            {
                List<Vector3> route = null;
                float len = float.MaxValue;
                try { route = Pathfinder.FindPath(from, spots[order[n]], ignoreRoot, out len); } catch { }
                if (route != null && len < routeLen) { routeLen = len; best = order[n]; }
            }
            return best;
        }

        // Walking distance along the navmesh; unreachable targets get a large penalty so reachable ones win.
        private static float RouteLength(Vector3 from, Vector3 to)
        {
            var p = new UnityEngine.AI.NavMeshPath();
            if (UnityEngine.AI.NavMesh.SamplePosition(from, out var a, 2f, ~0) && UnityEngine.AI.NavMesh.SamplePosition(to, out var b, 2f, ~0)
                && UnityEngine.AI.NavMesh.CalculatePath(a.position, b.position, ~0, p) && p.status == UnityEngine.AI.NavMeshPathStatus.PathComplete)
            {
                float len = 0f;
                for (int i = 1; i < p.corners.Length; i++) len += Vector3.Distance(p.corners[i - 1], p.corners[i]);
                return len;
            }
            return 1000f + Vector3.Distance(from, to);
        }

        private static bool StandUp(PlayerInteractor pi)
        {
            foreach (ChairInteractable chair in UnityEngine.Object.FindObjectsByType<ChairInteractable>(FindObjectsSortMode.None))
            {
                if (chair == null || !ReferenceEquals(chair.Networksitter, pi)) continue;
                var cmd = HarmonyLib.AccessTools.Method(typeof(ChairInteractable), "CmdStandUp");
                try { cmd?.Invoke(chair, new object[] { null }); return true; }
                catch (Exception e) { Plugin.Log.LogWarning("Stand up failed: " + (e.InnerException ?? e).Message); }
            }
            return false;
        }

        private void StartWalking(PlayerInteractor pi)
        {
            if (selectedObject == null || !Useful(selectedObject)) { Speech.Say("No object selected. Press right bracket to pick one.", true); return; }
            if (pi.isSitting)
            {
                if (StandUp(pi))
                {
                    Speech.Say("Standing up", true);
                    walkAfterStandAt = Time.unscaledTime + 1.2f;
                }
                else Speech.Say("You are sitting. Press space to stand up first.", true);
                return;
            }
            Vector3 target = Goal();
            Vector3 from = pi.transform.position;
            MetaPlayerController mpc = MetaPlayerController.Instance;
            if (mpc != null) { sprintWas = mpc.isSprinting; mpc.isSprinting = true; }
            Plan(pi, target);
            pathIndex = 0;
            walking = true;
            doorsTried.Clear();
            doorWaitUntil = 0f;
            unstuckTries = 0;
            totalStuck = 0;
            sidestepPending = false;
            sidestepUntil = 0f;
            nextShortcutCheck = 0f;
            progressPos = from;
            progressCheckAt = Time.unscaledTime + 1f;
            Speech.Say("Walking to " + Plugin.ObjectName(selectedObject), true);
        }

        private void StopWalking(string message)
        {
            walking = false;
            MetaPlayerController c = MetaPlayerController.Instance;
            if (c != null) { c.movementVector = Vector2.zero; c.isSprinting = sprintWas; }
            if (message != null) Speech.Say(message, true);
        }

        private void WalkStep(Keyboard kb, PlayerInteractor pi)
        {
            MetaPlayerController c = MetaPlayerController.Instance;
            if (c == null || selectedObject == null || !Useful(selectedObject)) { StopWalking("Auto walk stopped"); return; }
            if (kb.wKey.isPressed || kb.sKey.isPressed || kb.aKey.isPressed || kb.dKey.isPressed || kb.upArrowKey.isPressed || kb.downArrowKey.isPressed)
            {
                StopWalking("Auto walk stopped");
                return;
            }
            Vector3 pos = pi.transform.position;
            Vector3 target = Goal();
            if (Flat(pos, target) < arriveRadius)
            {
                Arrive(target);
                return;
            }
            c.isSprinting = true;
            float now = Time.unscaledTime;
            if (now < doorWaitUntil) { c.movementVector = Vector2.zero; HoldProgress(pos); return; }
            if (TryOpenDoorAhead(pi, target)) { c.movementVector = Vector2.zero; HoldProgress(pos); return; }
            // Sidestep for a moment to get around something, then re-plan.
            if (now < sidestepUntil)
            {
                c.movementVector = sidestepMove;
                return;
            }
            if (sidestepPending)
            {
                sidestepPending = false;
                Replan(pi);
                HoldProgress(pos);
            }
            while (pathIndex < path.Count - 1 && Flat(pos, path[pathIndex]) < 0.45f) pathIndex++;
            // Skip ahead whenever a later waypoint can be reached in a straight line.
            if (now >= nextShortcutCheck && path.Count > 0)
            {
                nextShortcutCheck = now + 0.25f;
                for (int k = Mathf.Min(path.Count - 1, pathIndex + 3); k > pathIndex; k--)
                {
                    if (Pathfinder.Clear(new Vector3(pos.x, path[k].y, pos.z), path[k], pi.transform.root)) { pathIndex = k; break; }
                }
            }
            Vector3 next = pathIndex < path.Count ? path[pathIndex] : target;
            Transform body = Body();
            Vector3 dir = Vector3.ProjectOnPlane(next - pos, Vector3.up);
            float off = 0f;
            if (body != null && dir.sqrMagnitude > 0.01f)
            {
                float want = Quaternion.LookRotation(dir).eulerAngles.y;
                SetYaw(body, Mathf.MoveTowardsAngle(body.eulerAngles.y, want, 540f * Time.deltaTime));
                off = Mathf.Abs(Mathf.DeltaAngle(body.eulerAngles.y, want));
            }
            // Turn on the spot for sharp corners instead of swinging wide into furniture.
            if (off > 60f) { c.movementVector = Vector2.zero; HoldProgress(pos); return; }
            c.movementVector = new Vector2(0f, 1f);
            if (off < 20f && Flat(pos, next) > 1.5f && walkBoost.Value > 1.01f) c.EnqueueAdditionalSpeed(walkBoost.Value);
            if (now >= progressCheckAt)
            {
                float moved = Flat(pos, progressPos);
                if (moved < 0.15f)
                {
                    if (Flat(pos, target) < arriveRadius + 0.8f) { Arrive(target); return; }
                    unstuckTries++;
                    totalStuck++;
                    if (unstuckTries > 6 || totalStuck > 14)
                    {
                        StopWalking("Blocked. " + Plugin.ObjectName(selectedObject) + " is " + Mathf.RoundToInt(Flat(pos, target)) + " meters " + Relative(Body(), target));
                        return;
                    }
                    Unstick(pi, pos, body);
                    return;
                }
                if (moved > 1f) unstuckTries = 0;
                HoldProgress(pos);
            }
        }

        private void HoldProgress(Vector3 pos)
        {
            progressPos = pos;
            progressCheckAt = Time.unscaledTime + 0.6f;
        }

        // First just plan a fresh route from where we are. If that does not help, step toward the
        // side that has room, and back away a little on later tries.
        private void Unstick(PlayerInteractor pi, Vector3 pos, Transform body)
        {
            float now = Time.unscaledTime;
            if (unstuckTries == 1)
            {
                Plugin.Log.LogInfo("Auto walk: stuck, planning a new route");
                Replan(pi);
                HoldProgress(pos);
                return;
            }
            Vector3 fwd = body != null ? Vector3.ProjectOnPlane(body.forward, Vector3.up).normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            float y = path.Count > 0 ? path[Mathf.Min(pathIndex, path.Count - 1)].y : pos.y;
            Vector3 feet = new Vector3(pos.x, y, pos.z);
            bool rightClear = Pathfinder.Clear(feet, feet + right * 0.9f, pi.transform.root);
            bool leftClear = Pathfinder.Clear(feet, feet - right * 0.9f, pi.transform.root);
            float side = rightClear == leftClear ? (unstuckTries % 2 == 0 ? 1f : -1f) : (rightClear ? 1f : -1f);
            sidestepMove = new Vector2(side, unstuckTries >= 4 ? -0.8f : 0.2f);
            sidestepUntil = now + 0.4f;
            sidestepPending = true;
            progressPos = pos;
            progressCheckAt = now + 1f;
            Plugin.Log.LogInfo("Auto walk: stuck, sidestepping " + (side > 0 ? "right" : "left") + (unstuckTries >= 4 ? " and back" : ""));
        }

        private float doorWaitUntil, nextDoorCheck;
        private readonly HashSet<Interactable> doorsTried = new HashSet<Interactable>();

        // Open closed doors that are in the way. Never opens the start-of-day door unless it is the destination.
        private bool TryOpenDoorAhead(PlayerInteractor pi, Vector3 goal)
        {
            if (Time.unscaledTime < nextDoorCheck) return false;
            nextDoorCheck = Time.unscaledTime + 0.3f;
            Vector3 pos = pi.transform.position;
            Transform body = Body();
            Vector3 fwd = body != null ? Vector3.ProjectOnPlane(body.forward, Vector3.up).normalized : Vector3.forward;
            foreach (Interactable it in allInteractables)
            {
                if (!(it is Door || it is FloorEntranceDoor) || it == null || !it.isActiveAndEnabled || doorsTried.Contains(it)) continue;
                if (it is Door sd && sd.startsGame && !sd.IsOpen && !ReferenceEquals(it, selectedObject))
                {
                    // The closed day door is in the way. Opening it starts the day or casts a vote,
                    // so that stays the player's choice: stop here and say what to press.
                    Vector3 gap = TargetPoint(it) - pos;
                    gap.y = 0f;
                    if (gap.magnitude > 2.4f || Vector3.Dot(gap.normalized, fwd) < 0.2f) continue;
                    deskAfterDoor = selectedObject is MonitorInteractable;
                    bool vote = ReviewVoteOpen(out bool voted, out string tally);
                    string say = vote && voted ? "The day door is closed and in the way. You already voted to continue, " + tally + ". I'll take you to the work computer when it opens."
                        : "The day door is closed and in the way. Press N to " + (vote ? "vote to continue" : "open it and start the day")
                            + (deskAfterDoor ? ". I'll take you to the work computer when it opens." : ".");
                    if (vote && voted) { watchDoor = sd; watchReported = true; watchGiveUpAt = Time.unscaledTime + 120f; }
                    Plugin.Log.LogInfo("Auto walk: stopped at closed day door");
                    StopWalking(say);
                    return true;
                }
                if (it is Door d && (d.IsOpen || (d.startsGame && !ReferenceEquals(it, selectedObject)))) continue;
                if (it is FloorEntranceDoor f && f.IsOpen) continue;
                Vector3 to = TargetPoint(it) - pos;
                to.y = 0f;
                if (to.magnitude > 1.8f || Vector3.Dot(to.normalized, fwd) < 0.3f) continue;
                if (!it.CanInteract(pi)) continue;
                doorsTried.Add(it);
                Face(TargetPoint(it));
                it.CmdInteract(pi);
                doorWaitUntil = Time.unscaledTime + 1.4f; // hold doors open after about a second
                Speech.Say("Opening door", true);
                Plugin.Log.LogInfo("Auto walk: opening " + it.name);
                return true;
            }
            return false;
        }

        private float sidestepUntil, nextShortcutCheck;
        private Vector2 sidestepMove;
        private bool sidestepPending;
        private int unstuckTries, totalStuck;

        private void Arrive(Vector3 target)
        {
            StopWalking(null);
            Action<PlayerInteractor> done = onArrive;
            string hint = arriveHint;
            ClearGoal();
            if (done != null)
            {
                try { done(PlayerInteractor.LocalPlayerInstance); }
                catch (Exception e) { Plugin.Log.LogWarning("Arrive action failed: " + e.Message); }
                return;
            }
            Face(TargetPoint(selectedObject));
            Speech.Say("Arrived. " + Plugin.ObjectName(selectedObject) + " is in front of you." + (hint != null ? " " + hint : ""), true);
        }

        // Route planning: our own grid search first (the office has no navmesh), then navmesh, then straight.
        private void Plan(PlayerInteractor pi, Vector3 target)
        {
            path.Clear();
            pathIndex = 0;
            SyncPlayerRadius();
            List<Vector3> route = null;
            try { route = Pathfinder.FindPath(pi.transform.position, target, pi.transform.root, out _); }
            catch (Exception e) { Plugin.Log.LogWarning("Pathfinder failed: " + e.Message); }
            if (route != null && route.Count > 0) { path.AddRange(route); return; }
            var navPath = new UnityEngine.AI.NavMeshPath();
            if (UnityEngine.AI.NavMesh.SamplePosition(pi.transform.position, out var a, 2f, ~0) && UnityEngine.AI.NavMesh.SamplePosition(target, out var b, 3f, ~0)
                && UnityEngine.AI.NavMesh.CalculatePath(a.position, b.position, ~0, navPath) && navPath.status == UnityEngine.AI.NavMeshPathStatus.PathComplete)
            {
                for (int i = 1; i < navPath.corners.Length; i++) path.Add(navPath.corners[i]);
                Plugin.Log.LogInfo("Auto walk: navmesh path with " + path.Count + " points");
                return;
            }
            path.Add(target);
            Plugin.Log.LogInfo("Auto walk: no route found, walking straight");
        }

        private static float loggedRadius = -1f;

        private static void SyncPlayerRadius()
        {
            MetaPlayerController c = MetaPlayerController.Instance;
            if (c == null || c.CharacterController == null) return;
            Pathfinder.PlayerRadius = c.CharacterController.radius;
            if (!Mathf.Approximately(loggedRadius, Pathfinder.PlayerRadius))
            {
                loggedRadius = Pathfinder.PlayerRadius;
                Plugin.Log.LogInfo("Auto walk: player radius " + loggedRadius.ToString("0.00") + " m");
            }
        }

        private void Replan(PlayerInteractor pi)
        {
            Plan(pi, Goal());
        }

        private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        // ---------- Turning ----------

        private static Transform Body()
        {
            MetaPlayerController c = MetaPlayerController.Instance;
            if (c == null) return null;
            return c.polarTransform != null ? c.polarTransform : c.transform;
        }

        private void HandleTurning(Keyboard kb)
        {
            Transform body = Body();
            if (body == null) return;
            bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            int dir = 0;
            if (shift)
            {
                if (kb[turnLeftKey.Value].wasPressedThisFrame) dir = -1;
                if (kb[turnRightKey.Value].wasPressedThisFrame) dir = 1;
                if (dir != 0)
                {
                    float yaw = Mathf.Round((body.eulerAngles.y + 90f * dir) / 90f) * 90f;
                    SetYaw(body, yaw);
                    Speech.Say(Compass(yaw), true);
                }
                return;
            }
            if (kb[turnLeftKey.Value].isPressed) dir -= 1;
            if (kb[turnRightKey.Value].isPressed) dir += 1;
            if (dir != 0) body.Rotate(Vector3.up * dir * turnSpeed.Value * Time.unscaledDeltaTime, Space.World);
            if (kb[turnLeftKey.Value].wasReleasedThisFrame || kb[turnRightKey.Value].wasReleasedThisFrame)
                Speech.Say(Compass(body.eulerAngles.y), true);
        }

        private static void SetYaw(Transform body, float yaw)
        {
            Vector3 e = body.eulerAngles;
            body.rotation = Quaternion.Euler(e.x, yaw, e.z);
        }

        private static string Compass(float yaw)
        {
            string[] names = { "north", "north east", "east", "south east", "south", "south west", "west", "north west" };
            int i = Mathf.RoundToInt(((yaw % 360f) + 360f) % 360f / 45f) % 8;
            return names[i];
        }

        private static void LevelView()
        {
            MetaPlayerController c = MetaPlayerController.Instance;
            if (c == null) return;
            c.SetLookY(0f);
            Speech.Say("Looking straight ahead", true);
        }

        // ---------- Walls ----------

        private void Walls(PlayerInteractor pi)
        {
            Transform body = Body();
            if (body == null || !InGameplay) return;
            Vector3 pos = body.position;
            float yaw = body.eulerAngles.y;
            bool moving = (pos - lastPos).sqrMagnitude > 0.0004f || Mathf.Abs(Mathf.DeltaAngle(yaw, lastYaw)) > 0.5f;
            lastPos = pos;
            lastYaw = yaw;
            if (!moving) return;

            Vector3 origin = pi.transform.position + Vector3.up * 0.9f;
            Vector3 fwd = Vector3.ProjectOnPlane(body.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3[] dirs = { fwd, -right, right };
            float[] pans = { 0f, -0.9f, 0.9f };
            float[] pitches = { 1.25f, 0.85f, 0.85f };
            Transform root = pi.transform.root;
            for (int i = 0; i < 3; i++)
            {
                float d = WallDistance(origin, dirs[i], root);
                if (d > wallRange.Value || Time.unscaledTime < nextWallPing[i]) continue;
                float closeness = 1f - d / wallRange.Value;
                wallSource.panStereo = pans[i];
                wallSource.pitch = pitches[i];
                wallSource.PlayOneShot(wallClip, wallVolume.Value * (0.35f + 0.65f * closeness));
                nextWallPing[i] = Time.unscaledTime + Mathf.Lerp(0.5f, 0.1f, closeness);
            }
        }

        private readonly RaycastHit[] hits = new RaycastHit[12];

        private float WallDistance(Vector3 origin, Vector3 dir, Transform playerRoot)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, hits, wallRange.Value, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (hits[i].collider == null || hits[i].collider.transform.root == playerRoot) continue;
                if (hits[i].collider.attachedRigidbody != null && !hits[i].collider.attachedRigidbody.isKinematic) continue;
                if (hits[i].distance < best) best = hits[i].distance;
            }
            return best;
        }

        // ---------- Objects ----------

        private void ScanInteractables()
        {
            if (Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 1f;
            allInteractables = UnityEngine.Object.FindObjectsByType<Interactable>(FindObjectsSortMode.None);
        }

        private static bool Useful(Interactable it)
        {
            if (it == null || !it.isActiveAndEnabled || !it.gameObject.activeInHierarchy) return false;
            if (it.IsDisplayModel) return false;
            if (it is PlayerInteractor) return false;
            if (it.GetComponentInParent<MetaPlayer>() != null) return false;
            return true;
        }

        private List<Interactable> Nearby(Vector3 pos, float range)
        {
            var list = new List<Interactable>();
            foreach (Interactable it in allInteractables)
            {
                if (!Useful(it)) continue;
                if ((it.transform.position - pos).sqrMagnitude <= range * range) list.Add(it);
            }
            list.Sort((a, b) => (a.transform.position - pos).sqrMagnitude.CompareTo((b.transform.position - pos).sqrMagnitude));
            return list;
        }

        private void Beacons(PlayerInteractor pi)
        {
            if (Time.unscaledTime < nextBeacon || !InGameplay) return;
            nextBeacon = Time.unscaledTime + 1.5f;
            Vector3 pos = pi.transform.position;
            List<Interactable> near = Nearby(pos, beaconRange.Value);
            int count = 0;
            foreach (Interactable it in near)
            {
                if (count >= 3) break;
                float d = Vector3.Distance(pos, it.transform.position);
                float vol = beaconVolume.Value * Mathf.Clamp01(1.15f - d / beaconRange.Value);
                Ping(it.transform.position, vol, it == selectedObject ? 1.5f : 1f);
                count++;
            }
            if (selectedObject != null && !near.Contains(selectedObject) && Useful(selectedObject))
                Ping(selectedObject.transform.position, beaconVolume.Value * 0.5f, 1.5f);
        }

        private void Ping(Vector3 at, float volume, float pitch)
        {
            var go = new GameObject("ScamAccessPing");
            go.transform.position = at;
            var src = go.AddComponent<AudioSource>();
            src.clip = beaconClip;
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = 1f;
            src.maxDistance = beaconRange.Value * 2.5f;
            src.dopplerLevel = 0f;
            src.pitch = pitch;
            src.volume = volume;
            src.Play();
            UnityEngine.Object.Destroy(go, 1f);
        }

        private void Cycle(int step)
        {
            PlayerInteractor pi = PlayerInteractor.LocalPlayerInstance;
            if (Time.unscaledTime - nearbyBuiltAt > 4f || nearby.Count == 0)
            {
                nearby = Nearby(pi.transform.position, 25f);
                nearbyBuiltAt = Time.unscaledTime;
                selected = step > 0 ? -1 : 0;
            }
            nearbyBuiltAt = Time.unscaledTime;
            if (nearby.Count == 0) { Speech.Say("Nothing nearby", true); return; }
            selected = ((selected + step) % nearby.Count + nearby.Count) % nearby.Count;
            selectedObject = nearby[selected];
            DescribeSelected(false);
        }

        private void DescribeSelected(bool reface)
        {
            PlayerInteractor pi = PlayerInteractor.LocalPlayerInstance;
            if (selectedObject == null || !Useful(selectedObject)) { Speech.Say("No object selected. Press right bracket to pick one.", true); return; }
            Vector3 target = TargetPoint(selectedObject);
            Vector3 from = pi.transform.position;
            float dist = Vector3.Distance(new Vector3(from.x, 0, from.z), new Vector3(target.x, 0, target.z));
            string where = Relative(Body(), target);
            Face(target);
            string name = Plugin.ObjectName(selectedObject);
            string reach = dist < 1.8f ? "within reach" : Mathf.RoundToInt(dist) + " meters";
            Speech.Say(name + ", " + reach + (reface ? ", " + where : ""), true);
        }

        private static Vector3 TargetPoint(Interactable it)
        {
            Collider c = it.GetComponentInChildren<Collider>();
            return c != null ? c.bounds.center : it.transform.position;
        }

        private static string Relative(Transform body, Vector3 target)
        {
            if (body == null) return "nearby";
            Vector3 to = Vector3.ProjectOnPlane(target - body.position, Vector3.up);
            float angle = Vector3.SignedAngle(Vector3.ProjectOnPlane(body.forward, Vector3.up), to, Vector3.up);
            float a = Mathf.Abs(angle);
            string side = angle < 0 ? "left" : "right";
            if (a < 20) return "ahead";
            if (a < 70) return "ahead " + side;
            if (a < 110) return "to your " + side;
            if (a < 160) return "behind " + side;
            return "behind you";
        }

        private static void Face(Vector3 target)
        {
            Transform body = Body();
            MetaPlayerController c = MetaPlayerController.Instance;
            if (body == null || c == null) return;
            Vector3 flat = Vector3.ProjectOnPlane(target - body.position, Vector3.up);
            if (flat.sqrMagnitude > 0.0001f) SetYaw(body, Quaternion.LookRotation(flat).eulerAngles.y);
            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 d = target - cam.transform.position;
                float horiz = new Vector2(d.x, d.z).magnitude;
                float down = Mathf.Atan2(-d.y, Mathf.Max(0.01f, horiz)) * Mathf.Rad2Deg;
                c.SetLookY(down);
            }
        }
    }
}
