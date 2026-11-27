using System;
using System.Collections.Generic;
using Alta.Pages;
using MelonLoader;
using UnityEngine;

namespace LuckySpriggull
{
    /// <summary>
    /// The reel symbols: the game's own book drawings (glyphs) of each creature, inked onto the reels like the paytable.
    /// One flat drawing per symbol keeps the reels cheap to draw, where 3D creature models cost many parts each.
    /// The first in the list is the rarest: the reels show it for the biggest win.
    /// </summary>
    internal static class SlotIcons
    {
        /// <summary>
        /// The symbols, and the book drawing for each. The first four are the prize symbols, biggest prize first (three
        /// Spriggulls = the jackpot, three Goteras = smallest); the rest are the bonus rows and filler. The order must
        /// stay the same on every game, because the server's results are read as positions in this list.
        /// </summary>
        private static readonly KeyValuePair<string, string>[] Wanted =
        {
            new KeyValuePair<string, string>("Spriggull", "Glyph_Spriggull"),
            new KeyValuePair<string, string>("Phantom", "Glyph_Phantom"),
            new KeyValuePair<string, string>("Wyrm", "Glyph_Wyrm"),
            new KeyValuePair<string, string>("Gotera", "Glyph_Gotera"),
            new KeyValuePair<string, string>("Turabada", "Glyph_Turabada"),
            new KeyValuePair<string, string>("Babu", "Glyph_Babu"),
            new KeyValuePair<string, string>("Dais", "Glyph_Dais"),
            new KeyValuePair<string, string>("Schmeechee", "Glyph_HealthIcon"),            // the heal bonus: the game's health heart
            new KeyValuePair<string, string>("Mushroom", "Glyph_Glowing_Mushroom"),         // the Glowing Mushroom: the glow bonus
        };

        /// <summary>Which creature means which prize, biggest first.</summary>
        public static readonly string[] PrizeSymbols = { "Spriggull", "Phantom", "Wyrm", "Gotera" };   // it's The Lucky Spriggull: three Spriggulls is the jackpot

        /// <summary>The name of each symbol built, in order (e.g. "Phantom").</summary>
        public static List<string> Names { get; } = new List<string>();

        public const float Size = 0.075f;   // height of a symbol on the win line, in metres

        private static readonly Color Ink = new Color(0.12f, 0.08f, 0.05f, 0.95f);   // the books' dark ink, as on the paytable

        /// <summary>
        /// Builds one inactive template per symbol under <paramref name="holder"/>. Every symbol is listed on every game
        /// (even the server, which draws nothing), so results line up; a missing drawing falls back to a coloured block.
        /// </summary>
        public static List<GameObject> Build(Transform holder, Mesh cube, Material[] fallback, MelonLogger.Instance log)
        {
            var icons = new List<GameObject>();
            var found = new List<string>();
            Names.Clear();
            for (int i = 0; i < Wanted.Length; i++)
            {
                string name = Wanted[i].Key;
                var icon = new GameObject(name + " Symbol");
                icon.transform.SetParent(holder, false);
                Glyph glyph = SlotGlyphs.Find(Wanted[i].Value, name);
                if (glyph != null)
                {
                    SlotGlyphs.Engrave(icon, name, glyph, Vector3.zero, Vector3.forward, Size, Ink);
                    found.Add($"{name} ({glyph.name})");
                }
                else
                {
                    var block = new GameObject("Block");
                    block.transform.SetParent(icon.transform, false);
                    block.transform.localScale = new Vector3(Size * 0.8f, Size * 0.8f, 0.004f);
                    block.AddComponent<MeshFilter>().sharedMesh = cube;
                    block.AddComponent<MeshRenderer>().sharedMaterial = fallback.Length > 0 ? fallback[i % fallback.Length] : null;
                    found.Add($"{name} (no drawing; a coloured block)");
                }
                icons.Add(icon);
                Names.Add(name);
            }
            log.Msg("Reel symbols (rarest first): " + string.Join(", ", found.ToArray()));
            return icons;
        }

        /// <summary>The prize creature's name for a win of this size (same tiers as <see cref="ForMultiplier"/>).</summary>
        public static string PrizeFor(int multiplier) =>
            PrizeSymbols[multiplier >= 20 ? 0 : multiplier >= 5 ? 1 : multiplier >= 3 ? 2 : 3];

        /// <summary>The symbol for a win of this size: 20x+ = Spriggull (the jackpot), 5x+ = Phantom, 3x+ = Wyrm, else Gotera.</summary>
        public static int ForMultiplier(int multiplier)
        {
            int i = Names.IndexOf(PrizeFor(multiplier));
            return i >= 0 ? i : 0;
        }
    }
}
