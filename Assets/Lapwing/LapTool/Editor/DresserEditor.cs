using UnityEngine;
using UnityEditor;

namespace LapTool
{
    [CustomEditor(typeof(LapTool.Dresser))]
    [CanEditMultipleObjects]    
    public class DresserEditor : Editor
    {
        SerializedProperty avatar;
        
        void OnEnable()
        {
            avatar = serializedObject.FindProperty("avatar");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(avatar);
            serializedObject.ApplyModifiedProperties();
            
            if (GUILayout.Button("Dress")){
                if (PrefabUtility.GetPrefabAssetType(Selection.activeGameObject) != PrefabAssetType.NotAPrefab)
                    PrefabUtility.UnpackPrefabInstance(Selection.activeGameObject, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
                (target as LapTool.Dresser).Dress();
            }
        }
    }
}

