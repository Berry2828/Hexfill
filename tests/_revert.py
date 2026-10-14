with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "r", encoding="utf-8-sig") as f:
    content = f.read()

# Find the entire per-segment offset section and replace with simple overall direction
old_start = content.find("// Per-segment offset")
old_end = content.find("// Build lineId lookup", old_start)

# Replace with simple overall direction approach
new_code = """                    // Use overall direction from first to last cut point for consistent offset
                    double halfGap = 0.05;
                    var extents = pl.GeometricExtents;
                    double dx = extents.MaxPoint.X - extents.MinPoint.X;
                    double dy = extents.MaxPoint.Y - extents.MinPoint.Y;
                    double diag = Math.Sqrt(dx * dx + dy * dy) * 2.0;

                    Point3d firstPt = cutPts[0];
                    Point3d lastPt = cutPts[cutPts.Count - 1];
                    Vector3d overallDir = (lastPt - firstPt);
                    if (overallDir.Length < 0.01)
                        overallDir = (cutPts[1] - firstPt);
                    overallDir = overallDir.GetNormal();
                    Vector3d perp = new Vector3d(-overallDir.Y, overallDir.X, 0);

                    // Two parallel offset lines extending across entire target
                    Point3d line1P1 = firstPt + perp * halfGap - overallDir * diag;
                    Point3d line1P2 = firstPt + perp * halfGap + overallDir * diag;
                    Point3d line2P1 = firstPt - perp * halfGap - overallDir * diag;
                    Point3d line2P2 = firstPt - perp * halfGap + overallDir * diag;

                    var allHits = new List<(Point3d Pt, double Param, int LineId)>();
                    int nVerts = pl.NumberOfVertices;

                    using (var cutLine1 = new Line(line1P1, line1P2))
                    using (var cutLine2 = new Line(line2P1, line2P2))
                    {
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
                    }

"""

content = content[:old_start] + new_code + content[old_end:]

# Also remove the debug write
content = content.replace("""                    // DEBUG: write counts
                    try {
                        var dbgPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hplit_debug.txt");
                        System.IO.File.WriteAllText(dbgPath, string.Format("l1={0} l2={1} sc={2} uh={3}", l1Verts.Count, l2Verts.Count, splitCurves.Count, uniqueHits.Count));
                    } catch {}
                    """, "")

with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "w", encoding="utf-8-sig") as f:
    f.write(content)
print("Reverted to simple overall-direction offset (like original working code)")
