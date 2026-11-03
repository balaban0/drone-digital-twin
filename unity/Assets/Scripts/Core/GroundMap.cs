using System;
using UnityEngine;

// Lays web map tiles (satellite by default) on the ground of the 3D scene, in the same local frame
// as the flight log (origin = first log sample, x = east, z = north), so the twin flies over the real place.
// Two layers: sharp tiles around the flight, and coarser ones out to the horizon just below them.
public class GroundMap : MonoBehaviour {
    public FlightReplayer replayer;
    [Tooltip("Satellite = Esri World Imagery, Streets = OpenStreetMap")]
    public MapTiles.Source source = MapTiles.Source.Satellite;
    [Tooltip("Unlit material; each tile sets its texture on it (_BaseMap / _MainTex)")]
    public Material tileMaterial;
    [Tooltip("Ground level in metres; 0 = takeoff point")]
    public float height = 0f;

    [Header("Detail layer (around the flight)")]
    [Range(15, 19)] public int detailZoom = 19;
    [Tooltip("Extra ground around the track (m)")]
    public float detailMargin = 60f;

    [Header("Context layer (to the horizon)")]
    [Range(0, 18)] public int contextZoom = 16;    // 0 = off
    [Tooltip("Radius around the flight centre (m)")]
    public float contextRadius = 800f;

    [Tooltip("Hidden once the first map tile arrives (e.g. the old grey ground plane)")]
    public GameObject[] hideWhenLoaded;
    [Tooltip("Tile cache, relative to the Unity project folder")]
    public string cacheFolder = "../data/tiles";

    const int MaxTilesPerLayer = 400;
    static readonly int BaseMap = Shader.PropertyToID("_BaseMap"), MainTex = Shader.PropertyToID("_MainTex");
    Mesh quad;
    double lat0, lon0;
    bool built, shown;

    void Awake() { if (!replayer) replayer = FindAnyObjectByType<FlightReplayer>(); }

    void Update() {
        if (built || !replayer || replayer.Log == null || replayer.Log.Count < 2) return;
        built = true;
        var log = replayer.Log;
        lat0 = log[0].lat; lon0 = log[0].lon;  // FlightLog's origin

        double latMin = 90, latMax = -90, lonMin = 180, lonMax = -180;
        foreach (var s in log) {
            latMin = Math.Min(latMin, s.lat); latMax = Math.Max(latMax, s.lat);
            lonMin = Math.Min(lonMin, s.lon); lonMax = Math.Max(lonMax, s.lon);
        }
        quad = MakeQuad();
        Layer("Detail", detailZoom, latMin, latMax, lonMin, lonMax, detailMargin, height);
        if (contextZoom > 0) {
            double cLat = (latMin + latMax) / 2, cLon = (lonMin + lonMax) / 2;
            Layer("Context", contextZoom, cLat, cLat, cLon, cLon, contextRadius, height - 0.3f);
        }
    }

    // Tiles covering the lat/lon box grown by margin metres. Zoom drops if that needs too many tiles.
    void Layer(string name, int zoom, double latMin, double latMax, double lonMin, double lonMax, double margin, float y) {
        double dLat = margin / 111320.0, dLon = margin / (111320.0 * Math.Cos(lat0 * Math.PI / 180));
        int x0, y0, x1, y1;
        for (; ; zoom--) {
            var a = MapTiles.Pixel(latMax + dLat, lonMin - dLon, zoom);
            var b = MapTiles.Pixel(latMin - dLat, lonMax + dLon, zoom);
            x0 = (int)(a.x / MapTiles.Size); y0 = (int)(a.y / MapTiles.Size);
            x1 = (int)(b.x / MapTiles.Size); y1 = (int)(b.y / MapTiles.Size);
            if ((x1 - x0 + 1) * (y1 - y0 + 1) <= MaxTilesPerLayer || zoom <= 1) break;
        }

        var root = new GameObject($"{name}_z{zoom}").transform;
        root.SetParent(transform, false);
        for (int ty = y0; ty <= y1; ty++)
            for (int tx = x0; tx <= x1; tx++) {
                // Tile corners -> local metres. Lat/lon lines are straight in this frame, so a tile is an
                // axis-aligned rectangle: a unit quad scaled to its size.
                var nw = MapTiles.LatLon(tx * MapTiles.Size, ty * MapTiles.Size, zoom);
                var se = MapTiles.LatLon((tx + 1) * MapTiles.Size, (ty + 1) * MapTiles.Size, zoom);
                Vector2 p0 = FlightLog.ToLocal(se.lat, nw.lon, lat0, lon0);  // south-west
                Vector2 p1 = FlightLog.ToLocal(nw.lat, se.lon, lat0, lon0);  // north-east

                var go = new GameObject($"Tile_{zoom}_{tx}_{ty}", typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(root, false);
                go.transform.localPosition = new Vector3(p0.x, y, p0.y);
                go.transform.localScale = new Vector3(p1.x - p0.x, 1, p1.y - p0.y);
                go.GetComponent<MeshFilter>().sharedMesh = quad;
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial = tileMaterial;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.enabled = false;  // until its texture arrives
                StartCoroutine(MapTiles.Load(source, zoom, tx, ty, cacheFolder, tex => Show(r, tex)));
            }
        Debug.Log($"GroundMap: {name} layer zoom {zoom}, {(x1 - x0 + 1) * (y1 - y0 + 1)} tiles");
    }

    void Show(MeshRenderer r, Texture2D tex) {
        if (!tex || !r) return;
        var mpb = new MaterialPropertyBlock();
        mpb.SetTexture(BaseMap, tex); mpb.SetTexture(MainTex, tex);
        r.SetPropertyBlock(mpb);
        r.enabled = true;
        if (!shown) {
            shown = true;
            foreach (var g in hideWhenLoaded) if (g) g.SetActive(false);
        }
    }

    // 1 x 1 quad in the XZ plane, facing up, from (0,0) to (1,1); uv (0,0) = south-west.
    static Mesh MakeQuad() {
        var m = new Mesh { name = "GroundTileQuad" };
        m.vertices = new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 0, 0) };
        m.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
        m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        m.RecalculateNormals(); m.RecalculateBounds();
        return m;
    }
}
