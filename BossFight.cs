using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Metater;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ScamAccess
{
    // The day 16 boss fight with Mr Curry, by ear.
    // - A low beep comes from wherever he is; it speeds up while he charges.
    // - Warnings when he throws curry or charges, and whether it is aimed at you.
    // - Crackling from fires near you, and a spoken warning if you stand in one.
    // - His health every quarter, and yours when you get hurt.
    // - Punches during the fight always go towards him, and never land on a friend.
    // - Slash turns you to face him and says how far he is.
    internal static class BossFight
    {
        private static ConfigEntry<bool> autoAim, sounds;
        private static ConfigEntry<Key> whereKey;
        private static FieldInfo bossIdField, firesField, reachField;
        private static AudioClip beaconClip, warnClip, fireClip;

        private static bool announcedStart;
        private static BossBattleController.AttackPhase lastPhase;
        private static int lastQuarter = 4;
        private static float nextBeacon, nextFireSound, nextFireWarning, nextHurtSay, nextTooFar, nextPlayerScan;
        private static float lastMyHealth = -1f;
        private static PlayerHealth[] players = new PlayerHealth[0];

        public static void Init(ConfigFile cfg, Harmony harmony)
        {
            autoAim = cfg.Bind("Boss", "AutoAim", true, "During the boss fight, punches always go towards Mr Curry and never hit a friend.");
            sounds = cfg.Bind("Boss", "Sounds", true, "Beeps and warnings during the boss fight.");
            whereKey = cfg.Bind("Keys", "FaceBoss", Key.Slash, "During the boss fight, turn to face Mr Curry and say how far he is.");

            bossIdField = AccessTools.Field(typeof(BossBattleController), "bossNetworkId");
            firesField = AccessTools.Field(typeof(BossBattleController), "arenaFires");
            reachField = AccessTools.Field(typeof(RaycastWeapon), "maxDistance");

            beaconClip = Tone("ScamAccessBoss", 0.16f, 196f, 196f, 14f, false);
            warnClip = Tone("ScamAccessBossWarn", 0.35f, 500f, 1400f, 4f, false);
            fireClip = Tone("ScamAccessFire", 0.12f, 300f, 300f, 20f, true);

            try
            {
                MethodInfo fire = AccessTools.Method(typeof(RaycastWeapon), "FireRayFromRay");
                if (fire != null) harmony.Patch(fire, prefix: new HarmonyMethod(typeof(BossFight), nameof(BeforePunch)));
                else Plugin.Log.LogWarning("Boss fight: punch method not found, auto aim is off");
                MethodInfo windup = AccessTools.Method(typeof(BossBattleController), "UserCode_RpcCurryThrowWindup__UInt32");
                if (windup != null) harmony.Patch(windup, postfix: new HarmonyMethod(typeof(BossFight), nameof(AfterCurryWindup)));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Boss fight patch failed: " + e); }
        }

        // ---------- State ----------

        private static Transform BossTransform(BossBattleController b)
        {
            if (b == null || bossIdField == null) return null;
            uint id = (uint)bossIdField.GetValue(b);
            return id != 0 && NetworkClient.spawned.TryGetValue(id, out NetworkIdentity ni) && ni != null ? ni.transform : null;
        }

        private static bool Fighting(out BossBattleController b, out Transform boss)
        {
            b = BossBattleController.Instance;
            boss = null;
            if (b == null || !b.IsActive || b.IsIntro || b.Phase == BossBattleController.AttackPhase.Defeated) return false;
            boss = BossTransform(b);
            return boss != null;
        }

        private static Transform Body()
        {
            MetaPlayerController c = MetaPlayerController.Instance;
            if (c == null) return null;
            return c.polarTransform != null ? c.polarTransform : c.transform;
        }

        private static Vector3 Chest(Transform boss) => boss.position + Vector3.up * 1.2f;

        private static PlayerHealth Me()
        {
            foreach (PlayerHealth p in players) if (p != null && p.isLocalPlayer) return p;
            return null;
        }

        // The boss always aims at the player standing closest to him.
        private static PlayerHealth BossTarget(Transform boss)
        {
            PlayerHealth best = null;
            float bestD = float.MaxValue;
            foreach (PlayerHealth p in players)
            {
                if (p == null || p.CurrentHealth <= 0f) continue;
                PlayerRagdollManager r = p.GetComponent<PlayerRagdollManager>();
                if (r != null && r.IsRagdollActive) continue;
                float d = (p.transform.position - boss.position).sqrMagnitude;
                if (d < bestD) { bestD = d; best = p; }
            }
            return best;
        }

        private static string NameOf(PlayerHealth p)
        {
            PlayerName n = p != null ? p.GetComponentInParent<PlayerName>() : null;
            return n != null && !string.IsNullOrWhiteSpace(n.playerName) ? n.playerName.Trim() : "a friend";
        }

        // ---------- Every frame ----------

        public static void Update(Keyboard kb)
        {
            BossBattleController b = BossBattleController.Instance;
            if (b == null || !b.IsActive) { Reset(); return; }
            BossBattleController.AttackPhase phase = b.Phase;
            float now = Time.unscaledTime;
            if (now >= nextPlayerScan)
            {
                nextPlayerScan = now + 1f;
                players = UnityEngine.Object.FindObjectsByType<PlayerHealth>(FindObjectsSortMode.None);
            }

            if (phase == BossBattleController.AttackPhase.Defeated)
            {
                if (lastPhase != phase) Speech.Say("Mr Curry is defeated! You won the boss fight.", true);
                lastPhase = phase;
                return;
            }
            if (b.IsIntro) { lastPhase = phase; return; }

            Transform boss = BossTransform(b);
            if (boss == null) return;

            if (!announcedStart)
            {
                announcedStart = true;
                lastQuarter = 4;
                Speech.Say("Boss fight with Mr Curry. The low beep is where he is. Semicolon punches him, the mod aims for you. Slash turns you to face him. Step sideways with A and D to dodge.", false);
            }

            AnnouncePhase(phase, boss);
            AnnounceHealth(b);
            if (sounds.Value) Sounds(b, boss, phase, now);
            Hurt(now);

            if (whereKey.Value != Key.None && kb[whereKey.Value].wasPressedThisFrame) FaceAndDescribe(b, boss);
        }

        private static void Reset()
        {
            announcedStart = false;
            lastQuarter = 4;
            lastMyHealth = -1f;
            lastPhase = BossBattleController.AttackPhase.Waiting;
        }

        private static void AnnouncePhase(BossBattleController.AttackPhase phase, Transform boss)
        {
            if (phase == lastPhase) return;
            lastPhase = phase;
            PlayerHealth target = BossTarget(boss);
            bool me = target != null && target.isLocalPlayer;
            switch (phase)
            {
                case BossBattleController.AttackPhase.MovingToCorner:
                    Speech.Say("He is running to a corner.", true);
                    break;
                case BossBattleController.AttackPhase.Charging:
                    Speech.Say(me ? "He is charging at you! Run sideways!" : "He is charging at " + NameOf(target) + ".", true);
                    PlayAt(boss.position, warnClip, 1f, 0.8f);
                    break;
                case BossBattleController.AttackPhase.FrontFlip:
                    Speech.Say("He does a front flip.", true);
                    break;
            }
        }

        private static void AnnounceHealth(BossBattleController b)
        {
            int quarter = Mathf.CeilToInt(b.Health01 * 4f);
            if (quarter >= lastQuarter) return;
            lastQuarter = quarter;
            if (quarter > 0) Speech.Say("Mr Curry health " + quarter * 25 + " percent.", false);
        }

        private static void Hurt(float now)
        {
            PlayerHealth me = Me();
            if (me == null) return;
            float h = me.CurrentHealth;
            if (lastMyHealth >= 0f && h < lastMyHealth - 0.5f && now >= nextHurtSay)
            {
                nextHurtSay = now + 2f;
                Speech.Say(h <= 0f ? "You are knocked out." : "Hurt. Your health " + Mathf.RoundToInt(me.Health01 * 100f) + " percent.", false);
            }
            lastMyHealth = h;
        }

        private static void Sounds(BossBattleController b, Transform boss, BossBattleController.AttackPhase phase, float now)
        {
            if (now >= nextBeacon)
            {
                bool charging = phase == BossBattleController.AttackPhase.Charging;
                nextBeacon = now + (charging ? 0.3f : 0.8f);
                PlayAt(Chest(boss), beaconClip, 0.9f, charging ? 1.3f : 1f);
            }

            Transform body = Body();
            if (body == null || firesField == null) return;
            if (!(firesField.GetValue(b) is IList<Vector3> fires) || fires.Count == 0) return;
            Vector3 pos = body.position;
            if (now >= nextFireSound)
            {
                nextFireSound = now + 0.45f;
                var near = new List<Vector3>();
                foreach (Vector3 f in fires) if ((f - pos).sqrMagnitude < 36f) near.Add(f);
                near.Sort((x, y) => (x - pos).sqrMagnitude.CompareTo((y - pos).sqrMagnitude));
                for (int i = 0; i < near.Count && i < 3; i++) PlayAt(near[i], fireClip, 0.6f, 1f);
            }
            if (now >= nextFireWarning)
            {
                foreach (Vector3 f in fires)
                {
                    if (!BossBattleController.IsWithinArenaFire(pos, f, 1.6f)) continue;
                    nextFireWarning = now + 2f;
                    Speech.Say("You are standing in fire! Move!", true);
                    break;
                }
            }
        }

        private static void AfterCurryWindup(BossBattleController __instance)
        {
            try
            {
                if (!Fighting(out _, out Transform boss)) return;
                PlayerHealth target = BossTarget(boss);
                if (sounds.Value) PlayAt(Chest(boss), warnClip, 1f, 1.2f);
                if (target != null && target.isLocalPlayer) Speech.Say("Curry at you! Step sideways!", true);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Boss curry warning failed: " + e.Message); }
        }

        // ---------- Facing ----------

        private static void Face(Transform boss)
        {
            Transform body = Body();
            if (body == null) return;
            Vector3 d = boss.position - body.position;
            d.y = 0f;
            if (d.sqrMagnitude < 0.01f) return;
            Vector3 e = body.eulerAngles;
            body.rotation = Quaternion.Euler(e.x, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, e.z);
            MetaPlayerController.Instance?.SetLookY(0f);
        }

        private static string Direction(Transform body, Vector3 at)
        {
            Vector3 d = at - body.position;
            d.y = 0f;
            float angle = Vector3.SignedAngle(Vector3.ProjectOnPlane(body.forward, Vector3.up), d, Vector3.up);
            if (Mathf.Abs(angle) <= 30f) return "ahead";
            if (Mathf.Abs(angle) >= 150f) return "behind you";
            return angle < 0f ? "to your left" : "to your right";
        }

        private static void FaceAndDescribe(BossBattleController b, Transform boss)
        {
            Transform body = Body();
            if (body == null) return;
            string was = Direction(body, boss.position);
            Face(boss);
            float dist = Vector3.Distance(body.position, boss.position);
            Speech.Say("Mr Curry was " + was + ", now facing him. " + Mathf.RoundToInt(dist) + " meters. Health " + Mathf.RoundToInt(b.Health01 * 100f) + " percent.", true);
        }

        // ---------- Punch aiming ----------

        private static readonly RaycastHit[] hits = new RaycastHit[16];

        private static bool BeforePunch(RaycastWeapon __instance, ref Ray ray)
        {
            try
            {
                if (!autoAim.Value || !__instance.isLocalPlayer) return true;
                if (!Fighting(out _, out Transform boss)) return true;

                Vector3 to = Chest(boss) - ray.origin;
                float dist = to.magnitude;
                if (dist < 0.01f) return true;
                ray = new Ray(ray.origin, to / dist);
                Face(boss);

                // Never land a punch on a friend: if another player is first in line, skip this punch.
                Transform myRoot = __instance.transform.root;
                int n = Physics.RaycastNonAlloc(ray, hits, dist, ~0, QueryTriggerInteraction.Ignore);
                float nearest = float.MaxValue;
                Transform first = null;
                for (int i = 0; i < n; i++)
                {
                    Transform root = hits[i].transform.root;
                    if (root == myRoot || hits[i].distance >= nearest) continue;
                    nearest = hits[i].distance;
                    first = root;
                }
                if (first != null && first != boss.root && first.GetComponentInChildren<PlayerHealth>() != null)
                {
                    Speech.Say("A friend is in the way.", true);
                    return false;
                }

                float reach = reachField != null ? (float)reachField.GetValue(__instance) : 3f;
                if (dist > reach + 0.5f && Time.unscaledTime >= nextTooFar)
                {
                    nextTooFar = Time.unscaledTime + 1.5f;
                    Speech.Say("Too far, " + Mathf.RoundToInt(dist) + " meters. Walk forward.", true);
                }
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Boss auto aim failed: " + e.Message);
                return true;
            }
        }

        // ---------- Sound ----------

        private static void PlayAt(Vector3 at, AudioClip clip, float volume, float pitch)
        {
            var go = new GameObject("ScamAccessBossSound");
            go.transform.position = at;
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = 2f;
            src.maxDistance = 45f;
            src.dopplerLevel = 0f;
            src.pitch = pitch;
            src.volume = volume;
            src.ignoreListenerPause = true;
            src.Play();
            UnityEngine.Object.Destroy(go, clip.length / Mathf.Max(0.1f, pitch) + 0.2f);
        }

        private static AudioClip Tone(string name, float seconds, float fromHz, float toHz, float decay, bool noisy)
        {
            int rate = 44100, n = (int)(rate * seconds);
            var data = new float[n];
            var rng = new System.Random(7);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / rate;
                float hz = Mathf.Lerp(fromHz, toHz, t / seconds);
                phase += 2 * Math.PI * hz / rate;
                float s = (float)(Math.Sin(phase) + 0.4 * Math.Sin(2 * phase));
                if (noisy) s = s * 0.2f + (float)(rng.NextDouble() * 2 - 1) * 0.8f;
                data[i] = s * Mathf.Exp(-decay * t) * 0.5f * Mathf.Min(1f, i / 100f);
            }
            AudioClip clip = AudioClip.Create(name, n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
