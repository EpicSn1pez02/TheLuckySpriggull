using System;
using System.Linq;
using Alta.Alchemy;
using MelonLoader;
using UnityEngine;

namespace LuckySpriggull
{
    /// <summary>
    /// Bonus rows: three of a non-prize creature gives the player a boost (the bet is spent, no coins come back).
    /// Schmeechees heal you fully, Babus fill your hunger, Dais give you a burst of speed, Firebugs give night vision.
    /// The boosts use the game's own effects (the ones food and potions apply), so they work like eating does.
    /// </summary>
    internal static class Bonus
    {
        /// <summary>Result kinds 90, 91, 92, 93 in the signal dock are these bonus rows.</summary>
        public const int KindBase = 90;
        public static readonly string[] Symbols = { "Schmeechee", "Babu", "Dais", "Firebug", "Turabada" };

        public static bool IsBonus(int kind) => kind >= KindBase && kind < KindBase + Symbols.Length;
        public static string SymbolFor(int kind) => IsBonus(kind) ? Symbols[kind - KindBase] : null;
        public static int KindFor(string symbol) => KindBase + Array.IndexOf(Symbols, symbol);

        public static string Describe(string symbol)
        {
            switch (symbol)
            {
                case "Schmeechee": return "a full heal";
                case "Babu": return "a full belly";
                case "Dais": return "2x speed for 5 minutes";
                case "Firebug": return "night vision for 5 minutes";
                case "Turabada": return "5x damage and damage protection for 8 minutes";
                default: return "a bonus";
            }
        }

        /// <summary>Logs the game's effects once, so the right names can be picked (and checked in the log).</summary>
        public static void LogEffects(MelonLogger.Instance log)
        {
            try
            {
                string[] names = EffectDefinition.All.Where(e => e != null).Select(e => $"{e.name} [{e.GetType().Name}]").OrderBy(n => n).ToArray();
                log.Msg($"Game effects ({names.Length}): " + string.Join(" | ", names));
                log.Msg($"Bonus effects: speed = {Find("Speed")?.name ?? "none found"}, night vision = {FindNightVision()?.name ?? "none found"}.");
            }
            catch (Exception e) { log.Warning("Couldn't list the game's effects: " + e.Message); }
        }

        /// <summary>Gives the player the boost for this bonus row. Returns what happened, for the log.</summary>
        public static string Apply(int playerId, string symbol)
        {
            global::Player player = global::Player.AllPlayers.OfType<global::Player>()
                .FirstOrDefault(p => p != null && p.UserInfo != null && p.UserInfo.Identifier == playerId);
            PlayerEffectController fx = player?.PlayerController != null ? player.PlayerController.GetComponent<PlayerEffectController>() : null;
            if (fx == null) return "the player isn't here any more";

            switch (symbol)
            {
                case "Schmeechee":
                    if (fx.Health == null) return "no health to heal";
                    fx.Health.Health = fx.Health.MaxHealth;
                    return "healed to full";

                case "Babu":
                    // The stat the game's "Nourish" food effect raises is the hunger bar; fill it to the top.
                    if (Find("Nourish") is StatEffect nourish && fx.StatManager != null && fx.StatManager.GetStat(nourish.StatToAffect, out var hunger))
                    {
                        hunger.Base = hunger.Maximum;
                        return "hunger filled";
                    }
                    return "couldn't find the hunger stat";

                case "Dais":
                    return Boost(fx, Find("Speed"), SpeedTimes, SpeedSeconds);

                case "Firebug":
                    return Boost(fx, FindNightVision(), 0f, VisionSeconds);   // the effect's own strength, for longer

                case "Turabada":
                    return Boost(fx, Find("Damage Indirect"), DamageTimes, DamageSeconds) + "; " +
                           Boost(fx, Find("Damage Protection"), ProtectionTimes, DamageSeconds);
            }
            return "unknown bonus";
        }

        // How strong and how long the timed boosts are.
        private const float SpeedTimes = 2f, DamageTimes = 5f, ProtectionTimes = 2f;
        private const float SpeedSeconds = 300f, VisionSeconds = 300f, DamageSeconds = 480f;

        /// <summary>
        /// A timed boost through the game's own buff system (the one cooked meat uses), but at a chosen strength and length:
        /// "times" is what the boosted stat ends up multiplied by (2 = double speed). The game turns a buff's value into a
        /// multiplier with a curve per stat, so the value is found by searching that curve; if the game caps the stat
        /// lower, the cap is used and the log says so. times 0 = the effect's own normal strength.
        /// </summary>
        private static string Boost(PlayerEffectController fx, EffectDefinition effect, float times, float seconds)
        {
            var def = effect as Alta.StatSystem.IndirectStatModifierDefinition;
            if (def == null) return "no such effect in this game";
            if (fx.StatManager == null || !fx.StatManager.GetStat(def.TargetStat, out var stat)) return def.name + ": the player has no " + def.TargetStat?.name;
            var curve = FieldOf(def, "curveOverDuration") as global::Curve;
            float baseValue = FieldOf(def, "baseValue") is float bv ? bv : 1f;
            if (curve == null) return def.name + ": couldn't read its curve";

            AnimationCurve toMultiplier = stat.Definition.IndirectModificationCurve;
            float value = baseValue, reached = toMultiplier != null ? toMultiplier.Evaluate(baseValue) : 0f;
            string note = "";
            if (times > 0f && toMultiplier != null)
            {
                // The smallest buff value the game turns into at least this multiplier (or the strongest it allows).
                float best = 0f, bestOut = float.MinValue;
                bool hit = false;
                for (float v = 0f; v <= 200f; v += 0.05f)
                {
                    float m = toMultiplier.Evaluate(v);
                    if (m > bestOut) { bestOut = m; best = v; }
                    if (m >= times) { value = v; reached = m; hit = true; break; }
                }
                if (!hit) { value = best; reached = bestOut; note = $" (the game caps it at {bestOut:0.##}x)"; }
            }

            // Its strength curve starts at curve(0) x base value; scale so it starts at the value wanted.
            float start = curve.Evaluate(0f);
            float scaled = Mathf.Abs(start) > 0.0001f ? value / start : value;
            float tick = FieldOf(fx.StatManager, "tickTime") is float tt && tt > 0f ? tt : 1f;
            uint ticks = (uint)Mathf.Max(1f, seconds / tick);
            fx.StatManager.ApplyIndirectModifier(def.TargetStat, new Alta.StatSystem.IndirectModifier(scaled, ticks, curve));
            return $"{def.name}: {(times > 0f ? reached.ToString("0.##") + "x" : "normal strength")}{note} for {seconds:0} s";
        }

        private static object FieldOf(object target, string name)
        {
            for (Type t = target?.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(target);
            }
            return null;
        }

        /// <summary>An effect whose name contains this word ("Speed" finds "Speed Indirect Effect").</summary>
        private static EffectDefinition Find(string word) =>
            EffectDefinition.All.Where(e => e != null && e.name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(e => e.name.Length).FirstOrDefault();

        private static EffectDefinition FindNightVision() =>
            Find("Night Vision") ?? Find("NightVision") ?? Find("Night") ?? Find("Vision") ?? Find("Lumino") ?? Find("Dark");   // the game's "Luminosiy indirect effect" lights you up in the dark
    }
}
