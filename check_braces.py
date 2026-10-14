# -*- coding: utf-8 -*-
from pathlib import Path
text = Path(r"C:\Users\allen\Documents\Hexfill 2\Commands\HtcCommand.cs").read_text(encoding="utf-8")
depth = 0
lines = text.splitlines()
for i, line in enumerate(lines, 1):
    opens = line.count("{")
    closes = line.count("}")
    depth += opens - closes
    if depth != 0 and i > 680 and i < 1150:
        if opens or closes:
            print(i, "depth=", depth, "|", line.strip()[:80])
