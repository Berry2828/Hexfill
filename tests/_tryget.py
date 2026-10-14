with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "r", encoding="utf-8-sig") as f:
    content = f.read()

# Fix GetValueOrDefault -> TryGetValue pattern
content = content.replace(
    "int sId = ptToLineId.GetValueOrDefault(sKey, -1);",
    "int sId; if (!ptToLineId.TryGetValue(sKey, out sId)) sId = -1;"
)
content = content.replace(
    "int eId = ptToLineId.GetValueOrDefault(eKey, -1);",
    "int eId; if (!ptToLineId.TryGetValue(eKey, out eId)) eId = -1;"
)

with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "w", encoding="utf-8-sig") as f:
    f.write(content)
print("Fixed GetValueOrDefault -> TryGetValue")
