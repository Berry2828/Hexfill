with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "r", encoding="utf-8-sig") as f:
    content = f.read()

# Find where the dedup code should begin
dedup_start = content.find("// Build lineId lookup")
if dedup_start < 0:
    # Try to find where the old classification is
    dedup_start = content.find("var uniqueHits = new List")

# Find the end of the HPLIT method (before "catch")
catch_start = content.find("catch (System.Exception ex)", dedup_start)

# The dedup/split/classify code should be inserted here
insert_point = content.rfind("}\n", 0, catch_start)
# Find the closing brace of the transaction
insert_point = content.rfind("                    }\n", 0, catch_start) + len("                    }\n")

# Missing code
missing = """
                    var uniqueHits = new List<(Point3d Pt, double Param, int LineId)>();
                    foreach (var hit in allHits)
                    {
                        bool dup = false;
                        foreach (var uh in uniqueHits)
                            if (hit.Pt.DistanceTo(uh.Pt) < 0.001) { dup = true; break; }
                        if (!dup) uniqueHits.Add(hit);
                    }

                    int l1c = uniqueHits.Count(h => h.LineId == 1);
                    int l2c = uniqueHits.Count(h => h.LineId == 2);
                    if (l1c < 2 || l2c < 2)
                    {
                        ed.WriteMessage(String.Format("\\nError: not enough intersections (L1:{0}, L2:{1})", l1c, l2c));
                        return;
                    }

                    uniqueHits.Sort((a, b) => a.Param.CompareTo(b.Param));

                    var splitPts = new Point3dCollection();
                    foreach (var hit in uniqueHits) splitPts.Add(hit.Pt);
                    var splitCurves = pl.GetSplitCurves(splitPts);

                    // Split points are in same order as uniqueHits
                    var hitLineIds = new int[uniqueHits.Count];
                    for (int i = 0; i < uniqueHits.Count; i++)
                        hitLineIds[i] = uniqueHits[i].LineId;

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

                    foreach (Curve c in splitCurves) c.Dispose();

"""

content = content[:insert_point] + missing + content[insert_point:]

with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "w", encoding="utf-8-sig") as f:
    f.write(content)
print(f"Inserted dedup+split+classify at position {insert_point}")
