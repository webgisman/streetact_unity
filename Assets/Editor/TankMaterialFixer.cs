using UnityEngine;
using UnityEditor;

public class TankMaterialFixer
{
    [MenuItem("Tools/Réparer les Couleurs du Tank Leopard")]
    public static void FixMaterials()
    {
        string bodyMatPath = "Assets/kucher/Tank Leopard2/Materials/TankBodyMaterial.mat";
        string leftTrackMatPath = "Assets/kucher/Tank Leopard2/Materials/LeftTrackMaterial.mat";
        string rightTrackMatPath = "Assets/kucher/Tank Leopard2/Materials/RightTrackMaterial.mat";

        Material bodyMat = AssetDatabase.LoadAssetAtPath<Material>(bodyMatPath);
        Material leftMat = AssetDatabase.LoadAssetAtPath<Material>(leftTrackMatPath);
        Material rightMat = AssetDatabase.LoadAssetAtPath<Material>(rightTrackMatPath);

        Texture2D bodyTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/kucher/Tank Leopard2/Textures/Tank Body Textures/TankBodyDiffuseMap.png");
        Texture2D trackTex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/kucher/Tank Leopard2/Textures/Tank Track Textures/TankTrackDiffuseMap.png");

        if (bodyMat != null && bodyTex != null) { bodyMat.mainTexture = bodyTex; EditorUtility.SetDirty(bodyMat); }
        if (leftMat != null && trackTex != null) { leftMat.mainTexture = trackTex; EditorUtility.SetDirty(leftMat); }
        if (rightMat != null && trackTex != null) { rightMat.mainTexture = trackTex; EditorUtility.SetDirty(rightMat); }
        AssetDatabase.SaveAssets();

        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/kucher/Tank Leopard2/Prefabs/Leopard2.prefab");
        if (prefab != null)
        {
            string assetPath = AssetDatabase.GetAssetPath(prefab);
            using (var editingScope = new PrefabUtility.EditPrefabContentsScope(assetPath))
            {
                GameObject root = editingScope.prefabContentsRoot;
                foreach (MeshRenderer mr in root.GetComponentsInChildren<MeshRenderer>())
                {
                    // Essayer de deviner quel matériel appliquer en fonction du nom du mesh
                    string meshName = mr.gameObject.name.ToLower();
                    if (meshName.Contains("track") && meshName.Contains("left"))
                    {
                        mr.sharedMaterial = leftMat;
                    }
                    else if (meshName.Contains("track") && meshName.Contains("right"))
                    {
                        mr.sharedMaterial = rightMat;
                    }
                    else
                    {
                        mr.sharedMaterial = bodyMat; // Tout le reste est le corps ou la tourelle
                    }
                }
            }
        }

        // --- NOUVEAU : Réparer AUSSI les tanks déjà présents dans la scène ---
        int tanksFixedInScene = 0;
        foreach (MeshRenderer mr in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude))
        {
            if (mr.transform.root.name.ToLower().Contains("leopard"))
            {
                string meshName = mr.gameObject.name.ToLower();
                if (meshName.Contains("track") && meshName.Contains("left")) mr.sharedMaterial = leftMat;
                else if (meshName.Contains("track") && meshName.Contains("right")) mr.sharedMaterial = rightMat;
                else mr.sharedMaterial = bodyMat;
                
                tanksFixedInScene++;
            }
        }

        if (tanksFixedInScene > 0)
        {
            Debug.Log($"<color=green><b>[Couleurs Réparées] Les textures ont été ré-appliquées au Prefab ET à {tanksFixedInScene} Mesh(s) dans la scène !</b></color>");
        }
        else
        {
            Debug.Log("<color=green><b>[Couleurs Réparées] Les textures ont été ré-appliquées au Prefab Tank !</b></color>");
        }
    }
}
