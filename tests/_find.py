with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "r", encoding="utf-8-sig") as f:
    lines = f.readlines()

start = end = None
for i, ln in enumerate(lines):
    if 'CommandMethod("HPLIT")' in ln:
        start = i
    if start and "private static bool PointOnLine" in ln:
        end = i
        break

if start and end:
    for j in range(end-1, start, -1):
        if lines[j].strip() == "}":
            end = j + 1
            break
    print(f"HPLIT: lines {start+1}-{end} ({end-start} lines)")
else:
    print(f"NOT FOUND: start={start}, end={end}")
