with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "r", encoding="utf-8-sig") as f:
    content = f.read()

# Replace the entire offset+intersection section
old_section = """                    // Build extended cut polyline and offset by +/-0.05mm (gap=0.1mm)
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
                    cutPl.Dispose();

                    if (offsetPlus.Count == 0 || offsetMinus.Count == 0)
                    {
                        foreach (DBObject o in offsetPlus) o.Dispose();
                        foreach (DBObject o in offsetMinus) o.Dispose();
                        ed.WriteMessage("\\nError: cannot offset cut polyline");
                        return;
                    }

                    var cutLine1 = offsetPlus[0] as Curve;
                    var cutLine2 = offsetMinus[0] as Curve;
                    if (cutLine1 == null || cutLine2 == null)
                    {
                        foreach (DBObject o in offsetPlus) o.Dispose();
                        foreach (DBObject o in offsetMinus) o.Dispose();
                        ed.WriteMessage("\\nError: offset produced non-curve result");
                        return;
                    }

                    // Reference lines for side classification (use first segment direction)
                    Point3d firstPt = cutPts[0];
                    Point3d secondPt = cutPts[1];
                    Vector3d firstDir = (secondPt - firstPt).GetNormal();
                    Vector3d firstPerp = new Vector3d(-firstDir.Y, firstDir.X, 0);
                    Point3d line1RefP1 = firstPt + firstPerp * halfGap;
                    Point3d line1RefP2 = firstPt + firstPerp * halfGap + firstDir;
                    Point3d line2RefP1 = firstPt - firstPerp * halfGap;
                    Point3d line2RefP2 = firstPt - firstPerp * halfGap + firstDir;

                    // Find intersections
                    var allHits = new List<(Point3d Pt, double Param, int LineId)>();
                    int nVerts = pl.NumberOfVertices;
                    for (int i = 0; i < nVerts; i++)
                    {
                        Point3d vi = pl.GetPoint3dAt(i);
                        Point3d vj = pl.GetPoint3dAt((i + 1) % nVerts);
                        if (vi.DistanceTo(vj) < 1e-9) continue;

                        var pts = new Point3dCollection();
                        using (var edge = new Line(vi, vj))
                        {
                            cutLine1.IntersectWith(edge, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);
                            foreach (Point3d p in pts)
                            {
                                Point3d onCurve = pl.GetClosestPointTo(p, false);
                                allHits.Add((onCurve, pl.GetParameterAtPoint(onCurve), 1));
                            }
                        }
                        pts.Clear();
                        using (var edge2 = new Line(vi, vj))
                        {
                            cutLine2.IntersectWith(edge2, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);
                            foreach (Point3d p in pts)
                            {
                                Point3d onCurve = pl.GetClosestPointTo(p, false);
                                allHits.Add((onCurve, pl.GetParameterAtPoint(onCurve), 2));
                            }
                        }
                    }

                    cutLine1.Dispose();
                    cutLine2.Dispose();
                    foreach (DBObject o in offsetPlus) { if (o != cutLine1) o.Dispose(); }
                    foreach (DBObject o in offsetMinus) { if (o != cutLine2) o.Dispose(); }"""

new_section = """                    // Per-segment offset: each segment uses its own perpendicular direction
                    double halfGap = 0.05;
                    var extents = pl.GeometricExtents;
                    double dx = extents.MaxPoint.X - extents.MinPoint.X;
                    double dy = extents.MaxPoint.Y - extents.MinPoint.Y;
                    double diag = Math.Sqrt(dx * dx + dy * dy) * 2.0;

                    // Use first segment direction for consistent +/- side classification
                    Point3d firstPt = cutPts[0];
                    Point3d secondPt = cutPts[1];
                    Vector3d firstDir = (secondPt - firstPt).GetNormal();
                    Vector3d refPerp = new Vector3d(-firstDir.Y, firstDir.X, 0);
                    // Determine side: +halfGap along refPerp is "line1", -halfGap is "line2"
                    Point3d line1RefP1 = firstPt + refPerp * halfGap;
                    Point3d line1RefP2 = firstPt + refPerp * halfGap + firstDir;
                    Point3d line2RefP1 = firstPt - refPerp * halfGap;
                    Point3d line2RefP2 = firstPt - refPerp * halfGap + firstDir;

                    var allHits = new List<(Point3d Pt, double Param, int LineId)>();
                    int nVerts = pl.NumberOfVertices;

                    // For each cut segment, offset using its own perpendicular
                    for (int si = 0; si < cutPts.Count - 1; si++)
                    {
                        Point3d s = cutPts[si];
                        Point3d e = cutPts[si + 1];
                        Vector3d segDir = (e - s);
                        if (segDir.Length < 0.01) continue;
                        segDir = segDir.GetNormal();
                        Vector3d segPerp = new Vector3d(-segDir.Y, segDir.X, 0);

                        // Determine which offset direction (line1 or line2) this segment contributes to
                        // Dot product with refPerp: if positive -> same side as refPerp -> line1 offset
                        double dot = segPerp.X * refPerp.X + segPerp.Y * refPerp.Y;
                        int lineId = (dot >= 0) ? 1 : 2;
                        Vector3d offsetDir = (dot >= 0) ? segPerp : -segPerp;

                        // Extend segment beyond target
                        Point3d extS = s - segDir * diag;
                        Point3d extE = e + segDir * diag;

                        using (var cutLine = new Line(extS + offsetDir * halfGap, extE + offsetDir * halfGap))
                        {
                            for (int i = 0; i < nVerts; i++)
                            {
                                Point3d vi = pl.GetPoint3dAt(i);
                                Point3d vj = pl.GetPoint3dAt((i + 1) % nVerts);
                                if (vi.DistanceTo(vj) < 1e-9) continue;

                                var pts = new Point3dCollection();
                                using (var edge = new Line(vi, vj))
                                {
                                    cutLine.IntersectWith(edge, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);
                                    foreach (Point3d p in pts)
                                    {
                                        Point3d onCurve = pl.GetClosestPointTo(p, false);
                                        allHits.Add((onCurve, pl.GetParameterAtPoint(onCurve), lineId));
                                    }
                                }
                            }
                        }
                    }"""

content = content.replace(old_section, new_section)
with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "w", encoding="utf-8-sig") as f:
    f.write(content)
print("Replaced: per-segment offset with dot-product side classification")
