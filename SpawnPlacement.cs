using System.Linq;
using Alta.Chunks;
using Alta.Console.Commands;
using Alta.Networking;
using HarmonyLib;
using UnityEngine;

namespace LuckySpriggull
{
    /// <summary>
    /// "spawn &lt;player&gt; 21251" puts things at head height 2 m in front of the player, facing world north.
    /// For the Slot Machine, drop it onto the ground there and turn its front towards the nearest player instead.
    /// </summary>
    [HarmonyPatch(typeof(SpawnCommandModule), nameof(SpawnCommandModule.Spawn),
        new[] { typeof(NetworkPrefab), typeof(int), typeof(Chunk), typeof(Vector3), typeof(Quaternion), typeof(string[]) })]
    internal static class SpawnPlacement
    {
        private static void Prefix(NetworkPrefab prefab, ref Vector3 position, ref Quaternion rotation)
        {
            if (prefab == null || prefab.Hash != SlotMachine.PrefabHash) return;

            // Onto the ground (or whatever is below: a floor, a table).
            RaycastHit[] hits = Physics.RaycastAll(position + Vector3.up * 0.5f, Vector3.down, 8f, ~0, QueryTriggerInteraction.Ignore);
            RaycastHit ground = hits.Where(h => h.collider.attachedRigidbody == null || h.collider.attachedRigidbody.isKinematic)
                                    .OrderBy(h => h.distance).FirstOrDefault();
            if (ground.collider != null) position = ground.point;

            // Face the closest player.
            Vector3 best = Vector3.zero;
            float bestDist = float.MaxValue;
            foreach (var ip in global::Player.AllPlayers)
            {
                PlayerController pc = (ip as global::Player)?.PlayerController;
                if (pc == null || pc.Head == null) continue;
                float d = (pc.Head.position - position).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = pc.Head.position; }
            }
            Vector3 toPlayer = best - position;
            toPlayer.y = 0f;
            if (bestDist < float.MaxValue && toPlayer.sqrMagnitude > 0.01f)
                rotation = Quaternion.LookRotation(toPlayer.normalized, Vector3.up);
            MelonLoader.MelonLogger.Msg($"[The Lucky Spriggull] Placing a slot machine at {position} ({(ground.collider != null ? "on " + ground.collider.name : "no ground found")}), front facing {rotation * Vector3.forward}.");
        }
    }
}
