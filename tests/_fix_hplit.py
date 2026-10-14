with open(r'C:\Users\allen\Documents\Hexfill 2\HexFill.cs', 'r', encoding='utf-8-sig') as f:
    lines = f.readlines()

missing = [
    '\n',
    '                    var uniqueHits = new List<(Point3d Pt, double Param, int LineId)>();\n',
    '                    foreach (var hit in allHits)\n',
    '                    {\n',
    '                        bool dup = false;\n',
    '                        foreach (var uh in uniqueHits)\n',
    '                            if (hit.Pt.DistanceTo(uh.Pt) < 0.001) { dup = true; break; }\n',
    '                        if (!dup) uniqueHits.Add(hit);\n',
    '                    }\n',
    '\n',
    '                    int l1c = uniqueHits.Count(h => h.LineId == 1);\n',
    '                    int l2c = uniqueHits.Count(h => h.LineId == 2);\n',
    '                    if (l1c < 2 || l2c < 2)\n',
    '                    {\n',
    '                        ed.WriteMessage(String.Format("\\nError: not enough intersections (L1:{0}, L2:{1})", l1c, l2c));\n',
    '                        return;\n',
    '                    }\n',
    '\n',
    '                    uniqueHits.Sort((a, b) => a.Param.CompareTo(b.Param));\n',
    '\n',
    '                    var splitPts = new Point3dCollection();\n',
    '                    foreach (var hit in uniqueHits) splitPts.Add(hit.Pt);\n',
    '                    var splitCurves = pl.GetSplitCurves(splitPts);\n',
    '\n',
    '                    var l1Verts = new List<Point3d>();\n',
    '                    var l2Verts = new List<Point3d>();\n',
    '\n',
    '                    for (int i = 0; i < splitCurves.Count; i++)\n',
    '                    {\n',
    '                        var seg = splitCurves[i] as Curve;\n',
    '                        if (seg == null) continue;\n',
    '\n',
    '                        Point3d s = seg.StartPoint;\n',
    '                        Point3d e = seg.EndPoint;\n',
    '                        bool sOn1 = PointOnLine(s, line1RefP1, line1RefP2, 0.01);\n',
    '                        bool eOn1 = PointOnLine(e, line1RefP1, line1RefP2, 0.01);\n',
    '                        bool sOn2 = PointOnLine(s, line2RefP1, line2RefP2, 0.01);\n',
    '                        bool eOn2 = PointOnLine(e, line2RefP1, line2RefP2, 0.01);\n',
    '\n',
    '                        if (sOn1 && eOn1)\n',
    '                            CollectSegVerts(seg, l1Verts);\n',
    '                        else if (sOn2 && eOn2)\n',
    '                            CollectSegVerts(seg, l2Verts);\n',
    '                    }\n',
    '\n',
    '                    foreach (Curve c in splitCurves) c.Dispose();\n',
    '\n',
]

insert_after = None
for i, ln in enumerate(lines):
    if 'foreach (DBObject o in offsetMinus)' in ln:
        insert_after = i + 1
        break

if insert_after:
    for j, code in enumerate(missing):
        lines.insert(insert_after + j, code)
    print(f'Inserted {len(missing)} lines after line {insert_after+1}')

# Change halfGap from 0.5 to 0.05
for i, ln in enumerate(lines):
    if ln.strip() == 'double halfGap = 0.5;':
        lines[i] = '                    double halfGap = 0.05;\n'
        print(f'Changed halfGap to 0.05 at line {i+1}')
        break

with open(r'C:\Users\allen\Documents\Hexfill 2\HexFill.cs', 'w', encoding='utf-8-sig') as f:
    f.writelines(lines)

print('Done')
