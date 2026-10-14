# -*- coding: utf-8 -*-
import subprocess
from pathlib import Path

def get_depth(text):
    depth = 0
    depths = []
    for line in text.splitlines():
        depth += line.count("{") - line.count("}")
        depths.append(depth)
    return depths

cur = Path(r"C:\Users\allen\Documents\Hexfill 2\Commands\HtcCommand.cs").read_text(encoding="utf-8")
old = subprocess.run(["git", "-C", r"C:\Users\allen\Documents\Hexfill 2",
                      "show", "dcd2285:Commands/HtcCommand.cs"],
                     capture_output=True, text=True, encoding="utf-8").stdout
dc, do = get_depth(cur), get_depth(old)
print("当前版本行数:", len(dc), "最终depth:", dc[-1])
print("dcd2285 行数:", len(do), "最终depth:", do[-1])
# 找第一个分歧点
n = min(len(dc), len(do))
for i in range(n):
    if dc[i] != do[i]:
        print("首个分歧: 行", i + 1, "当前depth=", dc[i], "旧depth=", do[i])
        for j in range(max(0, i - 4), min(n, i + 6)):
            print("  ", j + 1, "cur=", cur.splitlines()[j][:70])
        break
