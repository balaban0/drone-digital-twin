using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Draws the flight track, home point and drone arrow on top of the map tiles (one UI mesh).
// Points are in the parent's local units (x right, y down); FlightMap fills them in.
public class MapOverlay : MaskableGraphic {
    public List<Vector2> points = new List<Vector2>();
    public int flown;              // points[0..flown) are drawn bright, the rest dim
    public Vector2 drone; public float heading; public bool showDrone;
    public float lineWidth = 3f, droneSize = 14f;
    public Color trackDim = new Color(1, 1, 1, 0.35f), trackFlown = new Color(0.2f, 0.85f, 0.95f, 1f),
                 droneColor = new Color(1f, 0.55f, 0.1f, 1f), homeColor = new Color(1f, 0.9f, 0.2f, 1f);

    // Extra markers (e.g. detected objects): diamonds with a dark outline, size in the same units as points.
    public struct Mark { public Vector2 p; public Color color; public float size; }
    public readonly List<Mark> marks = new List<Mark>();

    protected override void OnPopulateMesh(VertexHelper vh) {
        vh.Clear();
        for (int i = 1; i < points.Count; i++)
            Segment(vh, P(points[i - 1]), P(points[i]), lineWidth, i < flown ? trackFlown : trackDim);
        if (points.Count > 0) Square(vh, P(points[0]), droneSize * 0.6f, homeColor);
        foreach (var m in marks) {
            Diamond(vh, P(m.p), m.size * 1.4f, new Color(0, 0, 0, 0.75f));
            Diamond(vh, P(m.p), m.size, m.color);
        }
        if (showDrone) Arrow(vh, P(drone), heading, droneSize, droneColor);
    }

    static void Diamond(VertexHelper vh, Vector2 p, float s, Color c) {
        float h = s * 0.5f;
        Quad(vh, p + new Vector2(0, -h), p + new Vector2(-h, 0), p + new Vector2(0, h), p + new Vector2(h, 0), c);
    }

    static Vector2 P(Vector2 p) => new Vector2(p.x, -p.y);

    static void Segment(VertexHelper vh, Vector2 a, Vector2 b, float w, Color c) {
        var d = b - a; if (d.sqrMagnitude < 1e-6f) return;
        var n = new Vector2(-d.y, d.x).normalized * (w * 0.5f);
        Quad(vh, a - n, a + n, b + n, b - n, c);
    }

    static void Square(VertexHelper vh, Vector2 p, float s, Color c) {
        float h = s * 0.5f;
        Quad(vh, p + new Vector2(-h, -h), p + new Vector2(-h, h), p + new Vector2(h, h), p + new Vector2(h, -h), c);
    }

    // Heading in degrees, 0 = north (up), clockwise.
    static void Arrow(VertexHelper vh, Vector2 p, float heading, float s, Color c) {
        float r = -heading * Mathf.Deg2Rad;
        Vector2 R(float x, float y) => p + new Vector2(x * Mathf.Cos(r) - y * Mathf.Sin(r), x * Mathf.Sin(r) + y * Mathf.Cos(r));
        int i = vh.currentVertCount;
        vh.AddVert(R(0, s), c, Vector4.zero);
        vh.AddVert(R(s * 0.6f, -s * 0.6f), c, Vector4.zero);
        vh.AddVert(R(0, -s * 0.25f), c, Vector4.zero);
        vh.AddVert(R(-s * 0.6f, -s * 0.6f), c, Vector4.zero);
        vh.AddTriangle(i, i + 1, i + 2); vh.AddTriangle(i, i + 2, i + 3);
    }

    static void Quad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c2, Vector2 d, Color c) {
        int i = vh.currentVertCount;
        vh.AddVert(a, c, Vector4.zero); vh.AddVert(b, c, Vector4.zero);
        vh.AddVert(c2, c, Vector4.zero); vh.AddVert(d, c, Vector4.zero);
        vh.AddTriangle(i, i + 1, i + 2); vh.AddTriangle(i, i + 2, i + 3);
    }
}
