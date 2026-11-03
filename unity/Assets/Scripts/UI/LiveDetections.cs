using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// Live mode: receives detection results from python/live_detect_track.py over UDP (default 5007)
// and drops a marker where the drone was when that frame was captured (matched by phone timestamp).
// Attach to DroneRoot next to UdpTwin. Markers are only visible while UdpTwin is enabled (live mode).
public class LiveDetections : MonoBehaviour {
    [Serializable] class DetObj { public int id; public string label; public float score; public float[] bbox; }
    [Serializable] class DetPacket { public long frame_id, ts; public int w, h; public DetObj[] objects; }

    public UdpTwin udp;
    public int port = 5007;
    public float markerSize = 4f;
    [Tooltip("A new marker is created only if the drone moved this far (m) since the last marker of the same label")]
    public float minSpacing = 3f;

    class Item { public string label; public float score; public GameObject go, labelGo; public TextMesh text; }
    readonly List<Item> items = new List<Item>();
    readonly ConcurrentQueue<DetPacket> queue = new ConcurrentQueue<DetPacket>();
    readonly HashSet<string> seenLastPacket = new HashSet<string>();
    UdpClient client; Thread thread; volatile bool running;
    Camera cam; Font font; Shader shader;

    void Awake() { if (!udp) udp = GetComponent<UdpTwin>(); }

    void OnEnable() {
        running = true;
        client = new UdpClient(port);
        thread = new Thread(Listen) { IsBackground = true };
        thread.Start();
    }

    void OnDisable() {
        running = false;
        client?.Close();
        thread?.Join(200);
    }

    void Listen() {
        var ep = new IPEndPoint(IPAddress.Any, 0);
        while (running) {
            try {
                var data = client.Receive(ref ep);
                var p = JsonUtility.FromJson<DetPacket>(Encoding.UTF8.GetString(data));
                if (p != null) queue.Enqueue(p);
            } catch (SocketException) { /* closed */ }
            catch (ObjectDisposedException) { }
            catch (Exception e) { Debug.LogWarning("Detection UDP parse: " + e.Message); }
        }
    }

    void Update() {
        bool live = udp && udp.enabled;
        while (queue.TryDequeue(out var p)) if (live) Handle(p);

        if (cam == null || !cam.isActiveAndEnabled) { cam = Camera.main; if (cam == null && Camera.allCamerasCount > 0) cam = Camera.allCameras[0]; }
        foreach (var it in items) {
            it.go.SetActive(live);
            if (live && cam) it.labelGo.transform.rotation = cam.transform.rotation;
        }
    }

    void Handle(DetPacket p) {
        var seenNow = new HashSet<string>();
        if (p.objects != null)
            foreach (var o in p.objects) {
                if (string.IsNullOrEmpty(o.label)) continue;
                seenNow.Add(o.label);
                // Without ts_ms in telemetry, fall back to where the drone is right now
                if (!udp.TryGetPositionAt(p.ts, out var pos)) pos = udp.transform.position;

                var lastOfLabel = items.FindLast(i => i.label == o.label);
                if (lastOfLabel != null && Vector3.Distance(lastOfLabel.go.transform.position, pos) < minSpacing) {
                    if (o.score > lastOfLabel.score) { lastOfLabel.score = o.score; lastOfLabel.text.text = $"{o.label} {o.score:0.00}"; }
                } else {
                    items.Add(MakeMarker(o.label, o.score, pos));
                }
                // Banner only when the object (re)appears, not on every tracked frame
                if (!seenLastPacket.Contains(o.label) && TwinUI.Instance)
                    TwinUI.Instance.ShowBanner($"DETECTION: {o.label}  {o.score * 100:0}%");
            }
        seenLastPacket.Clear();
        seenLastPacket.UnionWith(seenNow);
    }

    Item MakeMarker(string label, float score, Vector3 pos) {
        if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (shader == null) { shader = Shader.Find("Universal Render Pipeline/Unlit"); if (shader == null) shader = Shader.Find("Sprites/Default"); }

        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "LiveDet_" + label;
        go.transform.position = pos;
        go.transform.localScale = Vector3.one * markerSize;
        go.GetComponent<Renderer>().material = new Material(shader) { color = DetectionMarkers.ColorFor(label) };

        var lbl = new GameObject("lbl");
        lbl.transform.SetParent(go.transform, false);
        lbl.transform.localPosition = new Vector3(0, 1.4f, 0);
        var tm = lbl.AddComponent<TextMesh>();
        tm.font = font; tm.text = $"{label} {score:0.00}";
        tm.fontSize = 48; tm.characterSize = 0.12f; tm.anchor = TextAnchor.MiddleCenter;
        lbl.GetComponent<MeshRenderer>().material = font.material;
        return new Item { label = label, score = score, go = go, labelGo = lbl, text = tm };
    }
}
