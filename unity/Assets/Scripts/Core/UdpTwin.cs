using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;

//Live mode: receives JSON telemetry over UDP and drives the drone.
//Attach to DroneRoot. Disable FlightReplayer / FlightReplay / DroneController while this is on.
public class UdpTwin : MonoBehaviour {
    [Serializable]
    class Packet {
        public float t, alt, heading, pitch, roll, speed, battery;
        public double lat, lon;
        public int sats;
        public long ts_ms;   //phone clock (ms); same clock as frame/detection "ts". 0 = sender doesn't provide it
        public string mode, message;
    }

    struct Fix { public long ts; public Vector3 pos; }
    const int HistoryLength = 600; // ~60 s at 10 Hz

    public int port = 5005;
    public LineRenderer trail;
    public Text hudText;
    [Tooltip("How quickly the drone follows incoming data (higher = snappier)")]
    public float smoothing = 12f;
    [Tooltip("Seconds without packets before showing LINK LOST")]
    public float linkTimeout = 1.0f;

    UdpClient client; Thread thread; volatile bool running;
    readonly ConcurrentQueue<Packet> queue = new ConcurrentQueue<Packet>();
    Packet last; bool hasOrigin; double lat0, lon0;
    Vector3 targetPos; Quaternion targetRot; float lastRx = -999;
    int received;
    readonly System.Collections.Generic.List<Fix> history = new System.Collections.Generic.List<Fix>();

    //Drone position at phone time tsMs (interpolated). False if no ts_ms data or tsMs is out of range.
    public bool TryGetPositionAt(long tsMs, out Vector3 pos) {
        pos = transform.position;
        if (history.Count == 0 || tsMs < history[0].ts || tsMs > history[history.Count - 1].ts + 500) return false;
        int i = history.FindLastIndex(f => f.ts <= tsMs);
        if (i < 0) return false;
        if (i == history.Count - 1) { pos = history[i].pos; return true; }
        Fix a = history[i], b = history[i + 1];
        pos = Vector3.Lerp(a.pos, b.pos, Mathf.InverseLerp(a.ts, b.ts, tsMs));
        return true;
    }

    void OnEnable() {
        running = true;
        client = new UdpClient(port);
        thread = new Thread(Listen) { IsBackground = true };
        thread.Start();
        if (trail) { trail.positionCount = 0; trail.useWorldSpace = true; }
        hasOrigin = false;
        history.Clear();
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
                var p = JsonUtility.FromJson<Packet>(Encoding.UTF8.GetString(data));
                if (p != null) queue.Enqueue(p);
            } catch (SocketException) { /* closed */ }
            catch (ObjectDisposedException) { }
            catch (Exception e) { Debug.LogWarning("UDP parse: " + e.Message); }
        }
    }

    void Update() {
        while (queue.TryDequeue(out var p)) {
            if (!hasOrigin) {
                lat0 = p.lat; lon0 = p.lon; hasOrigin = true;
                transform.position = new Vector3(0, p.alt, 0);
            }
            float north = (float)((p.lat - lat0) * 111320.0);
            float east = (float)((p.lon - lon0) * 111320.0 * Math.Cos(lat0 * Math.PI / 180));
            targetPos = new Vector3(east, p.alt, north);
            targetRot = Quaternion.Euler(-p.pitch, p.heading, -p.roll);
            last = p; lastRx = Time.time; received++;
            if (p.ts_ms > 0) {
                history.Add(new Fix { ts = p.ts_ms, pos = targetPos });
                if (history.Count > HistoryLength) history.RemoveAt(0);
            }
        }
        if (last == null) return;

        float k = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, targetPos, k);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, k);

        if (trail) {
            int n = trail.positionCount;
            if (n == 0 || Vector3.Distance(trail.GetPosition(n - 1), transform.position) > 0.3f) {
                trail.positionCount = n + 1;
                trail.SetPosition(n, transform.position);
            }
        }

        bool live = Time.time - lastRx < linkTimeout;
        if (hudText) hudText.text =
            (live ? "LIVE" : "LINK LOST") + $"  pkts {received}\n" +
            $"ALT {last.alt:0.0} m\nSPD {last.speed:0.0} m/s\nHDG {last.heading:0}°\n" +
            $"BAT {(last.battery < 0 ? "--" : last.battery.ToString("0"))}%  SAT {last.sats}\nMODE {last.mode}\n{last.message}";
    }
}
