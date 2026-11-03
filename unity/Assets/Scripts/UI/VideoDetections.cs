using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

// Task 3 results on the replay: boxes over the CAMERA video in sync with the frame on screen, each object's
// detection runs marked on the timeline, and a banner when an object comes into view.
// Reads the detections_<clip>.json files written by the offline detector (live packet format, ts = ms
// from the start of that clip). Attach to DroneRoot next to FlightReplayer, FlightVideo and TwinUI.
public class VideoDetections : MonoBehaviour {
    public FlightReplayer replayer;
    public FlightVideo video;
    [Tooltip("Folder with detections_<clip>.json. Empty = ../detections/<log name> next to the flight_logs folder")]
    public string detectionsFolder = "";
    [Tooltip("Detections less than this far apart (s) count as one run (timeline + banner)")]
    public float mergeGap = 1f;

    [Serializable] class Obj { public int id; public string label; public float score; public float[] bbox; }
    [Serializable] class Packet { public int frame_id; public long ts; public Obj[] objects; }
    [Serializable] class DetFile { public string video; public float fps; public int w, h; public Packet[] packets; }

    struct Run { public int id; public string label; public float start, end, score; }

    // One detection: replay time, box centre (normalized 0..1 from the top-left) and the clip's frame size.
    public struct Hit { public int id; public string label; public float t, u, v, score; public int w, h; }
    public IReadOnlyList<Hit> Hits => hits;  // sorted by time
    public bool Loaded => ready;
    readonly List<Hit> hits = new List<Hit>();

    // Same colours as the offline detector's preview video, by object id
    static readonly Color[] Palette = {
        new Color(0f, 1f, 0f), new Color(1f, 0.78f, 0f), new Color(0f, 0.47f, 1f), new Color(1f, 0f, 1f),
        new Color(1f, 1f, 0f), new Color(1f, 0.31f, 0.31f), new Color(0f, 1f, 1f) };
    public static Color ColorFor(int id) => Palette[((id % Palette.Length) + Palette.Length) % Palette.Length];

    readonly Dictionary<string, DetFile> byClip = new Dictionary<string, DetFile>(StringComparer.OrdinalIgnoreCase);
    readonly List<Run> runs = new List<Run>();
    readonly List<Text> labels = new List<Text>();
    BoxOverlay overlay;
    bool ready; float prevTime;

    void Awake() {
        if (!replayer) replayer = GetComponent<FlightReplayer>();
        if (!video) video = GetComponent<FlightVideo>();
    }

    void Init() {
        ready = true;
        string folder = detectionsFolder.Trim().Trim('"');
        folder = Path.GetFullPath(folder == "" ? replayer.FlightDataFolder("detections") : folder);
        if (!Directory.Exists(folder)) { Debug.Log("VideoDetections: no detections folder " + folder); return; }

        foreach (var path in Directory.GetFiles(folder, "detections_*.json")) {
            var f = JsonUtility.FromJson<DetFile>(File.ReadAllText(path));
            if (f?.packets == null || string.IsNullOrEmpty(f.video)) continue;
            if (!video.TryClipStart(f.video, out float clipStart)) {
                Debug.LogWarning($"VideoDetections: {Path.GetFileName(path)} is for {f.video}, which is not matched to a recording segment");
                continue;
            }
            byClip[f.video] = f;
            BuildRuns(f, clipStart);
        }
        runs.Sort((a, b) => a.start.CompareTo(b.start));
        hits.Sort((a, b) => a.t.CompareTo(b.t));

        var marks = new List<(float, float, Color, string)>();
        foreach (var r in runs) marks.Add((r.start, r.end, ColorFor(r.id), r.label));
        TwinUI.Instance.SetTimelineMarks(marks, replayer.Duration);
        Debug.Log($"VideoDetections: {byClip.Count} clip(s), {runs.Count} detection run(s) from {folder}");
    }

    // Per object: detections closer than mergeGap form one run, in replay (log) time.
    void BuildRuns(DetFile f, float clipStart) {
        var open = new Dictionary<int, Run>();
        foreach (var p in f.packets) {
            float t = clipStart + p.ts / 1000f;
            foreach (var o in p.objects ?? Array.Empty<Obj>()) {
                if (o.bbox != null && o.bbox.Length >= 4)
                    hits.Add(new Hit { id = o.id, label = o.label, t = t, score = o.score, w = f.w, h = f.h,
                                       u = o.bbox[0] + o.bbox[2] / 2, v = o.bbox[1] + o.bbox[3] / 2 });
                if (open.TryGetValue(o.id, out var r) && t - r.end <= mergeGap) {
                    r.end = t; r.score = Mathf.Max(r.score, o.score); open[o.id] = r;
                } else {
                    if (open.ContainsKey(o.id)) runs.Add(open[o.id]);
                    open[o.id] = new Run { id = o.id, label = o.label, start = t, end = t, score = o.score };
                }
            }
        }
        runs.AddRange(open.Values);
    }

    void Update() {
        if (!replayer || replayer.Log == null || replayer.Log.Count < 2 || !video || !video.Ready) return;
        if (!ready) Init();
        if (byClip.Count == 0) return;

        Obj[] shown = null;
        if (replayer.enabled && video.Showing && video.CurrentClip != null &&
            byClip.TryGetValue(video.CurrentClip, out var f) && video.PlaybackTime >= 0)
            shown = PacketAt(f, video.PlaybackTime * 1000.0);
        Draw(shown);

        // Banner when the replay passes the start of a run
        float now = replayer.CurrentTime;
        if (replayer.enabled && replayer.playing && now > prevTime && now - prevTime < 2f && TwinUI.Instance)
            foreach (var r in runs)
                if (prevTime < r.start && r.start <= now)
                    TwinUI.Instance.ShowBanner($"REFERENCE OBJECT: {r.label}  {r.score:0.00}");
        prevTime = now;
    }

    // Objects of the packet for the frame at ms (none if there is no packet within 1.5 frames).
    static Obj[] PacketAt(DetFile f, double ms) {
        var ps = f.packets; int lo = 0, hi = ps.Length - 1;
        if (hi < 0 || ms < ps[0].ts) return null;
        while (lo < hi) { int m = (lo + hi + 1) / 2; if (ps[m].ts <= ms) lo = m; else hi = m - 1; }
        double frame = f.fps > 0 ? 1000.0 / f.fps : 42;
        return ms - ps[lo].ts <= 1.5 * frame ? ps[lo].objects : null;
    }

    void Draw(Obj[] objs) {
        if (overlay == null) {
            if (video.ImageRect == null) return;
            var go = new GameObject("Detections", typeof(RectTransform), typeof(CanvasRenderer), typeof(BoxOverlay));
            go.transform.SetParent(video.ImageRect, false);
            overlay = go.GetComponent<BoxOverlay>(); overlay.raycastTarget = false;
            TwinUI.Stretch(overlay.rectTransform, 0);
        }
        overlay.boxes.Clear();
        int n = objs?.Length ?? 0;
        for (int i = 0; i < n; i++) {
            var o = objs[i];
            if (o.bbox == null || o.bbox.Length < 4) continue;
            var rect = new Rect(o.bbox[0], o.bbox[1], o.bbox[2], o.bbox[3]);
            overlay.boxes.Add(new BoxOverlay.Box { r = rect, color = ColorFor(o.id) });
            var lbl = LabelAt(i);
            var local = overlay.ToLocal(rect);
            lbl.rectTransform.anchoredPosition = new Vector2(local.xMin, local.yMax);
            lbl.text = $"{o.id} {o.label} {o.score:0.00}"; lbl.color = ColorFor(o.id);
            lbl.gameObject.SetActive(true);
        }
        for (int i = n; i < labels.Count; i++) labels[i].gameObject.SetActive(false);
        overlay.SetVerticesDirty();
    }

    Text LabelAt(int i) {
        while (labels.Count <= i) {
            var t = TwinUI.Instance.Label("DetLabel", overlay.transform, "", 18, TextAnchor.LowerLeft);
            var r = t.rectTransform;
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f); r.pivot = new Vector2(0, 0); r.sizeDelta = new Vector2(400, 24);
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.gameObject.AddComponent<Outline>().effectColor = new Color(0, 0, 0, 0.8f);
            labels.Add(t);
        }
        return labels[i];
    }
}
