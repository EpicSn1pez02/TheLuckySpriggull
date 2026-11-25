using System.Collections.Generic;
using UnityEngine;

namespace LuckySpriggull
{
    /// <summary>
    /// The marquee on top of the machine: "THE LUCKY / SPRIGGULL" spelled out in little warm bulbs on a dark,
    /// gold-framed board, blinking slowly like old incandescent speakeasy bulbs (they warm up, hold, then fade
    /// as the filaments cool, with a faint flicker).
    /// </summary>
    internal static class SlotSign
    {
        public const float Width = 0.48f, Height = 0.20f, Depth = 0.025f;   // narrower than the cabinet: sits between its gold trim strips, like the screen frame

        private static readonly string[] Lines = { "THE LUCKY", "SPRIGGULL" };

        /// <summary>5×7 dot letters, top row first.</summary>
        private static readonly Dictionary<char, string[]> Font = new Dictionary<char, string[]>
        {
            { 'T', new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." } },
            { 'H', new[] { "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" } },
            { 'E', new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#####" } },
            { 'L', new[] { "#....", "#....", "#....", "#....", "#....", "#....", "#####" } },
            { 'U', new[] { "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." } },
            { 'C', new[] { ".###.", "#...#", "#....", "#....", "#....", "#...#", ".###." } },
            { 'K', new[] { "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#" } },
            { 'Y', new[] { "#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.." } },
            { 'S', new[] { ".####", "#....", "#....", ".###.", "....#", "....#", "####." } },
            { 'P', new[] { "####.", "#...#", "#...#", "####.", "#....", "#....", "#...." } },
            { 'R', new[] { "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#" } },
            { 'I', new[] { ".###.", "..#..", "..#..", "..#..", "..#..", "..#..", ".###." } },
            { 'G', new[] { ".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".###." } },
            { 'O', new[] { ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." } },
            { 'N', new[] { "#...#", "##..#", "#.#.#", "#.#.#", "#..##", "#...#", "#...#" } },
            { 'W', new[] { "#...#", "#...#", "#...#", "#.#.#", "#.#.#", "##.##", "#...#" } },
        };

        /// <summary>
        /// A small one-line label in the same dot letters (e.g. "COINS" under the coin box), standing slightly proud of
        /// the surface whose front-face centre is <paramref name="at"/>, letters <paramref name="height"/> tall, in
        /// <paramref name="mat"/> (iron on the gold shelf reads like letters stamped into a brass plate).
        /// </summary>
        public static void Label(GameObject parent, string text, Vector3 at, float height, Material mat)
        {
            float pitch = height / 7f, dot = pitch * 0.82f, depth = 0.003f;
            int cols = text.Length * 6 - 1;
            Mesh unit = SlotMachine.CubeFor(mat);
            var dots = new List<CombineInstance>();
            for (int ci = 0; ci < text.Length; ci++)
            {
                if (!Font.TryGetValue(char.ToUpperInvariant(text[ci]), out string[] glyph)) continue;
                for (int r = 0; r < 7; r++)
                    for (int c = 0; c < 5; c++)
                    {
                        if (glyph[r][c] != '#') continue;
                        int col = ci * 6 + c;
                        float x = -(col - (cols - 1) / 2f) * pitch;   // mirrored: the machine's +X is the viewer's left
                        float y = (3f - r) * pitch;
                        dots.Add(new CombineInstance
                        {
                            mesh = unit,
                            transform = Matrix4x4.TRS(new Vector3(x, y, depth / 2f), Quaternion.identity, new Vector3(dot, dot, depth)),
                        });
                    }
            }
            var mesh = new Mesh { name = "Slot Label " + text };
            mesh.CombineMeshes(dots.ToArray(), true, true);
            var go = new GameObject("Label " + text);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = at;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        /// <summary>The bulbs' material: shared by every machine's sign, so all signs glow together.</summary>
        public static Material BulbMaterial { get; private set; }

        private static readonly Color Warm = new Color(1f, 0.58f, 0.2f);   // amber filament glow

        /// <summary>Builds the sign under <paramref name="parent"/>, its bottom edge at <paramref name="bottom"/> (local space).</summary>
        public static void Build(GameObject parent, Vector3 bottom, Mesh cube, Material black, Material gold, Material baseMat)
        {
            var sign = new GameObject("Sign");
            sign.transform.SetParent(parent.transform, false);
            sign.transform.localPosition = bottom + new Vector3(0f, Height / 2f, 0f);

            // Board and gold frame.
            Part(sign, "Sign Board", Vector3.zero, new Vector3(Width, Height, Depth), black, cube, collide: true);
            // The frame stands proud of the bulbs (front at +12 mm vs the bulbs' ~6 mm), so nothing pokes through it.
            float t = 0.016f, fd = 0.012f, fz = Depth / 2f + fd / 2f;
            Part(sign, "Sign Frame Top", new Vector3(0, Height / 2f - t / 2f, fz), new Vector3(Width, t, fd), gold, cube, false);
            Part(sign, "Sign Frame Bottom", new Vector3(0, -Height / 2f + t / 2f, fz), new Vector3(Width, t, fd), gold, cube, false);
            // (The side bars fit between the top and bottom bars, so the corners never overlap and flicker.)
            Part(sign, "Sign Frame Left", new Vector3(-Width / 2f + t / 2f, 0, fz), new Vector3(t, Height - 2f * t, fd), gold, cube, false);
            Part(sign, "Sign Frame Right", new Vector3(Width / 2f - t / 2f, 0, fz), new Vector3(t, Height - 2f * t, fd), gold, cube, false);

            // The lettering: one mesh made of every bulb dot, so the whole sign is a single renderer.
            BulbMaterial = SlotMachine.Mat(baseMat, "Slot Sign Bulbs", Warm * 0.5f, Warm * 0.2f, 0.6f, 0f);
            int cols = 0;
            foreach (string line in Lines) cols = Mathf.Max(cols, line.Length * 6 - 1);
            const int rows = 7 * 2 + 2;                                         // two lines of 7 with a 2-dot gap
            float pitch = Mathf.Min((Width - 0.07f) / cols, (Height - 0.07f) / rows);   // clear of the frame all round
            float dot = pitch * 0.78f;
            var dots = new List<CombineInstance>();
            for (int li = 0; li < Lines.Length; li++)
            {
                string line = Lines[li];
                int lineCols = line.Length * 6 - 1;
                for (int ci = 0; ci < line.Length; ci++)
                {
                    if (!Font.TryGetValue(line[ci], out string[] glyph)) continue;   // a space
                    for (int r = 0; r < 7; r++)
                        for (int c = 0; c < 5; c++)
                        {
                            if (glyph[r][c] != '#') continue;
                            int col = ci * 6 + c, row = li * 9 + r;
                            // The machine's +X is the viewer's left (its front faces the player), so lay letters out right-to-left in local space.
                            float x = -(col - (lineCols - 1) / 2f) * pitch;
                            float y = ((rows - 1) / 2f - row) * pitch;
                            dots.Add(new CombineInstance
                            {
                                // A flat glowing square (2 triangles) rather than a cube (12): only its front is ever seen.
                                mesh = Quad(),
                                transform = Matrix4x4.TRS(new Vector3(x, y, Depth / 2f + 0.003f), Quaternion.identity, new Vector3(dot, dot, 1f)),
                            });
                        }
                }
            }
            var letters = new Mesh { name = "Slot Sign Lettering" };
            letters.CombineMeshes(dots.ToArray(), true, true);
            var lettering = new GameObject("Sign Lettering");
            lettering.transform.SetParent(sign.transform, false);
            lettering.AddComponent<MeshFilter>().sharedMesh = letters;
            var lr = lettering.AddComponent<MeshRenderer>();
            lr.sharedMaterial = BulbMaterial;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // A warm glow in front, rising and falling with the bulbs.
            var glow = new GameObject("Sign Glow");
            glow.transform.SetParent(sign.transform, false);
            glow.transform.localPosition = new Vector3(0f, 0f, Depth / 2f + 0.25f);
            var light = glow.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = Warm;
            light.range = 1.6f;
            light.intensity = 0f;
            light.shadows = LightShadows.None;
        }

        private static Mesh quad;

        /// <summary>A unit square facing +Z (the machine's front), drawn from the front only.</summary>
        private static Mesh Quad()
        {
            if (quad != null) return quad;
            quad = new Mesh { name = "Slot Sign Bulb" };
            quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            quad.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
            quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };   // Cross(b - a, c - a) points +Z: seen from the front
            quad.RecalculateBounds();
            return quad;
        }

        private static void Part(GameObject parent, string name, Vector3 at, Vector3 size, Material mat, Mesh cube, bool collide)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = at;
            go.transform.localScale = size;
            go.AddComponent<MeshFilter>().sharedMesh = SlotMachine.CubeFor(mat);   // a calm patch of the texture, not the whole thing
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            if (collide) go.AddComponent<BoxCollider>();
        }

        // ---------- the slow incandescent blink ----------

        private const float OnSeconds = 3.5f, OffSeconds = 2.5f;     // one slow blink cycle
        private const float WarmUp = 1.0f, CoolDown = 1.5f;          // slow, soft fades; filaments heat faster than they cool
        private static float filament, lastTime = -1f;               // 0 = cold, 1 = fully lit
        private static int lastFrame = -1;

        /// <summary>The bulbs' brightness right now (0-1). Updates the shared bulb material once per frame.</summary>
        public static float Animate(float now)
        {
            if (Time.frameCount == lastFrame) return filament;
            lastFrame = Time.frameCount;
            float dt = lastTime < 0f ? 0f : Mathf.Clamp(now - lastTime, 0f, 0.2f);
            lastTime = now;

            bool on = now % (OnSeconds + OffSeconds) < OnSeconds;
            float target = on ? 1f : 0.15f;                       // a dim ember glow stays in the filament when off
            float tau = target > filament ? WarmUp : CoolDown;
            filament += (target - filament) * (1f - Mathf.Exp(-dt / tau));

            // A faint, slow flicker, like old wiring.
            float flicker = 1f - 0.04f * Mathf.PerlinNoise(now * 1.7f, 0.37f);
            float b = Mathf.Clamp01(filament * flicker);

            if (BulbMaterial != null)
            {
                // Cool filaments glow deep orange; hot ones turn a brighter, yellower amber.
                Color hue = Color.Lerp(new Color(0.9f, 0.25f, 0.05f), new Color(1f, 0.72f, 0.32f), b);
                if (BulbMaterial.HasProperty("_Color")) BulbMaterial.SetColor("_Color", Color.Lerp(new Color(0.25f, 0.1f, 0.05f), hue, b));
                if (BulbMaterial.HasProperty("_EmissionColor")) BulbMaterial.SetColor("_EmissionColor", hue * (0.08f + 2.4f * b));
            }
            return b;
        }
    }
}
