using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Optional feature (button "OBJECTS ON MAP", off by default): puts the Task 3 detections (VideoDetections) on
// the ground of the 3D scene and on the 2D map. Each detection's box centre is cast as a ray from the camera
// (drone position + gimbal pitch/yaw from the log, intrinsics from the camera config) onto the flat ground.
// Markers appear as the replay passes the detections: per object a pin at the median of its hits so far,
// plus small dots for the individual hits (their spread shows how uncertain the position is).
// Attach to DroneRoot next to VideoDetections.
public class DetectionMapMarkers : MonoBehaviour {
    public FlightReplayer replayer;
    public VideoDetections detections;
    public FlightMap map;
    public GroundMap ground;
    [Tooltip("Camera intrinsics (JSON, see config/cameras), relative to the Unity project folder")]
    public string cameraConfig = "../config/cameras/phantom3pro_fc300x_1080p.json";
    public bool showOnStart = false;
    [Tooltip("Ignore hits farther than this from the drone on the ground (m)")]
    public float maxRange = 150f;
    [Tooltip("Keep at most one hit dot per object every this many seconds")]
    public float dotSpacing = 0.25f;
    public float pinHeight = 6f;

    [Serializable] class CameraConfig { public int[] image_size; public float[] focal_length_px; public float[] principal_point_px; }

    struct Dot { public float t; public Vector3 p; public GameObject go; }
    class Obj {
        public int id; public string label; public Color color;
        public readonly List<Dot> dots = new List<Dot>();  // sorted by time
        public GameObject pin; public TextMesh text; public int shown = -1; public Vector3 at;
    }

    readonly Dictionary<int, Obj> objs = new Dictionary<int, Obj>();
    CameraConfig cam;
    Transform root;
    bool on, built;
    double lat0, lon0;

    void Awake() {
        if (!replayer) replayer = GetComponent<FlightReplayer>();
        if (!detections) detections = GetComponent<VideoDetections>();
        if (!map) map = GetComponent<FlightMap>();
        if (!ground) ground = FindAnyObjectByType<GroundMap>();
    }

    void Start() {
        on = showOnStart;
        if (TwinUI.Instance) TwinUI.Instance.AddToggle("OBJECTS ON MAP", on, v => { on = v; Refresh(true); });
    }

    void Build() {
        built = true;
        string path = Path.GetFullPath(cameraConfig.Trim().Trim('"'));
        if (!File.Exists(path)) { Debug.LogWarning("DetectionMapMarkers: camera config not found: " + path); return; }
        cam = JsonUtility.FromJson<CameraConfig>(File.ReadAllText(path));
        var log = replayer.Log;
        lat0 = log[0].lat; lon0 = log[0].lon;
        float groundY = ground ? ground.height : 0f;

        root = new GameObject("DetectedObjects").transform;  // world space, not under the moving drone
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        int used = 0, skipped = 0;

        foreach (var h in detections.Hits) {
            if (!Locate(h, groundY, out var p)) { skipped++; continue; }
            if (!objs.TryGetValue(h.id, out var o)) {
                o = objs[h.id] = new Obj { id = h.id, label = h.label, color = VideoDetections.ColorFor(h.id) };
                var mat = new Material(shader) { color = o.color };
                o.pin = MakePin(o, mat, font);
            }
            if (o.dots.Count > 0 && h.t - o.dots[o.dots.Count - 1].t < dotSpacing) continue;
            var dot = Prim(PrimitiveType.Cylinder, "Hit", o.pin.GetComponentInChildren<Renderer>().sharedMaterial);
            dot.transform.position = p + Vector3.up * 0.05f;
            dot.transform.localScale = new Vector3(0.5f, 0.02f, 0.5f);
            o.dots.Add(new Dot { t = h.t, p = p, go = dot });
            used++;
        }
        Debug.Log($"DetectionMapMarkers: {objs.Count} object(s), {used} hit dot(s), {skipped} detection(s) not on the ground");
    }

    // Ray from the camera through the box centre onto the ground plane.
    bool Locate(VideoDetections.Hit h, float groundY, out Vector3 p) {
        p = default;
        var log = replayer.Log;
        int i = replayer.IndexAt(h.t), k = Mathf.Min(i + 1, log.Count - 1);
        float f = Mathf.InverseLerp(log[i].t, log[k].t, h.t);
        Sample a = log[i], b = log[k];
        if (float.IsNaN(a.gimbalPitch) || float.IsNaN(a.gimbalHeading)) return false;
        Vector3 camPos = Vector3.Lerp(a.pos, b.pos, f);
        float pitch = Mathf.Lerp(a.gimbalPitch, b.gimbalPitch, f), yaw = Mathf.LerpAngle(a.gimbalHeading, b.gimbalHeading, f);

        float sx = (float)h.w / cam.image_size[0], sy = (float)h.h / cam.image_size[1];
        float fx = cam.focal_length_px[0] * sx, fy = cam.focal_length_px[1] * sy;
        float cx = cam.principal_point_px[0] * sx, cy = cam.principal_point_px[1] * sy;
        var local = new Vector3((h.u * h.w - cx) / fx, -(h.v * h.h - cy) / fy, 1f);  // x right, y up, z forward
        Vector3 dir = Quaternion.Euler(-pitch, yaw, 0f) * local;                  // pitch -90 = straight down
        if (dir.y > -0.05f) return false;                                         // at or above the horizon
        float s = (groundY - camPos.y) / dir.y;
        if (s <= 0) return false;
        p = camPos + dir * s;
        return new Vector2(p.x - camPos.x, p.z - camPos.z).magnitude <= maxRange;
    }

    GameObject MakePin(Obj o, Material mat, Font font) {
        var pin = new GameObject("Object_" + o.label);
        pin.transform.SetParent(root, false);
        var stem = Prim(PrimitiveType.Cylinder, "Stem", mat);
        stem.transform.SetParent(pin.transform, false);
        stem.transform.localPosition = Vector3.up * pinHeight / 2; stem.transform.localScale = new Vector3(0.2f, pinHeight / 2, 0.2f);
        var head = Prim(PrimitiveType.Sphere, "Head", mat);
        head.transform.SetParent(pin.transform, false);
        head.transform.localPosition = Vector3.up * pinHeight; head.transform.localScale = Vector3.one * 1.2f;
        var lbl = new GameObject("Label");
        lbl.transform.SetParent(pin.transform, false);
        lbl.transform.localPosition = Vector3.up * (pinHeight + 2.5f);
        o.text = lbl.AddComponent<TextMesh>();
        o.text.font = font; o.text.fontSize = 48; o.text.characterSize = 0.25f;  // ~2.5 m tall, readable from the top camera
        o.text.anchor = TextAnchor.MiddleCenter; o.text.color = o.color;
        lbl.GetComponent<MeshRenderer>().material = font.material;
        return pin;
    }

    GameObject Prim(PrimitiveType type, string name, Material mat) {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        Destroy(go.GetComponent<Collider>());
        go.GetComponent<Renderer>().sharedMaterial = mat;
        go.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        go.transform.SetParent(root, false);
        return go;
    }

    void LateUpdate() {
        if (!built) {
            if (!replayer || replayer.Log == null || replayer.Log.Count < 2 || !detections || !detections.Loaded) return;
            Build();
            Refresh(true);
        }
        Refresh(false);
        var c = Camera.main;
        if (!c || !c.isActiveAndEnabled) c = Camera.allCamerasCount > 0 ? Camera.allCameras[0] : null;  // CameraSwitcher
        if (on && c) foreach (var o in objs.Values) if (o.text) o.text.transform.rotation = c.transform.rotation;
    }

    // Show what the replay has passed: hit dots up to now and each object's pin at the median of those hits.
    void Refresh(bool force) {
        if (!root) return;
        bool show = on && replayer.enabled;  // replay only
        root.gameObject.SetActive(show);
        bool changed = force;
        float now = replayer.CurrentTime;
        foreach (var o in objs.Values) {
            int n = 0;
            while (n < o.dots.Count && o.dots[n].t <= now) n++;
            if (n == o.shown) continue;
            changed = true; o.shown = n;
            for (int i = 0; i < o.dots.Count; i++) o.dots[i].go.SetActive(i < n);
            o.pin.SetActive(n > 0);
            if (n == 0) continue;
            var xs = new List<float>(n); var zs = new List<float>(n);
            for (int i = 0; i < n; i++) { xs.Add(o.dots[i].p.x); zs.Add(o.dots[i].p.z); }
            xs.Sort(); zs.Sort();
            o.at = new Vector3(xs[n / 2], o.dots[0].p.y, zs[n / 2]);
            o.pin.transform.position = o.at;
            o.text.text = $"{o.label} ({n})";
        }
        if (!changed || !map) return;

        var marks = new List<(double, double, Color, float)>();
        if (show)
            foreach (var o in objs.Values) {
                for (int i = 0; i < o.shown; i++) {
                    var d = FlightLog.FromLocal(new Vector2(o.dots[i].p.x, o.dots[i].p.z), lat0, lon0);
                    marks.Add((d.lat, d.lon, new Color(o.color.r, o.color.g, o.color.b, 0.5f), 5f));
                }
                if (o.shown > 0) {
                    var m = FlightLog.FromLocal(new Vector2(o.at.x, o.at.z), lat0, lon0);
                    marks.Add((m.lat, m.lon, o.color, 16f));
                }
            }
        map.SetMarks(marks);
    }
}
