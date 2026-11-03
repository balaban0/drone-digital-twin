using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

// Shows the drone's own camera video in TwinUI's CAMERA panel, locked to the replay timeline.
// The log's CAMERA.isVideo flag splits the flight into recording segments; the n-th video file
// (sorted by name, as DJI numbers them) is played during the n-th segment.
// Attach to DroneRoot next to FlightReplayer and TwinUI.
public class FlightVideo : MonoBehaviour {
    public FlightReplayer replayer;
    [Tooltip("Folder with this flight's videos. Empty = ../videos/<log name> next to the flight_logs folder")]
    public string videoFolder = "";
    [Tooltip("Seconds added to every clip start (+ = video starts later in the log)")]
    public float offset = 0f;
    [Tooltip("Resync when the video drifts more than this from the log time (seconds, scaled by replay speed)")]
    public float maxDrift = 0.4f;

    struct Segment { public float start, end; }
    readonly List<Segment> segs = new List<Segment>();
    string[] clips = new string[0];
    string folder;
    VideoPlayer vp; RenderTexture rt;
    RawImage view; AspectRatioFitter fit; Text status;
    int current = -1; bool ready;
    double lastSeek = -1; float nextSync;

    // For overlays on the video (VideoDetections)
    public bool Ready => ready;
    public bool Showing => view && view.enabled;
    public RectTransform ImageRect => view ? view.rectTransform : null;
    public string CurrentClip => current >= 0 && current < clips.Length ? Path.GetFileName(clips[current]) : null;
    public double PlaybackTime => vp && vp.isPrepared ? vp.time : -1;  // time of the frame on screen (s)

    // Where a clip (file name) starts on the log timeline; false if it isn't matched to a recording segment.
    public bool TryClipStart(string clipFile, out float logStart) {
        int i = System.Array.FindIndex(clips, c => string.Equals(Path.GetFileName(c), clipFile, System.StringComparison.OrdinalIgnoreCase));
        logStart = i >= 0 && i < segs.Count ? segs[i].start + offset : 0;
        return i >= 0 && i < segs.Count;
    }

    void Awake() { if (!replayer) replayer = GetComponent<FlightReplayer>(); }

    void Start() {
        var ui = TwinUI.Instance;
        if (!ui || ui.VideoArea == null) { enabled = false; return; }

        var go = new GameObject("VideoImage", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage), typeof(AspectRatioFitter));
        go.transform.SetParent(ui.VideoArea, false);
        view = go.GetComponent<RawImage>(); view.raycastTarget = false; view.enabled = false;
        fit = go.GetComponent<AspectRatioFitter>(); fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        fit.aspectRatio = 16f / 9f;

        status = ui.Label("VideoStatus", ui.VideoArea, "", 24, TextAnchor.MiddleCenter);
        status.color = new Color(1, 1, 1, 0.6f);
        TwinUI.Stretch(status.rectTransform, 20);

        vp = gameObject.AddComponent<VideoPlayer>();
        vp.playOnAwake = false; vp.isLooping = false; vp.skipOnDrop = true;
        vp.audioOutputMode = VideoAudioOutputMode.None;
        vp.renderMode = VideoRenderMode.RenderTexture;
        vp.prepareCompleted += OnPrepared;
        vp.errorReceived += (_, msg) => { status.text = "Could not open video:\n" + msg; view.enabled = false; };
    }

    void OnDestroy() { if (rt) rt.Release(); }

    // Build segments once the replayer has loaded its log.
    void Init() {
        ready = true;
        segs.Clear();
        var log = replayer.Log;
        int s = -1;
        for (int i = 0; i < log.Count; i++) {
            if (log[i].recording && s < 0) s = i;
            if ((!log[i].recording || i == log.Count - 1) && s >= 0) {
                segs.Add(new Segment { start = log[s].t, end = log[i].t }); s = -1;
            }
        }

        folder = videoFolder.Trim().Trim('"');
        folder = Path.GetFullPath(folder == "" ? replayer.FlightDataFolder("videos") : folder);
        if (Directory.Exists(folder))
            clips = Directory.GetFiles(folder)
                .Where(f => new[] { ".mov", ".mp4" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f).ToArray();
        Debug.Log($"FlightVideo: {segs.Count} recording segment(s) in log, {clips.Length} clip(s) in {folder}");
        if (clips.Length > 0 && clips.Length != segs.Count)
            Debug.LogWarning("FlightVideo: clip count does not match the log's recording segments; check the order / offset.");
    }

    void Update() {
        if (!replayer || replayer.Log == null || replayer.Log.Count < 2) return;
        if (!ready) Init();
        if (!replayer.enabled) { Show(-1, "No video in live mode"); return; }  // live mode

        float t = replayer.CurrentTime;
        int seg = segs.FindIndex(g => t >= g.start + offset && t < g.end + offset);
        if (seg < 0) { Show(-1, NextHint(t)); return; }
        if (seg >= clips.Length) {
            Show(-1, $"No video file for recording #{seg + 1} ({Fmt(segs[seg].start)}–{Fmt(segs[seg].end)})\n{folder}");
            return;
        }

        if (seg != current) {
            current = seg; view.enabled = false;
            status.text = "Loading: " + Path.GetFileName(clips[seg]);
            vp.Stop(); vp.url = clips[seg]; vp.Prepare(); lastSeek = -1;
            return;
        }
        if (!vp.isPrepared) return;

        double target = t - (segs[seg].start + offset);
        vp.playbackSpeed = Mathf.Clamp(replayer.speedMultiplier, 0.25f, 10f);
        // A seek takes a few frames to land, so don't issue a new one on every frame.
        if (replayer.playing) {
            if (!vp.isPlaying) vp.Play();
            bool drift = System.Math.Abs(vp.time - target) > maxDrift * Mathf.Max(1f, replayer.speedMultiplier);
            if (drift && Time.unscaledTime > nextSync) { vp.time = target; lastSeek = target; nextSync = Time.unscaledTime + 1f; }
        } else {
            if (vp.isPlaying) vp.Pause();
            if (System.Math.Abs(target - lastSeek) > 0.05) { vp.time = target; lastSeek = target; }  // scrubbing
        }
    }

    void OnPrepared(VideoPlayer p) {
        if (rt == null || rt.width != (int)p.width || rt.height != (int)p.height) {
            if (rt) rt.Release();
            rt = new RenderTexture((int)p.width, (int)p.height, 0);
            p.targetTexture = rt; view.texture = rt;
        }
        fit.aspectRatio = (float)p.width / p.height;
        view.enabled = true; status.text = "";
    }

    void Show(int seg, string msg) {
        if (current != seg) { current = seg; vp.Stop(); }
        view.enabled = false; status.text = msg;
    }

    string NextHint(float t) {
        if (segs.Count == 0) return "No video recorded on this flight";
        foreach (var g in segs) if (g.start + offset > t) return $"Camera not recording\nnext recording at {Fmt(g.start + offset)}";
        return "Camera not recording";
    }

    static string Fmt(float s) { int m = (int)(s / 60); return $"{m}:{(int)(s % 60):00}"; }
}
