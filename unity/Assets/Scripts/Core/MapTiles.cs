using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

// Web map tiles (Web Mercator / EPSG:3857, the tiling used by Esri and OSM) shared by the 2D map and the 3D ground.
// Load order: memory -> disk cache -> download (then saved to the disk cache).
public static class MapTiles {
    public enum Source { Satellite, Streets }

    public const int Size = 256;
    static readonly Dictionary<string, Texture2D> loaded = new Dictionary<string, Texture2D>();
    static readonly HashSet<string> pending = new HashSet<string>();
    public static int Failures { get; private set; }

    // The project enters Play mode without a domain reload, so statics survive between runs: textures from the
    // last run are destroyed and loads cut off by stopping Play stay "pending" forever. Start clean every run.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { loaded.Clear(); pending.Clear(); Failures = 0; }

    public static string Attribution(Source s) =>
        s == Source.Satellite ? "© Esri, Maxar, Earthstar Geographics" : "© OpenStreetMap contributors";

    // Global pixel coordinates at zoom z (x right, y down).
    public static (double x, double y) Pixel(double lat, double lon, int z) {
        double n = Size * Math.Pow(2, z), r = lat * Math.PI / 180;
        return ((lon + 180) / 360 * n, (1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * n);
    }

    public static (double lat, double lon) LatLon(double px, double py, int z) {
        double n = Size * Math.Pow(2, z);
        return (Math.Atan(Math.Sinh(Math.PI * (1 - 2 * py / n))) * 180 / Math.PI, px / n * 360 - 180);
    }

    public static double MetersPerPixel(double lat, int z) => 156543.03392 * Math.Cos(lat * Math.PI / 180) / Math.Pow(2, z);

    // Calls done(texture), or done(null) if the tile could not be loaded.
    public static IEnumerator Load(Source src, int z, int x, int y, string cacheFolder, Action<Texture2D> done) {
        string name = src == Source.Satellite ? "esri" : "osm";
        string key = $"{name}/{z}/{x}/{y}";
        while (pending.Contains(key)) yield return null;  // same tile already on its way (2D map + 3D ground)
        if (loaded.TryGetValue(key, out var hit) && hit) { done(hit); yield break; }

        pending.Add(key);
        byte[] bytes = null;
        string file = Path.GetFullPath(Path.Combine(cacheFolder, name, z.ToString(), x.ToString(), y + ".img"));
        if (File.Exists(file)) bytes = File.ReadAllBytes(file);
        else {
            string url = src == Source.Satellite
                ? $"https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"
                : $"https://tile.openstreetmap.org/{z}/{x}/{y}.png";
            using (var req = UnityWebRequest.Get(url)) {
                req.SetRequestHeader("User-Agent", "unity-drone-digital-twin/0.1 (research prototype)");
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) {
                    bytes = req.downloadHandler.data;
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllBytes(file, bytes);
                } else if (Failures++ == 0) {
                    Debug.LogWarning($"MapTiles: download failed ({req.error}) {url}");
                }
            }
        }

        Texture2D tex = null;
        if (bytes != null) {
            tex = new Texture2D(2, 2, TextureFormat.RGBA32, true) {
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 8, name = key
            };
            if (tex.LoadImage(bytes)) loaded[key] = tex;
            else { UnityEngine.Object.Destroy(tex); tex = null; }
        }
        pending.Remove(key);
        done(tex);
    }
}
