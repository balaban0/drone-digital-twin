using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

//Replays the DJI flight CSV (Airdata or DJI flight record *-aircraft.csv, see FlightLog).
// Attach to DroneRoot. TwinUI controls it (play, speed, seek).
public class FlightReplayer : MonoBehaviour {
    [Tooltip("Absolute, or relative to the Unity project folder (e.g. ../data/flight_logs/x.csv)")]
    public string csvPath = "../data/flight_logs/flight-aircraft.csv";
    public LineRenderer trail;
    public Text hudText;
    [Range(0.25f, 16f)] public float speedMultiplier = 1f;
    public bool playing = true;
    public bool loop = true;
    [Tooltip("Lifts the whole flight so the drone is not inside the ground")]
    public float groundOffset = 1f;

    List<Sample> log; float time; int drawn;

    public List<Sample> Log => log;
    public float CurrentTime => time;
    public float Duration => (log != null && log.Count > 0) ? log[log.Count - 1].t : 0f;
    public void Seek(float t) { time = Mathf.Clamp(t, 0f, Duration); }
    public void SetSpeed(float s) { speedMultiplier = s; }
    public void TogglePlay() { playing = !playing; }

    // Per-flight data folder next to flight_logs/: <data>/<kind>/<log name without -aircraft>,
    // e.g. data/videos/DJIFlightRecord_2026-11-02_[14-22-12].
    public string FlightDataFolder(string kind) {
        string csv = Path.GetFullPath(csvPath.Trim().Trim('"'));
        string name = Path.GetFileNameWithoutExtension(csv).Replace("-aircraft", "");
        return Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(csv)), kind, name);
    }

    // Index of the last sample at or before t (log must be loaded).
    public int IndexAt(float t) {
        int lo = 0, hi = log.Count - 1;
        while (hi - lo > 1) { int m = (lo + hi) / 2; if (log[m].t <= t) lo = m; else hi = m; }
        return lo;
    }

    void OnEnable() {
        drawn = 0;
        if (trail) { trail.positionCount = 0; trail.useWorldSpace = true; }
    }

    void Start() {
        try { log = FlightLog.Load(csvPath.Trim().Trim('"')); }
        catch (System.Exception e) { Debug.LogError("CSV load failed: " + e.Message); }
    }

    //Trail grows only as the drone passes samples; shrinks when seeking back / looping.
    void UpdateTrail(int lo, Vector3 current) {
        if (!trail) return;
        Vector3 off = Vector3.up * groundOffset;
        if (lo + 1 < drawn) drawn = lo + 1;
        while (drawn <= lo) {
            trail.positionCount = drawn + 1;
            trail.SetPosition(drawn, log[drawn].pos + off);
            drawn++;
        }
        trail.positionCount = drawn + 1;
        trail.SetPosition(drawn, current);
    }

    void Update() {
        if (log == null || log.Count < 2) return;
        float end = Duration;
        if (playing) time += Time.deltaTime * speedMultiplier;
        if (time > end) {
            if (loop) time = 0; else { time = end; playing = false; }
        }

        int lo = IndexAt(time), hi = Mathf.Min(lo + 1, log.Count - 1);
        Sample a = log[lo], b = log[hi];
        float k = Mathf.InverseLerp(a.t, b.t, time);

        transform.position = Vector3.Lerp(a.pos, b.pos, k) + Vector3.up * groundOffset;
        UpdateTrail(lo, transform.position);

        float yaw = Mathf.LerpAngle(a.heading, b.heading, k);
        float pitch = Mathf.Lerp(a.pitch, b.pitch, k);
        float roll = Mathf.Lerp(a.roll, b.roll, k);
        transform.rotation = Quaternion.Euler(-pitch, yaw, -roll);

        if (hudText) hudText.text =
            $"REPLAY  {speedMultiplier}x\n" +
            $"ALT {a.pos.y:0.0} m\nSPD {Mathf.Lerp(a.speed, b.speed, k):0.0} m/s\n" +
            $"HDG {yaw:0}°\nBAT {a.battery:0}%  SAT {a.sats}\nMODE {a.mode}\n{a.message}";
    }
}
