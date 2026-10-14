with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "r", encoding="utf-8-sig") as f:
    content = f.read()

# Add debug output before creating polylines
old_dbg = '                    if (l1Verts.Count < 2 || l2Verts.Count < 2)'
new_dbg = '                    ed.WriteMessage(String.Format("\\nHPLIT debug: L1={0}v L2={1}v splitCurves={2} hits={3}", l1Verts.Count, l2Verts.Count, splitCurves.Count, uniqueHits.Count));\n                    if (l1Verts.Count < 2 || l2Verts.Count < 2)'

content = content.replace(old_dbg, new_dbg)
with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "w", encoding="utf-8-sig") as f:
    f.write(content)
print("Added debug output")
