using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class DumpBlendShapes
{
    // Dumps every SkinnedMeshRenderer's blend shape names under a named root, so a new
    // avatar's shape vocabulary can be inspected before writing a mapping for it.
    [MenuItem("Tools/Dump Blend Shape Names (Lapwing)")]
    public static void DumpLapwing() => DumpRoot("Lapwing");

    [MenuItem("Tools/Dump Blend Shape Names (Selected)")]
    public static void DumpSelected()
    {
        if (Selection.activeGameObject == null)
        {
            Debug.LogError("DumpBlendShapes: select an avatar GameObject in the Hierarchy first.");
            return;
        }
        DumpRoot(Selection.activeGameObject.name, Selection.activeGameObject);
    }

    private static void DumpRoot(string rootName, GameObject known = null)
    {
        var root = known != null ? known : GameObject.Find(rootName);
        if (root == null)
        {
            Debug.LogError($"DumpBlendShapes: no GameObject named '{rootName}' in the scene.");
            return;
        }

        var lines = new List<string>();
        var total = 0;

        foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr.sharedMesh == null || smr.sharedMesh.blendShapeCount == 0) continue;

            lines.Add($"### {smr.name} ({smr.sharedMesh.blendShapeCount} shapes)");
            for (var i = 0; i < smr.sharedMesh.blendShapeCount; i++)
            {
                lines.Add($"{i}\t{smr.sharedMesh.GetBlendShapeName(i)}");
                total++;
            }
            lines.Add("");
        }

        if (total == 0)
        {
            Debug.LogError($"DumpBlendShapes: no blend shapes found under '{root.name}'.");
            return;
        }

        var outputPath = Path.Combine(Application.dataPath, $"blendshape_dump_{root.name}.txt");
        File.WriteAllLines(outputPath, lines);
        AssetDatabase.Refresh();

        Debug.Log($"DumpBlendShapes: dumped {total} shape names from '{root.name}' to {outputPath}");
    }
}
