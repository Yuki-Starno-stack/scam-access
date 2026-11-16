using System;
using System.Collections.Generic;
using UnityEngine;

namespace ScamAccess
{
    // The office has no navmesh, so plan routes ourselves: scan a grid of standing spots around
    // the player and the goal with physics checks, then run A* and straighten the result.
    internal static class Pathfinder
    {
        private const float Cell = 0.3f;
        private const float MinRadius = 0.2f;
        private const float Margin = 10f;
        private const int MaxCells = 220; // per side, keeps the scan cheap

        // The player's real collision radius, set by Navigation. Routes are planned with a little extra
        // room around it so the player does not catch on desk corners and door frames.
        public static float PlayerRadius = 0.3f;
        private static float Radius = MinRadius;

        private static readonly Collider[] overlap = new Collider[16];
        private static readonly RaycastHit[] hits = new RaycastHit[16];

        public static List<Vector3> FindPath(Vector3 start, Vector3 goal, Transform ignoreRoot, out float length)
        {
            // Plan with full clearance first; if nothing fits, fall back to a tight squeeze.
            float wide = Mathf.Clamp(PlayerRadius + 0.05f, MinRadius, 0.6f);
            List<Vector3> route = Search(start, goal, ignoreRoot, wide, out length);
            if (route == null && wide > MinRadius + 0.01f)
            {
                route = Search(start, goal, ignoreRoot, MinRadius, out length);
                if (route != null) Plugin.Log.LogInfo("Pathfinder: only a tight route fits");
            }
            return route;
        }

        private static List<Vector3> Search(Vector3 start, Vector3 goal, Transform ignoreRoot, float radius, out float length)
        {
            Radius = radius;
            length = float.MaxValue;
            float floorY = FloorUnder(start + Vector3.up, ignoreRoot) ?? start.y;
            start.y = floorY;
            Vector3 min = Vector3.Min(start, goal) - new Vector3(Margin, 0, Margin);
            Vector3 max = Vector3.Max(start, goal) + new Vector3(Margin, 0, Margin);
            int w = Mathf.Min(MaxCells, Mathf.CeilToInt((max.x - min.x) / Cell) + 1);
            int h = Mathf.Min(MaxCells, Mathf.CeilToInt((max.z - min.z) / Cell) + 1);
            // Re-centre if the box was clamped.
            Vector3 centre = (start + goal) * 0.5f;
            min = new Vector3(centre.x - w * Cell * 0.5f, floorY, centre.z - h * Cell * 0.5f);

            var open = new sbyte[w, h]; // 0 unknown, 1 free, -1 blocked
            Func<int, int, bool> free = (x, z) =>
            {
                if (x < 0 || z < 0 || x >= w || z >= h) return false;
                if (open[x, z] == 0) open[x, z] = (sbyte)(Standable(CellPos(min, x, z, floorY), ignoreRoot) ? 1 : -1);
                return open[x, z] == 1;
            };

            Vector2Int s = ToCell(min, start), g = ToCell(min, goal);
            if (!Nearest(free, ref s, 4) || !Nearest(free, ref g, 9)) return null;

            // A* over 8 neighbours.
            var cameFrom = new Dictionary<int, int>();
            var cost = new Dictionary<int, float> { [Key(s, w)] = 0f };
            var frontier = new SortedSet<(float f, int k)>();
            frontier.Add((Heur(s, g), Key(s, w)));
            int goalKey = Key(g, w);
            int expanded = 0;
            bool found = false;
            while (frontier.Count > 0 && expanded < 30000)
            {
                var cur = frontier.Min;
                frontier.Remove(cur);
                if (cur.k == goalKey) { found = true; break; }
                expanded++;
                int cx = cur.k % w, cz = cur.k / w;
                float baseCost = cost[cur.k];
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = cx + dx, nz = cz + dz;
                        if (!free(nx, nz)) continue;
                        if (dx != 0 && dz != 0 && (!free(cx + dx, cz) || !free(cx, cz + dz))) continue; // no corner cutting
                        int nk = nz * w + nx;
                        float nc = baseCost + (dx != 0 && dz != 0 ? 1.414f : 1f);
                        // Prefer the middle of corridors: cells touching an obstacle cost extra.
                        if (!free(nx + 1, nz) || !free(nx - 1, nz) || !free(nx, nz + 1) || !free(nx, nz - 1)) nc += 1.5f;
                        if (cost.TryGetValue(nk, out float old) && old <= nc) continue;
                        if (cost.ContainsKey(nk)) frontier.Remove((old + Heur(new Vector2Int(nx, nz), g), nk));
                        cost[nk] = nc;
                        cameFrom[nk] = cur.k;
                        frontier.Add((nc + Heur(new Vector2Int(nx, nz), g), nk));
                    }
            }
            if (!found) { Plugin.Log.LogInfo("Pathfinder: no route (" + expanded + " cells searched)"); return null; }

            var cells = new List<Vector3>();
            for (int k = goalKey; ; k = cameFrom[k])
            {
                cells.Add(CellPos(min, k % w, k / w, floorY));
                if (!cameFrom.ContainsKey(k)) break;
            }
            cells.Reverse();

            // Straighten: skip waypoints that have a clear line between them.
            var smooth = new List<Vector3>();
            int i = 0;
            Vector3 last = start;
            while (i < cells.Count)
            {
                int j = cells.Count - 1;
                while (j > i && !Clear(last, cells[j], ignoreRoot)) j--;
                smooth.Add(cells[j]);
                last = cells[j];
                i = j + 1;
            }
            length = 0f;
            Vector3 p = start;
            foreach (Vector3 q in smooth) { length += Vector3.Distance(p, q); p = q; }
            Plugin.Log.LogInfo("Pathfinder: route " + Mathf.RoundToInt(length) + " m, " + smooth.Count + " turns, " + expanded + " cells searched");
            return smooth;
        }

        public static float? FloorUnder(Vector3 from, Transform ignoreRoot)
        {
            int n = Physics.RaycastNonAlloc(from, Vector3.down, hits, 3f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            float? y = null;
            for (int i = 0; i < n; i++)
            {
                if (hits[i].collider == null || (ignoreRoot != null && hits[i].collider.transform.root == ignoreRoot)) continue;
                if (hits[i].distance < best) { best = hits[i].distance; y = hits[i].point.y; }
            }
            return y;
        }

        private static int Key(Vector2Int c, int w) => c.y * w + c.x;
        private static float Heur(Vector2Int a, Vector2Int b) => Vector2Int.Distance(a, b);
        private static Vector2Int ToCell(Vector3 min, Vector3 p) => new Vector2Int(Mathf.RoundToInt((p.x - min.x) / Cell), Mathf.RoundToInt((p.z - min.z) / Cell));
        private static Vector3 CellPos(Vector3 min, int x, int z, float y) => new Vector3(min.x + x * Cell, y, min.z + z * Cell);

        private static bool Nearest(Func<int, int, bool> free, ref Vector2Int c, int radius)
        {
            if (free(c.x, c.y)) return true;
            for (int r = 1; r <= radius; r++)
                for (int dx = -r; dx <= r; dx++)
                    for (int dz = -r; dz <= r; dz++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r) continue;
                        if (free(c.x + dx, c.y + dz)) { c = new Vector2Int(c.x + dx, c.y + dz); return true; }
                    }
            return false;
        }

        private static bool Blocking(Collider c, Transform ignoreRoot)
        {
            if (c == null || c.isTrigger) return false;
            if (ignoreRoot != null && c.transform.root == ignoreRoot) return false;
            // Doors can be opened on the way, so they don't count as walls.
            if (c.GetComponentInParent<Door>() != null || c.GetComponentInParent<FloorEntranceDoor>() != null) return false;
            Rigidbody rb = c.attachedRigidbody;
            if (rb != null && !rb.isKinematic && rb.mass < 20f) return false; // small loose props get pushed aside
            return true;
        }

        // A spot is standable if there is floor under it and nothing in the way at body height.
        public static bool Standable(Vector3 feet, Transform ignoreRoot)
        {
            int n = Physics.RaycastNonAlloc(feet + Vector3.up * 0.6f, Vector3.down, hits, 1.4f, ~0, QueryTriggerInteraction.Ignore);
            bool floor = false;
            for (int i = 0; i < n; i++) if (Blocking(hits[i].collider, ignoreRoot) || hits[i].collider.attachedRigidbody == null) { floor = true; break; }
            if (!floor) return false;
            int m = Physics.OverlapCapsuleNonAlloc(feet + Vector3.up * (0.35f + Radius), feet + Vector3.up * 1.6f, Radius, overlap, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < m; i++) if (Blocking(overlap[i], ignoreRoot)) return false;
            return true;
        }

        public static bool Clear(Vector3 a, Vector3 b, Transform ignoreRoot)
        {
            Vector3 dir = b - a;
            dir.y = 0f;
            float dist = dir.magnitude;
            if (dist < 0.01f) return true;
            Vector3 p1 = a + Vector3.up * (0.35f + Radius), p2 = a + Vector3.up * 1.6f;
            int n = Physics.CapsuleCastNonAlloc(p1, p2, Radius * 0.9f, dir / dist, hits, dist, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++) if (Blocking(hits[i].collider, ignoreRoot)) return false;
            return true;
        }
    }
}
