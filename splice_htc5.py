# -*- coding: utf-8 -*-
"""修复正则转义: 匹配字面 \\W/\\H 需要双反斜杠"""
from pathlib import Path

f = Path(r"C:\Users\allen\Documents\Hexfill 2\Commands\HtcCommand.cs")
text = f.read_text(encoding="utf-8")

pairs = [
    (r'Regex.Match(contents, @"W([0-9.]+);")', r'Regex.Match(contents, @"\W([0-9.]+);")'),
    (r'Regex.Match(contents, @"H([0-9.]+)x;")', r'Regex.Match(contents, @"\H([0-9.]+)x;")'),
    (r'Regex.Match(contents, @"H([0-9.]+);")', r'Regex.Match(contents, @"\H([0-9.]+);")'),
]
for old, new in pairs:
    assert old in text, "not found: " + old
    text = text.replace(old, new, 1)
f.write_text(text, encoding="utf-8")
print("正则转义已修复 (\\W / \\H)")
