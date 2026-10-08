#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace LapTool
{
    [ExecuteInEditMode]
    public class Dresser : MonoBehaviour
    {
        public GameObject avatar = null;
        private Transform avatarRoot = null;

        void OnEnable()
        {
            if (null != this.gameObject.transform.parent)
                avatar = this.gameObject.transform.parent.gameObject;
        }

        public void Dress()
        {
            SetParentAvatarIfNeeded();
            Transform dressRoot = this.gameObject.transform.Find("Armature")?.transform;
            if (null == dressRoot || null == avatar)
                return;
            avatarRoot = avatar?.transform.Find("Armature")?.transform;
            
            while (dressRoot.childCount > 0)
                AttachBoneRecursive(dressRoot);

            DestroyImmediate(this);
        }

        private void SetParentAvatarIfNeeded()
        {
            if (null == this.gameObject.transform.parent)
                this.gameObject.transform.SetParent(avatar?.transform, false);
        }

        private void AttachBoneRecursive(Transform bone)
        {
            if (null == bone)
                return;

            int childCount = 0;
            foreach (Transform childBone in bone)
            {
                Transform sameNameBone = FindSameNameBone(avatarRoot, childBone.name);
                if(null != sameNameBone)
                    childCount++;
                AttachBoneRecursive(childBone);
            }

            if (childCount == 0)
                AttachBone(bone);
        }

        private void AttachBone(Transform dressBone)
        {
            Transform targetBone = FindSameNameBone(avatarRoot, dressBone.name);
            if (null == targetBone)
            {
                return;
            } else {
                dressBone.SetParent(targetBone, false);
                dressBone.localPosition = Vector3.zero;
                dressBone.localRotation = Quaternion.identity;
            }
        }

        public Transform FindSameNameBone(Transform parent, string boneName)
        {
            if (PrefabUtility.IsAddedGameObjectOverride(parent.gameObject))
                return null;
            foreach (Transform child in parent)
            {
                if (child.name == boneName)
                {
                    return child;
                }
                var result = FindSameNameBone(child, boneName);
                if (result != null)
                    return result;
            }
            return null;
        }    
    }
}

#endif