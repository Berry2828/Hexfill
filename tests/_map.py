with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "r", encoding="utf-8-sig") as f:
    content = f.read()

# Find and replace the classification section
old_classify = """                    var l1Verts = new List<Point3d>();
                    var l2Verts = new List<Point3d>();

                    for (int i = 0; i < splitCurves.Count; i++)
                    {
                        var seg = splitCurves[i] as Curve;
                        if (seg == null) continue;

                        Point3d s = seg.StartPoint;
                        Point3d e = seg.EndPoint;
                        bool sOn1 = PointOnLine(s, line1RefP1, line1RefP2, 0.05);
                        bool eOn1 = PointOnLine(e, line1RefP1, line1RefP2, 0.05);
                        bool sOn2 = PointOnLine(s, line2RefP1, line2RefP2, 0.05);
                        bool eOn2 = PointOnLine(e, line2RefP1, line2RefP2, 0.05);

                        if (sOn1 && eOn1)
                            CollectSegVerts(seg, l1Verts);
                        else if (sOn2 && eOn2)
                            CollectSegVerts(seg, l2Verts);
                    }

                    foreach (Curve c in splitCurves) c.Dispose();"""

new_classify = """                    // Build lookup: split point coordinate -> lineId
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
                        int sId = ptToLineId.GetValueOrDefault(sKey, -1);
                        int eId = ptToLineId.GetValueOrDefault(eKey, -1);

                        if (sId == 1 && eId == 1)
                            CollectSegVerts(seg, l1Verts);
                        else if (sId == 2 && eId == 2)
                            CollectSegVerts(seg, l2Verts);
                    }

                    foreach (Curve c in splitCurves) c.Dispose();"""

content = content.replace(old_classify, new_classify)

with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "w", encoding="utf-8-sig") as f:
    f.write(content)
print("Classification: PointOnLine -> coordinate mapping")
