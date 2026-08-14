using UnityEngine;
using UnityEditor;

public class LeopardHierarchyPrinter
{
    [MenuItem("Tools/Print Leopard Hierarchy")]
    public static void PrintHierarchy()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/kucher/Tank Leopard2/Prefabs/Leopard2.prefab");
        if (prefab == null) 
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/kucher/Tank Leopard2/Models/Leopard2.fbx");

        if (prefab != null)
        {
            Debug.Log("<color=yellow><b>Leopard Hierarchy:</b></color>");
            PrintRecursive(prefab.transform, "");
        }
        else
        {
            Debug.LogError("Leopard prefab/fbx not found!");
        }
    }

    private static void PrintRecursive(Transform t, string indent)
    {
        Debug.Log(indent + "- " + t.name);
        foreach (Transform child in t)
        {
            PrintRecursive(child, indent + "  ");
        }
    }
}
