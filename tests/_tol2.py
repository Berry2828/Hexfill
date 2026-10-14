with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "r", encoding="utf-8-sig") as f:
    content = f.read()

# 0.2 -> 0.05 (half the 0.1mm gap)
content = content.replace("PointOnLine(s, line1RefP1, line1RefP2, 0.2)", "PointOnLine(s, line1RefP1, line1RefP2, 0.05)")
content = content.replace("PointOnLine(e, line1RefP1, line1RefP2, 0.2)", "PointOnLine(e, line1RefP1, line1RefP2, 0.05)")
content = content.replace("PointOnLine(s, line2RefP1, line2RefP2, 0.2)", "PointOnLine(s, line2RefP1, line2RefP2, 0.05)")
content = content.replace("PointOnLine(e, line2RefP1, line2RefP2, 0.2)", "PointOnLine(e, line2RefP1, line2RefP2, 0.05)")

# Also remove debug line
content = content.replace('ed.WriteMessage(String.Format("\\nHPLIT debug: L1={0}v L2={1}v splitCurves={2} hits={3}", l1Verts.Count, l2Verts.Count, splitCurves.Count, uniqueHits.Count));\n                    ', '')

with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "w", encoding="utf-8-sig") as f:
    f.write(content)
print("Tol: 0.05, debug removed")
