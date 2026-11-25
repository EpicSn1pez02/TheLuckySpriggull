using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Alta;
using Alta.Inventory;
using Alta.Networking;
using Alta.Pages;
using MelonLoader;
using UnityEngine;

namespace LuckySpriggull
{
    /// <summary>
    /// The Slot Machine: a fixed structure (not an item), spawned with the console and standing on the ground like the ATM.
    /// Built in code under a fixed prefab hash, the same way SyncLib registers custom prefabs, so the client and
    /// server agree without syncing. Front is +Z; the origin is the middle of the base, on the floor.
    ///
    /// The component also lives on every spawned machine: it keeps the list of machines, and on each game
    /// pushes the spin button's cap down while a hand is pressing it.
    /// </summary>
    internal sealed class SlotMachine : MonoBehaviour
    {
        public const string Name = "Slot Machine";
        public const uint PrefabHash = 21251;   // fixed: placed machines are saved by this number

        // Layout (metres, local space).
        private const float W = 0.56f, D = 0.44f, BaseH = 0.12f, CabTop = 1.58f;   // tall enough for the paytable and the sign between the screen and the topper
        /// <summary>The paytable panel sits between the reels' screen and the sign (local to the cabinet's visual).</summary>
        private const float PayBottom = 1.06f, PayHeight = 0.24f, SignBottom = 1.34f;
        private const float TopperH = 0.09f;   // the slim band that carries the ring of lights
        /// <summary>The whole cabinet stands on a pedestal this tall (m), so the screen is near eye level (top ~1.6 m).</summary>
        private const float Lift = 0.25f;
        private const float ShelfDepth = 0.32f, CoinBoxFloorTop = 0.49f, LidThickness = 0.025f, BodyInset = 0.025f;
        private const float ShelfY = 0.62f, ShelfTop = ShelfY + 0.025f, ShelfZ = D / 2f + 0.14f;   // a deep shelf: the coin slot sits well clear of the screen

        /// <summary>The "Coin In" catcher (the ATM's deposit auto-dock, invisible), just above the coin slot in the middle of the shelf.</summary>
        public static readonly Vector3 InDockLocal = new Vector3(0f, Lift + CoinBoxFloorTop + 0.035f, ShelfZ);   // inside the coin box, under the slot
        /// <summary>The coin slot: coins and coin pouches touching it here are taken.</summary>
        public static readonly Vector3 ChuteMouthLocal = new Vector3(0f, Lift + ShelfTop + 0.02f, ShelfZ);
        /// <summary>The "Winnings" bubble, over the payout tray near the bottom.</summary>
        private const float RampTop = CoinBoxFloorTop - 0.015f, RampSlope = 12f, RampLength = 0.13f, RampBackZ = D / 2f + ShelfDepth - BodyInset - 0.006f;
        /// <summary>The Winnings bubble: resting near the bottom of the payout ramp, under the coin drum.</summary>
        public static readonly Vector3 OutDockLocal = new Vector3(0f, Lift + RampTop, RampBackZ) +
            Quaternion.Euler(RampSlope, 0f, 0f) * new Vector3(0f, 0.035f, RampLength * 0.5f);
        /// <summary>The coin store holding the bet until the lever is pulled: hidden inside the cabinet; its count shows above the slot.</summary>
        /// <summary>Where the bottom middle of the coin slot's model sits: on the shelf.</summary>
        public static readonly Vector3 SlotSeatLocal = new Vector3(0f, Lift + ShelfTop, ShelfZ);
        public static readonly Vector3 BetLocal = new Vector3(0f, Lift + 0.62f, -0.12f);
        /// <summary>The hidden result-signal dock, inside the cabinet where nobody can see or reach it.</summary>
        public static readonly Vector3 SignalLocal = new Vector3(0f, Lift + 0.45f, -0.05f);

        /// <summary>
        /// The signal dock's coin count after each spin is seq * 100 + kind, where seq changes every spin (so two
        /// identical results in a row still look different) and kind is 0 for a loss, the multiplier for a win,
        /// or <see cref="SignalNoCoins"/> when SPIN was pressed with nothing bet.
        /// </summary>
        public const int SignalNoCoins = 98;
        public static int SignalCode(int seq, int kind) => (seq % 90 + 1) * 100 + kind;


        private static readonly HashSet<SlotMachine> all = new HashSet<SlotMachine>();
        public static IEnumerable<SlotMachine> All => all.Where(m => m != null);

        private static Mesh cube;
        private static GameObject staging;


        public NetworkEntity Entity { get; private set; }

        // ---------- button and reel animation (every game; purely visual) ----------

        /// <summary>The server works out the result this long after the press, so the reels spin first.</summary>
        public const float ResultDelay = 2.0f;
        /// <summary>Presses are at least this many seconds apart.</summary>
        public const float PressCooldown = 1f;

        private static int symbolCount;                            // how many reel symbols, set when the prefab is built
        private readonly GameObject[,][] reelIcons = new GameObject[3, 3][];   // [reel, row][symbol]: row 0 above, 1 on the win line, 2 below
        private readonly int[,] shown = new int[3, 3];             // which symbol each reel row shows
        private PickupDock signal;
        private int signalAtPress, spinMultiplier;
        private float lastPress = -10f;
        private bool spinning;
        private float spinStart, nextTick;
        private float[] stopAt;
        private bool[] stopped;
        private int[] finalSymbol;

        private void Awake()
        {
            Entity = GetComponent<NetworkEntity>();
            leverPivot = transform.Find("Visual/Lever Pivot");
            handle = leverPivot != null ? leverPivot.GetComponentInChildren<Pickup>(true) : null;
            for (int r = 0; r < 3; r++)
                for (int row = 0; row < 3; row++)
                {
                    Transform slot = transform.Find($"Visual/Reel Slot {r + 1}-{row}");
                    if (slot == null) continue;
                    var icons = new GameObject[slot.childCount];
                    for (int k = 0; k < icons.Length; k++)
                    {
                        icons[k] = slot.GetChild(k).gameObject;
                        if (icons[k].activeSelf) shown[r, row] = k;
                    }
                    reelIcons[r, row] = icons;
                }
            var ring = new List<Renderer>();
            for (int i = 1; ; i++)
            {
                Renderer b = transform.Find("Visual/Bulb " + i)?.GetComponent<Renderer>();
                if (b == null) break;
                ring.Add(b);
            }
            bulbs = ring.ToArray();
            signGlow = transform.Find("Visual/Sign/Sign Glow")?.GetComponent<Light>();
            frontGlow = transform.Find("Visual/Glow")?.GetComponent<Light>();
            var glows = new List<Renderer>();
            for (int i = 0; ; i++)
            {
                Renderer g = transform.Find("Visual/Paytable Glow " + i)?.GetComponent<Renderer>();
                if (g == null) break;
                glows.Add(g);
            }
            rowGlows = glows.ToArray();
            if (!ApplicationManager.IsHeadless && !trianglesLogged) triangleLogAt = Time.time + 3f;   // once the labels have drawn
            if (!ApplicationManager.IsHeadless) RefreshLabels();
        }

        private void OnEnable() => all.Add(this);
        private void OnDisable() => all.Remove(this);

        /// <summary>Raised on every game (server included) when a hand pushes the SPIN button down. Args: machine, player id.</summary>
        public static event Action<SlotMachine, int> Pressed;

        // ---------- staying cheap: only do work when someone is close enough for it to matter ----------

        private const float PressCheckRange = 3f;   // nobody further than this can be pressing the button
        private const float AnimateRange = 25f;     // past this the lights and sign aren't worth animating
        private const float LightRange = 10f;       // the real lights only switch on within this distance

        private float nextRangeCheck;
        private bool playerInReach, viewerNear = true, viewerClose = true;
        private Light frontGlow;

        /// <summary>A few times a second: is any player near enough to press, and how far is this game's own view?</summary>
        private void CheckRanges(float now)
        {
            if (now < nextRangeCheck) return;
            nextRangeCheck = now + 0.25f;
            Vector3 here = transform.position;
            float nearest = float.MaxValue;
            foreach (Alta.Networking.Scripts.Player.IPlayer ip in global::Player.AllPlayers)
            {
                PlayerController pc = (ip as global::Player)?.PlayerController;
                if (pc == null || pc.Head == null) continue;
                nearest = Mathf.Min(nearest, (pc.Head.position - here).sqrMagnitude);
            }
            playerInReach = nearest <= PressCheckRange * PressCheckRange;
            if (!ApplicationManager.IsHeadless)
            {
                Camera cam = Camera.main;
                float d = cam != null ? (cam.transform.position - here).magnitude : 0f;
                viewerNear = d <= AnimateRange;
                viewerClose = d <= LightRange;
                if (frontGlow != null && frontGlow.enabled != viewerClose) frontGlow.enabled = viewerClose;
                if (signGlow != null && signGlow.enabled != viewerClose) signGlow.enabled = viewerClose;
                if (viewerNear) ShowBet();
            }
        }

        private TextRenderer betCountText;
        private PickupDock betStore;
        private int betShown = -1;

        /// <summary>Shows how many coins are bet (blank when none), read from the machine's hidden coin store.</summary>
        private void ShowBet()
        {
            if (betCountText == null) betCountText = transform.Find(BetCountName)?.GetComponent<TextRenderer>();
            if (betStore == null) SlotDocks.Find(this, out betStore, out _);   // the coin slot holds the bet
            if (betCountText == null || betStore == null) return;
            int bet = betStore.HasDockedPickup ? betStore.QuantityStored : 0;
            if (bet == betShown) return;
            betShown = bet;
            try { betCountText.Text = bet > 0 ? bet.ToString() : ""; } catch (Exception) { }
        }

        private void Update()
        {
            float now = Time.time;
            CheckRanges(now);

            // Every game runs the same lever check, so the server and every player's reels agree on each pull.
            // Skipped entirely while nobody is within reach of the lever.
            if (playerInReach && DetectPull(now, out int puller) && now - lastPress >= PressCooldown)
            {
                lastPress = now;
                StartSpin(now);
                try { Pressed?.Invoke(this, puller); }
                catch (Exception e) { MelonLogger.Error("[The Lucky Spriggull] Lever pull handler failed: " + e); }
            }
            if (!playerInReach) holder = null;
            AnimateLever(Time.deltaTime);

            if (spinning) AnimateReels(now);
            if (!ApplicationManager.IsHeadless)
            {
                if (triangleLogAt > 0f && now >= triangleLogAt) { triangleLogAt = -1f; if (!trianglesLogged) { trianglesLogged = true; LogTriangles(); } }
                AnimateFireworks(now, Time.deltaTime);   // only has work during a win
                if (viewerNear)
                {
                    AnimateBulbs(now);
                    AnimateRowGlow(now);
                    float sign = SlotSign.Animate(now);
                    if (signGlow != null && viewerClose) signGlow.intensity = 1.4f * sign;
                }
            }
        }

        private Light signGlow;

        // ---------- triangle count (logged once per game, a few seconds after the first machine appears) ----------

        private static bool trianglesLogged;
        private float triangleLogAt = -1f;

        /// <summary>Logs how many triangles this machine draws, by part, plus the hidden reel copies kept in memory.</summary>
        private void LogTriangles()
        {
            try
            {
                var drawn = new SortedDictionary<string, long>();
                long hidden = 0, total = 0;
                foreach (MeshFilter mf in GetComponentsInChildren<MeshFilter>(true))
                {
                    Mesh mesh = mf.sharedMesh;
                    if (mesh == null) continue;
                    long tris = 0;
                    for (int s = 0; s < mesh.subMeshCount; s++)
                        if (mesh.GetTopology(s) == MeshTopology.Triangles) tris += mesh.GetIndexCount(s) / 3;
                    Renderer r = mf.GetComponent<Renderer>();
                    bool visible = r != null && r.enabled && mf.gameObject.activeInHierarchy;
                    if (!visible) { if (mf.transform.IsChildOf(transform.Find("Visual"))) hidden += tris; continue; }
                    string part = PartOf(mf.transform);
                    drawn[part] = (drawn.TryGetValue(part, out long t) ? t : 0) + tris;
                    total += tris;
                }
                MelonLogger.Msg($"[The Lucky Spriggull] Triangles drawn by one machine: {total:N0} (" +
                    string.Join(", ", drawn.Select(kv => $"{kv.Key} {kv.Value:N0}").ToArray()) +
                    $"). Hidden reel copies in memory, not drawn: {hidden:N0}.");
            }
            catch (Exception e) { MelonLogger.Warning("[The Lucky Spriggull] Couldn't count triangles: " + e.Message); }
        }

        private string PartOf(Transform t)
        {
            for (Transform p = t; p != null && p != transform; p = p.parent)
            {
                string n = p.name;
                if (n.StartsWith("Reel Slot")) return "reel creatures";
                if (n == "Sign") return "sign";
                if (n.StartsWith("Glyph ")) return "glyphs";
                if (n.StartsWith(LabelPrefix)) return "lettering";
                if (n.StartsWith("Bulb ")) return "bulbs";
                if (n.EndsWith(SlotDocks.InName) || n.EndsWith(SlotDocks.OutName) || n.EndsWith(SlotDocks.BetName) || n.EndsWith(SlotDocks.SignalName)) return "coin bubbles";
            }
            return "cabinet";
        }

        // ---------- the winning paytable row lights up ----------

        private Renderer[] rowGlows = new Renderer[0];
        private int glowRow = -1;

        /// <summary>During a win's celebration, the paytable row for that prize pulses warm; otherwise every row is dark.</summary>
        private void AnimateRowGlow(float now)
        {
            if (rowGlows.Length == 0) return;
            bool celebrating = now < celebrateUntil && glowRow >= 0 && glowRow < rowGlows.Length;
            bool on = celebrating && ((int)(now * 4f) & 1) == 0;   // a slower pulse than the bulbs
            for (int i = 0; i < rowGlows.Length; i++)
            {
                bool want = on && i == glowRow;
                if (rowGlows[i] != null && rowGlows[i].enabled != want) rowGlows[i].enabled = want;
            }
        }

        private int SignalCount()
        {
            if (signal == null) SlotDocks.Find(this, out _, out _, out signal);
            return signal != null ? signal.QuantityStored : 0;
        }

        private void StartSpin(float now)
        {
            if (symbolCount == 0 || reelIcons[0, 1] == null || spinning) return;
            spinning = true;
            spinStart = now;
            signalAtPress = SignalCount();
            stopAt = null;
            stopped = new bool[3];
            finalSymbol = null;
        }

        /// <summary>
        /// The reels flick through symbols, then stop left to right on the result the server put in the hidden signal
        /// dock: three of the prize creature on a win, a mix on a loss. The server pays out only after they've stopped.
        /// </summary>
        private void AnimateReels(float now)
        {
            float t = now - spinStart;

            if (stopAt == null)
            {
                int code = SignalCount();
                int kind = code != signalAtPress && code > 0 ? code % 100 : -1;
                if (kind == SignalNoCoins) { if (t >= 0.6f) Decide(now, 0); }   // nothing was bet
                else if (kind >= 0) { if (t >= 1.0f) Decide(now, kind); }
                else if (t >= ResultDelay + 3f) Decide(now, 0);                // never heard back: stop on a loss
            }

            if (now >= nextTick)
            {
                nextTick = now + 0.06f;
                for (int r = 0; r < 3; r++)
                {
                    if (stopped[r]) continue;
                    if (stopAt != null && now >= stopAt[r])
                    {
                        stopped[r] = true;
                        int s = finalSymbol[r];
                        Set(r, 1, s);
                        Set(r, 0, (s + 1 + UnityEngine.Random.Range(0, symbolCount - 1)) % symbolCount);
                        Set(r, 2, (s + 1 + UnityEngine.Random.Range(0, symbolCount - 1)) % symbolCount);
                        continue;
                    }
                    // Roll the reel down one symbol.
                    Set(r, 2, shown[r, 1]);
                    Set(r, 1, shown[r, 0]);
                    Set(r, 0, UnityEngine.Random.Range(0, symbolCount));
                }
                if (stopped.All(s => s))
                {
                    spinning = false;
                    if (spinWon) Celebrate(now);
                }
            }
        }

        // ---------- topper lights: a ring of bulbs with two "comets" chasing round it; all flash on a win ----------

        private static Material bulbOn, bulbOff;
        private static Material[] sparkMats;
        private Renderer[] bulbs;
        private float celebrateUntil = -1f;
        private bool spinWon;

        private void AnimateBulbs(float now)
        {
            if (bulbs == null || bulbs.Length == 0 || bulbOn == null) return;
            int n = bulbs.Length;
            // The pattern only changes 10-12 times a second, so nothing is touched between steps.
            int step = now < celebrateUntil ? -1 - (int)(now * 10f) : (int)(now * 12f);
            if (step == lastBulbStep) return;
            lastBulbStep = step;
            if (bulbLit == null || bulbLit.Length != n) bulbLit = new sbyte[n];
            if (now < celebrateUntil)
            {
                bool on = ((int)(now * 10f) & 1) == 0;   // everything blinks together
                for (int i = 0; i < n; i++) SetBulb(i, on);
                return;
            }
            int head = step % n;
            for (int i = 0; i < n; i++)
            {
                int behind = (head - i + n) % n;          // how far this bulb is behind the comet's head
                int behind2 = (head + n / 2 - i + n) % n;   // the second comet, opposite
                SetBulb(i, behind < 3 || behind2 < 3);
            }
        }

        private int lastBulbStep = int.MinValue;
        private sbyte[] bulbLit;   // 0 unknown, 1 lit, -1 dark: only bulbs that change get a new material

        private void SetBulb(int i, bool on)
        {
            sbyte want = on ? (sbyte)1 : (sbyte)-1;
            if (bulbLit[i] == want) return;
            bulbLit[i] = want;
            bulbs[i].sharedMaterial = on ? bulbOn : bulbOff;
        }

        // ---------- fireworks: little rockets that pop into falling, fading sparks ----------

        private sealed class Spark
        {
            public Transform T;
            public Vector3 Velocity;
            public float Born, Life, Size;
            public bool IsRocket;
            public Material Mat;
        }

        private readonly List<Spark> sparks = new List<Spark>();
        private readonly List<KeyValuePair<float, Vector3>> rocketsToLaunch = new List<KeyValuePair<float, Vector3>>();
        private Light flash;
        private float flashUntil;

        private void Celebrate(float now)
        {
            celebrateUntil = now + 3f;
            if (ApplicationManager.IsHeadless) return;
            glowRow = PaytableRows.FindIndex(r => r.Value == spinMultiplier);   // which paytable row this prize is
            WinSound.Play(this);
            int rockets = Bonus.IsBonus(spinMultiplier) ? 4 : spinMultiplier >= 20 ? 8 : spinMultiplier >= 5 ? 5 : 3;   // bigger prize, bigger show
            for (int i = 0; i < rockets; i++)
            {
                Vector3 from = new Vector3(UnityEngine.Random.Range(-0.22f, 0.22f), Lift + CabTop + TopperH + 0.03f, -0.02f);   // from the top of the topper
                rocketsToLaunch.Add(new KeyValuePair<float, Vector3>(now + i * 0.35f, from));
            }
        }

        private void AnimateFireworks(float now, float dt)
        {
            for (int i = rocketsToLaunch.Count - 1; i >= 0; i--)
            {
                if (now < rocketsToLaunch[i].Key) continue;
                Vector3 from = transform.TransformPoint(rocketsToLaunch[i].Value);
                rocketsToLaunch.RemoveAt(i);
                Vector3 up = transform.up * UnityEngine.Random.Range(2.4f, 3.0f) + transform.forward * UnityEngine.Random.Range(0.2f, 0.5f)
                             + transform.right * UnityEngine.Random.Range(-0.3f, 0.3f);
                AddSpark(from, up, 0.4f, 0.02f, bulbOn, rocket: true, now);
            }

            for (int i = sparks.Count - 1; i >= 0; i--)
            {
                Spark s = sparks[i];
                float age = now - s.Born;
                if (s.T == null) { sparks.RemoveAt(i); continue; }
                if (age >= s.Life)
                {
                    if (s.IsRocket) Burst(s.T.position, now);
                    Destroy(s.T.gameObject);
                    sparks.RemoveAt(i);
                    continue;
                }
                s.Velocity += Physics.gravity * (s.IsRocket ? 0.15f : 0.25f) * dt;
                s.Velocity *= 1f - (s.IsRocket ? 0f : 1.5f) * dt;   // a little air drag on sparks
                s.T.position += s.Velocity * dt;
                float k = s.IsRocket ? 1f : 1f - age / s.Life;      // sparks shrink away
                s.T.localScale = Vector3.one * s.Size * Mathf.Max(k, 0.05f);
            }

            if (flash != null)
            {
                float left = flashUntil - now;
                flash.intensity = left > 0f ? 4f * left / 0.4f : 0f;
                flash.enabled = left > 0f;
            }
        }

        private void Burst(Vector3 at, float now)
        {
            WinSound.PlayFirework(gameObject, at);
            Material colour = sparkMats[UnityEngine.Random.Range(0, sparkMats.Length)];
            for (int i = 0; i < 26; i++)   // enough sparks for a full burst, without the extra objects
            {
                Vector3 v = UnityEngine.Random.onUnitSphere * UnityEngine.Random.Range(1.2f, 2.0f) + Vector3.up * 0.4f;
                // Mostly one colour per burst, with a few gold sparkles.
                Material m = UnityEngine.Random.value < 0.8f ? colour : bulbOn;
                AddSpark(at, v, UnityEngine.Random.Range(0.7f, 1.2f), 0.014f, m, rocket: false, now);
            }
            if (flash == null)
            {
                var go = new GameObject("Slot Firework Flash");
                go.transform.SetParent(transform, false);
                flash = go.AddComponent<Light>();
                flash.type = LightType.Point;
                flash.range = 3f;
                flash.shadows = LightShadows.None;
            }
            flash.transform.position = at;
            flash.color = colour.HasProperty("_Color") ? colour.color : Color.white;
            flashUntil = now + 0.4f;
        }

        private void AddSpark(Vector3 at, Vector3 velocity, float life, float size, Material mat, bool rocket, float now)
        {
            var go = new GameObject(rocket ? "Slot Firework Rocket" : "Slot Firework Spark");
            go.transform.position = at;
            go.transform.rotation = UnityEngine.Random.rotation;
            go.transform.localScale = Vector3.one * size;
            go.AddComponent<MeshFilter>().sharedMesh = Cube();
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            sparks.Add(new Spark { T = go.transform, Velocity = velocity, Born = now, Life = life, Size = size, IsRocket = rocket, Mat = mat });
        }

        private void OnDestroy()
        {
            foreach (Spark s in sparks) if (s.T != null) Destroy(s.T.gameObject);
            sparks.Clear();
        }

        private void Decide(float now, int multiplier)
        {
            stopAt = new[] { now, now + 0.35f, now + 0.7f };
            finalSymbol = PickResult(multiplier);
            spinWon = multiplier > 0;
            spinMultiplier = multiplier;
        }

        /// <summary>A win shows three of its prize creature (Spriggulls for the jackpot, Goteras for the smallest prize).</summary>
        private int[] PickResult(int multiplier)
        {
            int n = symbolCount;
            if (Bonus.IsBonus(multiplier))
            {
                int b = SlotIcons.Names.IndexOf(Bonus.SymbolFor(multiplier));
                if (b >= 0) return new[] { b, b, b };
            }
            else if (multiplier > 0)
            {
                int s = SlotIcons.ForMultiplier(multiplier);
                return new[] { s, s, s };
            }
            var r = new[] { UnityEngine.Random.Range(0, n), UnityEngine.Random.Range(0, n), UnityEngine.Random.Range(0, n) };
            if (r[0] == r[1] && r[1] == r[2]) r[2] = (r[2] + 1) % n;   // never show three of a kind on a loss
            return r;
        }

        private void Set(int r, int row, int symbol)
        {
            GameObject[] icons = reelIcons[r, row];
            if (icons == null || symbol < 0 || symbol >= icons.Length) return;
            int old = shown[r, row];
            if (old != symbol && old >= 0 && old < icons.Length) icons[old].SetActive(false);
            icons[symbol].SetActive(true);
            shown[r, row] = symbol;
        }

        // ---------- the lever: grab the gold knob and pull it forward and down ----------

        /// <summary>The lever's pivot, on the right side of the cabinet as you face it (the machine's -X side), near the front so it's in reach.</summary>
        public static readonly Vector3 LeverPivotLocal = new Vector3(-(W / 2f) - 0.04f, Lift + 0.80f, D / 2f - 0.07f);
        public const float LeverLength = 0.34f;       // pivot to the middle of the knob
        public const float KnobRadius = 0.034f;
        /// <summary>At rest the lever tips a little toward the player (and just slightly out from the cabinet), ready to pull.</summary>
        public const float LeverLean = 5f;
        public const float RestAngle = 30f;
        public static readonly Quaternion LeanTurn = Quaternion.Euler(0f, 0f, LeverLean);
        private const float GrabRange = 0.11f;        // (no game handle) a hand this close to the knob takes hold of it
        private const float LetGoRange = 0.26f;       // ...and lets go once it's this far from where the knob is
        private const float PullAngle = 72f;          // pulled this far forward (degrees from upright), it spins
        private const float MaxAngle = 95f;

        private string holder;          // who holds the knob, or null
        private bool firedThisPull;     // one spin per pull: the hand has to let go before pulling again
        private float leverAngle = RestAngle;   // 0 = upright, + = forward and down (rests at RestAngle)
        private Transform leverPivot;
        private Pickup handle;          // the grabbable handle on the knob (a copy of a game lever's), if there is one

        /// <summary>The knob's centre (machine space) for a given lever angle.</summary>
        private static Vector3 KnobAt(float angle) =>
            LeverPivotLocal + LeanTurn * (Quaternion.Euler(angle, 0f, 0f) * new Vector3(0f, LeverLength, 0f));

        /// <summary>How far forward a hand at this point (machine space) has pulled the lever, in degrees.</summary>
        private static float AngleFor(Vector3 local)
        {
            Vector3 fromPivot = Quaternion.Inverse(LeanTurn) * (local - LeverPivotLocal);
            return Mathf.Clamp(Mathf.Atan2(fromPivot.z, fromPivot.y) * Mathf.Rad2Deg, 0f, MaxAngle);
        }

        /// <summary>
        /// While a player holds the lever's handle (a real grab, as on the game's own levers), the lever follows their hand
        /// round its pivot. Pulled past <see cref="PullAngle"/>, it spins (once per pull). Let go and it springs back.
        /// Every game runs this the same way (who holds what is shared), so the server and every player see the same pull.
        /// </summary>
        private bool DetectPull(float now, out int puller)
        {
            puller = 0;
            Vector3 hand;
            if (handle != null)
            {
                if (!handle.IsInteractedWith || handle.Interactors.Count == 0) { holder = null; firedThisPull = false; return false; }
                Vector3 sum = Vector3.zero;
                int n = 0;
                foreach (Interactor i in handle.Interactors)
                {
                    if (i == null || i.Controller == null || i.Controller.GripPoint == null) continue;
                    sum += i.Controller.GripPoint.position;
                    n++;
                }
                if (n == 0) return false;
                hand = transform.InverseTransformPoint(sum / n);
                holder = "handle";
                try { puller = handle.Interactors[0].Controller.PlayerController.NetworkPlayer.UserInfo.Identifier; } catch (Exception) { }
            }
            else
            {
                // No game handle to grab: a hand that reaches the knob takes hold of it.
                Vector3 knob = KnobAt(leverAngle);
                HandPoint? holding = null;
                foreach (HandPoint h in HandPoints())
                {
                    string key = h.PlayerId + "/" + h.Name;
                    Vector3 local = transform.InverseTransformPoint(h.Position);
                    if (holder == null && (local - knob).magnitude <= GrabRange) holder = key;
                    if (key == holder) holding = h;
                }
                if (holding == null) { holder = null; firedThisPull = false; return false; }
                hand = transform.InverseTransformPoint(holding.Value.Position);
                if ((hand - knob).magnitude > LetGoRange) { holder = null; firedThisPull = false; return false; }
                puller = holding.Value.PlayerId;
            }

            leverAngle = AngleFor(hand);
            if (firedThisPull || leverAngle < PullAngle) return false;
            firedThisPull = true;
            return true;
        }

        /// <summary>Shows the lever at its angle; while nobody holds it, it springs back. Keeps the handle on the knob.</summary>
        private void AnimateLever(float dt)
        {
            if (holder == null && leverAngle != RestAngle) leverAngle = Mathf.MoveTowards(leverAngle, RestAngle, dt * 260f);
            if (leverPivot != null) leverPivot.localRotation = LeanTurn * Quaternion.Euler(leverAngle, 0f, 0f);
            if (handle != null && leverPivot != null && !handle.IsInteractedWith)
            {
                Transform h = handle.transform;
                if (h.parent != leverPivot) h.SetParent(leverPivot, false);
                if (h.localPosition != new Vector3(0f, LeverLength, 0f)) { h.localPosition = new Vector3(0f, LeverLength, 0f); h.localRotation = Quaternion.identity; }
            }
        }

        /// <summary>A point relative to the knob at rest, in the machine's axes (x right, y up, z out the front). For logging.</summary>
        public Vector3 ButtonOffset(Vector3 world) => transform.InverseTransformPoint(world) - KnobAt(0f);

        public struct HandPoint { public int PlayerId; public string Name; public Vector3 Position; }

        /// <summary>
        /// Every point the game tracks for each player's hands: the controller itself, its grip point and the hand model.
        /// The hand you see can sit several centimetres from the controller, so any of them counts.
        /// </summary>
        public static IEnumerable<HandPoint> HandPoints()
        {
            foreach (Alta.Networking.Scripts.Player.IPlayer ip in global::Player.AllPlayers)
            {
                PlayerController pc = (ip as global::Player)?.PlayerController;
                if (pc == null) continue;
                int id = ip.UserInfo.Identifier;
                foreach (var side in new[] { new KeyValuePair<string, Controller>("left", pc.LeftController), new KeyValuePair<string, Controller>("right", pc.RightController) })
                {
                    Controller c = side.Value;
                    if (c == null) continue;
                    yield return new HandPoint { PlayerId = id, Name = side.Key + " controller", Position = c.transform.position };
                    if (c.GripPoint != null) yield return new HandPoint { PlayerId = id, Name = side.Key + " grip", Position = c.GripPoint.position };
                    if (c.Hand != null) yield return new HandPoint { PlayerId = id, Name = side.Key + " hand", Position = c.Hand.transform.position };
                }
            }
        }

        // ---------- building the prefab ----------

        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags Stat = BindingFlags.Static | BindingFlags.NonPublic;

        public static void Register(MelonLogger.Instance log)
        {
            PrefabManager.PrepareSpawnSetups();
            if (PrefabManager.Exists(PrefabHash))
                throw new InvalidOperationException($"Prefab hash {PrefabHash} is already used by another mod.");

            if (staging == null)
            {
                staging = new GameObject("Slot Machine template (inactive)");
                staging.SetActive(false);
                DontDestroyOnLoad(staging);
            }

            var root = new GameObject(Name);
            root.transform.SetParent(staging.transform, false);
            root.SetActive(true);

            Material baseMat = SlotDocks.DonorMaterial();
            BuildVisual(root, baseMat, log);

            NetworkEntity entity = root.AddComponent<NetworkEntity>();
            NetworkPrefab prefab = root.AddComponent<NetworkPrefab>();
            prefab.SaveMode = ATT.Saving.SaveMode.Base;
            root.AddComponent<SlotMachine>();

            SlotDocks.Add(root, prefab, log);
            AddGameLabels(root, log);

            // What SyncLib's RegisterCustomPrefab does: fill the [GetComponent]-style fields, set the hash, register.
            ApplyPrefabAttributes(entity);
            ApplyPrefabAttributes(prefab);
            typeof(NetworkPrefab).GetField("hash", Inst).SetValue(prefab, (int)PrefabHash);
            prefab.Initialize();
            entity.OnValidate();
            typeof(PrefabManager).GetMethod("AddToPrefabMap", Stat).Invoke(null, new object[] { new[] { prefab } });

            if (prefab.Hash != PrefabHash) throw new InvalidOperationException("Slot Machine prefab hash did not stick.");
            log.Msg($"Registered structure: {Name} (prefab {PrefabHash}). Spawn it with: spawn <player> {PrefabHash}");
        }

        // ---------- labels in the game's own lettering ----------

        private const string LabelPrefix = "Game Label ";
        private const string BetCountName = "Bet Count";

        /// <summary>
        /// COINS, SPIN and WINNINGS written with the game's own text renderer (the one that shows the count on a dock
        /// bubble, copied from the Winnings bubble), so they use the game's font and look like its signs.
        /// </summary>
        private static void AddGameLabels(GameObject root, MelonLogger.Instance log)
        {
            TextRenderer[] texts = root.GetComponentsInChildren<TextRenderer>(true);
            TextRenderer donor = texts.FirstOrDefault(t => Under(t.transform, SlotDocks.OutName))   // the Winnings bubble's counter
                                 ?? texts.FirstOrDefault(t => t.gameObject.activeSelf);
            if (donor == null) { log.Warning("Labels: no game text renderer to copy; leaving the machine unlabelled."); return; }

            // The Winnings count reads correctly from the front, so every label copies its facing.
            Quaternion facing = Quaternion.Inverse(root.transform.rotation) * donor.transform.rotation;
            Vector3 scale = donor.transform.lossyScale;

            // The dock counter's font only has digits, so take the letters from one of the game's lettered fonts.
            TextFont letters = LetteredFont(out object letterMaterial, out string from);
            if (letters != null) log.Msg($"Labels: using the game's lettered font '{letters.name}' (from {from}).");
            else log.Warning("Labels: found no game font with letters; INPUT and the x's may not show.");

            float front = D / 2f, shelfFront = front + ShelfDepth + 0.003f;
            // Only the coin input is labelled.
            // The bet, in brass numbers on the cabinet just above the coin slot (the dock counter's own digits).
            var brass = new Color(0.93f, 0.78f, 0.42f);
            // Lying flat on the platform between the slot and the front edge, readable from above.
            Quaternion flat = Quaternion.Euler(-90f, 0f, 0f) * facing;
            // BET, with the amount under it, centred on the counter in front of the slot.
            Vector3 counter = new Vector3(0f, Lift + ShelfTop + 0.0015f, D / 2f + ShelfDepth);
            Label(root, donor, "0", counter + new Vector3(0f, 0f, -0.032f), flat, scale * 0.7f, null, FindFieldValue(donor, "material"), brass);
            Transform betCount = root.transform.Find(LabelPrefix + "0");
            if (betCount != null) betCount.name = BetCountName;
            Label(root, donor, "BET", counter + new Vector3(0f, 0f, -0.07f), flat, scale * 0.36f, letters, letterMaterial, brass);
            // INSERT COINS on the sloped front face of the gold slot ring, tilted back to lie on it.
            // Underneath the coin box, where nobody standing up will see it.
            {
                Quaternion under = Quaternion.Euler(90f, 0f, 0f) * facing;   // facing down
                float y = Lift + CoinBoxFloorTop - 0.02f - 0.018f - 0.0015f;
                var ink = new Color(0.08f, 0.05f, 0.03f);
                Label(root, donor, "RIP LEGACY SERVERS", new Vector3(0f, y, D / 2f + 0.17f), under, scale * 0.17f, letters, letterMaterial, ink);
                Label(root, donor, "US1  US2  AUS  EU", new Vector3(0f, y, D / 2f + 0.12f), under, scale * 0.17f, letters, letterMaterial, ink);
            }
            Label(root, donor, "INSERT COINS", new Vector3(0f, Lift + ShelfTop + 0.016f, ShelfZ + 0.064f), Quaternion.Euler(-35f, 0f, 0f) * facing, scale * 0.17f, letters, letterMaterial, new Color(0.08f, 0.05f, 0.03f));
            // The paytable's amounts, at the right of each row of three creatures.
            for (int i = 0; i < PaytableRows.Count; i++)
                // The game's text sits a little above its anchor, so it's lowered to line up with the middle of its row.
                Label(root, donor, PaytableRows[i].Value + "X", new Vector3(PayTextX, Lift + PayRowY(i) - 0.008f, front + PayFace + 0.004f), facing, scale * 0.3f, letters, letterMaterial, Color.black);
            log.Msg($"Labels: the bet counter and {PaytableRows.Count} paytable amounts in the game's lettering.");
        }

        /// <summary>True if this object or one above it is named ...<paramref name="name"/> (works on inactive objects).</summary>
        private static bool Under(Transform t, string name)
        {
            for (; t != null; t = t.parent) if (t.name.EndsWith(name)) return true;
            return false;
        }

        /// <summary>
        /// A game font that has capital letters (the dock counter's font is digits only), taken from any of the game's
        /// own text renderers (signs, boards, books...), with that renderer's material. Null if none is found.
        /// </summary>
        private static TextFont LetteredFont(out object material, out string from)
        {
            material = null; from = null;
            Func<TextFont, int> capitals = f =>
            {
                try
                {
                    var lods = FindFieldValue(f, "textFontLODs") as Array;
                    if (lods == null || lods.Length == 0) return 0;
                    return (FindFieldValue(lods.GetValue(0), "alphaUpper") as Array)?.Length ?? 0;
                }
                catch (Exception) { return 0; }
            };
            TextRenderer best = Resources.FindObjectsOfTypeAll<TextRenderer>()
                .Where(t => t != null && t.Font != null && capitals(t.Font) >= 20)
                .OrderByDescending(t => capitals(t.Font))
                .FirstOrDefault();
            if (best == null) return null;
            material = FindFieldValue(best, "material");
            from = best.gameObject.name;
            return best.Font;
        }

        private static readonly Dictionary<Color, Material> inks = new Dictionary<Color, Material>();

        /// <summary>A copy of a text material with every colour it has set to <paramref name="color"/> (and no glow).</summary>
        private static Material Ink(Material source, Color color)
        {
            if (inks.TryGetValue(color, out Material ink)) return ink;
            ink = new Material(source) { name = "Slot Label Ink" };
            foreach (string p in new[] { "_Color", "_BaseColor", "_MainColor", "_TintColor", "_FaceColor", "_Tint" })
                if (ink.HasProperty(p)) ink.SetColor(p, color);
            if (ink.HasProperty("_EmissionColor")) ink.SetColor("_EmissionColor", Color.black);
            ink.DisableKeyword("_EMISSION");
            inks[color] = ink;
            return ink;
        }

        private static object FindFieldValue(object target, string field)
        {
            for (Type t = target?.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(target);
            }
            return null;
        }


        private static void Label(GameObject root, TextRenderer donor, string text, Vector3 at, Quaternion facing, Vector3 scale, TextFont font, object fontMaterial, Color color)
        {
            GameObject go = Instantiate(donor.gameObject, root.transform, false);
            go.name = LabelPrefix + text;
            go.SetActive(true);
            go.transform.localPosition = at;
            go.transform.localRotation = facing;
            go.transform.localScale = scale;
            TextRenderer tr = go.GetComponent<TextRenderer>();
            SetField(tr, "text", text);
            SetField(tr, "tintColor", color);
            if (font != null) SetField(tr, "font", font);
            // The font's own material is a bright colour that ignores the tint, so each label gets a copy in its own colour.
            if (fontMaterial is Material m) SetField(tr, "material", Ink(m, color));
        }

        /// <summary>Spawned labels redraw their text once the machine exists (the template only stores it).</summary>
        private void RefreshLabels()
        {
            foreach (TextRenderer tr in GetComponentsInChildren<TextRenderer>(true))
                if (tr.gameObject.name.StartsWith(LabelPrefix))
                    try
                    {
                        tr.gameObject.SetActive(true);
                        tr.SetVisibility(true);
                        tr.Text = tr.gameObject.name.Substring(LabelPrefix.Length);
                        tr.Refresh(isForced: true);
                    }
                    catch (Exception e) { MelonLogger.Warning("[The Lucky Spriggull] A label couldn't draw: " + e.Message); }
        }

        // ---------- the paytable ----------

        /// <summary>One row per prize, biggest first: (creature, multiplier). Set from the payout settings before the machine is built.</summary>
        public static List<KeyValuePair<string, int>> PaytableRows { get; private set; } = new List<KeyValuePair<string, int>>();

        private const float PayFace = 0.012f;      // how far the paytable panel stands out from the cabinet front
        // Each row (three creatures, then the amount) is centred on the paytable panel; the machine's +X is the viewer's left.
        private const float PayGlyphX = 0.123f;    // the leftmost creature of each row (the next two step 5.8 cm right)
        private const float PayTextX = -0.112f;    // each row's amount, just to the right of its three creatures

        /// <summary>The height of paytable row <paramref name="i"/> (local to the cabinet's visual).</summary>
        private static float PayRowY(int i)
        {
            const float margin = 0.026f;
            int rows = Math.Max(PaytableRows.Count, 1);
            float rowH = (PayHeight - 2f * margin) / rows;
            return PayBottom + PayHeight - margin - (i + 0.5f) * rowH;
        }

        /// <summary>Uses the machine's own payout settings, so the paytable always matches the real odds (biggest four prizes).</summary>
        public static void SetPayouts(IEnumerable<int> multipliers)
        {
            PaytableRows = multipliers.Distinct().OrderByDescending(m => m).Take(4)
                .Select(m => new KeyValuePair<string, int>(SlotIcons.PrizeFor(m), m)).ToList();
        }

        private static void SetField(object target, string field, object value)
        {
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) { f.SetValue(target, value); return; }
            }
        }

        private static void ApplyPrefabAttributes(Component c)
        {
            for (Type t = c.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    f.GetCustomAttribute<PrefabApplyAttribute>()?.HandleApply(c, f, c.gameObject);
        }

        // ---------- look ----------

        /// <summary>
        /// One of the base game's own metals (its physical material: the texture and colour every item of that metal
        /// uses), tried in order of <paramref name="names"/>, or failing that the matching ingot's material.
        /// Only vanilla names are used, so it looks the same on a server with no other mods. Returns null if none.
        /// </summary>
        private static Material GameMaterial(string role, MelonLogger.Instance log, params string[] names)
        {
            try
            {
                // Try each name fully (its physical material, then its ingot) before falling back to the next name.
                // Names are compared without spaces: the game stores some as e.g. "RedIron".
                Func<string, string> key = s => (s ?? "").Replace(" ", "").ToLowerInvariant();
                foreach (string want in names)
                {
                    PhysicalMaterial pm = HashedGeneralValue<PhysicalMaterial>.All
                        .FirstOrDefault(m => m != null && key(m.name) == key(want));
                    Material m1 = pm?.GetNonAtlasedNonDeformingMaterial(PhysicalMaterialChannel.A) ?? pm?.GetMaterial(PhysicalMaterialChannel.A);
                    if (m1 != null) { log.Msg($"{role}: using the game's {pm.name} material ({m1.name})."); return m1; }

                    Item ingot = HashedGeneralValue<Item>.All.FirstOrDefault(i => i != null && key(i.name) == key(want + " Ingot"));
                    Material m2 = ingot?.Prefab?.GetComponentsInChildren<MeshRenderer>(true).Select(r => r.sharedMaterial).FirstOrDefault(m => m != null);
                    if (m2 != null) { log.Msg($"{role}: using the {ingot.name}'s material ({m2.name})."); return m2; }
                }
                log.Warning($"{role}: couldn't find the game's {string.Join(" or ", names)}; using a plain colour.");
            }
            catch (Exception e)
            {
                log.Warning($"{role}: couldn't find the game's {string.Join(" or ", names)} ({e.Message}); using a plain colour.");
            }
            return null;
        }

        private static void BuildVisual(GameObject root, Material baseMat, MelonLogger.Instance log)
        {
            int layer = LayerMask.NameToLayer("Ground");
            if (layer < 0) layer = 0;

            // The body is built from the base game's own metals, used exactly as the game draws them, so the machine
            // looks like part of the game: Red Iron for the cabinet, Iron (the lantern metal) for every dark part and
            // the coin chute, and Gold (the gold bars' material) for the trim.
            Material red = GameMaterial("Cabinet", log, "Red Iron", "Redwood") ?? Mat(baseMat, "Slot Cabinet", new Color(0.55f, 0.06f, 0.07f), Color.black, 0.45f, 0.1f);
            Material darkRed = red;
            Material gold = GameMaterial("Gold trim", log, "Gold") ?? Mat(baseMat, "Slot Gold", new Color(0.95f, 0.72f, 0.22f), new Color(0.08f, 0.05f, 0f), 0.75f, 0.85f);
            goldMaterial = gold;   // shown with more of its texture (see CubeFor)
            Material black = GameMaterial("Dark metal", log, "Iron") ?? Mat(baseMat, "Slot Black", new Color(0.04f, 0.04f, 0.05f), Color.black, 0.8f, 0.2f);
            Material white = Mat(baseMat, "Slot Reel", new Color(0.95f, 0.93f, 0.86f), new Color(0.25f, 0.24f, 0.2f), 0.3f, 0f);
            Material cherry = Mat(baseMat, "Slot Cherry", new Color(0.85f, 0.08f, 0.15f), new Color(0.25f, 0f, 0.03f), 0.5f, 0f);
            Material seven = Mat(baseMat, "Slot Seven", new Color(1f, 0.75f, 0.05f), new Color(0.3f, 0.2f, 0f), 0.5f, 0.3f);
            Material bell = Mat(baseMat, "Slot Bell", new Color(0.15f, 0.7f, 0.25f), new Color(0.03f, 0.18f, 0.05f), 0.5f, 0f);
            Material bulb = Mat(baseMat, "Slot Bulb", new Color(1f, 0.9f, 0.55f), new Color(1.6f, 1.2f, 0.5f), 0.6f, 0f);
            Material button = Mat(baseMat, "Slot Button", new Color(0.95f, 0.1f, 0.08f), new Color(0.6f, 0.03f, 0.02f), 0.7f, 0f);

            var v = new GameObject("Visual") { layer = layer };
            v.transform.SetParent(root.transform, false);
            v.transform.localPosition = new Vector3(0f, Lift, 0f);   // everything below is laid out from the top of the pedestal
            Box(v, "Pedestal", new Vector3(0, -Lift / 2, 0), new Vector3(W + 0.02f, Lift, D + 0.02f), black, true);
            float front = D / 2f;

            // Base, cabinet and topper.
            Box(v, "Base", new Vector3(0, BaseH / 2, 0), new Vector3(W + 0.06f, BaseH, D + 0.06f), black, true);
            Box(v, "Cabinet", new Vector3(0, (BaseH + CabTop) / 2, 0), new Vector3(W, CabTop - BaseH, D), red, true);
            Box(v, "Trim Left", new Vector3(-W / 2 + 0.015f, (BaseH + CabTop) / 2, front + 0.005f), new Vector3(0.03f, CabTop - BaseH, 0.01f), gold, false);
            Box(v, "Trim Right", new Vector3(W / 2 - 0.015f, (BaseH + CabTop) / 2, front + 0.005f), new Vector3(0.03f, CabTop - BaseH, 0.01f), gold, false);
            Box(v, "Topper", new Vector3(0, CabTop + TopperH / 2f, -0.02f), new Vector3(W, TopperH, D - 0.04f), darkRed, true);
            Box(v, "Topper Trim", new Vector3(0, CabTop + 0.005f, 0), new Vector3(W + 0.02f, 0.02f, D + 0.02f), gold, false);
            Box(v, "Topper Cap", new Vector3(0, CabTop + TopperH + 0.005f, -0.02f), new Vector3(W + 0.02f, 0.02f, D - 0.02f), gold, false);
            // The marquee: "THE LUCKY SPRIGGULL" in warm bulbs, on the cabinet front between the screen and the light band.
            SlotSign.Build(v, new Vector3(0f, SignBottom, front + SlotSign.Depth / 2f), Cube(), black, gold, baseMat);
            // A band of bulbs wrapping right round the topper (front, right side, back, left side), numbered in
            // order round the machine so the lights can chase round it.
            bulbOn = bulb;
            bulbOff = Mat(baseMat, "Slot Bulb Off", new Color(0.45f, 0.38f, 0.22f), new Color(0.06f, 0.04f, 0.01f), 0.6f, 0f);
            sparkMats = new[]
            {
                Mat(baseMat, "Slot Spark Red", new Color(1f, 0.2f, 0.2f), new Color(2.2f, 0.25f, 0.25f), 0.5f, 0f),
                Mat(baseMat, "Slot Spark Green", new Color(0.3f, 1f, 0.35f), new Color(0.3f, 2.2f, 0.4f), 0.5f, 0f),
                Mat(baseMat, "Slot Spark Blue", new Color(0.35f, 0.55f, 1f), new Color(0.4f, 0.7f, 2.4f), 0.5f, 0f),
                Mat(baseMat, "Slot Spark Pink", new Color(1f, 0.4f, 0.9f), new Color(2.2f, 0.5f, 2f), 0.5f, 0f),
                Mat(baseMat, "Slot Spark White", new Color(1f, 1f, 1f), new Color(2f, 2f, 2f), 0.5f, 0f),
            };
            {
                float by = CabTop + TopperH / 2f + 0.005f, zf = front - 0.04f + 0.006f, zb = -0.02f - (D - 0.04f) / 2f - 0.006f;
                float xr = W / 2f + 0.006f, xl = -xr, xs = W / 2f - 0.035f;
                var band = new List<KeyValuePair<Vector3, Vector3>>();   // position, size
                Vector3 faceSize = new Vector3(0.028f, 0.028f, 0.012f), sideSize = new Vector3(0.012f, 0.028f, 0.028f);
                const int along = 8, across = 5;
                for (int i = 0; i < along; i++) band.Add(new KeyValuePair<Vector3, Vector3>(new Vector3(Mathf.Lerp(-xs, xs, i / (float)(along - 1)), by, zf), faceSize));
                for (int i = 0; i < across; i++) band.Add(new KeyValuePair<Vector3, Vector3>(new Vector3(xr, by, Mathf.Lerp(zf - 0.05f, zb + 0.05f, i / (float)(across - 1))), sideSize));
                for (int i = 0; i < along; i++) band.Add(new KeyValuePair<Vector3, Vector3>(new Vector3(Mathf.Lerp(xs, -xs, i / (float)(along - 1)), by, zb), faceSize));
                for (int i = 0; i < across; i++) band.Add(new KeyValuePair<Vector3, Vector3>(new Vector3(xl, by, Mathf.Lerp(zb + 0.05f, zf - 0.05f, i / (float)(across - 1))), sideSize));
                for (int i = 0; i < band.Count; i++)
                    Box(v, "Bulb " + (i + 1), band[i].Key, band[i].Value, bulbOff, false);
            }

            // Screen with three reels.
            float sy = 0.88f;
            Box(v, "Screen Frame", new Vector3(0, sy, front + 0.01f), new Vector3(0.48f, 0.34f, 0.02f), gold, false);
            Box(v, "Screen", new Vector3(0, sy, front + 0.021f), new Vector3(0.44f, 0.30f, 0.004f), black, false);
            Material bar = Mat(baseMat, "Slot Bar", new Color(0.15f, 0.35f, 0.9f), new Color(0.03f, 0.08f, 0.25f), 0.5f, 0f);
            // The symbols: tiny creatures. Each reel row holds one copy of every symbol and shows one at a time.
            var templates = new GameObject("Reel symbol templates");
            templates.transform.SetParent(staging.transform, false);
            List<GameObject> icons = SlotIcons.Build(templates.transform, Cube(), new[] { cherry, seven, bell, bar }, log);
            symbolCount = icons.Count;
            float[] rowY = { sy + 0.09f, sy, sy - 0.09f };
            for (int i = 0; i < 3; i++)
            {
                float x = -0.14f + i * 0.14f;
                Box(v, "Reel " + (i + 1), new Vector3(x, sy, front + 0.025f), new Vector3(0.12f, 0.26f, 0.004f), white, false);
                for (int row = 0; row < 3; row++)
                {
                    var slot = new GameObject($"Reel Slot {i + 1}-{row}");
                    slot.transform.SetParent(v.transform, false);
                    slot.transform.localPosition = new Vector3(x, rowY[row], front + 0.065f);
                    slot.transform.localScale = Vector3.one * (row == 1 ? 1f : 0.62f);
                    int start = symbolCount > 0 ? (i * 2 + row + 3) % symbolCount : 0;
                    for (int k = 0; k < icons.Count; k++)
                    {
                        GameObject copy = Instantiate(icons[k], slot.transform, false);
                        copy.name = "Symbol " + k;
                        copy.SetActive(k == start);
                    }
                }
            }
            Box(v, "Win Line", new Vector3(0, sy, front + 0.033f), new Vector3(0.44f, 0.006f, 0.002f), cherry, false);

            // Spriggulls only: the game's own inked Spriggull drawing (a book glyph), etched into both sides of the
            // cabinet and twice into the front of the pedestal, in a darker shade of each panel.
            {
                Glyph spriggull = SlotGlyphs.Find("Spriggull");
                if (spriggull != null)
                {
                    var redInk = new Color(0.16f, 0.03f, 0.02f, 0.75f);
                    var ironInk = new Color(0.06f, 0.06f, 0.07f, 0.8f);
                    SlotGlyphs.Engrave(v, "Spriggull Right", spriggull, new Vector3(W / 2f, 0.9f, 0f), Vector3.right, 0.34f, redInk);
                    SlotGlyphs.Engrave(v, "Spriggull Left", spriggull, new Vector3(-W / 2f, 0.42f, 0f), Vector3.left, 0.3f, redInk);   // below the lever on this side
                    float pedestalFront = (D + 0.02f) / 2f;
                    SlotGlyphs.Engrave(v, "Spriggull Pedestal", spriggull, new Vector3(0f, -Lift / 2f, pedestalFront), Vector3.forward, 0.2f, ironInk);
                    if (!ApplicationManager.IsHeadless) log.Msg($"Engravings: the game's '{spriggull.name}' glyph on both sides and the pedestal; paytable drawn.");
                }
                else log.Warning("Engravings: no Spriggull glyph found in the game's book drawings; leaving the cabinet plain.");
            }

            // The paytable: between the reels and the sign, a cream panel in a gold frame. Each row shows three of the
            // game's own book drawings of a prize creature, with what it pays written beside it (added with the labels).
            {
                float pw = SlotSign.Width, ph = PayHeight, cy = PayBottom + ph / 2f, t = 0.014f;
                // The cream panel sits inside the frame; the frame runs from the cabinet face to just in front of the
                // panel, so it wraps the panel's edges and no cream shows round the outside.
                Box(v, "Paytable Panel", new Vector3(0f, cy, front + PayFace / 2f), new Vector3(pw - 2f * t, ph - 2f * t, PayFace), white, false);
                float fd = PayFace + 0.008f, fz = front + fd / 2f;
                Box(v, "Paytable Frame Top", new Vector3(0f, cy + ph / 2f - t / 2f, fz), new Vector3(pw, t, fd), gold, false);
                Box(v, "Paytable Frame Bottom", new Vector3(0f, cy - ph / 2f + t / 2f, fz), new Vector3(pw, t, fd), gold, false);
                Box(v, "Paytable Frame Left", new Vector3(-pw / 2f + t / 2f, cy, fz), new Vector3(t, ph - 2f * t, fd), gold, false);
                Box(v, "Paytable Frame Right", new Vector3(pw / 2f - t / 2f, cy, fz), new Vector3(t, ph - 2f * t, fd), gold, false);

                // A warm glow behind each row, hidden until that prize is won (see AnimateRowGlow).
                Material rowGlow = Mat(baseMat, "Slot Paytable Glow", new Color(1f, 0.86f, 0.5f), new Color(1.1f, 0.7f, 0.25f), 0.4f, 0f);
                float rowH = (PayHeight - 2f * 0.026f) / Math.Max(PaytableRows.Count, 1);
                for (int i = 0; i < PaytableRows.Count; i++)
                {
                    Box(v, "Paytable Glow " + i, new Vector3(0f, PayRowY(i), front + PayFace + 0.0015f),
                        new Vector3(pw - 2f * t - 0.02f, rowH * 0.92f, 0.001f), rowGlow, false);
                    v.transform.Find("Paytable Glow " + i).GetComponent<Renderer>().enabled = false;
                }

                var ink = new Color(0.12f, 0.08f, 0.05f, 0.95f);   // the books' dark ink, on the cream paper
                for (int i = 0; i < PaytableRows.Count; i++)
                {
                    Glyph g = SlotGlyphs.Find(PaytableRows[i].Key);
                    if (g == null) continue;
                    // The machine's +X is the viewer's left: the three drawings sit on the left, the amount on the right.
                    for (int k = 0; k < 3; k++)
                        SlotGlyphs.Engrave(v, $"Paytable {PaytableRows[i].Key} {k + 1}", g,
                            new Vector3(PayGlyphX - k * 0.058f, PayRowY(i), front + PayFace), Vector3.forward, 0.042f, ink);
                }
            }

            // The shelf, with a coin slot in the middle: an iron plate on a gold surround, with a dark slit.
            // Drop coins on it, or touch a coin pouch to it, and the coins go in.
            // The coin box: the shelf is its lid (the gold slot ring sits on it); coins dropped through the slot land in the
            // small coin bubble inside, which can be reached through the open front to take coins back.
            const float shelfDepth = ShelfDepth;
            // Shaped like a half-round counter on a half-round drum: a thin iron counter top (it overhangs a little) on a
            // closed gold drum. The coin bubble sits inside the drum, under the slot.
            GameObject counterTop = SlabWithSlit(v, "Shelf", ShelfTop - LidThickness, LidThickness, W / 2f, shelfDepth, front, SlitHalfWidth, ShelfZ, SlitHalfDepth, black);
            OpenSlit(counterTop, ShelfTop - LidThickness, LidThickness, W / 2f, front, shelfDepth, SlitHalfWidth, ShelfZ, SlitHalfDepth);
            {
                float da = W / 2f - BodyInset, db = shelfDepth - BodyInset, drumBottom = CoinBoxFloorTop - 0.02f, drumTop = ShelfTop - LidThickness;
                // The floor sits inside the drum's wall (no lip), and two smaller steps under it round off the bottom.
                RoundedSlab(v, "Coin Box Floor", drumBottom, 0.02f, da - 0.014f, db - 0.014f, front, gold);
                RoundedSlab(v, "Coin Box Base 1", drumBottom - 0.01f, 0.01f, da - 0.012f, db - 0.012f, front, gold);
                RoundedSlab(v, "Coin Box Base 2", drumBottom - 0.018f, 0.008f, da - 0.035f, db - 0.035f, front, gold);
                // The drum's wall, with a real opening at the bottom of its front for the payout tunnel.
                float gap = PayoutGapAngle(da, PayoutMouthHalfWidth + 0.0065f);   // round the tunnel's lining
                float mouthBottom = CoinBoxFloorTop + 0.001f, mouthTop = mouthBottom + PayoutMouthHeight;
                var walls = new List<GameObject>
                {
                    CurvedWall(v, "Coin Box Drum", drumBottom, drumTop, da, db, front, 0f, 90f - gap, gold),
                    CurvedWall(v, "Coin Box Drum", drumBottom, drumTop, da, db, front, 90f + gap, 180f, gold),
                    CurvedWall(v, "Coin Box Drum", mouthTop + 0.0065f, drumTop, da, db, front, 90f - gap, 90f + gap, gold),
                };
                if (mouthBottom - 0.0065f - drumBottom > 0.002f) walls.Add(CurvedWall(v, "Coin Box Drum", drumBottom, mouthBottom - 0.0065f, da, db, front, 90f - gap, 90f + gap, gold));
                foreach (GameObject w in walls) UnityEngine.Object.DestroyImmediate(w.GetComponent<BoxCollider>());   // hollow: coins drop through the slit
            }
            Material slit = Mat(baseMat, "Slot Slit", new Color(0.02f, 0.015f, 0.01f), Color.black, 0.2f, 0f);
            // The chute's opening: dark inside the gold ring, so it reads as a hole the coins drop down.
            {
                // A short dark throat under the slit, so looking in it reads as a slot going down into the machine.
                float throatY = ShelfTop - LidThickness - 0.02f;
                Box(v, "Chute Throat Back", new Vector3(0f, throatY, ShelfZ - SlitHalfDepth - 0.001f), new Vector3(2f * SlitHalfWidth, 0.04f, 0.002f), slit, false);
                Box(v, "Chute Throat Front", new Vector3(0f, throatY, ShelfZ + SlitHalfDepth + 0.001f), new Vector3(2f * SlitHalfWidth, 0.04f, 0.002f), slit, false);
                Box(v, "Chute Throat Left", new Vector3(SlitHalfWidth + 0.001f, throatY, ShelfZ), new Vector3(0.002f, 0.04f, 2f * SlitHalfDepth), slit, false);
                Box(v, "Chute Throat Right", new Vector3(-SlitHalfWidth - 0.001f, throatY, ShelfZ), new Vector3(0.002f, 0.04f, 2f * SlitHalfDepth), slit, false);
            }
            Box(v, "Coin Slot Surround", new Vector3(0f, ShelfTop + 0.004f, ShelfZ), new Vector3(0.2f, 0.008f, 0.12f), gold, true);
            Box(v, "Coin Slot Plate", new Vector3(0f, ShelfTop + 0.011f, ShelfZ), new Vector3(0.17f, 0.008f, 0.095f), black, true);
            Box(v, "Coin Slot", new Vector3(0f, ShelfTop + 0.0152f, ShelfZ), new Vector3(0.1f, 0.001f, 0.016f), slit, false);

            // The lever, on the right side as you face the machine: an iron hub on the cabinet's side, an iron arm,
            // and a round gold knob. It turns about the hub; pulling it forward and down spins the reels.
            {
                Vector3 pivot = LeverPivotLocal - new Vector3(0f, Lift, 0f);   // in the Visual's space
                // An iron mounting plate on the cabinet's side, and a gold ball joint the arm comes out of.
                Box(v, "Lever Hub", new Vector3(-(W / 2f) - 0.0125f, pivot.y, pivot.z), new Vector3(0.025f, 0.15f, 0.11f), black, true);
                var joint = new GameObject("Lever Joint");
                joint.transform.SetParent(v.transform, false);
                joint.transform.localPosition = pivot;
                joint.transform.localScale = Vector3.one * 0.05f;
                joint.AddComponent<MeshFilter>().sharedMesh = Sphere();
                joint.AddComponent<MeshRenderer>().sharedMaterial = gold;
                var arm = new GameObject("Lever Pivot");
                arm.transform.SetParent(v.transform, false);
                arm.transform.localPosition = pivot;
                arm.transform.localRotation = LeanTurn * Quaternion.Euler(RestAngle, 0f, 0f);
                Box(arm, "Lever Arm", new Vector3(0f, LeverLength / 2f, 0f), new Vector3(0.022f, LeverLength, 0.022f), black, false);
                Box(arm, "Lever Collar", new Vector3(0f, LeverLength - KnobRadius - 0.008f, 0f), new Vector3(0.034f, 0.016f, 0.034f), gold, false);
                var knob = new GameObject("Lever Knob");
                knob.transform.SetParent(arm.transform, false);
                knob.transform.localPosition = new Vector3(0f, LeverLength, 0f);
                knob.transform.localScale = Vector3.one * (KnobRadius * 2f);
                knob.AddComponent<MeshFilter>().sharedMesh = Sphere();
                knob.AddComponent<MeshRenderer>().sharedMaterial = gold;
            }

            // Winnings come out of a dark mouth at the bottom of the coin drum's front and slide down a short gold ramp,
            // where the Winnings bubble waits at hand height.
            {
                // The payout tunnel: an open channel through the drum's wall, lined in iron, running back into the machine.
                {
                    float mouthBottom = CoinBoxFloorTop + 0.001f, mouthTop = mouthBottom + PayoutMouthHeight, apex = front + ShelfDepth - BodyInset;
                    float len = 0.07f, zMid = apex - len / 2f + 0.004f, hw = PayoutMouthHalfWidth;
                    Box(v, "Payout Tunnel Floor", new Vector3(0f, mouthBottom - 0.003f, zMid), new Vector3(2f * hw + 0.006f, 0.006f, len), black, true);
                    Box(v, "Payout Tunnel Roof", new Vector3(0f, mouthTop + 0.003f, zMid), new Vector3(2f * hw + 0.006f, 0.006f, len), black, false);
                    Box(v, "Payout Tunnel Side", new Vector3(hw + 0.003f, (mouthBottom + mouthTop) / 2f, zMid), new Vector3(0.006f, PayoutMouthHeight, len), black, false);
                    Box(v, "Payout Tunnel Side", new Vector3(-hw - 0.003f, (mouthBottom + mouthTop) / 2f, zMid), new Vector3(0.006f, PayoutMouthHeight, len), black, false);
                }
                var ramp = new GameObject("Payout Ramp");
                ramp.transform.SetParent(v.transform, false);
                ramp.transform.localPosition = new Vector3(0f, RampTop, RampBackZ);
                ramp.transform.localRotation = Quaternion.Euler(RampSlope, 0f, 0f);   // tips down toward the player
                // A small half-round tray, the same shape as the coin drum above it, with a low rim.
                const float halfWidth = 0.09f;
                RoundedSlab(ramp, "Ramp Floor", -0.018f, 0.018f, halfWidth, RampLength, 0f, gold);
                GameObject rim = CurvedWall(ramp, "Ramp Rim", 0f, 0.022f, halfWidth, RampLength, 0f, 0f, 180f, gold);
                UnityEngine.Object.DestroyImmediate(rim.GetComponent<BoxCollider>());   // open inside, so the bubble can be reached
            }

            // A warm glow on the front.
            var glow = new GameObject("Glow");
            glow.transform.SetParent(v.transform, false);
            glow.transform.localPosition = new Vector3(0, 1.0f, front + 0.35f);
            var light = glow.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.8f, 0.5f);
            light.range = 1.3f;
            light.intensity = 0.7f;
            light.shadows = LightShadows.None;

            foreach (Transform t in v.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }

        // ---------- rounded coin box pieces ----------

        private const float Roundness = 2.5f;
        private const float SlitHalfWidth = 0.095f, SlitHalfDepth = 0.022f;
        private const float PayoutMouthHalfWidth = 0.065f, PayoutMouthHeight = 0.03f;   // the payout tunnel's opening in the drum

        /// <summary>Half the angle (degrees, round the drum's outline from its front middle) the payout opening spans.</summary>
        private static float PayoutGapAngle(float a, float halfWidth)
        {
            float c = Mathf.Pow(Mathf.Clamp01(halfWidth / a), Roundness / 2f);
            return 90f - Mathf.Acos(c) * Mathf.Rad2Deg;
        }   // the coin slit through the counter, under the gold ring

        /// <summary>Swaps a slab's single collider for four round a slit, so a coin can actually drop into it.</summary>
        private static void OpenSlit(GameObject slab, float yBottom, float thickness, float a, float zBack, float b, float halfW, float zMid, float halfD)
        {
            UnityEngine.Object.DestroyImmediate(slab.GetComponent<BoxCollider>());
            float y = yBottom + thickness / 2f, zFront = zBack + b;
            Action<float, float, float, float> add = (x0, x1, z0, z1) =>
            {
                var c = slab.AddComponent<BoxCollider>();
                c.center = new Vector3((x0 + x1) / 2f, y, (z0 + z1) / 2f);
                c.size = new Vector3(x1 - x0, thickness, z1 - z0);
            };
            add(-a, a, zBack, zMid - halfD);          // behind the slit
            add(-a, a, zMid + halfD, zFront);         // in front of it
            add(-a, -halfW, zMid - halfD, zMid + halfD);   // either side
            add(halfW, a, zMid - halfD, zMid + halfD);
        }   // superellipse power: 2 = a half oval, higher = squarer with rounded corners

        /// <summary>A point on the box's outline: angle 0 = one side at the back, 90 = front middle, 180 = the other side.</summary>
        private static Vector3 Outline(float deg, float a, float b, float zBack, float y)
        {
            float c = Mathf.Cos(deg * Mathf.Deg2Rad), sn = Mathf.Sin(deg * Mathf.Deg2Rad);
            float x = a * Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), 2f / Roundness);
            float z = b * Mathf.Pow(Mathf.Abs(sn), 2f / Roundness);
            return new Vector3(x, y, zBack + z);
        }

        /// <summary>Adds a quad (p0..p3 in order round it), wound so it faces <paramref name="outward"/>.</summary>
        private static void Quad(List<Vector3> v, List<Vector2> uv, List<int> t, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
                                 Vector2 u0, Vector2 u1, Vector2 u2, Vector2 u3, Vector3 outward)
        {
            int i = v.Count;
            v.AddRange(new[] { p0, p1, p2, p3 });
            uv.AddRange(new[] { u0, u1, u2, u3 });
            bool flip = Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), outward) < 0f;
            t.AddRange(flip ? new[] { i, i + 2, i + 1, i, i + 3, i + 2 } : new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
        }

        private static GameObject Finish(GameObject parent, string name, List<Vector3> v, List<Vector2> uv, List<int> t, Material mat, Bounds collider)
        {
            var mesh = new Mesh { name = "Slot " + name };
            mesh.SetVertices(v);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            var box = go.AddComponent<BoxCollider>();
            box.center = collider.center;
            box.size = collider.size;
            return go;
        }

        private static Vector2 PatchUV(Rect patch, float u, float w) =>
            new Vector2(patch.x + patch.width * Mathf.Clamp01(u), patch.y + patch.height * Mathf.Clamp01(w));

        /// <summary>A flat slab whose outline is straight at the back (against the cabinet) and rounded at the front.</summary>
        private static GameObject RoundedSlab(GameObject parent, string name, float yBottom, float thickness, float a, float b, float zBack, Material mat)
        {
            const int Segments = 28;
            Rect patch = SmoothestPatch(mat, mat == goldMaterial ? 4f : 1f);
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            float yTop = yBottom + thickness;
            Func<Vector3, Vector2> flatUV = p => PatchUV(patch, p.x / (2f * a) + 0.5f, (p.z - zBack) / b);
            Vector3 ct = new Vector3(0f, yTop, zBack), cb = new Vector3(0f, yBottom, zBack);
            for (int k = 0; k < Segments; k++)
            {
                float d0 = 180f * k / Segments, d1 = 180f * (k + 1) / Segments;
                Vector3 t0 = Outline(d0, a, b, zBack, yTop), t1 = Outline(d1, a, b, zBack, yTop);
                Vector3 b0 = Outline(d0, a, b, zBack, yBottom), b1 = Outline(d1, a, b, zBack, yBottom);
                // Top and bottom: each wedge is a quad with two corners at the back centre (a triangle).
                Quad(v, uv, t, ct, t0, t1, ct, flatUV(ct), flatUV(t0), flatUV(t1), flatUV(ct), Vector3.up);
                Quad(v, uv, t, cb, b0, b1, cb, flatUV(cb), flatUV(b0), flatUV(b1), flatUV(cb), Vector3.down);
                // The rounded edge.
                Vector3 outward = (t0 + t1) / 2f - ct; outward.y = 0f;
                float u0 = (float)k / Segments, u1 = (float)(k + 1) / Segments;
                Quad(v, uv, t, b0, b1, t1, t0, PatchUV(patch, u0, 0f), PatchUV(patch, u1, 0f), PatchUV(patch, u1, 1f), PatchUV(patch, u0, 1f), outward);
            }
            // The straight back, against the cabinet.
            Quad(v, uv, t, new Vector3(-a, yBottom, zBack), new Vector3(a, yBottom, zBack), new Vector3(a, yTop, zBack), new Vector3(-a, yTop, zBack),
                 PatchUV(patch, 0f, 0f), PatchUV(patch, 1f, 0f), PatchUV(patch, 1f, 1f), PatchUV(patch, 0f, 1f), Vector3.back);
            return Finish(parent, name, v, uv, t, mat, new Bounds(new Vector3(0f, (yBottom + yTop) / 2f, zBack + b / 2f), new Vector3(2f * a, thickness, b)));
        }

        /// <summary>
        /// Like <see cref="RoundedSlab"/>, but with a real rectangular slit cut through it (centred at x 0, depth
        /// <paramref name="zMid"/>). The caps are built as a ring of quads between the slit's edge and the outline, so the
        /// slit is an actual opening; its inner walls are included.
        /// </summary>
        private static GameObject SlabWithSlit(GameObject parent, string name, float yBottom, float thickness, float a, float b, float zBack,
                                               float halfW, float zMid, float halfD, Material mat)
        {
            Rect patch = SmoothestPatch(mat, mat == goldMaterial ? 4f : 1f);
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            float yTop = yBottom + thickness;
            Func<Vector3, Vector2> flatUV = p => PatchUV(patch, p.x / (2f * a) + 0.5f, (p.z - zBack) / b);

            // The outline as a closed polygon (x, z): the rounded front, then the straight back edge closes it.
            const int OutlineSteps = 60;
            var outline = new List<Vector2>();
            for (int k = 0; k <= OutlineSteps; k++)
            {
                Vector3 p = Outline(180f * k / OutlineSteps, a, b, zBack, 0f);
                outline.Add(new Vector2(p.x, p.z));
            }

            // Rays out from the slit's centre; the corners of the slit and of the back edge get rays of their own.
            var c = new Vector2(0f, zMid);
            var angles = new List<float>();
            for (int k = 0; k < 90; k++) angles.Add(k * 4f);
            foreach (Vector2 corner in new[] { new Vector2(halfW, zMid + halfD), new Vector2(-halfW, zMid + halfD), new Vector2(-halfW, zMid - halfD),
                                               new Vector2(halfW, zMid - halfD), new Vector2(a, zBack), new Vector2(-a, zBack) })
            {
                float ang = Mathf.Atan2(corner.y - c.y, corner.x - c.x) * Mathf.Rad2Deg;
                angles.Add(ang < 0f ? ang + 360f : ang);
            }
            angles.Sort();
            var rays = new List<float>();
            foreach (float ang in angles) if (rays.Count == 0 || ang - rays[rays.Count - 1] > 0.01f) rays.Add(ang);

            Func<Vector2, Vector2> outerHit = d =>
            {
                float best = float.MaxValue;
                for (int k = 0; k < outline.Count; k++)
                {
                    Vector2 p = outline[k], q = outline[(k + 1) % outline.Count], e = q - p, w = p - c;
                    float den = d.x * e.y - d.y * e.x;
                    if (Mathf.Abs(den) < 1e-7f) continue;
                    float tt = (w.x * e.y - w.y * e.x) / den, u = (w.x * d.y - w.y * d.x) / den;
                    if (tt > 0f && u >= -1e-4f && u <= 1f + 1e-4f && tt < best) best = tt;
                }
                return c + d * (best == float.MaxValue ? 0f : best);
            };
            Func<Vector2, Vector2> innerHit = d =>
                c + d * Mathf.Min(Mathf.Abs(d.x) > 1e-6f ? halfW / Mathf.Abs(d.x) : float.MaxValue, Mathf.Abs(d.y) > 1e-6f ? halfD / Mathf.Abs(d.y) : float.MaxValue);

            int n = rays.Count;
            for (int k = 0; k < n; k++)
            {
                float a0 = rays[k] * Mathf.Deg2Rad, a1 = rays[(k + 1) % n] * Mathf.Deg2Rad;
                Vector2 d0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)), d1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));
                Vector2 i0 = innerHit(d0), i1 = innerHit(d1), o0 = outerHit(d0), o1 = outerHit(d1);
                Func<Vector2, float, Vector3> at = (p, y) => new Vector3(p.x, y, p.y);
                Quad(v, uv, t, at(i0, yTop), at(o0, yTop), at(o1, yTop), at(i1, yTop), flatUV(at(i0, yTop)), flatUV(at(o0, yTop)), flatUV(at(o1, yTop)), flatUV(at(i1, yTop)), Vector3.up);
                Quad(v, uv, t, at(i0, yBottom), at(o0, yBottom), at(o1, yBottom), at(i1, yBottom), flatUV(at(i0, yBottom)), flatUV(at(o0, yBottom)), flatUV(at(o1, yBottom)), flatUV(at(i1, yBottom)), Vector3.down);
                // The slit's inside wall, facing into the opening.
                Vector2 mid = (i0 + i1) / 2f - c;
                if ((i1 - i0).sqrMagnitude > 1e-10f)
                    Quad(v, uv, t, at(i0, yBottom), at(i1, yBottom), at(i1, yTop), at(i0, yTop),
                         PatchUV(patch, 0f, 0f), PatchUV(patch, 1f, 0f), PatchUV(patch, 1f, 1f), PatchUV(patch, 0f, 1f), new Vector3(-mid.x, 0f, -mid.y));
            }

            // The rounded outer edge and the straight back, as on a plain slab.
            const int Segments = 28;
            Vector3 ct = new Vector3(0f, yTop, zBack);
            for (int k = 0; k < Segments; k++)
            {
                float d0 = 180f * k / Segments, d1 = 180f * (k + 1) / Segments;
                Vector3 t0 = Outline(d0, a, b, zBack, yTop), t1 = Outline(d1, a, b, zBack, yTop);
                Vector3 b0 = Outline(d0, a, b, zBack, yBottom), b1 = Outline(d1, a, b, zBack, yBottom);
                Vector3 outward = (t0 + t1) / 2f - ct; outward.y = 0f;
                float u0 = (float)k / Segments, u1 = (float)(k + 1) / Segments;
                Quad(v, uv, t, b0, b1, t1, t0, PatchUV(patch, u0, 0f), PatchUV(patch, u1, 0f), PatchUV(patch, u1, 1f), PatchUV(patch, u0, 1f), outward);
            }
            Quad(v, uv, t, new Vector3(-a, yBottom, zBack), new Vector3(a, yBottom, zBack), new Vector3(a, yTop, zBack), new Vector3(-a, yTop, zBack),
                 PatchUV(patch, 0f, 0f), PatchUV(patch, 1f, 0f), PatchUV(patch, 1f, 1f), PatchUV(patch, 0f, 1f), Vector3.back);
            return Finish(parent, name, v, uv, t, mat, new Bounds(new Vector3(0f, (yBottom + yTop) / 2f, zBack + b / 2f), new Vector3(2f * a, thickness, b)));
        }

        /// <summary>A thin curved wall following the slab outline between two angles (see <see cref="Outline"/>).</summary>
        private static GameObject CurvedWall(GameObject parent, string name, float yBottom, float yTop, float a, float b, float zBack, float from, float to, Material mat)
        {
            const int Segments = 8;
            const float Thick = 0.015f;
            Rect patch = SmoothestPatch(mat, mat == goldMaterial ? 4f : 1f);
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            var bounds = new Bounds(); bool any = false;
            Vector3 up = Vector3.up * (yTop - yBottom);
            for (int k = 0; k < Segments; k++)
            {
                float d0 = Mathf.Lerp(from, to, (float)k / Segments), d1 = Mathf.Lerp(from, to, (float)(k + 1) / Segments);
                Vector3 o0 = Outline(d0, a, b, zBack, yBottom), o1 = Outline(d1, a, b, zBack, yBottom);
                Vector3 i0 = Outline(d0, a - Thick, b - Thick, zBack, yBottom), i1 = Outline(d1, a - Thick, b - Thick, zBack, yBottom);
                Vector3 outward = (o0 + o1) / 2f - new Vector3(0f, yBottom, zBack);
                float u0 = (float)k / Segments, u1 = (float)(k + 1) / Segments;
                Vector2 a0 = PatchUV(patch, u0, 0f), a1 = PatchUV(patch, u1, 0f), a2 = PatchUV(patch, u1, 1f), a3 = PatchUV(patch, u0, 1f);
                Quad(v, uv, t, o0, o1, o1 + up, o0 + up, a0, a1, a2, a3, outward);      // outside
                Quad(v, uv, t, i0, i1, i1 + up, i0 + up, a0, a1, a2, a3, -outward);     // inside
                Quad(v, uv, t, o0 + up, o1 + up, i1 + up, i0 + up, a0, a1, a2, a3, Vector3.up);     // top edge
                Quad(v, uv, t, o0, o1, i1, i0, a0, a1, a2, a3, Vector3.down);                       // bottom edge
                foreach (Vector3 p in new[] { o0, o1 + up, i0, i1 + up }) { if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; } else bounds.Encapsulate(p); }
            }
            // Close the wall's open front end.
            float end = (Mathf.Abs(from) < 0.01f || Mathf.Abs(from - 180f) < 0.01f) ? to : from;
            float back = end == to ? from : to;
            Vector3 eo = Outline(end, a, b, zBack, yBottom), ei = Outline(end, a - Thick, b - Thick, zBack, yBottom);
            Vector3 dir = eo - Outline(back, a, b, zBack, yBottom);
            Quad(v, uv, t, eo, ei, ei + up, eo + up, PatchUV(patch, 0f, 0f), PatchUV(patch, 1f, 0f), PatchUV(patch, 1f, 1f), PatchUV(patch, 0f, 1f), dir);
            return Finish(parent, name, v, uv, t, mat, bounds);
        }

        private static void Box(GameObject parent, string name, Vector3 center, Vector3 size, Material mat, bool collide)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = center;
            go.transform.localScale = size;
            go.AddComponent<MeshFilter>().sharedMesh = CubeFor(mat);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            if (collide) go.AddComponent<BoxCollider>();
        }

        /// <summary>A unit cube with flat-shaded faces, its whole texture on each face.</summary>
        internal static Mesh Cube() => cube ?? (cube = BuildCube(new Rect(0f, 0f, 1f, 1f), "Slot Machine Cube"));

        // ---------- smooth texturing: only a calm patch of each game texture ----------

        private static readonly Dictionary<Material, Mesh> cubesByMaterial = new Dictionary<Material, Mesh>();

        /// <summary>
        /// A cube for this material whose every face shows just the smoothest little patch of the material's texture.
        /// The game's metal textures are made for real models (tree rings, panel lines, grain); stretched whole over a
        /// flat panel they look wonky, but a calm patch gives each panel the game's colour and a little natural texture.
        /// </summary>
        internal static Mesh CubeFor(Material mat)
        {
            if (mat == null) return Cube();
            if (cubesByMaterial.TryGetValue(mat, out Mesh m)) return m;
            // Gold shows a wider section (about a quarter of its texture) so its grain comes through; the rest stay smooth.
            Rect patch = SmoothestPatch(mat, mat == goldMaterial ? 4f : 1f);
            m = patch.width >= 1f ? Cube() : BuildCube(patch, "Slot Machine Cube (" + mat.name + ")");
            cubesByMaterial[mat] = m;
            return m;
        }

        /// <summary>
        /// Looks at the material's texture in an 8×8 grid and picks the cell with the least light/dark variation
        /// (skipping near-black edges), then returns the middle half of that cell in mesh UVs (undoing the material's
        /// own tiling and offset). Whole texture if there's none or it can't be read (e.g. on the server, which draws nothing).
        /// </summary>
        private static Material goldMaterial;

        private static Rect SmoothestPatch(Material mat, float detail)
        {
            var whole = new Rect(0f, 0f, 1f, 1f);
            string prop = mat.HasProperty("_MainTex") ? "_MainTex" : mat.HasProperty("_BaseMap") ? "_BaseMap" : null;
            Texture tex = prop != null ? mat.GetTexture(prop) : null;
            if (tex == null || ApplicationManager.IsHeadless) return whole;
            try
            {
                const int N = 128, cells = 8, cell = N / cells;
                RenderTexture rt = RenderTexture.GetTemporary(N, N, 0);
                RenderTexture before = RenderTexture.active;
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var read = new Texture2D(N, N, TextureFormat.RGB24, false);
                read.ReadPixels(new Rect(0, 0, N, N), 0, 0);
                read.Apply();
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(rt);
                Color[] px = read.GetPixels();
                UnityEngine.Object.Destroy(read);

                int bestX = 0, bestY = 0;
                float best = float.MaxValue;
                for (int cy = 0; cy < cells; cy++)
                    for (int cx = 0; cx < cells; cx++)
                    {
                        float sum = 0f, sum2 = 0f;
                        for (int y = 0; y < cell; y++)
                            for (int x = 0; x < cell; x++)
                            {
                                Color c = px[(cy * cell + y) * N + cx * cell + x];
                                float l = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
                                sum += l; sum2 += l * l;
                            }
                        float n = cell * cell, mean = sum / n, variance = sum2 / n - mean * mean;
                        if (mean < 0.06f) continue;                       // a dark seam or edge, not the surface
                        if (variance < best) { best = variance; bestX = cx; bestY = cy; }
                    }

                // The middle half of the calmest cell, in texture space...
                float size = 1f / cells;
                // "detail" widens the window round the calm cell (1 = half a cell; 4 = two cells), kept inside the texture.
                float w = Mathf.Min(size * 0.5f * detail, 1f);
                float cxm = (bestX + 0.5f) * size, cym = (bestY + 0.5f) * size;
                var target = new Rect(Mathf.Clamp(cxm - w / 2f, 0f, 1f - w), Mathf.Clamp(cym - w / 2f, 0f, 1f - w), w, w);
                // ...in mesh UVs: the shader shows uv * scale + offset.
                Vector2 scale = mat.GetTextureScale(prop), offset = mat.GetTextureOffset(prop);
                if (Mathf.Abs(scale.x) < 1e-4f || Mathf.Abs(scale.y) < 1e-4f) return target;
                return new Rect((target.x - offset.x) / scale.x, (target.y - offset.y) / scale.y, target.width / scale.x, target.height / scale.y);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[The Lucky Spriggull] Couldn't read {mat.name}'s texture ({e.Message}); using it whole.");
                return whole;
            }
        }

        private static Mesh sphere;

        /// <summary>A smooth unit-diameter ball (16 x 10 segments, ~300 triangles) for the lever's knob.</summary>
        private static Mesh Sphere()
        {
            if (sphere != null) return sphere;
            const int lon = 16, lat = 10;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            for (int y = 0; y <= lat; y++)
                for (int x = 0; x <= lon; x++)
                {
                    float a = Mathf.PI * y / lat, b = 2f * Mathf.PI * x / lon;
                    verts.Add(0.5f * new Vector3(Mathf.Sin(a) * Mathf.Cos(b), Mathf.Cos(a), Mathf.Sin(a) * Mathf.Sin(b)));
                    uvs.Add(new Vector2(0.4f + 0.2f * x / lon, 0.4f + 0.2f * y / lat));   // a calm patch of the gold texture
                }
            for (int y = 0; y < lat; y++)
                for (int x = 0; x < lon; x++)
                {
                    int i0 = y * (lon + 1) + x, i1 = i0 + 1, i2 = i0 + lon + 1, i3 = i2 + 1;
                    // Wound so each face is drawn from outside.
                    tris.AddRange(new[] { i0, i1, i2, i1, i3, i2 });
                }
            sphere = new Mesh { name = "Slot Lever Knob" };
            sphere.SetVertices(verts);
            sphere.SetUVs(0, uvs);
            sphere.SetTriangles(tris, 0);
            sphere.RecalculateNormals();
            sphere.RecalculateBounds();
            // Make sure the faces point outwards (flip the order if the first face points in).
            int m = (lat / 2) * lon * 6;   // a triangle round the middle (the ones at the poles have no area)
            Vector3 n0 = Vector3.Cross(verts[tris[m + 1]] - verts[tris[m]], verts[tris[m + 2]] - verts[tris[m]]);
            if (Vector3.Dot(n0, verts[tris[m]] + verts[tris[m + 1]] + verts[tris[m + 2]]) < 0f)
            {
                for (int k = 0; k < tris.Count; k += 3) { int t = tris[k + 1]; tris[k + 1] = tris[k + 2]; tris[k + 2] = t; }
                sphere.SetTriangles(tris, 0);
                sphere.RecalculateNormals();
            }
            return sphere;
        }

        /// <summary>A unit cube with flat-shaded faces; every face's texture coordinates span <paramref name="uv"/>.</summary>
        private static Mesh BuildCube(Rect uv, string name)
        {
            Vector3[] normals = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            foreach (Vector3 n in normals)
            {
                Vector3 a = n.y != 0 ? Vector3.forward : Vector3.up;
                Vector3 u = Vector3.Cross(n, a), w = Vector3.Cross(u, n);
                int i = verts.Count;
                Vector3 c = n * 0.5f;
                verts.Add(c - u * 0.5f - w * 0.5f);
                verts.Add(c + u * 0.5f - w * 0.5f);
                verts.Add(c + u * 0.5f + w * 0.5f);
                verts.Add(c - u * 0.5f + w * 0.5f);
                for (int k = 0; k < 4; k++) norms.Add(n);
                uvs.Add(new Vector2(uv.xMin, uv.yMin)); uvs.Add(new Vector2(uv.xMax, uv.yMin));
                uvs.Add(new Vector2(uv.xMax, uv.yMax)); uvs.Add(new Vector2(uv.xMin, uv.yMax));
                // Unity draws a triangle (a, b, c) from the side Cross(b - a, c - a) points to, so order the corners
                // so that points outwards. (Getting this backwards made the whole machine inside-out / see-through.)
                if (Vector3.Dot(Vector3.Cross(verts[i + 1] - verts[i], verts[i + 2] - verts[i]), n) > 0)
                    tris.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
                else
                    tris.AddRange(new[] { i, i + 2, i + 1, i, i + 3, i + 2 });
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>A flat-colour copy of the ATM's own (opaque) material, so the machine matches the game's look.</summary>
        internal static Material Mat(Material baseMat, string name, Color tint, Color glow, float smooth, float metal)
        {
            Material m = baseMat != null ? new Material(baseMat) : new Material(Shader.Find("Standard"));
            m.name = name;
            foreach (string p in new[] { "_Color", "_BaseColor", "_MainColor", "_Tint", "_TintColor", "_Albedo" })
                if (m.HasProperty(p)) m.SetColor(p, tint);
            foreach (string p in new[] { "_MainTex", "_BaseMap", "_DetailAlbedoMap", "_BumpMap", "_MetallicGlossMap", "_EmissionMap" })
                if (m.HasProperty(p)) m.SetTexture(p, null);
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", glow);
            }
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
            return m;
        }
    }
}
