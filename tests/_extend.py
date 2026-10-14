with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "r", encoding="utf-8-sig") as f:
    content = f.read()

# Find and replace the offset section
old = """                    // Build cut polyline and offset by +/-0.05mm (gap=0.1mm)
                    double halfGap = 0.05;
                    var cutPl = new Polyline();
                    for (int i = 0; i < cutPts.Count; i++)
                        cutPl.AddVertexAt(i, new Point2d(cutPts[i].X, cutPts[i].Y), 0, 0, 0);

                    var offsetPlus = cutPl.GetOffsetCurves(halfGap);
                    var offsetMinus = cutPl.GetOffsetCurves(-halfGap);
                    cutPl.Dispose();"""

new = """                    // Build extended cut polyline and offset by +/-0.05mm (gap=0.1mm)
                    double halfGap = 0.05;
                    var extents = pl.GeometricExtents;
                    double dx = extents.MaxPoint.X - extents.MinPoint.X;
                    double dy = extents.MaxPoint.Y - extents.MinPoint.Y;
                    double diag = Math.Sqrt(dx * dx + dy * dy) * 2.0;

                    // Extend start and end points outward
                    var extCutPts = new List<Point3d>(cutPts);
                    Vector3d startDir = (cutPts[1] - cutPts[0]).GetNormal();
                    extCutPts[0] = cutPts[0] - startDir * diag;
                    Vector3d endDir = (cutPts[cutPts.Count - 1] - cutPts[cutPts.Count - 2]).GetNormal();
                    extCutPts[extCutPts.Count - 1] = cutPts[cutPts.Count - 1] + endDir * diag;

                    var cutPl = new Polyline();
                    for (int i = 0; i < extCutPts.Count; i++)
                        cutPl.AddVertexAt(i, new Point2d(extCutPts[i].X, extCutPts[i].Y), 0, 0, 0);

                    var offsetPlus = cutPl.GetOffsetCurves(halfGap);
                    var offsetMinus = cutPl.GetOffsetCurves(-halfGap);
                    cutPl.Dispose();"""

if old in content:
    content = content.replace(old, new)
    with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "w", encoding="utf-8-sig") as f:
        f.write(content)
    print("Extended cut polyline before offset")
else:
    print("NOT FOUND - searching...")
    if "halfGap = 0.05" in content:
        print("halfGap found")
    if "GetOffsetCurves" in content:
        print("GetOffsetCurves found")
