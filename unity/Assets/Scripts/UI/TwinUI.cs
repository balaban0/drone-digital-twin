using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem.UI;
#endif

// Builds the whole UI at runtime (HUD, play/pause, speed, timeline, mode switch, banner)
// plus the right-hand column (CAMERA video on top, MAP below) that FlightVideo / FlightMap fill.
// The 3D cameras are squeezed into the area left of the column and above the bottom bar.
// Attach to DroneRoot next to FlightReplayer and UdpTwin. No manual UI setup needed.
public class TwinUI : MonoBehaviour {
    public static TwinUI Instance;
    public FlightReplayer replayer;
    public UdpTwin udp;
    [Tooltip("Disables other scripts on this object (DroneController, FlighReplay...) so they don't fight over the transform")]
    public bool autoDisableOthers = true;
    [Tooltip("Width of the video/map column as a fraction of the screen")]
    [Range(0.2f, 0.5f)] public float sideWidth = 0.34f;

    public RectTransform VideoArea { get; private set; }
    public RectTransform MapArea { get; private set; }
    public Font UIFont => font;

    Font font;
    Text hud, timeLabel, modeLabel, playLabel, bannerText;
    Slider slider;
    GameObject replayPanel, bannerPanel;
    RectTransform barRect, timelineMarks, markTip;
    Text markTipText;
    Transform canvasRoot;
    int toggleCount;
    Camera[] cams;
    bool live; float bannerUntil;
    const float BarHeight = 110;

    void Awake() {
        Instance = this;
        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (!replayer) replayer = GetComponent<FlightReplayer>();
        if (!udp) udp = GetComponent<UdpTwin>();
        EnsureEventSystem();
        BuildUI();
    }

    void Start() {
        if (autoDisableOthers)
            foreach (var mb in GetComponents<MonoBehaviour>()) {
                if (mb == this || mb == replayer || mb == udp || mb is DetectionMarkers || mb is LiveDetections ||
                    mb is FlightVideo || mb is FlightMap || mb is VideoDetections ||
                    mb is DetectionMapMarkers) continue;
                mb.enabled = false;
            }
        if (replayer) replayer.hudText = hud;
        if (udp) udp.hudText = hud;
        cams = FindObjectsByType<Camera>(FindObjectsInactive.Include);
        SetMode(false);
    }

    // Keep every 3D camera out from under the side column and the bottom bar (screen can be resized).
    void LateUpdate() {
        float bottom = barRect.gameObject.activeInHierarchy ? ScreenHeight(barRect) / Screen.height : 0f;
        var r = new Rect(0, bottom, 1f - sideWidth, 1f - bottom);
        foreach (var c in cams) if (c && c.rect != r) c.rect = r;
    }

    static float ScreenHeight(RectTransform rt) {
        var w = new Vector3[4]; rt.GetWorldCorners(w);  // overlay canvas: world = screen pixels
        return w[1].y - w[0].y;
    }

    // On/off button for an optional feature, stacked at the top right of the 3D view.
    public void AddToggle(string name, bool on, System.Action<bool> changed) {
        bool state = on; Text lbl = null; Button b = null;
        b = MakeButton("", canvasRoot, new Vector2(1f - sideWidth, 1f), new Vector2(-20, -20 - toggleCount * 72),
                       new Vector2(340, 60), () => { state = !state; Apply(); changed(state); }, out lbl);
        b.GetComponent<RectTransform>().pivot = new Vector2(1, 1);
        lbl.fontSize = 24;
        toggleCount++;
        Apply();

        void Apply() {
            lbl.text = $"{name}: {(state ? "ON" : "OFF")}";
            b.GetComponent<Image>().color = state ? new Color(0.15f, 0.55f, 0.65f) : new Color(0.22f, 0.24f, 0.27f, 0.9f);
        }
    }

    // Coloured spans just under the timeline track (e.g. when a reference object was detected).
    // Times in replay seconds; replaces earlier marks. Hover shows "label · time", click jumps the replay
    // to just before the span starts.
    public void SetTimelineMarks(IList<(float start, float end, Color color, string label)> spans, float duration) {
        if (timelineMarks == null) {
            var area = (RectTransform)slider.handleRect.parent;  // same horizontal range as the slider value
            timelineMarks = new GameObject("TimelineMarks", typeof(RectTransform)).GetComponent<RectTransform>();
            timelineMarks.SetParent(area, false);
            Stretch(timelineMarks, 0);
            timelineMarks.SetAsFirstSibling();  // under the handle

            markTip = Panel("MarkTip", area, new Color(0.24f, 0.27f, 0.32f, 0.97f));
            markTip.pivot = new Vector2(0.5f, 0f); markTip.sizeDelta = new Vector2(260, 40);
            markTip.GetComponent<Image>().raycastTarget = false;
            markTipText = Label("MarkTipText", markTip, "", 22, TextAnchor.MiddleCenter);
            Stretch(markTipText.rectTransform, 4);
            markTip.gameObject.SetActive(false);
        }
        foreach (Transform c in timelineMarks) Destroy(c.gameObject);
        if (duration <= 0) return;
        foreach (var s in spans) {
            var m = Panel("Mark", timelineMarks, s.color);
            float a = Mathf.Clamp01(s.start / duration), b = Mathf.Clamp01(Mathf.Max(s.end, s.start + duration * 0.003f) / duration);
            m.anchorMin = new Vector2(a, 0f); m.anchorMax = new Vector2(b, 0.22f);
            m.offsetMin = new Vector2(-3, -8); m.offsetMax = new Vector2(3, 0);  // a little extra to hit thin marks
            var mark = m.gameObject.AddComponent<TimelineMark>();
            mark.color = s.color;
            float start = s.start, mid = (a + b) / 2;
            string tip = $"{s.label} · {Fmt(s.start)}";
            mark.onClick = () => { if (replayer && replayer.enabled) replayer.Seek(Mathf.Max(0, start - 0.5f)); };
            mark.onEnter = () => {
                markTip.anchorMin = markTip.anchorMax = new Vector2(mid, 1f);
                markTip.anchoredPosition = new Vector2(0, 10);
                markTipText.text = tip; markTip.SetAsLastSibling(); markTip.gameObject.SetActive(true);
            };
            mark.onExit = () => markTip.gameObject.SetActive(false);
        }
    }

    public void ShowBanner(string text, float seconds = 3f) {
        bannerText.text = text; bannerPanel.SetActive(true); bannerUntil = Time.time + seconds;
    }

    void SetMode(bool toLive) {
        if (toLive && !udp) return;
        live = toLive;
        if (replayer) replayer.enabled = !live;
        if (udp) udp.enabled = live;
        replayPanel.SetActive(!live);
        modeLabel.text = live ? "MODE: LIVE (tap)" : "MODE: REPLAY (tap)";
        if (live) hud.text = "LIVE  waiting for data on UDP...";
    }

    void Update() {
        if (bannerPanel.activeSelf && Time.time > bannerUntil) bannerPanel.SetActive(false);
        if (live || !replayer || replayer.Duration <= 0f) return;
        slider.maxValue = replayer.Duration;
        slider.SetValueWithoutNotify(replayer.CurrentTime);
        timeLabel.text = Fmt(replayer.CurrentTime) + " / " + Fmt(replayer.Duration);
        playLabel.text = replayer.playing ? "Pause" : "Play";
    }

    static string Fmt(float s) { int m = (int)(s / 60); return $"{m}:{(int)(s % 60):00}"; }

    // ---------- UI construction ----------
    void BuildUI() {
        var cgo = new GameObject("TwinCanvas");
        var canvas = cgo.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var sc = cgo.AddComponent<CanvasScaler>();
        sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        sc.referenceResolution = new Vector2(1920, 1080); sc.matchWidthOrHeight = 0.5f;
        cgo.AddComponent<GraphicRaycaster>();
        var root = canvasRoot = cgo.transform;

        // HUD (top-left)
        var hudPanel = Panel("HudPanel", root, new Color(0, 0, 0, 0.5f));
        Place(hudPanel, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 1), new Vector2(20, -20), new Vector2(460, 270));
        hud = Label("Hud", hudPanel, "", 28, TextAnchor.UpperLeft);
        Stretch(hud.rectTransform, 15);

        // Side column (right): camera video over map
        var side = Panel("SideColumn", root, new Color(0.06f, 0.07f, 0.09f, 1f));
        side.anchorMin = new Vector2(1f - sideWidth, 0); side.anchorMax = Vector2.one;
        side.offsetMin = new Vector2(0, BarHeight); side.offsetMax = Vector2.zero;
        VideoArea = SidePanel("Video", "CAMERA", side, 0.5f, 1f);
        MapArea = SidePanel("Map", "MAP", side, 0f, 0.5f);

        // Detection banner (top-center of the 3D view)
        var bp = Panel("Banner", root, new Color(0.85f, 0.2f, 0.1f, 0.85f));
        var bx = (1f - sideWidth) / 2f;
        Place(bp, new Vector2(bx, 1), new Vector2(bx, 1), new Vector2(0.5f, 1), new Vector2(0, -30), new Vector2(1000, 80));
        bannerText = Label("BannerText", bp, "", 34, TextAnchor.MiddleCenter);
        Stretch(bannerText.rectTransform, 10);
        bannerPanel = bp.gameObject; bannerPanel.SetActive(false);

        // Bottom bar: replay controls (hidden in live mode) + mode switch (always visible)
        var bottom = Panel("BottomBar", root, new Color(0.06f, 0.07f, 0.09f, 1f));
        Place(bottom, new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0), Vector2.zero, new Vector2(0, BarHeight));
        barRect = bottom;
        MakeButton("MODE", bottom, new Vector2(1, 0.5f), new Vector2(-20, 0), new Vector2(330, 64),
            () => SetMode(!live), out modeLabel);

        var bar = new GameObject("ReplayControls", typeof(RectTransform)).GetComponent<RectTransform>();
        bar.SetParent(bottom, false);
        Stretch(bar, 0); bar.offsetMax = new Vector2(-370, 0);
        replayPanel = bar.gameObject;

        var left = new Vector2(0, 0.5f);
        MakeButton("Pause", bar, left, new Vector2(20, 0), new Vector2(140, 64),
            () => { if (replayer) replayer.TogglePlay(); }, out playLabel);
        float[] speeds = { 1, 2, 5, 10 };
        for (int i = 0; i < speeds.Length; i++) {
            float s = speeds[i];
            MakeButton(s + "x", bar, left, new Vector2(180 + i * 90, 0), new Vector2(80, 64),
                () => { if (replayer) replayer.SetSpeed(s); }, out _);
        }

        var sgo = DefaultControls.CreateSlider(new DefaultControls.Resources());
        sgo.transform.SetParent(bar, false);
        var sr = sgo.GetComponent<RectTransform>();
        sr.anchorMin = new Vector2(0, 0.5f); sr.anchorMax = new Vector2(1, 0.5f); sr.pivot = new Vector2(0.5f, 0.5f);
        sr.offsetMin = new Vector2(560, -16); sr.offsetMax = new Vector2(-270, 16);
        slider = sgo.GetComponent<Slider>();
        slider.minValue = 0; slider.maxValue = 1;
        slider.transform.Find("Background").GetComponent<Image>().color = new Color(1, 1, 1, 0.25f);
        slider.fillRect.GetComponent<Image>().color = new Color(0.2f, 0.8f, 0.9f);
        slider.handleRect.GetComponent<Image>().color = Color.white;
        slider.onValueChanged.AddListener(v => { if (replayer) replayer.Seek(v); });

        timeLabel = Label("Time", bar, "0:00 / 0:00", 30, TextAnchor.MiddleRight);
        Place(timeLabel.rectTransform, new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f),
            new Vector2(-20, 0), new Vector2(240, 50));
    }

    void EnsureEventSystem() {
        if (FindAnyObjectByType<EventSystem>() != null) return;
        var es = new GameObject("EventSystem"); es.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        es.AddComponent<InputSystemUIInputModule>();
#else
        es.AddComponent<StandaloneInputModule>();
#endif
    }

    // Titled box inside the side column covering [yMin, yMax] of its height; returns the content area.
    RectTransform SidePanel(string n, string title, Transform p, float yMin, float yMax) {
        var box = Panel(n + "Panel", p, new Color(1, 1, 1, 0.04f));
        box.anchorMin = new Vector2(0, yMin); box.anchorMax = new Vector2(1, yMax);
        box.offsetMin = new Vector2(12, 6); box.offsetMax = new Vector2(-12, -6);
        var head = Label(n + "Title", box, title, 24, TextAnchor.MiddleLeft);
        head.color = new Color(0.55f, 0.8f, 0.9f);
        Place(head.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, 0), new Vector2(-24, 40));
        var content = Panel(n + "Content", box, Color.black);
        content.anchorMin = Vector2.zero; content.anchorMax = Vector2.one;
        content.offsetMin = new Vector2(6, 6); content.offsetMax = new Vector2(-6, -40);
        content.gameObject.AddComponent<RectMask2D>();
        return content;
    }

    RectTransform Panel(string n, Transform p, Color c) {
        var go = new GameObject(n, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(p, false); go.GetComponent<Image>().color = c;
        return go.GetComponent<RectTransform>();
    }

    static void Place(RectTransform r, Vector2 amin, Vector2 amax, Vector2 pivot, Vector2 pos, Vector2 size) {
        r.anchorMin = amin; r.anchorMax = amax; r.pivot = pivot; r.anchoredPosition = pos; r.sizeDelta = size;
    }

    public static void Stretch(RectTransform r, float pad) {
        r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
        r.offsetMin = new Vector2(pad, pad); r.offsetMax = new Vector2(-pad, -pad);
    }

    public Text Label(string n, Transform p, string s, int size, TextAnchor a) {
        var go = new GameObject(n, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        go.transform.SetParent(p, false);
        var t = go.GetComponent<Text>();
        t.font = font; t.fontSize = size; t.alignment = a; t.color = Color.white; t.text = s; t.raycastTarget = false;
        return t;
    }

    Button MakeButton(string label, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size,
                      UnityAction act, out Text lbl) {
        var go = DefaultControls.CreateButton(new DefaultControls.Resources());
        go.transform.SetParent(parent, false);
        Place(go.GetComponent<RectTransform>(), anchor, anchor, anchor, pos, size);
        go.GetComponent<Image>().color = new Color(0.15f, 0.55f, 0.65f, 1f);
        lbl = go.GetComponentInChildren<Text>();
        lbl.font = font; lbl.fontSize = 30; lbl.text = label; lbl.color = Color.white;
        var b = go.GetComponent<Button>(); b.onClick.AddListener(act);
        return b;
    }
}
