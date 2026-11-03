using UnityEngine;

public class GodCamera : MonoBehaviour
{
    public Transform target;
    public float height = 40f;
    public float minHeight = 5f;
    public float maxHeight = 300f;
    public float zoomSpeed = 5f;

    void Start()
    {
        if (target == null)
        {
            var replay = FindAnyObjectByType<FlightReplayer>(); // DroneRoot; UdpTwin lives on the same object
            if (replay != null) target = replay.transform;
        }
        transform.rotation = Quaternion.Euler(90f, 0f, 0f); // look down, top of the screen = north (+Z)
    }

    void LateUpdate()
    {
        if (target == null) return;
        height = Mathf.Clamp(height - Input.mouseScrollDelta.y * zoomSpeed, minHeight, maxHeight);
        Vector3 p = target.position;
        transform.position = new Vector3(p.x, p.y + height, p.z);
    }
}