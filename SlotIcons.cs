using System;
using System.Collections.Generic;
using System.Linq;
using Alta.Networking;
using MelonLoader;
using UnityEngine;

namespace LuckySpriggull
{
    /// <summary>
    /// The reel symbols: tiny copies of the game's own creatures (their real meshes and materials), shrunk to fit a reel.
    /// The first in the list is the rarest: the reels show it for the biggest win.
    /// </summary>
    internal static class SlotIcons
    {
        /// <summary>
        /// Creatures to look for. The first four are the prize symbols, biggest prize first (three Spriggulls = the jackpot,
        /// three Goteras = smallest); the rest only appear as filler while spinning and on losses. Missing ones are skipped.
        /// </summary>
        private static readonly string[] Wanted = { "Spriggull", "Phantom", "Wyrm", "Gotera", "Turabada", "Babu", "Dais", "Schmeechee", "Firebug" };

        /// <summary>Which creature means which prize, biggest first.</summary>
        public static readonly string[] PrizeSymbols = { "Spriggull", "Phantom", "Wyrm", "Gotera" };   // it's The Lucky Spriggull: three Spriggulls is the jackpot

        /// <summary>The name of each symbol built, in order (e.g. "Phantom").</summary>
        public static List<string> Names { get; } = new List<string>();

        public const float Size = 0.075f;   // longest side of a symbol on the win line, in metres

        /// <summary>Builds one inactive template per symbol under <paramref name="holder"/>. Pads with plain coloured blocks if fewer than 4 creatures are found.</summary>
        public static List<GameObject> Build(Transform holder, Mesh cube, Material[] fallback, MelonLogger.Instance log)
        {
            var icons = new List<GameObject>();
            var names = new List<string>();
            foreach (string want in Wanted)
            {
                try
                {
                    NetworkPrefab creature = FindCreature(want);
                    if (creature == null) { log.Msg($"Reel symbols: no {want} found."); continue; }
                    GameObject icon = FromPrefab(creature, want, holder);
                    if (icon == null) { log.Msg($"Reel symbols: {creature.name} has no usable mesh."); continue; }
                    icons.Add(icon);
                    Names.Add(want);
                    names.Add($"{want} ({creature.name})");
                }
                catch (Exception e)
                {
                    log.Warning($"Reel symbols: couldn't copy {want}: {e.Message}");
                }
            }

            for (int i = 0; icons.Count < 4 && i < fallback.Length; i++)
            {
                var block = new GameObject("Block " + (i + 1));
                block.transform.SetParent(holder, false);
                var part = new GameObject("Block");
                part.transform.SetParent(block.transform, false);
                part.transform.localScale = new Vector3(Size, Size, 0.01f);
                part.AddComponent<MeshFilter>().sharedMesh = cube;
                part.AddComponent<MeshRenderer>().sharedMaterial = fallback[i];
                icons.Add(block);
                Names.Add("Block " + (i + 1));
                names.Add("coloured block");
            }
            log.Msg("Reel symbols (rarest first): " + string.Join(", ", names.ToArray()));
            return icons;
        }

        /// <summary>
        /// The symbol for a win of this size: 20x+ = Spriggull (the jackpot), 5x+ = Phantom, 3x+ = Wyrm, else Gotera.
        /// If that creature wasn't found, the next prize symbol that was.
        /// </summary>
        /// <summary>The prize creature's name for a win of this size (same tiers as <see cref="ForMultiplier"/>).</summary>
        public static string PrizeFor(int multiplier) =>
            PrizeSymbols[multiplier >= 20 ? 0 : multiplier >= 5 ? 1 : multiplier >= 3 ? 2 : 3];

        public static int ForMultiplier(int multiplier)
        {
            int tier = multiplier >= 20 ? 0 : multiplier >= 5 ? 1 : multiplier >= 3 ? 2 : 3;
            for (int t = tier; t < PrizeSymbols.Length; t++)
            {
                int i = Names.IndexOf(PrizeSymbols[t]);
                if (i >= 0) return i;
            }
            for (int t = tier - 1; t >= 0; t--)
            {
                int i = Names.IndexOf(PrizeSymbols[t]);
                if (i >= 0) return i;
            }
            return Math.Min(tier, Math.Max(Names.Count - 1, 0));
        }

        /// <summary>The creature itself (has AI), not its drops: "Spriggull" over "Spriggull Feather". Shortest name wins.</summary>
        private static NetworkPrefab FindCreature(string want) =>
            PrefabManager.AllPrefabs
                .Where(p => p != null && p.name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0
                            && p.GetComponentInChildren<Alta.Intelligence.Agent>(true) != null)
                .OrderBy(p => p.name.Length)
                .FirstOrDefault();

        /// <summary>Copies the creature's visible meshes (its first lower-detail level) into a small, centred model.</summary>
        /// <summary>Extra turn (degrees) for creatures whose model lies down: the Wyrm's body runs along its forward axis, so tip its head up.</summary>
        private static readonly Dictionary<string, Vector3> Tilt = new Dictionary<string, Vector3>
        {
            { "Wyrm", new Vector3(-90f, 0f, 0f) },
        };

        /// <summary>Seasonal extras some creatures carry (switched on only during events).</summary>
        private static readonly string[] Seasonal = { "ear", "bunny", "easter", "hat", "christmas", "xmas", "santa", "festive", "halloween", "pumpkin" };

        /// <summary>Parts to leave out of particular creatures.</summary>
        private static readonly Dictionary<string, string[]> Exclude = new Dictionary<string, string[]>
        {
            { "Gotera", new[] { "vine", "whip", "tendril" } },
            { "Wyrm", new[] { "groundrock", "rock", "dirt" } },   // the rocks round its burrow
        };

        /// <summary>
        /// Parts placed by the bone they are skinned to rather than where their object sits: the Turabada's eyebrows ride
        /// on its face bones, so their own objects' resting spot is off its face.
        /// </summary>
        private static readonly Dictionary<string, string[]> PlaceByBone = new Dictionary<string, string[]>
        {
            { "Turabada", new[] { "eyebrow" } },
        };

        /// <summary>True if this object and every parent up to the creature's root are switched on in the prefab.</summary>
        private static bool ShownByDefault(Transform t, Transform root)
        {
            for (; t != null && t != root; t = t.parent)
                if (!t.gameObject.activeSelf) return false;
            return true;
        }

        private static GameObject FromPrefab(NetworkPrefab prefab, string name, Transform holder)
        {
            var kept = new List<string>();
            var skipped = new List<string>();
            try { return FromPrefab(prefab, name, holder, kept, skipped); }
            finally
            {
                MelonLogger.Msg($"[The Lucky Spriggull] Reel symbol {name}: parts {string.Join(", ", kept.ToArray())}" +
                                (skipped.Count > 0 ? $"; left out {string.Join(", ", skipped.ToArray())}" : ""));
            }
        }

        private static GameObject FromPrefab(NetworkPrefab prefab, string name, Transform holder, List<string> kept, List<string> skipped)
        {
            Transform root = prefab.transform;
            IEnumerable<Renderer> renderers = prefab.GetComponentsInChildren<Renderer>(true);
            LODGroup lod = prefab.GetComponentInChildren<LODGroup>(true);
            if (lod != null && lod.GetLODs().Length > 0)
            {
                // The game's first lower-detail level: at reel size (about 7 cm) it looks the same as full detail for a
                // fraction of the triangles. Falls back to full detail if that level is empty.
                LOD[] lods = lod.GetLODs();
                int level = Math.Min(1, lods.Length - 1);
                if (!lods[level].renderers.Any(r => r != null)) level = 0;
                var best = new HashSet<Renderer>(lods[level].renderers.Where(r => r != null));
                var lodded = new HashSet<Renderer>(lod.GetLODs().SelectMany(l => l.renderers).Where(r => r != null));
                renderers = renderers.Where(r => best.Contains(r) || !lodded.Contains(r));
            }

            var icon = new GameObject(name + " Symbol");
            icon.transform.SetParent(holder, false);
            // Some creatures' models are stored lying down; "Pose" stands them up (it turns about the model's centre).
            var pose = new GameObject("Pose");
            pose.transform.SetParent(icon.transform, false);
            if (Tilt.TryGetValue(name, out Vector3 tilt)) pose.transform.localRotation = Quaternion.Euler(tilt);
            var model = new GameObject("Model");
            model.transform.SetParent(pose.transform, false);

            var bounds = new Bounds();
            bool any = false;
            foreach (Renderer r in renderers)
            {
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;   // no particles, trails, lines
                Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null || mesh.vertexCount == 0) continue;
                string n = r.gameObject.name.ToLowerInvariant();
                if (n.Contains("shadow") || n.Contains("collider") || n.Contains("hitbox")) continue;
                // Only what the creature normally shows: skip parts that are switched off (seasonal extras like the
                // Spriggull's Easter bunny ears), seasonal names, and anything listed for this creature (the Gotera's vine).
                if (!r.enabled || !ShownByDefault(r.transform, root)) { skipped.Add(r.gameObject.name + " (off)"); continue; }
                if (Seasonal.Any(s => n.Contains(s)) || (Exclude.TryGetValue(name, out string[] ex) && ex.Any(e => n.Contains(e))))
                { skipped.Add(r.gameObject.name); continue; }
                kept.Add(r.gameObject.name);

                Matrix4x4 m = root.worldToLocalMatrix * r.transform.localToWorldMatrix;   // the part's place on the creature
                if (r is SkinnedMeshRenderer skin && PlaceByBone.TryGetValue(name, out string[] byBone) && byBone.Any(b => n.Contains(b)))
                {
                    Transform[] bones = skin.bones;
                    Matrix4x4[] binds = mesh.bindposes;
                    int bone = Array.FindIndex(bones ?? new Transform[0], b => b != null);
                    if (bone >= 0 && bone < binds.Length)
                    {
                        Matrix4x4 boned = root.worldToLocalMatrix * bones[bone].localToWorldMatrix * binds[bone];
                        MelonLogger.Msg($"[The Lucky Spriggull] {name} {r.gameObject.name}: placed by bone {bones[bone].name} at {(Vector3)boned.GetColumn(3)} (its object is at {(Vector3)m.GetColumn(3)}).");
                        m = boned;
                    }
                    else MelonLogger.Msg($"[The Lucky Spriggull] {name} {r.gameObject.name}: skinned but no usable bone; left where its object is.");
                }
                else if (PlaceByBone.TryGetValue(name, out string[] byBone2) && byBone2.Any(b => n.Contains(b)))
                    MelonLogger.Msg($"[The Lucky Spriggull] {name} {r.gameObject.name}: a {r.GetType().Name} under {r.transform.parent?.name}, at {(Vector3)m.GetColumn(3)}.");
                var part = new GameObject(r.gameObject.name);
                part.transform.SetParent(model.transform, false);
                part.transform.localPosition = m.GetColumn(3);
                part.transform.localRotation = m.rotation;
                part.transform.localScale = m.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = part.AddComponent<MeshRenderer>();
                mr.sharedMaterials = r.sharedMaterials;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;

                // Grow the creature's bounds by this part's mesh bounds, in the creature's space.
                Bounds b = mesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 p = m.MultiplyPoint3x4(corner);
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                    else bounds.Encapsulate(p);
                }
            }
            if (!any) { UnityEngine.Object.DestroyImmediate(icon); return null; }

            // Shrink to fit, centre it, and turn it a little so it reads as 3D.
            float scale = Size / Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z, 0.001f);
            model.transform.localScale = Vector3.one * scale;
            model.transform.localPosition = -bounds.center * scale;
            icon.transform.localRotation = Quaternion.Euler(0f, -25f, 0f);
            return icon;
        }
    }
}
