using UnityEditor;
using UnityEngine;

public static class ListMicrophones
{
    // Unity reports device names as the OS exposes them, which rarely matches what the
    // Windows sound settings show. ChatdollMicrophone.DeviceName has to match one of these
    // strings exactly, so print them rather than guessing.
    [MenuItem("Tools/List Microphone Devices")]
    public static void List()
    {
        var devices = Microphone.devices;
        if (devices == null || devices.Length == 0)
        {
            Debug.LogWarning("ListMicrophones: Unity reports no capture devices.");
            return;
        }

        Debug.Log($"ListMicrophones: {devices.Length} device(s) found:");
        for (var i = 0; i < devices.Length; i++)
        {
            Microphone.GetDeviceCaps(devices[i], out var min, out var max);
            Debug.Log($"  [{i}] \"{devices[i]}\"   (min {min} Hz, max {max} Hz){(i == 0 ? "   <- Unity default, used when DeviceName is empty" : "")}");
        }
    }
}
