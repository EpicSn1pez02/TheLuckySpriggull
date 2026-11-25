using System;
using System.Linq;
using Alta;
using Alta.Pages;
using MelonLoader;
using UnityEngine;

namespace LuckySpriggull
{
    /// <summary>
    /// Engravings made from the game's own book drawings: the Glyphs that pages are drawn with (e.g. the inked Spriggull).
    /// Each is a flat decal of the glyph's sprite laid on a panel, showing only its ink lines, tinted a darker shade of the
    /// panel so it looks etched in. Players' games only; the server draws nothing.
    /// </summary>
    internal static class SlotGlyphs
    {
        private static Shader shader;

        /// <summary>Logs every glyph in the game once, so we know what drawings there are to choose from.</summary>
        public static void LogAll(MelonLogger.Instance log)
        {
            try
            {
                HashedGeneralValue<Glyph>.CheckItems();
                var names = HashedGeneralValue<Glyph>.All.Where(g => g != null).Select(g => g.name).OrderBy(n => n).ToArray();
                log.Msg($"Book glyphs ({names.Length}): " + string.Join(" | ", names));
            }
            catch (Exception e) { log.Warning("Couldn't list the book glyphs: " + e.Message); }
        }

        private static readonly System.Collections.Generic.Dictionary<Glyph, Texture2D> smooth = new System.Collections.Generic.Dictionary<Glyph, Texture2D>();

        /// <summary>
        /// The game keeps its book drawings in an atlas with no smaller versions for viewing at a distance, so a small
        /// drawing on the machine shimmers and looks jagged. This copies just this drawing into its own texture, scaled up,
        /// with mipmaps and trilinear filtering, so it stays smooth up close and far away. Null if it can't be copied.
        /// </summary>
        private static Texture2D Smooth(Glyph glyph)
        {
            if (smooth.TryGetValue(glyph, out Texture2D t)) return t;
            try
            {
                Sprite s = glyph.Sprite;
                Texture src = s.texture;
                Rect r = s.textureRect;
                int h = 512, w = Mathf.Clamp(Mathf.RoundToInt(h * r.width / Mathf.Max(r.height, 1f)), 16, 1024);
                RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                rt.filterMode = FilterMode.Bilinear;
                Graphics.Blit(src, rt, new Vector2(r.width / src.width, r.height / src.height), new Vector2(r.x / src.width, r.y / src.height));
                RenderTexture before = RenderTexture.active;
                RenderTexture.active = rt;
                t = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "Slot Glyph " + glyph.name };
                t.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                t.SetPixels32(Bolden(t.GetPixels32(), w, h));
                t.Apply(true, true);   // build the mipmaps, then free the CPU copy
                RenderTexture.active = before;
                RenderTexture.ReleaseTemporary(rt);
                t.filterMode = FilterMode.Trilinear;
                t.anisoLevel = 8;
                t.wrapMode = TextureWrapMode.Clamp;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[The Lucky Spriggull] Couldn't make a smooth copy of {glyph.name}: {e.Message}");
                t = null;
            }
            smooth[glyph] = t;
            return t;
        }

        /// <summary>
        /// Makes the ink bolder and crisper: the drawings are low-resolution, so once scaled up their lines are soft
        /// and faint. Each line is thickened by a couple of pixels, its soft edge sharpened, and coloured solid
        /// (the panel's ink tint then colours it). Ink is wherever the drawing is opaque and dark.
        /// </summary>
        private static Color32[] Bolden(Color32[] px, int w, int h)
        {
            var ink = new float[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                float a = px[i].a / 255f;
                float dark = 1f - (px[i].r + px[i].g + px[i].b) / (3f * 255f);
                ink[i] = a * Mathf.Clamp01(dark * 1.5f);
            }
            // Thicken: each pixel takes the strongest ink within 2 pixels.
            const int rad = 2;
            var thick = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float m = 0f;
                    for (int dy = -rad; dy <= rad; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= h) continue;
                        for (int dx = -rad; dx <= rad; dx++)
                        {
                            int xx = x + dx;
                            if (xx < 0 || xx >= w || dx * dx + dy * dy > rad * rad) continue;
                            float v = ink[yy * w + xx];
                            if (v > m) m = v;
                        }
                    }
                    thick[y * w + x] = m;
                }
            var outPx = new Color32[w * h];
            for (int i = 0; i < outPx.Length; i++)
            {
                byte a = (byte)(255f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.15f, 0.55f, thick[i])));   // sharp edge
                outPx[i] = new Color32(255, 255, 255, a);   // white ink: the material's tint gives it its colour
            }
            return outPx;
        }

        /// <summary>The first glyph whose name contains any of the words (best match first), or null.</summary>
        public static Glyph Find(params string[] words)
        {
            try
            {
                HashedGeneralValue<Glyph>.CheckItems();
                var all = HashedGeneralValue<Glyph>.All.Where(g => g != null && g.Sprite != null).ToList();
                foreach (string w in words)
                {
                    Glyph exact = all.FirstOrDefault(g => string.Equals(g.name, w, StringComparison.OrdinalIgnoreCase));
                    if (exact != null) return exact;
                    Glyph part = all.Where(g => g.name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(g => g.name.Length).FirstOrDefault();
                    if (part != null) return part;
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// Lays <paramref name="glyph"/> on a panel: <paramref name="at"/> is a point on the panel's surface,
        /// <paramref name="outward"/> its outward direction (both local to <paramref name="parent"/>),
        /// <paramref name="height"/> the drawing's height; <paramref name="ink"/> the colour of its lines.
        /// </summary>
        public static void Engrave(GameObject parent, string name, Glyph glyph, Vector3 at, Vector3 outward, float height, Color ink)
        {
            if (glyph == null || glyph.Sprite == null || ApplicationManager.IsHeadless) return;
            if (shader == null) shader = Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default");
            if (shader == null) return;

            Sprite s = glyph.Sprite;
            Rect r = s.textureRect;
            float width = height * (r.width / Mathf.Max(r.height, 1f));
            // A smooth copy of just this drawing (see Smooth); falls back to the game's own atlas if that fails.
            Texture tex = Smooth(glyph);
            Rect uv = new Rect(0f, 0f, 1f, 1f);
            if (tex == null)
            {
                tex = s.texture;
                uv = new Rect(r.x / tex.width, r.y / tex.height, r.width / tex.width, r.height / tex.height);
            }

            // A quad facing local +Z. The panel's outward is local +Z, and the viewer's right is local -X, so the
            // drawing's left edge goes on +X to read the right way round.
            float hw = width / 2f, hh = height / 2f;
            var mesh = new Mesh { name = "Slot Glyph " + glyph.name };
            mesh.vertices = new[] { new Vector3(hw, -hh, 0f), new Vector3(-hw, -hh, 0f), new Vector3(-hw, hh, 0f), new Vector3(hw, hh, 0f) };
            mesh.uv = new[] { new Vector2(uv.xMin, uv.yMin), new Vector2(uv.xMax, uv.yMin), new Vector2(uv.xMax, uv.yMax), new Vector2(uv.xMin, uv.yMax) };
            mesh.normals = Enumerable.Repeat(Vector3.forward, 4).ToArray();
            Vector3 c = Vector3.Cross(mesh.vertices[1] - mesh.vertices[0], mesh.vertices[2] - mesh.vertices[0]);
            mesh.triangles = c.z > 0 ? new[] { 0, 1, 2, 0, 2, 3 } : new[] { 0, 2, 1, 0, 3, 2 };   // drawn from the +Z side
            mesh.RecalculateBounds();

            var mat = new Material(shader) { name = "Slot Glyph Ink", mainTexture = tex, color = ink };
            var go = new GameObject("Glyph " + name);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = at + outward.normalized * 0.005f;   // clearly off the surface, so it never flickers or sinks in
            go.transform.localRotation = Quaternion.LookRotation(outward, Vector3.up);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }
    }
}
