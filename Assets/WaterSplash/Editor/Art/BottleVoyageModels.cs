using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace WaterSplash.Editor
{
    /// <summary>
    /// Imports the Blender-built Bottle Voyage props (Tools/Blender/voyage_models.py) and assigns them by object name.
    /// Meshes are exported in their Unity pivot space, so scene transforms stay untouched.
    /// </summary>
    public static class BottleVoyageModels
    {
        public const string Folder = "Assets/WaterSplash/Art/BottleVoyage/Blender";
        const string Textures = Folder + "/Textures";

        class Importer : AssetPostprocessor
        {
            void OnPreprocessModel()
            {
                if (!assetPath.StartsWith(Folder + "/")) return;
                var model = (ModelImporter)assetImporter;
                model.materialImportMode = ModelImporterMaterialImportMode.None;
                model.importNormals = ModelImporterNormals.Import;
                model.importTangents = ModelImporterTangents.CalculateMikk;
                model.importAnimation = false;
                model.animationType = ModelImporterAnimationType.None;
                model.importCameras = false;
                model.importLights = false;
                model.importBlendShapes = false;
                model.isReadable = false;
                model.meshCompression = ModelImporterMeshCompression.Off;
                model.optimizeMeshVertices = true;
                model.optimizeMeshPolygons = true;
            }

            void OnPreprocessTexture()
            {
                if (!assetPath.StartsWith(Textures + "/")) return;
                var texture = (TextureImporter)assetImporter;
                bool normal = assetPath.EndsWith(" normal.png"), mask = assetPath.EndsWith(" mask.png");
                texture.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                texture.sRGBTexture = !normal && !mask;
                texture.alphaSource = mask ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
                texture.mipmapEnabled = true;
                texture.anisoLevel = 4;
                texture.wrapMode = assetPath.Contains("/Cotton rope ") ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
            }
        }

        public static Mesh Mesh(string fbx, string name)
        {
            string path = $"{Folder}/{fbx}.fbx";
            var mesh = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().FirstOrDefault(m => m.name == name);
            if (mesh == null) throw new InvalidOperationException($"Mesh '{name}' missing in {path}. Run Tools/Blender/voyage_models.py.");
            return mesh;
        }

        /// <summary>URP Lit material using a baked atlas: albedo, tangent normal and mask (R metal, G AO, A smoothness).</summary>
        public static Material Atlas(string label, Vector2 tiling, float normalScale = 1)
        {
            string path = $"{Folder}/{label}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = Shader.Find("Universal Render Pipeline/Lit");
            Texture2D albedo = Texture(label + " albedo"), normal = Texture(label + " normal"), mask = Texture(label + " mask");
            material.SetTexture("_BaseMap", albedo);
            material.SetColor("_BaseColor", Color.white);
            material.SetTexture("_BumpMap", normal);
            material.SetFloat("_BumpScale", normalScale);
            material.SetTexture("_MetallicGlossMap", mask);
            material.SetTexture("_OcclusionMap", mask);
            material.SetFloat("_OcclusionStrength", 1);
            material.SetFloat("_Smoothness", 1);
            material.SetFloat("_Metallic", 1);
            material.SetFloat("_SmoothnessTextureChannel", 0);
            material.SetFloat("_WorkflowMode", 1);
            material.EnableKeyword("_NORMALMAP");
            material.EnableKeyword("_METALLICSPECGLOSSMAP");
            material.EnableKeyword("_OCCLUSIONMAP");
            material.SetTextureScale("_BaseMap", tiling);
            material.mainTextureScale = tiling;
            material.enableInstancing = true;
            EditorUtility.SetDirty(material);
            return material;
        }

        static Texture2D Texture(string name)
        {
            string path = $"{Textures}/{name}.png";
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null) throw new InvalidOperationException($"Missing baked texture {path}.");
            return texture;
        }

        /// <summary>Cork, saddle cradles (shared atlas) and the neck cord (tiled rope).</summary>
        public static void Fittings(Transform bottle)
        {
            AssetDatabase.Refresh();
            Material fittings = Atlas("Bottle fittings", Vector2.one), rope = Atlas("Cotton rope", Vector2.one);
            foreach (string name in new[] { "Cork stopper", "Walnut saddle cradle -1.12", "Walnut saddle cradle 0.55", "Cotton neck cord" })
            {
                var target = bottle.Find(name);
                if (target == null) throw new InvalidOperationException($"{name} is missing under the bottle.");
                Assign(target.gameObject, Mesh("Bottle fittings", name), name == "Cotton neck cord" ? rope : fittings);
            }
        }

        /// <summary>Replaces the procedural boat parts with the Blender hull/sails atlas mesh and the rigging rope mesh.</summary>
        public static void Boat(Transform boat)
        {
            var line = boat.GetComponent<LineRenderer>();
            if (line != null) Undo.DestroyObjectImmediate(line);
            for (int i = boat.childCount - 1; i >= 0; i--) Undo.DestroyObjectImmediate(boat.GetChild(i).gameObject);
            Material hull = Atlas("Sailboat", Vector2.one), rope = Atlas("Cotton rope", Vector2.one);
            foreach (string name in new[] { "Sailboat", "Sailboat rigging" })
            {
                var go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "Blender boat");
                go.transform.SetParent(boat, false);
                Assign(go, Mesh("Sailboat", name), name == "Sailboat" ? hull : rope);
            }
        }

        /// <summary>Desk still life: baked props atlas, painted paper atlas and tiled rope meshes, all resting on the tabletop.</summary>
        public static void Desk()
        {
            const string fbx = "Desk props";
            Material props = Atlas("Desk props", Vector2.one), paper = Atlas("Desk paper", Vector2.one), rope = Atlas("Cotton rope", Vector2.one);
            var all = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include);
            Transform Find(string name)
            {
                var t = all.FirstOrDefault(x => x != null && x.name == name && x.gameObject.scene.IsValid());
                if (t == null) throw new InvalidOperationException($"Desk prop '{name}' is missing.");
                return t;
            }
            void Strip(Transform t)
            {
                var line = t.GetComponent<LineRenderer>();
                if (line != null) Undo.DestroyObjectImmediate(line);
                for (int i = t.childCount - 1; i >= 0; i--) Undo.DestroyObjectImmediate(t.GetChild(i).gameObject);
                Undo.RecordObject(t, "Blender desk");
            }

            var compass = Find("Pocket compass");
            Strip(compass);
            compass.localScale = Vector3.one;
            Assign(compass.gameObject, Mesh(fbx, "Pocket compass"), props);

            var chart = Find("Hand-drawn nautical chart");
            Strip(chart);
            chart.localScale = Vector3.one;
            Assign(chart.gameObject, Mesh(fbx, "Hand-drawn nautical chart"), paper);

            var card = Find("Blank voyage postcard");
            Strip(card);
            card.localScale = Vector3.one;
            Assign(card.gameObject, Mesh(fbx, "Blank voyage postcard"), paper);
            var pencil = new GameObject("Pencil");
            Undo.RegisterCreatedObjectUndo(pencil, "Blender desk");
            pencil.transform.SetParent(card, false);
            Assign(pencil, Mesh(fbx, "Pencil"), props);

            foreach (var (name, scale) in new[] { ("Scallop keepsake", 1f), ("Small scallop", .19f / .27f) })
            {
                var shell = Find(name);
                Strip(shell);
                var p = shell.position;
                shell.position = new Vector3(p.x, -.11f, p.z);
                shell.localScale = Vector3.one * scale;
                Assign(shell.gameObject, Mesh(fbx, "Ribbed scallop"), props);
            }

            var tail = all.FirstOrDefault(x => x != null && x.name == "Loose rope tail");
            if (tail != null) Undo.DestroyObjectImmediate(tail.gameObject);
            var mooring = Find("Coiled mooring rope");
            Strip(mooring);
            mooring.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            mooring.localScale = Vector3.one;
            Assign(mooring.gameObject, Mesh(fbx, "Coiled mooring rope"), rope);

            var coil = Find("Cotton coil");
            Strip(coil);
            coil.rotation = Quaternion.identity;
            coil.localScale = Vector3.one;
            Assign(coil.gameObject, Mesh(fbx, "Cotton coil"), rope);

            var pebbles = all.Where(x => x != null && x.name == "Beach pebble" && x.parent == null).OrderBy(x => x.position.x).ToArray();
            if (pebbles.Length != 5) throw new InvalidOperationException($"Expected 5 desk pebbles, found {pebbles.Length}.");
            for (int i = 0; i < pebbles.Length; i++)
            {
                Strip(pebbles[i]);
                pebbles[i].localScale = Vector3.one;
                Assign(pebbles[i].gameObject, Mesh(fbx, $"Beach pebble {i + 6}"), props);
            }
        }

        public static void Assign(GameObject go, Mesh mesh, Material material, ShadowCastingMode shadows = ShadowCastingMode.On)
        {
            var filter = go.GetComponent<MeshFilter>();
            if (filter == null) filter = Undo.AddComponent<MeshFilter>(go);
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer == null) renderer = Undo.AddComponent<MeshRenderer>(go);
            Undo.RecordObject(filter, "Blender mesh");
            Undo.RecordObject(renderer, "Blender material");
            filter.sharedMesh = mesh;
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = shadows;
            var collider = go.GetComponent<Collider>();
            if (collider != null) Undo.DestroyObjectImmediate(collider);
        }
    }
}
