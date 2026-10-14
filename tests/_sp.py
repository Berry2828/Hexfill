with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "r", encoding="utf-8-sig") as f:
    content = f.read()

# Replace the classification section with simpler split-point matching
old = """                    // Build lookup: split point coordinate -> lineId
                    var ptToLineId = new Dictionary<(double, double), int>();
                    foreach (var hit in uniqueHits)
                    {
                        var key = (Math.Round(hit.Pt.X, 4), Math.Round(hit.Pt.Y, 4));
                        if (!ptToLineId.ContainsKey(key))
                            ptToLineId[key] = hit.LineId;
                    }

                    var l1Verts = new List<Point3d>();
                    var l2Verts = new List<Point3d>();

                    for (int i = 0; i < splitCurves.Count; i++)
                    {
                        var seg = splitCurves[i] as Curve;
                        if (seg == null) continue;

                        var sKey = (Math.Round(seg.StartPoint.X, 4), Math.Round(seg.StartPoint.Y, 4));
                        var eKey = (Math.Round(seg.EndPoint.X, 4), Math.Round(seg.EndPoint.Y, 4));
                        int sId; if (!ptToLineId.TryGetValue(sKey, out sId)) sId = -1;
                        int eId; if (!ptToLineId.TryGetValue(eKey, out eId)) eId = -1;

                        if (sId == 1 && eId == 1)
                            CollectSegVerts(seg, l1Verts);
                        else if (sId == 2 && eId == 2)
                            CollectSegVerts(seg, l2Verts);
                    }

                    foreach (Curve c in splitCurves) c.Dispose();"""

new = """                    // Build lineId lookup for each unique hit point
                    var hitLineIds = new int[uniqueHits.Count];
                    for (int i = 0; i < uniqueHits.Count; i++)
                        hitLineIds[i] = uniqueHits[i].LineId;

                    // Each splitCurves segment corresponds to (hit k -> hit k+1) in cyclic order
                    // Segment is body if both endpoints have same lineId, gap if different
                    var l1Verts = new List<Point3d>();
                    var l2Verts = new List<Point3d>();
                    int m = splitCurves.Count;

                    for (int i = 0; i < m; i++)
                    {
                        var seg = splitCurves[i] as Curve;
                        if (seg == null) continue;

                        int idStart = hitLineIds[i];
                        int idEnd = hitLineIds[(i + 1) % m];

                        if (idStart == 1 && idEnd == 1)
                            CollectSegVerts(seg, l1Verts);
                        else if (idStart == 2 && idEnd == 2)
                            CollectSegVerts(seg, l2Verts);
                    }

                    foreach (Curve c in splitCurves) c.Dispose();"""

content = content.replace(old, new)

with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "w", encoding="utf-8-sig") as f:
    f.write(content)
print("Classification: split-point array matching (most robust)")
