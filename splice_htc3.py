# -*- coding: utf-8 -*-
"""MTEXT 分支: 高度=字高而非文字框高; 墨迹顶对齐文字框顶"""
from pathlib import Path

f = Path(r"C:\Users\allen\Documents\Hexfill 2\Commands\HtcCommand.cs")
text = f.read_text(encoding="utf-8")

old = """                    else if (isMText && textHeight > 1e-6)
                    {
                        // MTEXT: 高度拟合显示框, 锚在显示框左下
                        scale = extH / gdH;
                        anchorX = ext.MinPoint.X;
                        anchorY = ext.MinPoint.Y;
                    }
"""
new = """                    else if (isMText && textHeight > 1e-6)
                    {
                        // MTEXT: 高度 = 字高 (文字框含行距留白, 不能作基准);
                        // 墨迹顶部对齐文字框顶部, 左缘对齐文字框左缘 (TopLeft 附着)。
                        scale = textHeight / gdH;
                        anchorX = ext.MinPoint.X;
                        anchorY = ext.MaxPoint.Y - gdH * scale;
                    }
"""
assert old in text, "MTEXT branch not found"
text = text.replace(old, new, 1)
f.write_text(text, encoding="utf-8")
print("MTEXT 映射已修正: 高度=字高, 顶部对齐文字框顶")
