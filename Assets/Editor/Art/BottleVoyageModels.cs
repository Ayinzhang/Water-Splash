using UnityEditor;
using UnityEngine;

namespace WaterSplash.Editor
{
    // Import settings for the Blender props exported by Tools/Blender/voyage_models.py.
    sealed class BottleVoyageModels : AssetPostprocessor
    {
        const string Folder = "Assets/WaterSplash/Art/BottleVoyage/Blender/";

        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(Folder)) return;
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
            if (!assetPath.StartsWith(Folder + "Textures/")) return;
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
}