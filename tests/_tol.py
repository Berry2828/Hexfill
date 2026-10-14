with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "r", encoding="utf-8-sig") as f:
    content = f.read()

# Increase tolerance from 0.01 to 0.2 (gap is 0.1mm, so 0.2mm tolerance is safe and 10x more relaxed)
old_tol = "PointOnLine(s, line1RefP1, line1RefP2, 0.01)"
new_tol = "PointOnLine(s, line1RefP1, line1RefP2, 0.2)"
content = content.replace(old_tol, new_tol)

old_tol2 = "PointOnLine(e, line1RefP1, line1RefP2, 0.01)"
new_tol2 = "PointOnLine(e, line1RefP1, line1RefP2, 0.2)"
content = content.replace(old_tol2, new_tol2)

old_tol3 = "PointOnLine(s, line2RefP1, line2RefP2, 0.01)"
new_tol3 = "PointOnLine(s, line2RefP1, line2RefP2, 0.2)"
content = content.replace(old_tol3, new_tol3)

old_tol4 = "PointOnLine(e, line2RefP1, line2RefP2, 0.01)"
new_tol4 = "PointOnLine(e, line2RefP1, line2RefP2, 0.2)"
content = content.replace(old_tol4, new_tol4)

with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "w", encoding="utf-8-sig") as f:
    f.write(content)
print("Tolerance: 0.01 -> 0.2")
