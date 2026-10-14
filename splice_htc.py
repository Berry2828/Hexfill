# -*- coding: utf-8 -*-
"""用两段事务版 HTC 方法替换 HtcCommand.cs 中的旧方法"""
from pathlib import Path

f = Path(r"C:\Users\allen\Documents\Hexfill 2\Commands\HtcCommand.cs")
text = f.read_text(encoding="utf-8")
new_method = Path(r"C:\Users\allen\Documents\Hexfill 2\htc_method_new.txt").read_text(encoding="utf-8")

start_marker = "        public void HTC()"
end_marker = "        /// <summary>\n        /// 解析 MTEXT 内容"
si = text.index(start_marker)
ei = text.index(end_marker)
text = text[:si] + new_method + "\n\n" + text[ei:]
f.write_text(text, encoding="utf-8")
print("已替换: 原方法从偏移 %d 到 %d" % (si, ei))
