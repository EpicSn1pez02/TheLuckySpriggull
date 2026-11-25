using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Alta.Inventory;
using Alta.Networking;
using MelonLoader;
using UnityEngine;

namespace LuckySpriggull
{
    /// <summary>
    /// Gives the Slot Machine two item bubbles (PickupDocks), copied from the game's ATM:
    ///   "Coin In": players put coins in; the server takes them as the bet.
    ///   "Winnings": the server puts payouts here; players take them out. Nothing is ever dropped on the ground.
    /// Each dock lives on its own embedded NetworkEntity, wired up the same way the Pet mod builds its vendor stalls.
    /// Client and server run this identically, so the embedded hashes match.
    /// </summary>
    internal static class SlotDocks
    {
        public const string InName = "Slot Coin In";
        public const string OutName = "Slot Winnings";
        /// <summary>
        /// A hidden dock inside the cabinet that only the mod uses: the server sets its coin count to a code for each
        /// spin's result, and every game reads it (dock counts are synced to everyone). That way the reels can stop on
        /// the right symbols and celebrate *before* the winnings are paid.
        /// </summary>
        public const string SignalName = "Slot Result Signal";
        /// <summary>Where the bet's coins wait until the lever is pulled (hidden; its count is shown above the coin slot).</summary>
        public const string BetName = "Slot Bet";

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Add(GameObject root, NetworkPrefab prefab, MelonLogger.Instance log)
        {
            FindDonors(out PickupDock inDonor, out PickupDock outDonor, out string donorName);
            log.Msg($"Copying coin bubbles from {donorName}.");

            Item coin = CommonPrefabs.CoinPrefab.GetComponent<Pickup>()?.Item
                ?? throw new InvalidOperationException("Coin prefab has no item.");

            // The coin slot is the market stall's own (its buyer's coin slot), if the game has one; else the ATM's chute.
            PickupDock slotDonor = StallCoinSlot(out string stallName, log) ?? inDonor;
            bool fromStall = slotDonor != inDonor;
            log.Msg(fromStall ? $"Coin slot copied from the market stall ({stallName})." : "No market stall found; the coin slot is the ATM's chute.");

            var grafted = new List<GameObject>
            {
                // The slot holds the bet itself, so coins put in by mistake can be grabbed straight back out.
                Graft(root, slotDonor, InName, SlotMachine.InDockLocal, coin, isPayout: false, log, grabbable: true, keepDonorTurn: fromStall),
                Graft(root, outDonor, OutName, SlotMachine.OutDockLocal, coin, isPayout: true, log),
                Graft(root, inDonor, SignalName, SlotMachine.SignalLocal, coin, isPayout: false, log),
                Graft(root, outDonor, BetName, SlotMachine.BetLocal, coin, isPayout: false, log),   // unused now (the slot holds the bet); kept so the dock list stays the same
            };
            HideSignal(grafted[2]);
            HideSignal(grafted[3]);
            grafted[0].transform.localScale *= 0.55f;   // a small coin bubble, inside the coin box
            grafted[1].transform.localScale *= 0.7f;    // a smaller Winnings bubble, to fit the payout tray
            GameObject slotModel = fromStall ? CopyStallSlotModel(root, slotDonor, log) : null;
            if (slotModel != null && SitOnShelf(root, slotModel, log, true))
                foreach (string part in new[] { "Coin Slot", "Coin Slot Plate", "Coin Slot Surround" })
                {
                    Transform t = root.transform.Find("Visual/" + part);   // the stall's own slot replaces the machine's plain one
                    if (t != null) UnityEngine.Object.DestroyImmediate(t.gameObject);
                }
            foreach (TextRenderer t in grafted[0].GetComponentsInChildren<TextRenderer>(true))
                t.gameObject.SetActive(false);   // the machine shows the bet in its own brass numbers

            GameObject handle = GraftHandle(root, log);
            if (handle != null) grafted.Add(handle);
            Bind(root, prefab, grafted);
            foreach (GameObject g in grafted) log.Msg(Describe(g.transform, ""));
        }

        /// <summary>The coin slot buyers pay into on the game's market stall (TradeVendor.temporaryCoinDock), or null.</summary>
        private static PickupDock StallCoinSlot(out string stallName, MelonLogger.Instance log)
        {
            stallName = null;
            try
            {
                NetworkPrefab stall = PrefabManager.AllPrefabs.FirstOrDefault(p => p != null && p.GetComponentInChildren<Alta.Trading.TradeVendor>(true) != null);
                if (stall == null) return null;
                stallName = stall.name;
                var vendor = stall.GetComponentInChildren<Alta.Trading.TradeVendor>(true);
                log.Msg("Market stall layout:\n" + Describe(stall.transform, "  "));
                return Field<PickupDock>(vendor, "temporaryCoinDock") ?? Field<PickupDock>(vendor, "payments");
            }
            catch (Exception e) { log.Warning("Couldn't read the market stall: " + e.Message); return null; }
        }

        private const string StallPartPrefix = "Stall Slot ";

        /// <summary>
        /// The stall's coin slot model is part of the stall's body, which the game adds at spawn (AdditionalStaticContent),
        /// not part of its coin dock. Copies the body's coin-slot pieces (named coin/slot, else the small pieces right at
        /// the dock) onto the copied dock, in the same place relative to it. Returns how many pieces.
        /// </summary>
        private static GameObject CopyStallSlotModel(GameObject root, PickupDock dock, MelonLogger.Instance log)
        {
            try
            {
                Transform stall = dock.transform;
                while (stall.parent != null) stall = stall.parent;
                Component asc = stall.GetComponent("AdditionalStaticContent");
                if (!(Field<Array>(asc, "staticContent") is Array contents)) { log.Msg("Market stall: no body content to copy a coin slot from."); return null; }

                Matrix4x4 dockInStall = stall.worldToLocalMatrix * dock.transform.localToWorldMatrix;
                Vector3 dockAt = dockInStall.GetColumn(3);
                var parts = new List<KeyValuePair<MeshRenderer, Matrix4x4>>();
                var lines = new List<string>();
                foreach (object c in contents)
                {
                    var body = Field<GameObject>(c, "prefab");
                    if (body == null) continue;
                    Matrix4x4 place = Matrix4x4.TRS((Vector3)FindField(c.GetType(), "localPosition").GetValue(c),
                                                    (Quaternion)FindField(c.GetType(), "localRotation").GetValue(c),
                                                    (Vector3)FindField(c.GetType(), "localScale").GetValue(c));
                    foreach (MeshRenderer r in body.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        Mesh mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
                        if (mesh == null) continue;
                        string n = r.name.ToLowerInvariant();
                        if (n.Contains("_lod1") || n.Contains("_lod2") || n.Contains("_lod3")) continue;
                        Matrix4x4 m = place * body.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
                        Vector3 centre = m.MultiplyPoint3x4(mesh.bounds.center);
                        Vector3 size = Vector3.Scale(mesh.bounds.size, m.lossyScale);
                        lines.Add($"    {r.name} ({body.name}): {(centre - dockAt).magnitude * 100:0} cm from the coin dock, {size.x * 100:0}x{size.y * 100:0}x{size.z * 100:0} cm");
                        parts.Add(new KeyValuePair<MeshRenderer, Matrix4x4>(r, m));
                    }
                }
                log.Msg("Market stall body pieces:\n" + string.Join("\n", lines.ToArray()));

                // Only the gold slot ring (tradeDeck_coinSlot), not the dispenser or the rest of the stall.
                var chosen = parts.FirstOrDefault(kv => kv.Key.name.ToLowerInvariant().Contains("coinslot"));
                if (chosen.Key == null) { log.Msg("Market stall: no coinSlot piece found."); return null; }

                // Laid on the shelf with its long side across the machine, facing the player.
                var go = new GameObject(StallPartPrefix + chosen.Key.name);
                go.transform.SetParent(root.transform, false);
                Matrix4x4 pose = chosen.Value;
                go.transform.localRotation = Quaternion.Euler(0f, 90f, 0f) * pose.rotation;
                go.transform.localScale = pose.lossyScale;
                go.transform.localPosition = SlotMachine.SlotSeatLocal;
                go.AddComponent<MeshFilter>().sharedMesh = chosen.Key.GetComponent<MeshFilter>().sharedMesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = chosen.Key.sharedMaterials;
                log.Msg("Coin slot model from the stall: " + chosen.Key.name);
                return go;
            }
            catch (Exception e) { log.Warning("Couldn't copy the stall's coin slot model: " + e.Message); return null; }
        }

        /// <summary>Moves the copied slot so the bottom of its model rests on the shelf, centred where the slot goes.</summary>
        private static bool SitOnShelf(GameObject root, GameObject slot, MelonLogger.Instance log, bool slotModelOnly)
        {
            bool any = false;
            var b = new Bounds();
            foreach (MeshFilter mf in slot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || mf.GetComponent<TextRenderer>() != null) continue;
                if (slotModelOnly && !mf.name.StartsWith(StallPartPrefix)) continue;   // rest the slot's model on the shelf, not its bubble
                Renderer r = mf.GetComponent<Renderer>();
                if (r == null || !r.enabled) continue;
                Bounds mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 p = root.transform.InverseTransformPoint(mf.transform.TransformPoint(c));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            if (!any) { log.Msg("Coin slot: no model to rest on the shelf."); return false; }
            Vector3 want = SlotMachine.SlotSeatLocal;
            Vector3 offset = new Vector3(want.x - b.center.x, want.y - b.min.y, want.z - b.center.z);
            slot.transform.localPosition += offset;
            log.Msg($"Coin slot model {b.size.x * 100:0.#} x {b.size.y * 100:0.#} x {b.size.z * 100:0.#} cm, moved by ({offset.x * 100:0.#}, {offset.y * 100:0.#}, {offset.z * 100:0.#}) cm to sit on the shelf.");
            return true;
        }

        public const string HandleName = "Slot Lever Handle";

        /// <summary>
        /// A real grabbable handle for the lever: a copy of the game's own lever handle (the gacha machine's crank, else
        /// the market stall's lever, else any lever's). It stays put when grabbed (no joint to the hand), its own model is
        /// hidden (the machine's gold knob shows instead), and the machine turns the lever to follow the hand holding it.
        /// </summary>
        private static GameObject GraftHandle(GameObject root, MelonLogger.Instance log)
        {
            Transform pivot = root.transform.Find("Visual/Lever Pivot");
            if (pivot == null) { log.Warning("Lever: no pivot to put a handle on."); return null; }
            Pickup donor = null; string from = null;
            foreach (NetworkPrefab p in PrefabManager.AllPrefabs)
            {
                if (p == null) continue;
                Pickup h = Field<Pickup>(p.GetComponentInChildren<GachaHandle>(true), "targetPickup");
                if (h != null) { donor = h; from = "the gacha machine's handle (" + p.name + ")"; break; }
            }
            if (donor == null)
            {
                NetworkPrefab stall = PrefabManager.AllPrefabs.FirstOrDefault(p => p != null && p.GetComponentInChildren<Alta.Trading.TradeVendor>(true) != null);
                donor = Field<Pickup>(stall?.GetComponentInChildren<Alta.Trading.TradeVendor>(true), "leverPickup");
                if (donor != null) from = "the market stall's lever (" + stall.name + ")";
            }
            if (donor == null)
            {
                NetworkPrefab lever = PrefabManager.AllPrefabs.FirstOrDefault(p => p != null && p.GetComponentInChildren<Alta.Mechanisms.NonPhysicalLever>(true) != null);
                donor = Field<Pickup>(lever?.GetComponentInChildren<Alta.Mechanisms.NonPhysicalLever>(true), "handle");
                if (donor != null) from = "a lever (" + lever.name + ")";
            }
            if (donor == null) { log.Warning("Lever: found no game lever handle to copy; the lever follows a hand that reaches its knob instead."); return null; }

            try
            {
                GameObject copy = UnityEngine.Object.Instantiate(donor.gameObject, pivot, false);
                copy.name = HandleName;
                copy.SetActive(true);
                copy.transform.localPosition = new Vector3(0f, SlotMachine.LeverLength, 0f);
                copy.transform.localRotation = Quaternion.identity;
                copy.transform.localScale = Vector3.one;

                foreach (NetworkPrefab np in copy.GetComponentsInChildren<NetworkPrefab>(true)) UnityEngine.Object.DestroyImmediate(np);
                foreach (Joint j in copy.GetComponentsInChildren<Joint>(true)) UnityEngine.Object.DestroyImmediate(j);
                // Whatever drove the donor's handle (its lever or crank) stays behind; the machine drives this one.
                foreach (MonoBehaviour b in copy.GetComponentsInChildren<MonoBehaviour>(true))
                    if (b is GachaHandle || b is Trigger || b is NonJointLever || b is Alta.Mechanisms.NonPhysicalLever)
                        UnityEngine.Object.DestroyImmediate(b);
                if (copy.GetComponent<NetworkEntity>() == null) copy.AddComponent<NetworkEntity>();

                Pickup pickup = copy.GetComponent<Pickup>() ?? throw new InvalidOperationException("the copy lost its Pickup");
                Set(pickup, "jointType", PickUpJointType.EmptyJoint);   // grabbing it doesn't pull it to the hand
                Rigidbody rb = copy.GetComponent<Rigidbody>();
                if (rb != null) { rb.isKinematic = true; rb.useGravity = false; }

                foreach (Renderer r in copy.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                foreach (Collider c in copy.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                var grab = copy.AddComponent<SphereCollider>();   // grab the gold knob
                grab.radius = SlotMachine.KnobRadius * 1.6f;

                log.Msg($"Lever: grabbable handle copied from {from}.");
                return copy;
            }
            catch (Exception e)
            {
                log.Warning("Lever: couldn't copy a game handle (" + e.Message + "); the lever follows a hand that reaches its knob instead.");
                Transform left = pivot.Find(HandleName);
                if (left != null) UnityEngine.Object.DestroyImmediate(left.gameObject);
                return null;
            }
        }

        /// <summary>One line per object in the copied dock: name and component types. For the log.</summary>
        private static string Describe(Transform t, string indent)
        {
            string line = indent + t.name + ": " + string.Join(", ", t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name).ToArray());
            foreach (Transform child in t) line += "\n" + Describe(child, indent + "  ");
            return line;
        }

        /// <summary>Finds the docks in this machine's clone (template or spawned copy).</summary>
        public static void Find(Component machine, out PickupDock coinIn, out PickupDock winnings) =>
            Find(machine, out coinIn, out winnings, out _);

        public static void Find(Component machine, out PickupDock coinIn, out PickupDock winnings, out PickupDock signal) =>
            Find(machine, out coinIn, out winnings, out signal, out _);

        public static void Find(Component machine, out PickupDock coinIn, out PickupDock winnings, out PickupDock signal, out PickupDock bet)
        {
            coinIn = winnings = signal = bet = null;
            foreach (PickupDock d in machine.GetComponentsInChildren<PickupDock>(true))
            {
                // Spawned entities get renamed "<id> - <name>", so match the end of the name.
                if (d.gameObject.name.EndsWith(InName)) coinIn = d;
                else if (d.gameObject.name.EndsWith(OutName)) winnings = d;
                else if (d.gameObject.name.EndsWith(SignalName)) signal = d;
                else if (d.gameObject.name.EndsWith(BetName)) bet = d;
            }
        }

        /// <summary>Nobody can see, reach or fill the signal dock: no auto-docking, no colliders, no hand docking.</summary>
        private static void HideSignal(GameObject signal)
        {
            foreach (Behaviour b in signal.GetComponentsInChildren<Behaviour>(true))
                if (b.GetType().Name == "AutomaticDockArea") b.enabled = false;
            foreach (Collider c in signal.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (Renderer r in signal.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            PickupDock dock = signal.GetComponent<PickupDock>();
            Set(dock.Settings, "isHandDockingAllowed", false);
            Set(dock.Settings, "isDockingAutomatic", false);
        }

        /// <summary>Sets a dock's coin count, spawning the coin stack the first time. Returns null, or why it failed.</summary>
        public static string SetCount(PickupDock dock, int count)
        {
            if (dock.HasDockedPickup && dock.QuantityStored > 0)
            {
                dock.QuantityStored = count;
                return null;
            }
            NetworkEntity coin = SpawnHelper.SpawnUnchunked(CommonPrefabs.CoinPrefab, SpawnData.Default);
            if (coin == null || coin.CommonPickup == null) return "couldn't spawn a coin";
            PickupDock.AllowedResult r = coin.CommonPickup.DockInto(null, dock, count, isInstantMovementForced: true);
            if (r == PickupDock.AllowedResult.Ok) return null;
            coin.SceneDestroy();
            return "the dock refused the coins: " + r;
        }

        // ---------- donors ----------

        /// <summary>An opaque, coloured material from the ATM's model, to build the machine's look from.</summary>
        public static Material DonorMaterial()
        {
            NetworkPrefab atm = PrefabManager.AllPrefabs.FirstOrDefault(p => p != null && p.GetComponentInChildren<ATMMachine>(true) != null);
            if (atm == null) return null;
            return atm.GetComponentsInChildren<MeshRenderer>(true)
                .SelectMany(r => r.sharedMaterials)
                .Where(m => m != null && m.renderQueue < 2500 && (m.HasProperty("_Color") || m.HasProperty("_BaseColor")))
                .GroupBy(m => m)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
        }

        private static void FindDonors(out PickupDock inDonor, out PickupDock outDonor, out string donorName)
        {
            NetworkPrefab atm = PrefabManager.AllPrefabs.FirstOrDefault(p => p != null && p.GetComponentInChildren<ATMMachine>(true) != null);
            if (atm != null)
            {
                ATMMachine m = atm.GetComponentInChildren<ATMMachine>(true);
                inDonor = Field<PickupDock>(m, "depositDock");
                outDonor = Field<PickupDock>(m, "withdrawDock");
                if (inDonor != null && outDonor != null) { donorName = "the ATM (" + atm.name + ")"; return; }
            }

            // Fallback: the Repair Box's input dock (the Pet mod's vendor stalls use the same one).
            NetworkPrefab box = PrefabManager.AllPrefabs.FirstOrDefault(p => p != null && p.GetComponentInChildren<RepairBox>(true) != null);
            PickupDock dock = box != null ? Field<PickupDock>(box.GetComponentInChildren<RepairBox>(true), "dock1") : null;
            if (dock == null) throw new InvalidOperationException("Found no ATM or Repair Box dock to copy.");
            inDonor = outDonor = dock;
            donorName = "the Repair Box (" + box.name + ")";
        }

        // ---------- copying one dock ----------

        private static GameObject Graft(GameObject root, PickupDock donor, string name, Vector3 localPos, Item coin, bool isPayout, MelonLogger.Instance log,
                                        bool? grabbable = null, bool keepDonorTurn = false)
        {
            GameObject copy = UnityEngine.Object.Instantiate(donor.gameObject, root.transform, false);
            copy.name = name;
            copy.SetActive(true);
            copy.transform.localPosition = localPos;
            // The game's machines face the other way to this one; turn the copy round to face the player. The stall's slot
            // also keeps its own tilt within the stall.
            Quaternion turn = Quaternion.identity;
            if (keepDonorTurn)
            {
                Transform top = donor.transform;
                while (top.parent != null) top = top.parent;
                turn = Quaternion.Inverse(top.rotation) * donor.transform.rotation;
            }
            // The stall's slot already faces the same way as this machine (its coins showed backwards when turned round).
            copy.transform.localRotation = keepDonorTurn ? turn : Quaternion.Euler(0f, 180f, 0f);
            copy.transform.localScale = donor.transform.lossyScale;

            foreach (NetworkPrefab np in copy.GetComponentsInChildren<NetworkPrefab>(true))
                UnityEngine.Object.DestroyImmediate(np);
            if (copy.GetComponent<NetworkEntity>() == null) copy.AddComponent<NetworkEntity>();

            PickupDock dock = copy.GetComponent<PickupDock>();
            if (dock == null) throw new InvalidOperationException("Copied dock lost its PickupDock.");

            // References the copy didn't take with it would still point into the ATM; repoint or clear them.
            foreach (string field in new[] { "snapPoint", "pickupRange", "dockVisual" })
                FixRef(dock, field, copy, log);
            if (Field<IList<Collider>>(dock, "hasContentsRange") is List<Collider> range)
                range.RemoveAll(c => c == null || !c.transform.IsChildOf(copy.transform));
            Set(dock, "quantityStored", 0);
            Set(dock, "dockedType", null);

            // Coins only. Bets can't be taken back out; winnings can.
            PickupDockSettings s = UnityEngine.Object.Instantiate(dock.Settings);
            s.name = name + " Settings";
            s.IncludedTags.Clear();
            s.ExcludedTags.Clear();
            s.IncludedItems.Clear();
            s.IncludedItems.Add(coin);
            Set(s, "isGrabbable", grabbable ?? isPayout);
            Set(s, "isHandDockingAllowed", !isPayout);   // winnings can only be taken out, never put in
            Set(s, "allowContainerInteraction", true);
            Set(s, "isAllowingSubDocking", false);
            if (isPayout) Set(s, "isDockingAutomatic", false);   // Coin In keeps the ATM chute's auto-docking
            Set(s, "isAllowingCraftedItems", false);
            Set(s, "maxContainedStacks", -1);
            Set(dock, "settings", s);

            // Coin In is the ATM's deposit chute: its AutomaticDockArea pulls in coins dropped near it, so keep it.
            // The Winnings bubble must never pull coins in by itself.
            if (isPayout)
                foreach (Behaviour b in copy.GetComponentsInChildren<Behaviour>(true))
                    if (b.GetType().Name == "AutomaticDockArea") b.enabled = false;
            return copy;
        }

        // ---------- network wiring (as in the Pet mod's ShopPrefab.Build) ----------

        private static void Bind(GameObject root, NetworkPrefab prefab, List<GameObject> grafted)
        {
            List<NetworkEntity> embedded = prefab.Embedded;
            int next = 1 + embedded.Where(e => e != null).Select(e => (int)e.Hash).DefaultIfEmpty(0).Max();

            var entities = grafted.SelectMany(g => g.GetComponentsInChildren<NetworkEntity>(true)).Distinct().ToList();
            foreach (NetworkEntity e in entities)
            {
                Set(e, "hash", next++);
                embedded.Add(e);
            }

            FieldInfo entityField = typeof(NetworkEntityBehaviour).GetField("entity", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (GameObject g in grafted)
                foreach (NetworkEntityBehaviour b in g.GetComponentsInChildren<NetworkEntityBehaviour>(true))
                {
                    NetworkEntity owner = NearestEntity(b.transform);
                    if (owner == null) throw new InvalidOperationException("No entity for " + b.GetType().Name);
                    entityField.SetValue(b, owner);
                }

            foreach (NetworkEntity e in entities)
            {
                // Every network behaviour whose closest entity is this one, including ones on child objects.
                MonoBehaviour[] behaviours = e.GetComponentsInChildren<MonoBehaviour>(true)
                    .Where(b => b is INetworkEntityBehaviour && NearestEntity(b.transform) == e).ToArray();
                Set(e, "networkEntityBehaviours", behaviours);
                Set(e, "hasNetworkBehaviours", behaviours.Length != 0);
            }
        }

        /// <summary>
        /// The closest NetworkEntity on this object or above it. Done by hand because GetComponentInParent skips
        /// inactive objects, and the template lives under an inactive holder.
        /// </summary>
        private static NetworkEntity NearestEntity(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                NetworkEntity e = t.GetComponent<NetworkEntity>();
                if (e != null) return e;
            }
            return null;
        }

        // ---------- reflection helpers ----------

        /// <summary>If a dock field points outside the copied dock, repoint it at the same kind of component inside the copy.</summary>
        private static void FixRef(object target, string field, GameObject copy, MelonLogger.Instance log)
        {
            FieldInfo f = FindField(target.GetType(), field);
            if (f == null || !typeof(Component).IsAssignableFrom(f.FieldType)) return;
            var value = f.GetValue(target) as Component;
            if (value == null || value.transform.IsChildOf(copy.transform)) return;
            Component replacement = copy.GetComponentInChildren(f.FieldType, true);
            f.SetValue(target, replacement);
            log.Warning($"{copy.name}: '{field}' pointed outside the copied dock; now {(replacement == null ? "unset" : replacement.name)}.");
        }

        private static T Field<T>(object target, string field) where T : class =>
            target == null ? null : FindField(target.GetType(), field)?.GetValue(target) as T;

        private static void Set(object target, string field, object value)
        {
            FieldInfo f = FindField(target.GetType(), field) ?? throw new MissingFieldException(target.GetType().Name, field);
            f.SetValue(target, value);
        }

        private static FieldInfo FindField(Type t, string name)
        {
            for (; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, Any | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }
    }
}
