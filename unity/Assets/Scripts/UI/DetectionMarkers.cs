using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;

[Serializable] public class Det { public float t; public string label; public float confidence; }
[Serializable] class DetList { public Det[] detections; }

// Places detection markers along the flight path from a JSON file.
// Click a marker to jump the replay there; a banner pops up when playback passes a detection.
public class DetectionMarkers : MonoBehaviour {
    public FlightReplayer replayer;
    [Tooltip("Empty = no markers. Example with fake data: Assets/Data/detections.json (fits test_flight.csv)")]
    public string jsonPath = "";
    public float markerSize = 4f;

    class Item { public Det d; public GameObject go, label; }
    readonly List<Item> items = new List<Item>();
    bool built; float prevTime; Camera cam; Font font;

    void Awake() { if (!replayer) replayer = GetComponent<FlightReplayer>(); }

    void Update() {
        if (!built) { if (replayer && replayer.Log != null) Build(); return; }
        if (cam == null || !cam.isActiveAndEnabled) { cam = Camera.main; if (cam == null && Camera.allCamerasCount > 0) cam = Camera.allCameras[0]; }

        bool show = replayer.enabled;
        float now = replayer.CurrentTime;
        foreach (var it in items) {
            it.go.SetActive(show);
            if (!show) continue;
            float near = Mathf.Clamp01(1f - Mathf.Abs(now - it.d.t) / 1.5f);   // pulse when drone is close in time
            it.go.transform.localScale = Vector3.one * markerSize * (1f + 0.8f * near);
            if (cam) it.label.transform.rotation = cam.transform.rotation;
            if (replayer.playing && prevTime <= it.d.t && it.d.t < now && now - prevTime < 2f && TwinUI.Instance)
                TwinUI.Instance.ShowBanner($"DETECTION: {it.d.label}  {it.d.confidence * 100:0}%   t={it.d.t:0.0}s");
        }
        prevTime = now;
        if (show) HandleClick();
    }

    void HandleClick() {
        bool clicked = false; Vector2 pos = Vector2.zero;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        var m = UnityEngine.InputSystem.Mouse.current;
        if (m != null && m.leftButton.wasPressedThisFrame) { clicked = true; pos = m.position.ReadValue(); }
#else
        if (Input.GetMouseButtonDown(0)) { clicked = true; pos = Input.mousePosition; }
#endif
        if (!clicked || cam == null) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
        if (!Physics.Raycast(cam.ScreenPointToRay(pos), out var hit, 10000f)) return;
        foreach (var it in items)
            if (it.go == hit.collider.gameObject) {
                replayer.Seek(Mathf.Max(0, it.d.t - 2f));
                if (TwinUI.Instance)
                    TwinUI.Instance.ShowBanner($"{it.d.label}  conf {it.d.confidence:0.00}  at {it.d.t:0.0}s", 4f);
            }
    }

    void Build() {
        built = true;
        string path = jsonPath.Trim().Trim('"');
        if (path == "") return;  // no detections for this flight
        if (!File.Exists(path)) { Debug.LogWarning("Detections file not found: " + path); return; }
        var list = JsonUtility.FromJson<DetList>(File.ReadAllText(path));
        if (list == null || list.detections == null) return;
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Sprites/Default");

        foreach (var d in list.detections) {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Det_" + d.label;
            go.transform.position = PosAt(d.t) + Vector3.up * replayer.groundOffset;
            go.transform.localScale = Vector3.one * markerSize;
            var mat = new Material(shader) { color = ColorFor(d.label) };
            go.GetComponent<Renderer>().material = mat;

            var lbl = new GameObject("lbl");
            lbl.transform.SetParent(go.transform, false);
            lbl.transform.localPosition = new Vector3(0, 1.4f, 0);
            var tm = lbl.AddComponent<TextMesh>();
            tm.font = font; tm.text = $"{d.label} {d.confidence:0.00}";
            tm.fontSize = 48; tm.characterSize = 0.12f; tm.anchor = TextAnchor.MiddleCenter;
            lbl.GetComponent<MeshRenderer>().material = font.material;
            items.Add(new Item { d = d, go = go, label = lbl });
        }
        Debug.Log("Detection markers: " + items.Count);
    }

    Vector3 PosAt(float t) {
        var log = replayer.Log;
        int lo = 0, hi = log.Count - 1;
        while (hi - lo > 1) { int m = (lo + hi) / 2; if (log[m].t <= t) lo = m; else hi = m; }
        float k = Mathf.InverseLerp(log[lo].t, log[hi].t, t);
        return Vector3.Lerp(log[lo].pos, log[hi].pos, k);
    }

    public static Color ColorFor(string label) {
        switch (label) {
            case "reference_object": return new Color(1f, 0.55f, 0.1f);
            case "person": return new Color(0.2f, 0.9f, 1f);
            case "vehicle": return new Color(1f, 0.95f, 0.2f);
            default: return Color.white;
        }
    }
}
