using UnityEngine;

public class CameraSwitcher : MonoBehaviour
{
    public Camera[] cameras;
    public int mouseButton = 0; // 0 = left click, 1 = right click
    int index;

    void Start() { Apply(); }

    void Update()
    {
        if (cameras.Length == 0) return;
        if (Input.GetMouseButtonDown(mouseButton) &&
            !(UnityEngine.EventSystems.EventSystem.current != null &&
              UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject())) // clicks on the UI must not switch cameras
        {
            index = (index + 1) % cameras.Length;
            Apply();
        }
    }

    void Apply()
    {
        for (int i = 0; i < cameras.Length; i++)
        {
            if (cameras[i] == null) continue;
            bool on = (i == index);
            cameras[i].enabled = on;
            var al = cameras[i].GetComponent<AudioListener>();
            if (al != null) al.enabled = on;
        }
    }
}