using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace WaterSplash.Editor
{
    internal static class SceneAssets
    {
        internal static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            if (slash < 0) throw new ArgumentException("Use an Assets-relative folder path.", nameof(path));
            string parent = path.Substring(0, slash);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
        }

        internal static Shader RequiredShader(string name)
        {
            var shader = Shader.Find(name);
            if (shader == null) throw new InvalidOperationException("Missing shader: " + name);
            return shader;
        }

        internal static void UpdateFeatureMap(UniversalRendererData renderer)
        {
            var serialized = new SerializedObject(renderer);
            var map = serialized.FindProperty("m_RendererFeatureMap");
            map.arraySize = renderer.rendererFeatures.Count;
            for (int i = 0; i < renderer.rendererFeatures.Count; i++)
            {
                var feature = renderer.rendererFeatures[i];
                if (feature == null) throw new InvalidOperationException("The renderer contains a missing feature.");
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out string guid, out long id);
                map.GetArrayElementAtIndex(i).longValue = id;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            renderer.SetDirty();
            EditorUtility.SetDirty(renderer);
        }

        internal static GameObject MeshObject(string name, Transform parent, Mesh mesh, Material material)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return go;
        }

        internal static GameObject Primitive(string name, PrimitiveType type, Transform parent,
            Vector3 position, Vector3 scale, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = material;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        internal static void Segment(string name, Transform parent, Vector3 start, Vector3 end,
            float radius, Material material)
        {
            var go = Primitive(name, PrimitiveType.Cylinder, parent, (start + end) * .5f,
                new Vector3(radius * 2, (end - start).magnitude * .5f, radius * 2), material);
            go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, end - start);
        }
    }
}
