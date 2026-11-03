using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// 2D map in TwinUI's MAP panel: web map tiles around the flight, the track (flown part bright)
// and the drone arrow at the replay time. North is up.
// Tiles are cached on disk (MapTiles), so after one online run the map also works offline.
// Attach to DroneRoot next to FlightReplayer and TwinUI.
public class FlightMap : MonoBehaviour {
    public FlightReplayer replayer;
    [Tooltip("Satellite = Esri World Imagery, Streets = OpenStreetMap")]
    public MapTiles.Source source = MapTiles.Source.Satellite;
    [Range(10, 19)] public int maxZoom = 19;
    [Tooltip("Upper limit of tiles per side; the highest zoom that fits is used")]
    [Range(1, 8)] public int maxTilesPerSide = 4;
    [Tooltip("Empty space kept around the track (m)")]
    public float marginMeters = 25f;
    [Tooltip("Tile cache, relative to the Unity project folder")]
    public string cacheFolder = "../data/tiles";
    [Tooltip("Track width on screen (px)")]
    public float lineWidth = 3f;

    const int TILE = MapTiles.Size;
    RectTransform area, content;
    MapOverlay overlay;
    Text status;
    readonly List<float> pointTimes = new List<float>();
    int zoom, tx0, ty0;
    Rect box;                       // track bbox + margin, in content units (y down)
    bool ready;

    // Extra markers on the map (e.g. DetectionMapMarkers); size in screen pixels.
    readonly List<(double lat, double lon, Color color, float sizePx)> extraMarks = new List<(double, double, Color, float)>();
    bool marksDirty;
    public void SetMarks(IEnumerable<(double lat, double lon, Color color, float sizePx)> m) {
        extraMarks.Clear(); if (m != null) extraMarks.AddRange(m); marksDirty = true;
    }

    void Awake() { if (!replayer) replayer = GetComponent<FlightReplayer>(); }

    void Start() {
        var ui = TwinUI.Instance;
        if (!ui || ui.MapArea == null) { enabled = false; return; }
        area = ui.MapArea;
        status = ui.Label("MapStatus", area, "", 22, TextAnchor.MiddleCenter);
        status.color = new Color(1, 1, 1, 0.6f);
        TwinUI.Stretch(status.rectTransform, 20);
    }

    void Init() {
        ready = true;
        var log = replayer.Log;
        double latMin = 90, latMax = -90, lonMin = 180, lonMax = -180;
        foreach (var s in log) {
            latMin = Math.Min(latMin, s.lat); latMax = Math.Max(latMax, s.lat);
            lonMin = Math.Min(lonMin, s.lon); lonMax = Math.Max(lonMax, s.lon);
        }

        // Highest zoom whose tile range (track + margin) stays within maxTilesPerSide.
        for (zoom = maxZoom; zoom > 1; zoom--) {
            double m = marginMeters / MapTiles.MetersPerPixel((latMin + latMax) / 2, zoom);
            var a = MapTiles.Pixel(latMax, lonMin, zoom); var b = MapTiles.Pixel(latMin, lonMax, zoom);
            tx0 = (int)Math.Floor((a.x - m) / TILE); ty0 = (int)Math.Floor((a.y - m) / TILE);
            int tx1 = (int)Math.Floor((b.x + m) / TILE), ty1 = (int)Math.Floor((b.y + m) / TILE);
            if (tx1 - tx0 + 1 <= maxTilesPerSide && ty1 - ty0 + 1 <= maxTilesPerSide) {
                box = Rect.MinMaxRect((float)(a.x - m - tx0 * TILE), (float)(a.y - m - ty0 * TILE),
                                      (float)(b.x + m - tx0 * TILE), (float)(b.y + m - ty0 * TILE));
                BuildTiles(tx1 - tx0 + 1, ty1 - ty0 + 1);
                break;
            }
        }
        if (content == null) { status.text = "Flight area does not fit the map"; return; }

        // Track, thinned to ~1 px steps so the mesh stays small.
        overlay.points.Clear(); pointTimes.Clear();
        foreach (var s in log) {
            var p = Local(s.lat, s.lon);
            int n = overlay.points.Count;
            if (n > 0 && (p - overlay.points[n - 1]).sqrMagnitude < 1f) continue;
            overlay.points.Add(p); pointTimes.Add(s.t);
        }
        overlay.SetVerticesDirty();
        Debug.Log($"FlightMap: zoom {zoom}, tiles from ({tx0},{ty0}), {overlay.points.Count} track points");
    }

    void BuildTiles(int nx, int ny) {
        content = new GameObject("MapContent", typeof(RectTransform)).GetComponent<RectTransform>();
        content.SetParent(area, false);
        content.anchorMin = content.anchorMax = new Vector2(0.5f, 0.5f);
        content.pivot = new Vector2(0, 1); content.sizeDelta = new Vector2(nx * TILE, ny * TILE);
        content.SetAsFirstSibling();  // under the status text

        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++) {
                var img = new GameObject($"Tile_{zoom}_{tx0 + i}_{ty0 + j}", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage))
                    .GetComponent<RawImage>();
                var r = img.rectTransform; r.SetParent(content, false);
                r.anchorMin = r.anchorMax = r.pivot = new Vector2(0, 1);
                r.anchoredPosition = new Vector2(i * TILE, -j * TILE); r.sizeDelta = new Vector2(TILE, TILE);
                img.color = new Color(0.12f, 0.12f, 0.14f); img.raycastTarget = false;
                StartCoroutine(MapTiles.Load(source, zoom, tx0 + i, ty0 + j, cacheFolder, tex => {
                    if (tex) { img.texture = tex; img.color = Color.white; }
                    else status.text = "Could not download map tiles\n(internet connection?)";
                }));
            }

        overlay = new GameObject("Track", typeof(RectTransform), typeof(CanvasRenderer), typeof(MapOverlay))
            .GetComponent<MapOverlay>();
        var o = overlay.rectTransform; o.SetParent(content, false);
        o.anchorMin = o.anchorMax = o.pivot = new Vector2(0, 1);
        o.anchoredPosition = Vector2.zero; o.sizeDelta = content.sizeDelta;
        overlay.raycastTarget = false;

        var attr = TwinUI.Instance.Label("MapAttribution", area, MapTiles.Attribution(source), 16, TextAnchor.LowerRight);
        attr.color = new Color(1, 1, 1, 0.7f);
        TwinUI.Stretch(attr.rectTransform, 6);
    }

    void LateUpdate() {
        if (!replayer || replayer.Log == null || replayer.Log.Count < 2) return;
        if (!ready) Init();
        if (content == null) return;

        // Fit the track box into the panel and centre it.
        var size = area.rect.size;
        if (size.x <= 0 || size.y <= 0) return;
        float s = Mathf.Min(size.x / box.width, size.y / box.height);
        content.localScale = Vector3.one * s;
        content.anchoredPosition = new Vector2(-box.center.x, box.center.y) * s;

        bool live = !replayer.enabled;
        float w = lineWidth / s;
        int flown = live ? 0 : UpperBound(pointTimes, replayer.CurrentTime);
        Vector2 drone = overlay.drone; float hd = overlay.heading;
        if (!live) {
            var log = replayer.Log; float t = replayer.CurrentTime;
            int i = replayer.IndexAt(t), k = Mathf.Min(i + 1, log.Count - 1);
            float f = Mathf.InverseLerp(log[i].t, log[k].t, t);
            drone = Vector2.Lerp(Local(log[i].lat, log[i].lon), Local(log[k].lat, log[k].lon), f);
            hd = Mathf.LerpAngle(log[i].heading, log[k].heading, f);
        }
        bool rescaled = !Mathf.Approximately(w, overlay.lineWidth);
        if (marksDirty || rescaled) {
            overlay.marks.Clear();
            foreach (var m in extraMarks)
                overlay.marks.Add(new MapOverlay.Mark { p = Local(m.lat, m.lon), color = m.color, size = m.sizePx / s });
            marksDirty = false;
            overlay.SetVerticesDirty();
        }
        if (flown != overlay.flown || drone != overlay.drone || hd != overlay.heading || rescaled || overlay.showDrone == live) {
            overlay.flown = flown; overlay.drone = drone; overlay.heading = hd; overlay.showDrone = !live;
            overlay.lineWidth = w; overlay.droneSize = 16f / s;
            overlay.SetVerticesDirty();
        }
    }

    static int UpperBound(List<float> a, float v) {
        int lo = 0, hi = a.Count;
        while (lo < hi) { int m = (lo + hi) / 2; if (a[m] <= v) lo = m + 1; else hi = m; }
        return lo;
    }

    // Position in content units (pixels at this zoom from the top-left tile, y down).
    Vector2 Local(double lat, double lon) {
        var p = MapTiles.Pixel(lat, lon, zoom);
        return new Vector2((float)(p.x - tx0 * TILE), (float)(p.y - ty0 * TILE));
    }
}
