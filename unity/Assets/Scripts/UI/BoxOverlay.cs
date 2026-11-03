using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Draws rectangle outlines over its own rect (one UI mesh). Boxes are normalized 0..1 from the top-left,
// the same as the detection packets' bbox [x, y, w, h], so the overlay just has to cover the video image.
public class BoxOverlay : MaskableGraphic {
    public struct Box { public Rect r; public Color color; }
    public readonly List<Box> boxes = new List<Box>();
    public float thickness = 3f;

    // Box in local UI units (for placing labels).
    public Rect ToLocal(Rect n) {
        var a = rectTransform.rect;
        return new Rect(a.xMin + n.x * a.width, a.yMax - (n.y + n.height) * a.height, n.width * a.width, n.height * a.height);
    }

    protected override void OnPopulateMesh(VertexHelper vh) {
        vh.Clear();
        foreach (var b in boxes) {
            var r = ToLocal(b.r); float t = thickness;
            Quad(vh, r.xMin, r.yMax - t, r.xMax, r.yMax, b.color);           // top
            Quad(vh, r.xMin, r.yMin, r.xMax, r.yMin + t, b.color);           // bottom
            Quad(vh, r.xMin, r.yMin + t, r.xMin + t, r.yMax - t, b.color);   // left
            Quad(vh, r.xMax - t, r.yMin + t, r.xMax, r.yMax - t, b.color);   // right
        }
    }

    static void Quad(VertexHelper vh, float x0, float y0, float x1, float y1, Color c) {
        int i = vh.currentVertCount;
        vh.AddVert(new Vector3(x0, y0), c, Vector4.zero); vh.AddVert(new Vector3(x0, y1), c, Vector4.zero);
        vh.AddVert(new Vector3(x1, y1), c, Vector4.zero); vh.AddVert(new Vector3(x1, y0), c, Vector4.zero);
        vh.AddTriangle(i, i + 1, i + 2); vh.AddTriangle(i, i + 2, i + 3);
    }
}
