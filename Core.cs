using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Alta.Inventory;
using Alta.Networking;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

[assembly: MelonInfo(typeof(LuckySpriggull.Core), "The Lucky Spriggull", "2.2.0", "Epic")]
[assembly: MelonGame("Alta", "A Township Tale")]

namespace LuckySpriggull
{
    /// <summary>
    /// Adds a Slot Machine: a fixed structure you spawn with the console (spawn &lt;player&gt; 21251).
    /// Drop coins into the gold coin chute (the ATM's deposit chute): they all go into your bet.
    /// Pull the lever on its side: the machine spins once for the whole bet and either puts bet Ã— multiplier into
    /// its Winnings bubble, or keeps the lot. Nothing is ever dropped on the ground.
    /// Install on BOTH server and clients (the structure must exist everywhere). The gambling itself only runs on the server.
    /// </summary>
    public sealed class Core : MelonMod
    {
        private MelonPreferences_Entry<string> payouts;
        private MelonPreferences_Entry<float> autoSpinSeconds;
        private MelonPreferences_Entry<int> maxBet;
        private MelonPreferences_Entry<string> bonuses;
        private MelonPreferences_Entry<float> nearMissChance;
        private MelonPreferences_Entry<int> bonusPayout;
        private MelonPreferences_Entry<bool> bonusEffects;
        private readonly Dictionary<string, int> bonusPays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private List<KeyValuePair<string, float>> bonusTable;   // (creature, chance 0-1)

        private readonly Dictionary<string, Bet> bets = new Dictionary<string, Bet>();
        private readonly List<Payout> payQueue = new List<Payout>();
        private readonly Dictionary<SlotMachine, PickupDock[]> docks = new Dictionary<SlotMachine, PickupDock[]>();
        private readonly Dictionary<SlotMachine, KeyValuePair<int, float>> settling = new Dictionary<SlotMachine, KeyValuePair<int, float>>();
        private readonly HashSet<uint> eaten = new HashSet<uint>();
        private readonly Dictionary<SlotMachine, float> handLogNext = new Dictionary<SlotMachine, float>();
        private MelonPreferences_Entry<bool> debugButton;
        private readonly Collider[] hits = new Collider[96];
        private readonly Collider[] drumHits = new Collider[32];
        private List<KeyValuePair<int, float>> table; // (multiplier, chance 0-1)
        private Ledger ledger;
        private bool ready;
        private float nextCheck;

        /// <summary>Coins one player has put into one machine that haven't been spun yet.</summary>
        private sealed class Bet { public SlotMachine Machine; public uint MachineId; public int PlayerId; public int Coins; public float SpinAt = float.MaxValue; }

        private sealed class Payout { public SlotMachine Machine; public uint MachineId; public int PlayerId; public int Amount; public float Next; public int Tries; public string Bonus; }

        public override void OnInitializeMelon()
        {
            var cat = MelonPreferences.CreateCategory(SettingsId, "The Lucky Spriggull");
            payouts = cat.CreateEntry("Payouts", "2x:20, 3x:6, 5x:3, 20x:1",
                description: "The four prizes: 'multiplier:percent chance' (multipliers 1-99). The biggest is three Spriggulls, then Phantoms, " +
                             "Wyrms and Goteras. Whatever is left over is a loss. The machines' paytables show these amounts on every player's game. " +
                             "Default pays back ~93% on average.");
            autoSpinSeconds = cat.CreateEntry("AutoSpinSeconds", 0f,
                description: "0 = only the lever spins. Above 0: also spin the bet automatically this many seconds after the last coin went in.");
            maxBet = cat.CreateEntry("MaxBet", 0,
                description: "Most coins one spin can take (0 = no limit). Coins over the limit wait for the next spin.");

            bonuses = cat.CreateEntry("Bonuses", "Schmeechee:2, Babu:2, Dais:2, Mushroom:2, Turabada:2",
                description: "Bonus rows: 'creature:percent chance', optionally ':Nx' to pay that creature N x the bet (e.g. 'Babu:2:1x'). " +
                             "Schmeechee = heal, Babu = hunger, Dais = speed, Mushroom (the Glowing Mushroom) = glow, Turabada = damage + damage protection (see the effect settings). " +
                             "Taken from the losing spins, so the prize odds don't change.");
            bonusPayout = cat.CreateEntry("BonusPayout", 0,
                description: "Coins a bonus row pays, as a multiple of the bet: 0 = none (the effect is the prize), 1 = the bet back, 2 = double... " +
                             "A creature's own ':Nx' in Bonuses overrides this.");
            bonusEffects = cat.CreateEntry("BonusEffects", true,
                description: "true = bonus rows give their effect (heal, hunger, speed, glow, damage). false = no effects: bonus rows only pay BonusPayout.");
            Bonus.Settings.Create(cat);
            nearMissChance = cat.CreateEntry("NearMissChance", 25f,
                description: "Percent of losing spins shown as a near miss: two Spriggulls, Phantoms, Wyrms or Goteras, and the third reel " +
                             "creeps onto the win line then rolls back off it. Only changes what the reels show; it's still a loss. 0 = off.");
            debugButton = cat.CreateEntry("DebugButton", true,
                description: "Log where hands are when they're near the lever's knob (for tuning).");
            MoveOldSettings();

            table = ParsePayouts(payouts.Value).OrderByDescending(kv => kv.Key).ToList();
            if (table.Count > 4) { LoggerInstance.Warning("Payouts: only four prizes fit the machine (Spriggull, Phantom, Wyrm, Gotera); the smallest extras are ignored."); table = table.Take(4).ToList(); }
            float rtp = table.Sum(kv => kv.Key * kv.Value);
            float win = table.Sum(kv => kv.Value);
            LoggerInstance.Msg($"Payouts: {string.Join(", ", table.Select(kv => $"{kv.Key}x @ {kv.Value * 100:0.#}%").ToArray())}. " +
                               $"Win chance {win * 100:0.#}%, average return {rtp * 100:0.#}% of each bet.");

            string bonusSpec = bonuses.Value ?? "";
            if (bonusSpec.IndexOf("Turabada", StringComparison.OrdinalIgnoreCase) < 0) bonusSpec += ", Turabada:2";
            bonusTable = ParseBonuses(bonusSpec, 1f - win);
            if (bonusTable.Count > 0)
                LoggerInstance.Msg("Bonus rows: " + string.Join(", ", bonusTable.Select(kv =>
                    $"{Bonus.Display(kv.Key)} @ {kv.Value * 100:0.#}% ({DescribeBonus(kv.Key)})").ToArray()) + ".");
            if (!bonusEffects.Value && bonusTable.Any(kv => BonusPaysFor(kv.Key) == 0))
                LoggerInstance.Warning("BonusEffects is off and some bonus rows pay no coins: those rows give nothing at all. Set BonusPayout (or a ':Nx') to pay them.");
            float bonusReturn = bonusTable.Sum(kv => BonusPaysFor(kv.Key) * kv.Value);
            if (bonusReturn > 0f)
                LoggerInstance.Msg($"With bonus-row coins, the average return is {(rtp + bonusReturn) * 100:0.#}% of each bet.");

            ledger = new Ledger(Path.Combine(MelonEnvironment.UserDataDirectory, SettingsId, "Ledger"), LoggerInstance);
            // So the books can tell ordinary luck from a machine paying too much or too little (bonus-row coins count too).
            ledger.SetOdds(table.Concat(bonusTable.Where(kv => BonusPaysFor(kv.Key) > 0)
                                .Select(kv => new KeyValuePair<int, float>(BonusPaysFor(kv.Key), kv.Value))));
        }

        private const string SettingsId = "LuckySpriggull";
        private const string OldSettingsId = "LuckySlots";

        /// <summary>
        /// The mod used to keep its settings under [LuckySlots]. The first time it starts under its new name it copies
        /// those values (payouts, bonus odds, max bet...) into [LuckySpriggull] and removes the old section, so there's
        /// only ever one place to change them.
        /// </summary>
        private void MoveOldSettings()
        {
            string file = Path.Combine(MelonEnvironment.UserDataDirectory, "MelonPreferences.cfg");
            try
            {
                if (!File.Exists(file)) return;
                var old = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bool inOld = false, hasOld = false, hasNew = false;
                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (line == "[" + OldSettingsId + "]") hasOld = true;
                    if (line == "[" + SettingsId + "]") hasNew = true;
                    if (line.StartsWith("[")) { inOld = line == "[" + OldSettingsId + "]"; continue; }
                    if (!inOld || line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string value = line.Substring(eq + 1).Trim();
                    if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"') value = value.Substring(1, value.Length - 2);
                    old[line.Substring(0, eq).Trim()] = value;
                }
                if (!hasOld) return;

                // Copy only the first time: once [LuckySpriggull] exists, its values are the ones that count.
                bool copy = !hasNew && old.Count > 0;
                if (copy)
                {
                if (old.TryGetValue("Payouts", out string pay)) payouts.Value = pay;
                if (old.TryGetValue("Bonuses", out string bon)) bonuses.Value = bon;
                if (old.TryGetValue("AutoSpinSeconds", out string auto) && float.TryParse(auto, NumberStyles.Float, CultureInfo.InvariantCulture, out float a)) autoSpinSeconds.Value = a;
                if (old.TryGetValue("MaxBet", out string max) && int.TryParse(max, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m)) maxBet.Value = m;
                if (old.TryGetValue("DebugButton", out string dbg) && bool.TryParse(dbg, out bool d)) debugButton.Value = d;
                }

                // Take the old section out of MelonLoader's copy of the file too, or its next save writes it straight back.
                object prefFile = typeof(MelonPreferences).GetField("DefaultFile", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(null);
                var remove = prefFile?.GetType().GetMethod("RemoveCategoryFromDocument", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (remove != null) remove.Invoke(prefFile, new object[] { OldSettingsId });
                else MelonPreferences.RemoveCategoryFromFile(file, OldSettingsId);
                MelonPreferences.Save();
                LoggerInstance.Msg(copy
                    ? $"Moved your settings from [{OldSettingsId}] to [{SettingsId}] (" + string.Join(", ", old.Select(kv => kv.Key + " = " + kv.Value).ToArray()) + ")."
                    : $"Removed the old [{OldSettingsId}] settings section; [{SettingsId}] is the one in use.");
            }
            catch (Exception e) { LoggerInstance.Warning($"Couldn't move the old [{OldSettingsId}] settings ({e.Message}); set them again under [{SettingsId}]."); }
        }

        public override void OnLateInitializeMelon()
        {
            try
            {
                SlotMachine.SetPayouts(table.Select(kv => kv.Key));   // the paytable shows these odds
                SlotMachine.Register(LoggerInstance);
                SlotMachine.Pressed += OnSpinPressed;
                SlotGlyphs.LogAll(LoggerInstance);
                Bonus.LogEffects(LoggerInstance);
                ready = true;
            }
            catch (Exception e)
            {
                LoggerInstance.Error("Slot Machine failed to register; the mod is disabled: " + e);
            }
        }

        public override void OnFixedUpdate()
        {
            if (!ready || !NetworkSceneManager.IsServer) return;
            float now = Time.time;
            try
            {
                bool check = now >= nextCheck;
                if (check) nextCheck = now + 0.1f;
                foreach (SlotMachine m in SlotMachine.All.ToList())
                {
                    LogNearbyHands(m, now);
                    AutoSpin(m, now);
                    if (check) EatCoins(m, now);
                    SendPaytable(m, now);
                }
                ResolveBets(now);
                PayOut(now);
            }
            catch (Exception e)
            {
                LoggerInstance.Error("Slot tick failed: " + e);
            }
        }

        // ---------- the lever ----------

        /// <summary>
        /// A hand pushed the button down (the machine detects the push; every game sees the same one).
        /// On the server, that spins every bet waiting on that machine.
        /// </summary>
        private void OnSpinPressed(SlotMachine m, int presser)
        {
            if (!ready || !NetworkSceneManager.IsServer || m == null) return;
            try { SpinPressed(m, presser, Time.time); }
            catch (Exception e) { LoggerInstance.Error("Lever pull failed: " + e); }
        }

        /// <summary>
        /// Pulling the lever takes the coins waiting in the machine's coin store (up to MaxBet) and spins for them.
        /// </summary>
        private void SpinPressed(SlotMachine m, int presser, float now)
        {
            uint id = m.Entity != null ? m.Entity.Identifier : 0;
            Signal(m, SlotMachine.SignalSpin);   // every game starts its reels now; the result follows
            PickupDock betDock = BetDockOf(m);
            int inBubble = betDock != null && betDock.HasDockedPickup ? betDock.QuantityStored : 0;
            if (inBubble <= 0)
            {
                LoggerInstance.Msg($"Player {presser} pulled the lever on slot machine {id}, but no coins are in.");
                Signal(m, SlotMachine.SignalNoCoins);   // the reels stop straight away on a blank
                return;
            }
            int stake = Math.Min(inBubble, maxBet.Value > 0 ? maxBet.Value : int.MaxValue);
            betDock.DestroyAmount(stake);
            betChangedAt.Remove(m);
            lastSlotCount[m] = inBubble - stake;   // these coins went into the spin, not back to the player

            string key = id + "/" + presser;
            if (!bets.TryGetValue(key, out Bet bet))
                bets[key] = bet = new Bet { Machine = m, MachineId = id, PlayerId = presser };
            bet.Coins += stake;
            bet.SpinAt = Math.Min(bet.SpinAt, now + SlotMachine.ResultDelay);   // matches the reel animation
            LoggerInstance.Msg($"Player {presser} pulled the lever on slot machine {id}, betting {stake} coin(s).");
        }

        private readonly Dictionary<SlotMachine, float> betChangedAt = new Dictionary<SlotMachine, float>();
        private readonly Dictionary<SlotMachine, int> lastBettor = new Dictionary<SlotMachine, int>();

        /// <summary>The bet is whatever is in the coin slot: players can grab coins back out until they pull the lever.</summary>
        private PickupDock BetDockOf(SlotMachine m) => DocksOf(m)[0];

        /// <summary>With AutoSpinSeconds above 0, a bet left alone that long spins by itself.</summary>
        private void AutoSpin(SlotMachine m, float now)
        {
            if (autoSpinSeconds.Value <= 0f || !betChangedAt.TryGetValue(m, out float at) || now - at < autoSpinSeconds.Value) return;
            betChangedAt.Remove(m);
            SpinPressed(m, lastBettor.TryGetValue(m, out int p) ? p : 0, now);
        }

        /// <summary>
        /// While testing: when a hand is near the button but doesn't count as a press, say where it is relative to the
        /// button (x right, y up, z out the front, in cm), so the press zone can be tuned from the log.
        /// </summary>
        private void LogNearbyHands(SlotMachine m, float now)
        {
            if (!debugButton.Value) return;
            if (handLogNext.TryGetValue(m, out float next) && now < next) return;
            var near = SlotMachine.HandPoints()
                .Select(h => new { h, off = m.ButtonOffset(h.Position) })
                .Where(x => x.off.magnitude < 0.35f)
                .OrderBy(x => x.off.magnitude)
                .ToList();
            if (near.Count == 0) return;
            handLogNext[m] = now + 1f;
            LoggerInstance.Msg("Hands near the lever, cm from its knob: " +
                string.Join("; ", near.Take(4).Select(x => $"{x.h.Name} ({x.off.x * 100:0},{x.off.y * 100:0},{x.off.z * 100:0})").ToArray()));
        }

        // ---------- taking coins ----------

        /// <summary>Coins grabbed back out of a slot, and when: they're left alone for a few seconds so the hand can take them away.</summary>
        private readonly Dictionary<uint, float> takenBack = new Dictionary<uint, float>();
        private const float TakeBackGrace = 6f;
        /// <summary>When coins were last taken back out of each machine's slot.</summary>
        private readonly Dictionary<SlotMachine, float> tookBackAt = new Dictionary<SlotMachine, float>();

        private PickupDock[] DocksOf(SlotMachine machine)
        {
            if (!docks.TryGetValue(machine, out PickupDock[] d) || d[0] == null || d[1] == null)
            {
                SlotDocks.Find(machine, out PickupDock coinIn, out PickupDock winnings);
                docks[machine] = d = new[] { coinIn, winnings };
                // Coins taken back out of the slot aren't swallowed again straight away.
                if (coinIn != null) coinIn.ItemUndocked += (interactor, pickup, dock) =>
                {
                    if (pickup != null && pickup.Entity != null) takenBack[pickup.Entity.Identifier] = Time.time;
                };
            }
            return d;
        }

        private readonly Dictionary<SlotMachine, int> lastSlotCount = new Dictionary<SlotMachine, int>();

        /// <summary>
        /// Coins dropped in the slot stay there as the bet (so they can be grabbed back out). Loose coins and pouches
        /// touching the slot are put in; any change to the bet restarts the auto-spin countdown and is logged.
        /// </summary>
        private void EatCoins(SlotMachine machine, float now)
        {
            PickupDock coinIn = DocksOf(machine)[0];
            if (coinIn == null) { EatLooseCoins(machine, now); return; }
            int q = coinIn.HasDockedPickup ? coinIn.QuantityStored : 0;
            // Coins taken back out of the slot: the game hands them over in a pouch, so for a while no held bag is
            // emptied back in (the player is carrying their coins away, maybe up through the slit).
            if (lastSlotCount.TryGetValue(machine, out int had) && q < had) tookBackAt[machine] = now;
            EatLooseCoins(machine, now);
            q = coinIn.HasDockedPickup ? coinIn.QuantityStored : 0;
            TrackCashOut(machine);
            bool known = lastSlotCount.TryGetValue(machine, out int before);
            if (known && before == q) return;
            lastSlotCount[machine] = q;
            uint id = machine.Entity != null ? machine.Entity.Identifier : 0;
            if (known && q < before)
            {
                ledger.TakenBack(NearestPlayer(machine, lastBettor.TryGetValue(machine, out int lb) ? lb : 0), id, before - q);
            }
            else if (known && q > before)
            {
                int who = coinIn.DockedPickup != null ? coinIn.DockedPickup.LastInteractorPlayerId : NearestPlayer(machine, 0);
                ledger.Inserted(who, id, q - before);
            }
            if (q <= 0) { betChangedAt.Remove(machine); return; }
            betChangedAt[machine] = now;
            if (coinIn.DockedPickup != null) lastBettor[machine] = coinIn.DockedPickup.LastInteractorPlayerId;
            LoggerInstance.Msg($"Slot machine {id}: the bet is now {q} coin(s){(known && q < before ? " (some taken back out)" : "")}.");
        }

        /// <summary>
        /// Coins and coin pouches touching the coin slot (dropped on it, or still held) go in. The ATM chute alone misses them:
        /// it ignores coins the server isn't simulating yet (just released from a hand) and only rechecks coins
        /// still inside its small pickup zone.
        /// </summary>
        private void EatLooseCoins(SlotMachine machine, float now)
        {
            uint coinHash = CommonPrefabs.CoinPrefab.Hash;
            Transform t = machine.transform;
            var taken = new Dictionary<int, int>();   // player -> coins
            var seen = new HashSet<Pickup>();
            // At the slot, and inside the coin drum (a coin dropped through the slit lands on its floor).
            int n = Physics.OverlapSphereNonAlloc(t.TransformPoint(SlotMachine.ChuteMouthLocal), 0.08f, hits, ~0, QueryTriggerInteraction.Ignore);
            int atSlot = n;
            int m = Physics.OverlapSphereNonAlloc(t.TransformPoint(SlotMachine.InDockLocal), 0.12f, drumHits, ~0, QueryTriggerInteraction.Ignore);
            for (int k = 0; k < m && n < hits.Length; k++) hits[n++] = drumHits[k];
            for (int i = 0; i < n; i++)
            {
                bool inDrum = i >= atSlot;
                Rigidbody rb = hits[i] != null ? hits[i].attachedRigidbody : null;
                Pickup p = rb != null ? rb.GetComponent<Pickup>() : null;
                // Held pouches count too: touch a bag of coins to the slot and it empties in. Held coins don't, so coins
                // grabbed back out of the slot aren't swallowed again.
                if (p == null || !seen.Add(p) || p.IsDocked) continue;
                NetworkEntity e = p.Entity;
                if (e == null || e.IsBeingDestroyed || e.Prefab == null) continue;
                int player = p.LastInteractorPlayerId;
                // Coins (or the pouch the game hands you) just grabbed back out of the slot are left alone.
                if (takenBack.TryGetValue(e.Identifier, out float when) && Time.time - when < TakeBackGrace) continue;
                // Inside the coin box only coins that fell in through the slit count: anything held there is a player
                // taking their coins back out, not putting them in.
                if (inDrum && (p.IsInteractedWith || e.Prefab.Hash != coinHash)) continue;
                if ((p.IsInteractedWith || e.Prefab.Hash != coinHash) && tookBackAt.TryGetValue(machine, out float tb) && now - tb < TakeBackGrace) continue;

                if (e.Prefab.Hash == coinHash)
                {
                    if (p.IsInteractedWith) continue;
                    // A loose coin.
                    if (!eaten.Add(e.Identifier)) continue;
                    taken[player] = taken.TryGetValue(player, out int c) ? c + 1 : 1;
                    e.SceneDestroy();
                }
                else if (p.SubDocks != null)
                {
                    // A coin pouch (or any container): take out every coin, leave the pouch for its owner to pick up.
                    int coins = EmptyCoinsFrom(p, coinHash);
                    if (coins > 0) taken[player] = (taken.TryGetValue(player, out int c) ? c : 0) + coins;
                }
            }
            if (eaten.Count > 5000) eaten.Clear();
            uint id = machine.Entity != null ? machine.Entity.Identifier : 0;
            foreach (var kv in taken) AddToBet(machine, id, kv.Key, kv.Value, now);
        }

        /// <summary>Destroys every coin stack in the container's docks (including pouches inside it). Returns how many coins.</summary>
        private static int EmptyCoinsFrom(Pickup container, uint coinHash)
        {
            int total = 0;
            foreach (PickupDock dock in container.SubDocks.TotalDocks.ToList())
            {
                if (dock == null || !dock.HasDockedPickup || dock.QuantityStored <= 0) continue;
                NetworkEntity docked = dock.DockedPickup.Entity;
                if (docked == null || docked.Prefab == null || docked.Prefab.Hash != coinHash) continue;
                total += dock.QuantityStored;
                dock.DestroyAmountToEmpty();
            }
            return total;
        }

        /// <summary>Coins put into the machine wait in the coin slot (their count shows above it) until the lever is pulled.</summary>
        private readonly Dictionary<SlotMachine, KeyValuePair<int, float>> paytableNext = new Dictionary<SlotMachine, KeyValuePair<int, float>>();
        private readonly HashSet<SlotMachine> paytableWarned = new HashSet<SlotMachine>();

        /// <summary>
        /// Every game shows this server's payouts: the paytable goes out through the machine's hidden paytable dock one
        /// row at a time, round and round (a dock's count only syncs up to 123,456, too small for all four at once).
        /// </summary>
        private void SendPaytable(SlotMachine machine, float now)
        {
            int row = 0;
            if (paytableNext.TryGetValue(machine, out var next)) { if (now < next.Value) return; row = next.Key; }
            paytableNext[machine] = new KeyValuePair<int, float>((row + 1) % 4, now + 0.75f);
            SlotDocks.Find(machine, out _, out _, out _, out PickupDock dock);
            if (dock == null) return;
            int multiplier = row < table.Count ? table[row].Key : 0;
            string why = SlotDocks.SetCount(dock, SlotMachine.EncodePaytableRow(row, multiplier));
            if (why != null && paytableWarned.Add(machine))
                LoggerInstance.Warning($"Couldn't send the paytable to slot machine {(machine.Entity != null ? machine.Entity.Identifier : 0)} ({why}).");
        }

        private readonly Dictionary<SlotMachine, int> lastWinnings = new Dictionary<SlotMachine, int>();

        /// <summary>Coins taken out of the Winnings bubble: recorded as a cash-out by the player standing at the machine.</summary>
        private void TrackCashOut(SlotMachine machine)
        {
            PickupDock winnings = DocksOf(machine)[1];
            if (winnings == null) return;
            int w = winnings.HasDockedPickup ? winnings.QuantityStored : 0;
            if (lastWinnings.TryGetValue(machine, out int had) && w < had)
                ledger.CashedOut(NearestPlayer(machine, 0), machine.Entity != null ? machine.Entity.Identifier : 0, had - w);
            lastWinnings[machine] = w;   // payouts raise it; only drops are pick-ups
        }

        /// <summary>The player whose head is closest to the machine (within 3 m), else <paramref name="fallback"/>.</summary>
        private static int NearestPlayer(SlotMachine machine, int fallback)
        {
            int best = fallback;
            float bestDist = 9f;
            foreach (var ip in global::Player.AllPlayers)
            {
                PlayerController pc = (ip as global::Player)?.PlayerController;
                if (pc == null || pc.Head == null || ip.UserInfo == null) continue;
                float d = (pc.Head.position - machine.transform.position).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = ip.UserInfo.Identifier; }
            }
            return best;
        }

        private void AddToBet(SlotMachine machine, uint machineId, int player, int coins, float now)
        {
            PickupDock betDock = BetDockOf(machine);
            if (betDock == null) { LoggerInstance.Warning($"Slot machine {machineId} has no coin store; {coins} coin(s) lost."); return; }
            int before = betDock.HasDockedPickup ? betDock.QuantityStored : 0;
            string why = SlotDocks.SetCount(betDock, before + coins);
            if (why != null) { LoggerInstance.Warning($"Couldn't put {coins} coin(s) into slot machine {machineId}'s coin store ({why})."); return; }
            betChangedAt[machine] = now;   // every new coin restarts the auto-spin countdown (if it's on)
            lastBettor[machine] = player;
            lastSlotCount[machine] = before + coins;
            ledger.Inserted(player, machineId, coins);
            LoggerInstance.Msg($"Player {player} put {coins} coin(s) into slot machine {machineId}; the bet is now {before + coins}.");
        }

        // ---------- spinning ----------

        /// <summary>Winnings land this long after the result, so the reels have stopped and the celebration has started.</summary>
        private const float PayDelay = 1.3f;

        private int spinSeq;

        /// <summary>Tells every game the result (through the hidden signal dock) so the reels can stop on it.</summary>
        private void Signal(SlotMachine m, int kind)
        {
            if (m == null) return;
            SlotDocks.Find(m, out _, out _, out PickupDock signal);
            string why = signal == null ? "it has no signal dock" : SlotDocks.SetCount(signal, SlotMachine.SignalCode(++spinSeq, kind));
            if (why != null) LoggerInstance.Warning($"Couldn't show the result on slot machine {(m.Entity != null ? m.Entity.Identifier : 0)} ({why}); its reels will stop on a loss.");
        }

        private void ResolveBets(float now)
        {
            var results = new Dictionary<SlotMachine, int>();   // machine -> best result kind spun this tick (0 = loss)
            foreach (string key in bets.Keys.ToList())
            {
                Bet bet = bets[key];
                if (now < bet.SpinAt) continue;

                int limit = maxBet.Value > 0 ? maxBet.Value : int.MaxValue;
                int stake = Math.Min(bet.Coins, limit);
                bet.Coins -= stake;
                bet.SpinAt = float.MaxValue;   // any coins over MaxBet wait for the next press
                if (bet.Coins <= 0) bets.Remove(key);

                int row = Roll();
                int multiplier = row >= 0 ? table[row].Key : 0;
                string bonus = row < 0 ? RollBonus() : null;
                // A win shows as its paytable row (1 = the top prize), a bonus as its creature; some losses show as a near miss.
                int kind = bonus != null ? Bonus.KindFor(bonus) : row >= 0 ? row + 1 : 0;
                bool nearMiss = bonus == null && multiplier <= 0 && UnityEngine.Random.value * 100f < nearMissChance.Value;
                if (nearMiss) kind = SlotMachine.SignalNearMiss + UnityEngine.Random.Range(0, 4);
                if (bet.Machine != null)
                    results[bet.Machine] = results.TryGetValue(bet.Machine, out int best) && Rank(best) >= Rank(kind) ? best : kind;
                if (bonus != null)
                {
                    if (bet.Machine == null) continue;
                    // The bonus coins (if any) and the effect (if on) come once the reels have stopped and the show has started.
                    int bonusCoins = (int)Math.Min((long)stake * BonusPaysFor(bonus), int.MaxValue);
                    ledger.Spin(bet.PlayerId, bet.MachineId, stake, "Bonus: " + Bonus.Display(bonus), bonusCoins, win: false, bonus: true, jackpot: false);
                    LoggerInstance.Msg($"Player {bet.PlayerId} spun three {Bonus.Plural(bonus)} on slot machine {bet.MachineId} with {stake} coin(s): {DescribeBonus(bonus)}. {ledger.Describe(bet.PlayerId)}");
                    payQueue.Add(new Payout { Machine = bet.Machine, MachineId = bet.MachineId, PlayerId = bet.PlayerId, Amount = bonusCoins, Next = now + PayDelay,
                                              Bonus = bonusEffects.Value ? bonus : null });
                    continue;
                }
                if (multiplier <= 0)
                {
                    ledger.Spin(bet.PlayerId, bet.MachineId, stake, nearMiss ? "Loss (near miss)" : "Loss", 0, win: false, bonus: false, jackpot: false);
                    LoggerInstance.Msg($"Player {bet.PlayerId} lost a {stake}-coin bet on slot machine {bet.MachineId}. {ledger.Describe(bet.PlayerId)}");
                    continue;
                }
                if (bet.Machine == null)
                {
                    LoggerInstance.Warning($"Player {bet.PlayerId} won {stake * multiplier} coins but slot machine {bet.MachineId} was removed mid-spin; the win is lost.");
                    continue;
                }

                long won = (long)stake * multiplier;
                int amount = (int)Math.Min(won, int.MaxValue);
                bool isJackpot = row == 0;
                ledger.Spin(bet.PlayerId, bet.MachineId, stake, multiplier + "x", amount, win: true, bonus: false, jackpot: isJackpot);
                string jackpot = isJackpot ? " JACKPOT!" : "";
                LoggerInstance.Msg($"Player {bet.PlayerId} bet {stake} and won {amount} coins ({multiplier}x) on slot machine {bet.MachineId}!{jackpot} {ledger.Describe(bet.PlayerId)}");
                payQueue.Add(new Payout { Machine = bet.Machine, MachineId = bet.MachineId, PlayerId = bet.PlayerId, Amount = amount, Next = now + PayDelay });
            }
            foreach (var r in results) Signal(r.Key, r.Value);
        }

        // ---------- paying out ----------

        /// <summary>Winnings go into the machine's "Winnings" bubble, the way an ATM fills its withdraw dock.</summary>
        private void PayOut(float now)
        {
            for (int i = payQueue.Count - 1; i >= 0; i--)
            {
                Payout p = payQueue[i];
                if (now < p.Next) continue;
                if (p.Machine == null)
                {
                    LoggerInstance.Warning($"Slot machine {p.MachineId} disappeared before paying player {p.PlayerId} {p.Amount} coins.");
                    payQueue.RemoveAt(i);
                    continue;
                }

                if (p.Bonus != null)
                {
                    string how;
                    try { how = Bonus.Apply(p.PlayerId, p.Bonus); } catch (Exception e) { how = "failed: " + e.Message; }
                    LoggerInstance.Msg($"Bonus for player {p.PlayerId} (three {Bonus.Plural(p.Bonus)}): {how}.");
                    p.Bonus = null;   // once, even if paying the coins back has to be retried
                }
                PickupDock winnings = DocksOf(p.Machine)[1];
                string why = winnings == null ? "it has no Winnings bubble" : p.Amount <= 0 ? null : PayInto(winnings, p.Amount);
                if (why == null) { payQueue.RemoveAt(i); continue; }

                // Keep retrying rather than lose the win.
                p.Next = now + 1f;
                if (++p.Tries == 1 || p.Tries % 30 == 0)
                    LoggerInstance.Warning($"Couldn't pay {p.Amount} coins into slot machine {p.MachineId} ({why}); retrying.");
            }
        }

        /// <summary>Adds coins to the dock. Returns null on success, or why it failed.</summary>
        private static string PayInto(PickupDock dock, int amount)
        {
            if (dock.HasDockedPickup && dock.QuantityStored > 0)
            {
                dock.QuantityStored += amount;
                return null;
            }
            NetworkEntity coin = SpawnHelper.SpawnUnchunked(CommonPrefabs.CoinPrefab, SpawnData.Default);
            if (coin == null || coin.CommonPickup == null) return "couldn't spawn a coin";
            PickupDock.AllowedResult r = coin.CommonPickup.DockInto(null, dock, amount, isInstantMovementForced: true);
            if (r == PickupDock.AllowedResult.Ok) return null;
            coin.SceneDestroy();
            return "the bubble refused the coins: " + r;
        }

        /// <summary>Which result shows when several bets on one machine finish together: any win, then a bonus, then a loss.</summary>
        private static int Rank(int kind) => kind <= 0 ? 0 : SlotMachine.IsNearMiss(kind) ? 1 : Bonus.IsBonus(kind) ? 2 : 10 - kind;   // prize row 1 ranks highest

        /// <summary>For a losing spin: maybe a bonus row instead (chances are out of all spins, taken from the losses).</summary>
        private string RollBonus()
        {
            float lose = 1f - table.Sum(kv => kv.Value);
            if (lose <= 0f || bonusTable == null) return null;
            float r = UnityEngine.Random.value * lose;
            foreach (var kv in bonusTable)
            {
                if (r < kv.Value) return kv.Key;
                r -= kv.Value;
            }
            return null;
        }

        private List<KeyValuePair<string, float>> ParseBonuses(string spec, float room)
        {
            var list = new List<KeyValuePair<string, float>>();
            float total = 0f;
            foreach (string part in (spec ?? "").Split(','))
            {
                string[] kv = part.Split(':');
                if (kv.Length != 2 && kv.Length != 3) continue;
                string name = Bonus.FromSetting(kv[0].Trim());
                if (name != null && kv.Length == 3)
                {
                    if (int.TryParse(kv[2].Trim().TrimEnd('x', 'X'), out int pays) && pays >= 0) bonusPays[name] = pays;
                    else LoggerInstance.Warning($"Bonus '{part.Trim()}': the payout should look like '1x'.");
                }
                if (name == null) { LoggerInstance.Warning($"Unknown bonus creature '{kv[0].Trim()}' (use {string.Join(", ", Bonus.Symbols)})."); continue; }
                if (!float.TryParse(kv[1].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float pct) || pct <= 0) continue;
                float chance = Mathf.Min(pct / 100f, room - total);
                if (chance <= 0f) break;
                total += chance;
                list.Add(new KeyValuePair<string, float>(name, chance));
            }
            return list;
        }

        /// <summary>Which prize this spin wins: its row (0 = the top prize), or -1 for none.</summary>
        private int Roll()
        {
            float r = UnityEngine.Random.value;
            for (int i = 0; i < table.Count; i++)
            {
                if (r < table[i].Value) return i;
                r -= table[i].Value;
            }
            return -1;
        }

        private int BonusPaysFor(string creature) => bonusPays.TryGetValue(creature, out int n) ? n : Math.Max(0, bonusPayout.Value);

        private string DescribeBonus(string creature)
        {
            int pays = BonusPaysFor(creature);
            string coins = pays == 0 ? "" : pays == 1 ? "the bet back" : $"{pays}x the bet";
            string effect = bonusEffects.Value ? Bonus.Describe(creature) : "";
            return coins.Length > 0 && effect.Length > 0 ? coins + " + " + effect : coins.Length > 0 ? coins : effect.Length > 0 ? effect : "nothing (effects off, no payout)";
        }

        /// <summary>"2x:20, 3x:6" -> [(2, 0.20), (3, 0.06)]. Bad entries are skipped; chances are capped at 100% total.</summary>
        private List<KeyValuePair<int, float>> ParsePayouts(string spec)
        {
            var list = new List<KeyValuePair<int, float>>();
            float total = 0f;
            foreach (string part in (spec ?? "").Split(','))
            {
                string[] kv = part.Split(':');
                if (kv.Length != 2) continue;
                if (!int.TryParse(kv[0].Trim().TrimEnd('x', 'X'), out int mult) || mult <= 0) { LoggerInstance.Warning($"Bad payout '{part.Trim()}'"); continue; }
                if (mult > 99) { LoggerInstance.Warning($"Payout '{part.Trim()}': the most a prize can pay is 99x; using 99x."); mult = 99; }
                if (!float.TryParse(kv[1].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float pct) || pct <= 0) { LoggerInstance.Warning($"Bad payout '{part.Trim()}'"); continue; }
                float chance = Mathf.Min(pct / 100f, 1f - total);
                if (chance <= 0) { LoggerInstance.Warning("Payout chances add up to more than 100%; extra entries ignored."); break; }
                total += chance;
                list.Add(new KeyValuePair<int, float>(mult, chance));
            }
            return list;
        }

    }
}
