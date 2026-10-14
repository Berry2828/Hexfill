with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "r", encoding="utf-8-sig") as f:
    content = f.read()

# Add debug output to a temp file
old = "                    if (l1Verts.Count < 2 || l2Verts.Count < 2)"
new = """                    // DEBUG: write counts
                    try {
                        var dbgPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hplit_debug.txt");
                        System.IO.File.WriteAllText(dbgPath, string.Format("l1={0} l2={1} sc={2} uh={3}", l1Verts.Count, l2Verts.Count, splitCurves.Count, uniqueHits.Count));
                    } catch {}
                    if (l1Verts.Count < 2 || l2Verts.Count < 2)"""

content = content.replace(old, new)
with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "w", encoding="utf-8-sig") as f:
    f.write(content)
print("Added debug file output")
