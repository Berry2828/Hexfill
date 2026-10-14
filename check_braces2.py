# -*- coding: utf-8 -*-
from pathlib import Path
text = Path(r"C:\Users\allen\Documents\Hexfill 2\Commands\HtcCommand.cs").read_text(encoding="utf-8")
depth = 0
lines = text.splitlines()
for i, line in enumerate(lines, 1):
    opens = line.count("{")
    closes = line.count("}")
    depth += opens - closes
    if i <= 60 and (opens or closes):
        print(i, "depth=", depth, "|", line.strip()[:70])
print("...")
# 只打印 depth 变负或最终
print("最终 depth =", depth)
# 找 depth 峰值位置
depth = 0; peak = 0; peakline = 0
for i, line in enumerate(lines, 1):
    depth += line.count("{") - line.count("}")
    if depth > peak:
        peak = depth; peakline = i
print("峰值 depth", peak, "在行", peakline)
