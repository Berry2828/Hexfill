# -*- coding: utf-8 -*-
"""移除第1遍样式切换(不再改原文字外观); 渲染内部强制雅黑"""
from pathlib import Path

f = Path(r"C:\Users\allen\Documents\Hexfill 2\Commands\HtcCommand.cs")
text = f.read_text(encoding="utf-8")

# 1. 删除第 1 遍事务块 + Regen
start_marker = "                // ===== 第 1 遍事务：统一字体（非雅黑一律切换为独立雅黑样式）并提交，"
end_marker = "                // ===== 第 2 遍事务：全新打开实体读取最终字体下的几何数据，"
si = text.index(start_marker)
ei = text.index(end_marker)
text = text[:si] + text[ei:]
print("第 1 遍事务已删除")

# 2. 渲染字体强制雅黑 (不改实体)
old2 = r'''                        // 文字实际显示范围（字体已在第 1 遍事务统一并提交，此处为最终字体下的真实范围）
                        Extents3d ext = ent.GeometricExtents;'''
new2 = r'''                        // 转换轮廓统一按微软雅黑 (用户要求), 但不修改原文字的样式与外观
                        fontFileName = "msyh.ttc";
                        bigFontFileName = null;
                        typeFaceName = "Microsoft YaHei";

                        // 文字实际显示范围 (原文字未做任何改动, 范围即原显示)
                        Extents3d ext = ent.GeometricExtents;'''
assert old2 in text, "old2 not found"
text = text.replace(old2, new2, 1)

f.write_text(text, encoding="utf-8")
print("渲染字体已强制雅黑, 原文字不再被改动")
