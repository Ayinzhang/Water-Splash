using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace WaterSplash.Editor
{
    /// <summary>Refines the round-plinth sunset scene: geometry, tag rope, baked lighting data and post.</summary>
    public static class BottleVoyagePolish
    {
        const string Art = "Assets/MlsMpm/BottleDemo/Art";
        const string Folder = Art + "/Polish";
        const string Settings = "Assets/MlsMpm/BottleDemo/Settings";
        const float TableTop = -.11f, TableBottom = -.47f, TableRadius = 3.35f, AOExtent = 3.5f;

        [MenuItem("Water Splash/Polish Bottle Voyage")]
        public static void Apply()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (EditorApplication.isPlayingOrWillChangePlaymode || scene.path != BottleVoyageScene.ScenePath)
                throw new InvalidOperationException("Open Bottle Voyage in Edit Mode first.");
            var voyage = Object.FindAnyObjectByType<BottleVoyage>();
            if (voyage == null || voyage.boundary == null) throw new InvalidOperationException("Bottle Voyage controller is missing.");
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder(Art, "Polish");
            Transform bottle = GameObject.Find("Horizontal drift bottle")?.transform;
            if (bottle == null) throw new InvalidOperationException("Horizontal drift bottle is missing.");

            Material brass = Load("Weathered brass"), wood = Load("Oiled walnut"), cork = Load("Natural cork"), rope = Load("Cotton rigging");
            Tabletop(brass);
            Cork(bottle, cork);
            Cradles(voyage, bottle, wood);
            TagAndRope(voyage, bottle, rope, brass);
            Texture2D profile = ProfileTexture();
            PostVolume();
            Cubemap reflection = BakeReflection(voyage);
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(voyage.gameObject);
            Undo.RecordObject(voyage, "Voyage look");
            voyage.reflection = reflection;
            voyage.bottleProfile = profile;
            EditorUtility.SetDirty(voyage);
            var cameraData = voyage.waterCamera.GetUniversalAdditionalCameraData();
            Undo.RecordObject(cameraData, "Voyage post");
            cameraData.renderPostProcessing = true;
            cameraData.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
            EditorUtility.SetDirty(cameraData);
            ShadowSettings(voyage.pipeline);
            BakeOcclusion();
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log("Bottle Voyage polish applied (scene not saved).");
        }

        // ---------------------------------------------------------------- Table

        static void Tabletop(Material brass)
        {
            var plinth = GameObject.Find("Porcelain plinth");
            if (plinth == null) throw new InvalidOperationException("Porcelain plinth is missing.");
            const float bevel = .05f;
            var edge = new List<Vector2>();
            for (int i = 0; i <= 6; i++)
            {
                float a = Mathf.PI * .5f * (1 - i / 6f);
                edge.Add(new Vector2(TableRadius - bevel + Mathf.Cos(a) * bevel, TableTop - bevel + Mathf.Sin(a) * bevel));
            }
            edge.Add(new Vector2(TableRadius, TableBottom + .11f));
            var strips = new List<Vector2[]>
            {
                new[] { new Vector2(0, TableBottom), new Vector2(TableRadius - .04f, TableBottom) },
                new[] { new Vector2(TableRadius - .04f, TableBottom), new Vector2(TableRadius - .04f, TableBottom + .05f) },
                new[] { new Vector2(TableRadius - .04f, TableBottom + .05f), new Vector2(TableRadius - .07f, TableBottom + .06f) },
                new[] { new Vector2(TableRadius - .07f, TableBottom + .06f), new Vector2(TableRadius - .07f, TableBottom + .085f) },
                new[] { new Vector2(TableRadius - .07f, TableBottom + .085f), new Vector2(TableRadius - .02f, TableBottom + .095f),
                        new Vector2(TableRadius, TableBottom + .11f) },
                edge.AsEnumerable().Reverse().ToArray(),
                new[] { new Vector2(TableRadius - bevel, TableTop), new Vector2(0, TableTop) },
            };
            Mesh table = SaveMesh(Lathe(strips, 160), "Walnut plinth");
            var filter = plinth.GetComponent<MeshFilter>();
            Undo.RecordObject(filter, "Plinth mesh");
            filter.sharedMesh = table;
            Undo.RecordObject(plinth.transform, "Plinth transform");
            plinth.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            plinth.transform.localScale = Vector3.one;
            var collider = plinth.GetComponent<Collider>();
            if (collider != null) Undo.DestroyObjectImmediate(collider);

            var renderer = plinth.GetComponent<MeshRenderer>();
            Material old = renderer.sharedMaterial;
            string path = Art + "/Polished walnut tabletop.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("WaterSplash/Bottle Tabletop"));
                if (old != null && old.HasProperty("_BaseColor"))
                {
                    material.SetColor("_BaseColor", old.GetColor("_BaseColor"));
                    material.SetColor("_GrainColor", old.GetColor("_GrainColor"));
                    material.SetFloat("_Grain", old.GetFloat("_Grain"));
                }
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = Shader.Find("WaterSplash/Bottle Tabletop");
            material.SetFloat("_AOExtent", AOExtent);
            EditorUtility.SetDirty(material);
            Undo.RecordObject(renderer, "Plinth material");
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;

            var inlay = Child(plinth.transform, "Brass inlay ring");
            float y = TableTop + .0015f;
            Mesh ring = SaveMesh(Lathe(new List<Vector2[]>
            {
                new[] { new Vector2(3.06f, TableTop - .002f), new Vector2(3.06f, y) },
                new[] { new Vector2(3.06f, y), new Vector2(3.02f, y) },
                new[] { new Vector2(3.02f, y), new Vector2(3.02f, TableTop - .002f) },
            }, 160), "Brass inlay ring");
            Assign(inlay, ring, brass, ShadowCastingMode.Off);
        }

        // ---------------------------------------------------------------- Bottle details

        static void Cork(Transform bottle, Material cork)
        {
            var stopper = bottle.Find("Cork stopper");
            if (stopper == null) return;
            var strips = new List<Vector2[]>
            {
                new[] { new Vector2(0, -.12f), new Vector2(.236f, -.12f) },
                RoundCorner(new Vector2(.236f, -.12f), new Vector2(.262f, -.094f), new Vector2(.262f, -.12f), 6),
                new[] { new Vector2(.262f, -.094f), new Vector2(.247f, .02f), new Vector2(.232f, .12f) },
                new[] { new Vector2(.232f, .12f), new Vector2(0, .12f) },
            };
            Mesh mesh = SaveMesh(Lathe(strips, 48), "Tapered cork");
            Undo.RecordObject(stopper, "Cork");
            stopper.localScale = Vector3.one;
            stopper.localRotation = Quaternion.Euler(0, 0, 90);
            Assign(stopper.gameObject, mesh, cork, ShadowCastingMode.On);
            var collider = stopper.GetComponent<Collider>();
            if (collider != null) Undo.DestroyObjectImmediate(collider);
        }

        static Vector2[] RoundCorner(Vector2 from, Vector2 to, Vector2 corner, int steps)
        {
            var points = new Vector2[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                Vector2 a = Vector2.Lerp(from, corner, t), b = Vector2.Lerp(corner, to, t);
                points[i] = Vector2.Lerp(a, b, t);
            }
            return points;
        }

        static void Cradles(BottleVoyage voyage, Transform bottle, Material wood)
        {
            foreach (Transform child in bottle.Cast<Transform>().ToArray())
                if (child.name == "Walnut bottle cradle" || child.name == "Cradle arm")
                    Undo.DestroyObjectImmediate(child.gameObject);
            float floor = TableTop - voyage.boundary.center.y;
            foreach (float x in new[] { -1.12f, .55f })
            {
                float radius = BottleBoundary.Radius(x, out _) + .034f;
                var outline = Saddle(radius, floor);
                Mesh mesh = SaveMesh(Extrude(outline, .2f), $"Saddle cradle {x:0.00}");
                var cradle = Child(bottle, $"Walnut saddle cradle {x:0.00}");
                cradle.transform.localPosition = new Vector3(x, 0, 0);
                cradle.transform.localRotation = Quaternion.Euler(0, 90, 0);
                cradle.transform.localScale = Vector3.one;
                Assign(cradle, mesh, wood, ShadowCastingMode.On);
            }
        }

        static List<Vector2> Saddle(float radius, float floor)
        {
            float a0 = -38 * Mathf.Deg2Rad;
            Vector2 tip = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * radius;
            var right = new List<Vector2>
            {
                new Vector2(.58f, floor), new Vector2(.58f, floor + .035f), new Vector2(.555f, floor + .055f),
                new Vector2(.47f, floor + .058f),
                new Vector2(tip.x + .07f, tip.y - .05f), new Vector2(tip.x + .055f, tip.y - .005f),
                new Vector2(tip.x + .025f, tip.y + .012f),
            };
            var outline = new List<Vector2> { new Vector2(-.58f, floor) };
            outline.AddRange(right);
            for (int i = 0; i <= 28; i++)
            {
                float a = Mathf.Lerp(a0, -Mathf.PI - a0, i / 28f);
                outline.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius);
            }
            for (int i = right.Count - 1; i >= 1; i--) outline.Add(new Vector2(-right[i].x, right[i].y));
            return outline;
        }

        // ---------------------------------------------------------------- Tag + rope

        static void TagAndRope(BottleVoyage voyage, Transform bottle, Material ropeMaterial, Material brass)
        {
            foreach (string name in new[] { "Tag string", "Paper voyage tag" })
            {
                var old = bottle.Find(name);
                if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
            }
            var rootObject = GameObject.Find("Voyage tag rope");
            if (rootObject == null)
            {
                rootObject = new GameObject("Voyage tag rope");
                Undo.RegisterCreatedObjectUndo(rootObject, "Voyage tag rope");
            }
            rootObject.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            rootObject.transform.localScale = Vector3.one;
            var rope = rootObject.GetComponent<TagRope>();
            if (rope == null) rope = Undo.AddComponent<TagRope>(rootObject);

            var tag = Child(rootObject.transform, "Voyage luggage tag");
            Vector2 size = new Vector2(.22f, .30f);
            Material paper = TagMaterial();
            Assign(tag, SaveMesh(TagMesh(size, .045f, .0125f, .005f), "Voyage luggage tag"), paper, ShadowCastingMode.On);
            var eyelet = Child(tag.transform, "Brass eyelet");
            eyelet.transform.localPosition = Vector3.zero;
            eyelet.transform.localRotation = Quaternion.identity;
            eyelet.transform.localScale = Vector3.one;
            Assign(eyelet, SaveMesh(Torus(.0175f, .0048f, 24, 8), "Tag eyelet"), brass, ShadowCastingMode.Off);

            Undo.RecordObject(rope, "Tag rope");
            rope.voyage = voyage;
            rope.tag = tag.transform;
            rope.ropeMaterial = ropeMaterial;
            rope.tagSize = size;
            rope.tableHeight = TableTop;
            EditorUtility.SetDirty(rope);
            // Edit-mode preview pose; play mode drives it from the simulation.
            Vector3 anchor = voyage.boundary.FromProfile(rope.anchorProfile);
            Vector3 facing = -voyage.waterCamera.transform.forward; facing.y = 0;
            tag.transform.SetPositionAndRotation(anchor + Vector3.down * rope.length + facing.normalized * .04f,
                Quaternion.LookRotation(-facing.normalized, Vector3.up));
            Undo.RecordObject(voyage, "Tag rope hook");
            voyage.tagRope = rope;
            EditorUtility.SetDirty(voyage);
        }

        static Mesh TagMesh(Vector2 size, float top, float hole, float thickness)
        {
            float w = size.x * .5f, bottom = -size.y, chamfer = .06f, round = .014f;
            var corners = new[]
            {
                new Vector2(-w + chamfer, top), new Vector2(-w, top - chamfer),
                new Vector2(-w, bottom + round), new Vector2(-w + round, bottom),
                new Vector2(w - round, bottom), new Vector2(w, bottom + round),
                new Vector2(w, top - chamfer), new Vector2(w - chamfer, top),
            };
            var outer = new List<Vector2>();
            for (int i = 0; i < corners.Length; i++)
            {
                Vector2 a = corners[i], b = corners[(i + 1) % corners.Length];
                int steps = Mathf.Max(1, Mathf.CeilToInt((b - a).magnitude / .012f));
                for (int s = 0; s < steps; s++) outer.Add(Vector2.Lerp(a, b, s / (float)steps));
            }
            // The outline is convex around the hole, so a polar strip triangulates it.
            outer = outer.OrderBy(p => Mathf.Atan2(p.y, p.x)).ToList();
            int n = outer.Count;
            var inner = outer.Select(p => p.normalized * hole).ToList();
            var v = new List<Vector3>(); var nr = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            float height = top - bottom;
            Vector2 Front(Vector2 p) => new Vector2((p.x / size.x + .5f) * .5f, (p.y - bottom) / height);
            Vector2 Back(Vector2 p) => new Vector2(.5f + (-p.x / size.x + .5f) * .5f, (p.y - bottom) / height);
            for (int face = 0; face < 2; face++)
            {
                float z = face == 0 ? -thickness * .5f : thickness * .5f;
                int start = v.Count;
                for (int i = 0; i < n; i++)
                {
                    v.Add(new Vector3(outer[i].x, outer[i].y, z)); v.Add(new Vector3(inner[i].x, inner[i].y, z));
                    nr.Add(new Vector3(0, 0, face == 0 ? -1 : 1)); nr.Add(nr[nr.Count - 1]);
                    uv.Add(face == 0 ? Front(outer[i]) : Back(outer[i])); uv.Add(face == 0 ? Front(inner[i]) : Back(inner[i]));
                }
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    int o0 = start + i * 2, i0 = o0 + 1, o1 = start + j * 2, i1 = o1 + 1;
                    if (face == 0) t.AddRange(new[] { o0, o1, i0, i0, o1, i1 });
                    else t.AddRange(new[] { o0, i0, o1, i0, i1, o1 });
                }
            }
            void Wall(List<Vector2> ring, bool inward)
            {
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    Vector2 a = ring[i], b = ring[j], d = (b - a).normalized;
                    Vector3 normal = new Vector3(d.y, -d.x, 0);
                    if (inward) normal = -normal;
                    int s = v.Count;
                    v.Add(new Vector3(a.x, a.y, -thickness * .5f)); v.Add(new Vector3(b.x, b.y, -thickness * .5f));
                    v.Add(new Vector3(a.x, a.y, thickness * .5f)); v.Add(new Vector3(b.x, b.y, thickness * .5f));
                    for (int k = 0; k < 4; k++) { nr.Add(normal); uv.Add(Front(a)); }
                    AddOriented(t, v, nr, s, s + 2, s + 1);
                    AddOriented(t, v, nr, s + 1, s + 2, s + 3);
                }
            }
            // Outline is ordered counter-clockwise, so (d.y,-d.x) points outward.
            Wall(outer, false);
            Wall(inner, true);
            var mesh = new Mesh { name = "Voyage luggage tag" };
            mesh.SetVertices(v); mesh.SetNormals(nr); mesh.SetUVs(0, uv); mesh.SetTriangles(t, 0);
            mesh.RecalculateBounds(); mesh.RecalculateTangents();
            return mesh;
        }

        static Material TagMaterial()
        {
            string texturePath = Folder + "/Voyage tag paper.png";
            var texture = new Texture2D(1024, 704, TextureFormat.RGBA32, false);
            var pixels = new Color32[texture.width * texture.height];
            var random = new System.Random(4127);
            Color paper = new Color(.93f, .86f, .72f), ink = new Color(.24f, .17f, .12f), red = new Color(.56f, .16f, .11f);
            // Each half of the atlas is one face, 512 x 704 px for 0.22 x 0.345 m.
            for (int y = 0; y < texture.height; y++)
                for (int x = 0; x < texture.width; x++)
                {
                    int lx = x % 512; bool back = x >= 512;
                    float u = lx / 511f, v = y / 703f;
                    float edge = Mathf.Min(Mathf.Min(u, 1 - u) * .22f, Mathf.Min(v, 1 - v) * .345f);
                    float fibre = Mathf.PerlinNoise(x * .05f, y * .4f) * .05f + Mathf.PerlinNoise(x * .013f + (back ? 40 : 0), y * .011f) * .09f;
                    Color c = paper * (1 - fibre) + new Color(.04f, .03f, 0) * fibre;
                    c = Color.Lerp(new Color(.62f, .45f, .28f), c, Mathf.SmoothStep(0, 1, edge / .012f));
                    float bx = Mathf.Min(u, 1 - u) * 512, by = Mathf.Min(v, 1 - v) * 704;
                    float border = Mathf.Min(bx, by);
                    if (Mathf.Abs(border - 24) < 1.6f || Mathf.Abs(border - 31) < 1f) c = Color.Lerp(c, red, .8f);
                    pixels[y * texture.width + x] = c;
                }
            void Line(int face, Vector2 a, Vector2 b, float width, Color color, float alpha = .9f)
            {
                int steps = Mathf.CeilToInt((b - a).magnitude * 2);
                for (int s = 0; s <= steps; s++)
                {
                    Vector2 p = Vector2.Lerp(a, b, s / (float)Mathf.Max(1, steps));
                    for (int dy = -Mathf.CeilToInt(width); dy <= Mathf.CeilToInt(width); dy++)
                        for (int dx = -Mathf.CeilToInt(width); dx <= Mathf.CeilToInt(width); dx++)
                        {
                            int x = Mathf.RoundToInt(p.x) + dx, y = Mathf.RoundToInt(p.y) + dy;
                            if (x < 0 || x >= 512 || y < 0 || y >= 704) continue;
                            float f = Mathf.Clamp01(width + .5f - Mathf.Sqrt(dx * dx + dy * dy));
                            int index = y * texture.width + x + face * 512;
                            pixels[index] = Color.Lerp(pixels[index], color, f * alpha);
                        }
                }
            }
            void Scribble(int face, float x0, float x1, float y)
            {
                float x = x0;
                while (x < x1)
                {
                    float word = 18 + (float)random.NextDouble() * 46;
                    Vector2 last = new Vector2(x, y);
                    for (float s = 0; s < word && x + s < x1; s += 3)
                    {
                        float h = Mathf.Sin(s * .9f + (float)random.NextDouble()) * 5 + (float)random.NextDouble() * 4;
                        var p = new Vector2(x + s, y + h);
                        Line(face, last, p, .9f, ink, .75f);
                        last = p;
                    }
                    x += word + 12;
                }
            }
            // Front: compass rose under the eyelet and an address block.
            Vector2 rose = new Vector2(256, 470);
            for (int i = 0; i < 16; i++)
            {
                float a = i * Mathf.PI / 8;
                float length = i % 4 == 0 ? 92 : i % 2 == 0 ? 58 : 34;
                Vector2 dir = new Vector2(Mathf.Sin(a), Mathf.Cos(a));
                Vector2 side = new Vector2(-dir.y, dir.x) * (i % 4 == 0 ? 11 : 6);
                Color color = i % 4 == 0 ? (i == 0 ? red : ink) : new Color(.45f, .34f, .24f);
                for (float k = 0; k <= 1; k += .04f)
                    Line(0, rose + side * (1 - k), rose + dir * length, 1.1f, color, .9f);
            }
            for (int r = 0; r < 2; r++)
                for (int i = 0; i < 180; i++)
                {
                    float a = i * Mathf.PI * 2 / 180, b = (i + 1) * Mathf.PI * 2 / 180;
                    float radius = r == 0 ? 70 : 76;
                    Line(0, rose + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius, rose + new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * radius, .9f, ink, .7f);
                }
            for (int i = 0; i < 4; i++)
            {
                float y = 290 - i * 52;
                Line(0, new Vector2(60, y), new Vector2(452, y), .7f, new Color(.55f, .42f, .3f), .6f);
                Scribble(0, 66 + (i == 0 ? 0 : 20), i == 3 ? 300 : 440, y + 8);
            }
            // Back: ruled log lines with entries and a wax seal.
            for (int i = 0; i < 9; i++)
            {
                float y = 600 - i * 58;
                Line(1, new Vector2(54, y), new Vector2(458, y), .7f, new Color(.55f, .42f, .3f), .6f);
                if (i > 0 && i < 7) Scribble(1, 60, 440 - (i * 37 % 120), y + 8);
            }
            Vector2 seal = new Vector2(380, 110);
            for (int y = -40; y <= 40; y++)
                for (int x = -40; x <= 40; x++)
                {
                    float d = Mathf.Sqrt(x * x + y * y) + Mathf.Sin(Mathf.Atan2(y, x) * 9) * 2.5f;
                    if (d > 38) continue;
                    int index = (int)(seal.y + y) * texture.width + (int)seal.x + x + 512;
                    float shade = 1 - .25f * Mathf.Clamp01((x - y) / 60f) - (Mathf.Abs(d - 24) < 2 ? .25f : 0);
                    pixels[index] = red * shade;
                }
            texture.SetPixels32(pixels);
            texture.Apply();
            System.IO.File.WriteAllBytes(texturePath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(texturePath);
            var importer = (TextureImporter)AssetImporter.GetAtPath(texturePath);
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.anisoLevel = 2;
            importer.SaveAndReimport();

            string path = Folder + "/Voyage tag paper.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath));
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Smoothness", .18f);
            material.SetFloat("_Metallic", 0);
            EditorUtility.SetDirty(material);
            return material;
        }

        // ---------------------------------------------------------------- Lighting data

        static Texture2D ProfileTexture()
        {
            string path = Folder + "/Bottle outer profile.asset";
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null)
            {
                texture = new Texture2D(256, 1, TextureFormat.R8, false, true) { name = "Bottle outer profile" };
                AssetDatabase.CreateAsset(texture, path);
            }
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            var pixels = new Color[256];
            for (int i = 0; i < 256; i++)
            {
                float x = i / 255f * 4 - 2, r;
                if (x < -1.9f || x > 1.96f) r = 0;
                else if (x > 1.72f) r = .262f;
                else if (x > 1.66f) r = .33f;
                else r = BottleBoundary.Radius(Mathf.Max(x, -1.88f), out _) + .03f;
                pixels[i] = new Color(r, r, r, 1);
            }
            texture.SetPixels(pixels);
            texture.Apply(false, false);
            EditorUtility.SetDirty(texture);
            return texture;
        }

        static Volume PostVolume()
        {
            string path = Settings + "/Voyage post.asset";
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, path);
            }
            T Effect<T>() where T : VolumeComponent
            {
                if (!profile.TryGet(out T component)) { component = profile.Add<T>(true); AssetDatabase.AddObjectToAsset(component, profile); }
                component.active = true;
                return component;
            }
            var bloom = Effect<Bloom>();
            bloom.threshold.Override(.92f);
            bloom.intensity.Override(.45f);
            bloom.scatter.Override(.62f);
            bloom.tint.Override(new Color(1, .9f, .78f));
            bloom.highQualityFiltering.Override(false);
            bloom.downscale.Override(BloomDownscaleMode.Quarter);
            bloom.maxIterations.Override(5);
            var grading = Effect<ColorAdjustments>();
            grading.contrast.Override(7);
            grading.saturation.Override(-4);
            grading.postExposure.Override(.06f);
            var balance = Effect<WhiteBalance>();
            balance.temperature.Override(2);
            balance.tint.Override(-3);
            var lift = Effect<LiftGammaGain>();
            lift.lift.Override(new Vector4(1, 1, 1.02f, -.01f));
            lift.gain.Override(new Vector4(1, 1, 1, 0));
            var vignette = Effect<Vignette>();
            vignette.intensity.Override(.2f);
            vignette.smoothness.Override(.5f);
            vignette.color.Override(new Color(.08f, .06f, .06f));
            EditorUtility.SetDirty(profile);

            var go = GameObject.Find("Voyage post volume");
            if (go == null)
            {
                go = new GameObject("Voyage post volume");
                Undo.RegisterCreatedObjectUndo(go, "Voyage post volume");
            }
            var volume = go.GetComponent<Volume>();
            if (volume == null) volume = Undo.AddComponent<Volume>(go);
            volume.isGlobal = true;
            volume.sharedProfile = profile;
            return volume;
        }

        static Cubemap BakeReflection(BottleVoyage voyage)
        {
            var go = GameObject.Find("Voyage reflection probe");
            if (go == null)
            {
                go = new GameObject("Voyage reflection probe");
                Undo.RegisterCreatedObjectUndo(go, "Voyage reflection probe");
            }
            go.transform.position = voyage.boundary.center;
            var probe = go.GetComponent<ReflectionProbe>();
            if (probe == null) probe = Undo.AddComponent<ReflectionProbe>(go);
            probe.mode = ReflectionProbeMode.Baked;
            probe.size = new Vector3(9, 5, 9);
            probe.resolution = 128;
            probe.hdr = false;
            probe.boxProjection = false;
            probe.importance = 1;
            // The glass and water must not reflect themselves.
            var hidden = new List<Renderer>();
            var bottle = GameObject.Find("Horizontal drift bottle");
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude))
            {
                bool glass = r.sharedMaterial != null && r.sharedMaterial.shader.name == "WaterSplash/Bottle Glass";
                bool inBottle = bottle != null && r.transform.IsChildOf(bottle.transform) && r.name != "Cork stopper"
                    && !r.name.StartsWith("Walnut saddle");
                if (!r.enabled || (!glass && !inBottle && !r.transform.IsChildOf(voyage.boat))) continue;
                r.enabled = false; hidden.Add(r);
            }
            string path = Folder + "/Voyage reflection.png";
            try
            {
                if (!Lightmapping.BakeReflectionProbe(probe, path)) throw new InvalidOperationException("Reflection probe bake failed.");
            }
            finally
            {
                foreach (var r in hidden) r.enabled = true;
            }
            AssetDatabase.ImportAsset(path);
            return AssetDatabase.LoadAssetAtPath<Cubemap>(path);
        }

        static void ShadowSettings(UniversalRenderPipelineAsset pipeline)
        {
            if (pipeline == null) return;
            var so = new SerializedObject(pipeline);
            var distance = so.FindProperty("m_ShadowDistance");
            var resolution = so.FindProperty("m_MainLightShadowmapResolution");
            if (distance != null) distance.floatValue = 15;
            if (resolution != null) resolution.intValue = 2048;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(pipeline);
        }

        /// <summary>Ray-traces contact occlusion from every object resting on the plinth into a top-down map.</summary>
        static void BakeOcclusion()
        {
            const int size = 256, rays = 48;
            const float reach = .75f;
            var temporary = new List<GameObject>();
            var weights = new Dictionary<Collider, float>();
            var plinth = GameObject.Find("Porcelain plinth");
            try
            {
                foreach (var filter in Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Exclude))
                {
                    var renderer = filter.GetComponent<MeshRenderer>();
                    if (renderer == null || !renderer.enabled || filter.sharedMesh == null || filter.gameObject == plinth) continue;
                    if (filter.transform.IsChildOf(plinth.transform)) continue;
                    if (renderer.bounds.min.y > TableTop + 1.2f) continue;
                    string shader = renderer.sharedMaterial != null ? renderer.sharedMaterial.shader.name : "";
                    if (shader.Contains("Unlit") || shader.Contains("Sunset")) continue;
                    var proxy = new GameObject("AO proxy") { hideFlags = HideFlags.HideAndDontSave };
                    proxy.transform.SetPositionAndRotation(filter.transform.position, filter.transform.rotation);
                    proxy.transform.localScale = filter.transform.lossyScale;
                    var collider = proxy.AddComponent<MeshCollider>();
                    collider.sharedMesh = filter.sharedMesh;
                    temporary.Add(proxy);
                    weights[collider] = shader == "WaterSplash/Bottle Glass" ? .3f : 1;
                }
                Physics.SyncTransforms();
                var directions = new Vector3[rays];
                for (int i = 0; i < rays; i++)
                {
                    float u = (i + .5f) / rays, phi = i * 2.39996323f;
                    float r = Mathf.Sqrt(u);
                    directions[i] = new Vector3(Mathf.Cos(phi) * r, Mathf.Sqrt(1 - u), Mathf.Sin(phi) * r);
                }
                var hits = new RaycastHit[16];
                var ao = new float[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        Vector3 origin = new Vector3(((x + .5f) / size * 2 - 1) * AOExtent, TableTop + .004f, ((y + .5f) / size * 2 - 1) * AOExtent);
                        float occlusion = 0;
                        for (int i = 0; i < rays; i++)
                        {
                            int count = Physics.RaycastNonAlloc(origin, directions[i], hits, reach, ~0, QueryTriggerInteraction.Ignore);
                            float best = 0;
                            for (int h = 0; h < count; h++)
                                if (weights.TryGetValue(hits[h].collider, out float weight))
                                    best = Mathf.Max(best, weight * (1 - hits[h].distance / reach));
                            occlusion += best;
                        }
                        ao[y * size + x] = occlusion / rays;
                    }
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
                var pixels = new Color[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float sum = 0, total = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int sx = Mathf.Clamp(x + dx, 0, size - 1), sy = Mathf.Clamp(y + dy, 0, size - 1);
                                float k = dx == 0 && dy == 0 ? 4 : dx == 0 || dy == 0 ? 2 : 1;
                                sum += ao[sy * size + sx] * k; total += k;
                            }
                        float value = 1 - Mathf.Clamp01(sum / total * 1.9f);
                        pixels[y * size + x] = new Color(value, value, value, 1);
                    }
                texture.SetPixels(pixels);
                texture.Apply();
                string path = Folder + "/Tabletop occlusion.png";
                System.IO.File.WriteAllBytes(path, texture.EncodeToPNG());
                Object.DestroyImmediate(texture);
                AssetDatabase.ImportAsset(path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.sRGBTexture = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
                var material = plinth.GetComponent<MeshRenderer>().sharedMaterial;
                material.SetTexture("_AOTex", AssetDatabase.LoadAssetAtPath<Texture2D>(path));
                EditorUtility.SetDirty(material);
            }
            finally
            {
                foreach (var go in temporary) Object.DestroyImmediate(go);
            }
        }

        // ---------------------------------------------------------------- Mesh helpers

        /// <summary>Revolves (radius, height) strips around Y. Strips are smooth inside and hard between.</summary>
        static Mesh Lathe(List<Vector2[]> strips, int sides)
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            float travelled = 0;
            foreach (var strip in strips)
            {
                int start = v.Count, count = strip.Length;
                var normals2 = new Vector2[count];
                for (int i = 0; i < count; i++)
                {
                    Vector2 sum = Vector2.zero;
                    if (i > 0) { Vector2 d = (strip[i] - strip[i - 1]).normalized; sum += new Vector2(d.y, -d.x); }
                    if (i < count - 1) { Vector2 d = (strip[i + 1] - strip[i]).normalized; sum += new Vector2(d.y, -d.x); }
                    normals2[i] = sum.normalized;
                }
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) travelled += (strip[i] - strip[i - 1]).magnitude;
                    for (int j = 0; j <= sides; j++)
                    {
                        float a = j * Mathf.PI * 2 / sides, c = Mathf.Cos(a), s = Mathf.Sin(a);
                        v.Add(new Vector3(c * strip[i].x, strip[i].y, s * strip[i].x));
                        n.Add(new Vector3(c * normals2[i].x, normals2[i].y, s * normals2[i].x));
                        uv.Add(new Vector2(j / (float)sides, travelled));
                    }
                }
                for (int i = 0; i < count - 1; i++)
                    for (int j = 0; j < sides; j++)
                    {
                        int a = start + i * (sides + 1) + j, b = a + 1, c = a + sides + 1, d = c + 1;
                        AddOriented(t, v, n, a, c, b);
                        AddOriented(t, v, n, b, c, d);
                    }
            }
            return Build(v, n, uv, t, "Lathe");
        }

        static void AddOriented(List<int> t, List<Vector3> v, List<Vector3> n, int a, int b, int c)
        {
            Vector3 face = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
            if (face.sqrMagnitude < 1e-14f) return;
            if (Vector3.Dot(face, n[a] + n[b] + n[c]) < 0) { t.Add(a); t.Add(c); t.Add(b); }
            else { t.Add(a); t.Add(b); t.Add(c); }
        }

        /// <summary>Extrudes a counter-clockwise outline along Z with hard caps and smooth gentle walls.</summary>
        static Mesh Extrude(List<Vector2> outline, float depth)
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            int count = outline.Count;
            float area = 0;
            for (int i = 0; i < count; i++) area += outline[i].x * outline[(i + 1) % count].y - outline[(i + 1) % count].x * outline[i].y;
            if (area < 0) outline.Reverse();
            var caps = Triangulate(outline);
            for (int face = 0; face < 2; face++)
            {
                float z = (face - .5f) * depth;
                int start = v.Count;
                foreach (var p in outline)
                {
                    v.Add(new Vector3(p.x, p.y, z));
                    n.Add(new Vector3(0, 0, face == 0 ? -1 : 1));
                    uv.Add(p * 2);
                }
                for (int i = 0; i < caps.Count; i += 3)
                    AddOriented(t, v, n, start + caps[i], start + caps[i + 1], start + caps[i + 2]);
            }
            var edgeNormals = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                Vector2 d = (outline[(i + 1) % count] - outline[i]).normalized;
                edgeNormals[i] = new Vector3(d.y, -d.x, 0);
            }
            float along = 0;
            for (int i = 0; i < count; i++)
            {
                int j = (i + 1) % count;
                Vector3 normal = edgeNormals[i];
                Vector3 prev = edgeNormals[(i + count - 1) % count], next = edgeNormals[j];
                Vector3 na = Vector3.Angle(prev, normal) < 25 ? (prev + normal).normalized : normal;
                Vector3 nb = Vector3.Angle(next, normal) < 25 ? (next + normal).normalized : normal;
                int s = v.Count;
                float length = (outline[j] - outline[i]).magnitude;
                v.Add(new Vector3(outline[i].x, outline[i].y, -depth * .5f)); n.Add(na); uv.Add(new Vector2(along * 2, 0));
                v.Add(new Vector3(outline[j].x, outline[j].y, -depth * .5f)); n.Add(nb); uv.Add(new Vector2((along + length) * 2, 0));
                v.Add(new Vector3(outline[i].x, outline[i].y, depth * .5f)); n.Add(na); uv.Add(new Vector2(along * 2, depth * 2));
                v.Add(new Vector3(outline[j].x, outline[j].y, depth * .5f)); n.Add(nb); uv.Add(new Vector2((along + length) * 2, depth * 2));
                AddOriented(t, v, n, s, s + 1, s + 2);
                AddOriented(t, v, n, s + 1, s + 3, s + 2);
                along += length;
            }
            return Build(v, n, uv, t, "Extrusion");
        }

        static List<int> Triangulate(List<Vector2> outline)
        {
            var result = new List<int>();
            var remaining = Enumerable.Range(0, outline.Count).ToList();
            for (int guard = 0; remaining.Count > 2 && guard < outline.Count * outline.Count; guard++)
            {
                bool found = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    int a = remaining[(i + remaining.Count - 1) % remaining.Count], b = remaining[i], c = remaining[(i + 1) % remaining.Count];
                    if (Cross(outline[b] - outline[a], outline[c] - outline[b]) <= 1e-9f) continue;
                    bool contains = false;
                    foreach (int p in remaining)
                        if (p != a && p != b && p != c && Inside(outline[p], outline[a], outline[b], outline[c])) { contains = true; break; }
                    if (contains) continue;
                    result.Add(a); result.Add(b); result.Add(c);
                    remaining.RemoveAt(i); found = true; break;
                }
                if (!found) break;
            }
            return result;
        }

        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c) =>
            Cross(b - a, p - a) >= 0 && Cross(c - b, p - b) >= 0 && Cross(a - c, p - c) >= 0;

        /// <summary>Torus around the local Z axis.</summary>
        static Mesh Torus(float major, float minor, int segments, int sides)
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
            for (int i = 0; i <= segments; i++)
            {
                float a = i * Mathf.PI * 2 / segments;
                Vector3 radial = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0);
                for (int j = 0; j <= sides; j++)
                {
                    float b = j * Mathf.PI * 2 / sides;
                    Vector3 normal = radial * Mathf.Cos(b) + Vector3.forward * Mathf.Sin(b);
                    v.Add(radial * major + normal * minor); n.Add(normal); uv.Add(new Vector2(i / (float)segments, j / (float)sides));
                }
            }
            for (int i = 0; i < segments; i++)
                for (int j = 0; j < sides; j++)
                {
                    int a = i * (sides + 1) + j, b = a + 1, c = a + sides + 1, d = c + 1;
                    AddOriented(t, v, n, a, c, b);
                    AddOriented(t, v, n, b, c, d);
                }
            return Build(v, n, uv, t, "Torus");
        }

        static Mesh Build(List<Vector3> v, List<Vector3> n, List<Vector2> uv, List<int> t, string name)
        {
            var mesh = new Mesh { name = name };
            if (v.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(v); mesh.SetNormals(n); mesh.SetUVs(0, uv); mesh.SetTriangles(t, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        static Mesh SaveMesh(Mesh mesh, string name)
        {
            string path = $"{Folder}/{name}.asset";
            mesh.name = name;
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null) { AssetDatabase.CreateAsset(mesh, path); return mesh; }
            EditorUtility.CopySerialized(mesh, existing);
            existing.name = name;
            EditorUtility.SetDirty(existing);
            Object.DestroyImmediate(mesh);
            return existing;
        }

        static GameObject Child(Transform parent, string name)
        {
            var existing = parent.Find(name);
            if (existing != null) return existing.gameObject;
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, name);
            go.transform.SetParent(parent, false);
            return go;
        }

        static void Assign(GameObject go, Mesh mesh, Material material, ShadowCastingMode shadows)
        {
            var filter = go.GetComponent<MeshFilter>();
            if (filter == null) filter = Undo.AddComponent<MeshFilter>(go);
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer == null) renderer = Undo.AddComponent<MeshRenderer>(go);
            Undo.RecordObject(filter, "Mesh"); Undo.RecordObject(renderer, "Renderer");
            filter.sharedMesh = mesh;
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = shadows;
        }

        static Material Load(string name)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>($"{Art}/{name}.mat");
            if (material == null) throw new InvalidOperationException($"Missing material {name}.");
            return material;
        }
    }
}
