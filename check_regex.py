# -*- coding: utf-8 -*-
from pathlib import Path
text = Path(r"C:\Users\allen\Documents\Hexfill 2\Commands\HtcCommand.cs").read_text(encoding="utf-8")
for line in text.splitlines():
    if "Regex.Match(contents" in line:
        print(repr(line))
