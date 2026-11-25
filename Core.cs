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

[assembly: MelonInfo(typeof(LuckySpriggull.Core), "The Lucky Spriggull", "2.0.0", "Epic")]
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
        private Stats stats;
        private bool ready;
        private float nextCheck;

        /// <summary>Coins one player has put into one machine that haven't been spun yet.</summary>
        private sealed class Bet { public SlotMachine Machine; public uint MachineId; public int PlayerId; public int Coins; public float SpinAt = float.MaxValue; }

        private sealed class Payout { public SlotMachine Machine; public uint MachineId; public int PlayerId; public int Amount; public float Next; public int Tries; public string Bonus; }

        public override void OnInitializeMelon()
        {
            var cat = MelonPreferences.CreateCategory(SettingsId, "The Lucky Spriggull");
            payouts = cat.CreateEntry("Payouts", "2x:20, 3x:6, 5x:3, 20x:1",
                description: "What a bet can win: 'multiplier:percent chance'. Whatever is left over is a loss. Default pays back ~93% on average.");
            autoSpinSeconds = cat.CreateEntry("AutoSpinSeconds", 0f,
                description: "0 = only the lever spins. Above 0: also spin the bet automatically this many seconds after the last coin went in.");
            maxBet = cat.CreateEntry("MaxBet", 0,
                description: "Most coins one spin can take (0 = no limit). Coins over the limit wait for the next spin.");

            bonuses = cat.CreateEntry("Bonuses", "Schmeechee:2, Babu:2, Dais:2, Firebug:2, Turabada:2",
                description: "Bonus rows: 'creature:percent chance'. Three in a row hands the bet back plus a boost: Schmeechee = full heal, " +
                             "Babu = full hunger, Dais = 2x speed, Firebug = night vision, Turabada = 5x damage + damage protection. Taken from the losing spins, so the prize odds don't change.");
            debugButton = cat.CreateEntry("DebugButton", true,
                description: "Log where hands are when they're near the lever's knob (for tuning).");
            MoveOldSettings();

            table = ParsePayouts(payouts.Value);
            float rtp = table.Sum(kv => kv.Key * kv.Value);
            float win = table.Sum(kv => kv.Value);
            LoggerInstance.Msg($"Payouts: {string.Join(", ", table.Select(kv => $"{kv.Key}x @ {kv.Value * 100:0.#}%").ToArray())}. " +
                               $"Win chance {win * 100:0.#}%, average return {rtp * 100:0.#}% of each bet.");

            string bonusSpec = bonuses.Value ?? "";
            if (bonusSpec.IndexOf("Turabada", StringComparison.OrdinalIgnoreCase) < 0) bonusSpec += ", Turabada:2";
            bonusTable = ParseBonuses(bonusSpec, 1f - win);
            if (bonusTable.Count > 0)
                LoggerInstance.Msg("Bonus rows: " + string.Join(", ", bonusTable.Select(kv => $"{kv.Key} @ {kv.Value * 100:0.#}% ({Bonus.Describe(kv.Key)})").ToArray()) + ".");

            stats = new Stats(Path.Combine(MelonEnvironment.UserDataDirectory, SettingsId));
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
            stats.Add(presser, coinsIn: stake, coinsOut: 0);

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
            if (lastSlotCount.TryGetValue(machine, out int before) && before == q) return;
            lastSlotCount[machine] = q;
            if (q <= 0) { betChangedAt.Remove(machine); return; }
            betChangedAt[machine] = now;
            if (coinIn.DockedPickup != null) lastBettor[machine] = coinIn.DockedPickup.LastInteractorPlayerId;
            uint id = machine.Entity != null ? machine.Entity.Identifier : 0;
            LoggerInstance.Msg($"Slot machine {id}: the bet is now {q} coin(s){(q < before ? " (some taken back out)" : "")}.");
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

                int multiplier = Roll();
                string bonus = multiplier <= 0 ? RollBonus() : null;
                int kind = bonus != null ? Bonus.KindFor(bonus) : Math.Min(Math.Max(multiplier, 0), Bonus.KindBase - 1);
                if (bet.Machine != null)
                    results[bet.Machine] = results.TryGetValue(bet.Machine, out int best) && Rank(best) >= Rank(kind) ? best : kind;
                if (bonus != null)
                {
                    if (bet.Machine == null) continue;
                    // The bet comes back, and the boost is given once the reels have stopped and the show has started.
                    stats.Add(bet.PlayerId, coinsIn: 0, coinsOut: stake);
                    LoggerInstance.Msg($"Player {bet.PlayerId} spun three {bonus}s on slot machine {bet.MachineId}: bet of {stake} back, plus {Bonus.Describe(bonus)}.");
                    payQueue.Add(new Payout { Machine = bet.Machine, MachineId = bet.MachineId, PlayerId = bet.PlayerId, Amount = stake, Next = now + PayDelay, Bonus = bonus });
                    continue;
                }
                if (multiplier <= 0)
                {
                    LoggerInstance.Msg($"Player {bet.PlayerId} lost a {stake}-coin bet on slot machine {bet.MachineId}. {stats.Describe(bet.PlayerId)}");
                    continue;
                }
                if (bet.Machine == null)
                {
                    LoggerInstance.Warning($"Player {bet.PlayerId} won {stake * multiplier} coins but slot machine {bet.MachineId} was removed mid-spin; the win is lost.");
                    continue;
                }

                long won = (long)stake * multiplier;
                int amount = (int)Math.Min(won, int.MaxValue);
                stats.Add(bet.PlayerId, coinsIn: 0, coinsOut: amount);
                string jackpot = multiplier == table.Max(kv => kv.Key) ? " JACKPOT!" : "";
                LoggerInstance.Msg($"Player {bet.PlayerId} bet {stake} and won {amount} coins ({multiplier}x) on slot machine {bet.MachineId}!{jackpot} {stats.Describe(bet.PlayerId)}");
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
                    LoggerInstance.Msg($"Bonus for player {p.PlayerId} ({p.Bonus}s): {how}.");
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
        private static int Rank(int kind) => kind <= 0 ? 0 : Bonus.IsBonus(kind) ? 1 : 2 + kind;

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
                if (kv.Length != 2) continue;
                string name = Bonus.Symbols.FirstOrDefault(s => s.Equals(kv[0].Trim(), StringComparison.OrdinalIgnoreCase));
                if (name == null) { LoggerInstance.Warning($"Unknown bonus creature '{kv[0].Trim()}' (use {string.Join(", ", Bonus.Symbols)})."); continue; }
                if (!float.TryParse(kv[1].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float pct) || pct <= 0) continue;
                float chance = Mathf.Min(pct / 100f, room - total);
                if (chance <= 0f) break;
                total += chance;
                list.Add(new KeyValuePair<string, float>(name, chance));
            }
            return list;
        }

        private int Roll()
        {
            float r = UnityEngine.Random.value;
            foreach (var kv in table)
            {
                if (r < kv.Value) return kv.Key;
                r -= kv.Value;
            }
            return 0;
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
                if (!float.TryParse(kv[1].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float pct) || pct <= 0) { LoggerInstance.Warning($"Bad payout '{part.Trim()}'"); continue; }
                float chance = Mathf.Min(pct / 100f, 1f - total);
                if (chance <= 0) { LoggerInstance.Warning("Payout chances add up to more than 100%; extra entries ignored."); break; }
                total += chance;
                list.Add(new KeyValuePair<int, float>(mult, chance));
            }
            return list;
        }

        /// <summary>Per-player coins in / out, saved to UserData/LuckySpriggull/stats.txt as "playerId|coinsIn|coinsOut".</summary>
        private sealed class Stats
        {
            private readonly string file;
            private readonly Dictionary<int, long[]> rows = new Dictionary<int, long[]>();

            public Stats(string dir)
            {
                Directory.CreateDirectory(dir);
                file = Path.Combine(dir, "stats.txt");
                if (!File.Exists(file)) return;
                foreach (string line in File.ReadAllLines(file))
                {
                    string[] f = line.Split('|');
                    if (f.Length == 3 && int.TryParse(f[0], out int id) && long.TryParse(f[1], out long a) && long.TryParse(f[2], out long b))
                        rows[id] = new[] { a, b };
                }
            }

            public void Add(int player, int coinsIn, int coinsOut)
            {
                if (!rows.TryGetValue(player, out long[] r)) rows[player] = r = new long[2];
                r[0] += coinsIn;
                r[1] += coinsOut;
                try { File.WriteAllLines(file, rows.Select(kv => $"{kv.Key}|{kv.Value[0]}|{kv.Value[1]}").ToArray()); }
                catch (IOException) { }
            }

            public string Describe(int player) =>
                rows.TryGetValue(player, out long[] r) ? $"(lifetime: {r[0]} in, {r[1]} out)" : "";
        }
    }
}
