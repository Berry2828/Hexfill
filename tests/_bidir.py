with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "r", encoding="utf-8-sig") as f:
    content = f.read()

# Fix: create BOTH offset directions per segment
old = '''                        // Determine which offset direction (line1 or line2) this segment contributes to
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
                        }'''

new = '''                        // Both sides: +halfGap and -halfGap along this segment's perpendicular
                        Point3d extS = s - segDir * diag;
                        Point3d extE = e + segDir * diag;

                        // Determine which side is "1" vs "2" based on dot with reference perpendicular
                        double dot = segPerp.X * refPerp.X + segPerp.Y * refPerp.Y;
                        int idPlus = (dot >= 0) ? 1 : 2;
                        int idMinus = (dot >= 0) ? 2 : 1;

                        // Line on +side
                        using (var cutLPlus = new Line(extS + segPerp * halfGap, extE + segPerp * halfGap))
                        {
                            AddIntersections(pl, cutLPlus, idPlus, allHits);
                        }
                        // Line on -side
                        using (var cutLMinus = new Line(extS - segPerp * halfGap, extE - segPerp * halfGap))
                        {
                            AddIntersections(pl, cutLMinus, idMinus, allHits);
                        }'''

content = content.replace(old, new)

# Also add the helper method after CollectSegVerts
helper = '''
        private static void AddIntersections(Polyline pl, Line cutLine, int lineId, List<(Point3d Pt, double Param, int LineId)> hits)
        {
            int nVerts = pl.NumberOfVertices;
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
                        hits.Add((onCurve, pl.GetParameterAtPoint(onCurve), lineId));
                    }
                }
            }
        }
'''

# Insert before CollectSegVerts
if "private static void CollectSegVerts" in content:
    content = content.replace("private static void CollectSegVerts", helper + "\n        private static void CollectSegVerts")

with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "w", encoding="utf-8-sig") as f:
    f.write(content)

print("Fixed: bidirectional offset + AddIntersections helper")
