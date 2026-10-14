# -*- coding: utf-8 -*-
"""最终映射: 墨迹1:1 (scale=字高/1000), 锚文字位置; MTEXT 顶对齐框顶左对齐框左"""
from pathlib import Path

f = Path(r"C:\Users\allen\Documents\Hexfill 2\Commands\HtcCommand.cs")
text = f.read_text(encoding="utf-8")

# 1. 替换盒拟合块为墨迹1:1
start_marker = "                    double scale = 0.0;"
end_marker = '                    ed.WriteMessage(string.Format(\n                        "\\n[HTC调试] 字高='
si = text.index(start_marker)
ei = text.index(end_marker)

new_block = '''                    double scale = 0.0;
                    double scaleX = 0.0, scaleY = 0.0;
                    double anchorX = 0.0;
                    double anchorY = 0.0;
                    bool useBaselineMapping = true;
                    double baselineGDI = pathBaselineOffset;

                    // ===== 墨迹 1:1 映射 (最终方案) =====
                    // 圆与可见文字字形严格 1:1: GDI emSize=1000 对应字高;
                    // 锚在文字自身位置(DBText=插入点, MText=框顶左缘);
                    // 不以文字框(含行距留白/框宽余量)为基准, 不修改原文字。
                    scale = textHeight / 1000.0;
                    if (!isMText)
                    {
                        double offXEm = 0.0, offYEm = 0.0;
                        bool isDefaultAlign = hMode == TextHorizontalMode.TextLeft &&
                                              vMode == TextVerticalMode.TextBase;
                        Point3d pivot = isDefaultAlign ? position : alignmentPoint;
                        if (!isDefaultAlign)
                        {
                            switch (hMode)
                            {
                                case TextHorizontalMode.TextCenter: offXEm = -gdW / 2.0; break;
                                case TextHorizontalMode.TextRight: offXEm = -gdW; break;
                            }
                            switch (vMode)
                            {
                                case TextVerticalMode.TextVerticalMid: offYEm = -gdH / 2.0; break;
                                case TextVerticalMode.TextTop: offYEm = -gdH; break;
                            }
                        }
                        anchorX = pivot.X + (offXEm * cosR - offYEm * sinR) * scale;
                        anchorY = pivot.Y + (offXEm * sinR + offYEm * cosR) * scale;
                    }
                    else
                    {
                        // MTEXT (TopLeft): 墨迹顶对齐文字框顶, 左缘对齐文字框左缘
                        anchorX = ext.MinPoint.X;
                        anchorY = ext.MaxPoint.Y - (baselineGDI - minY) * scale;
                    }

'''
text = text[:si] + new_block + text[ei:]

# 2. 点映射改回 墨迹1:1
old2 = r'''                                    if (useBaselineMapping)
                                    {
                                        // 墨迹左下角锚定显示框左下角; 宽高分别拟合显示框; 旋转时绕墨迹角旋转
                                        double lx = (gpts[j].X - minX) * scaleX;
                                        double ly = (maxY - gpts[j].Y) * scaleY;
                                        double rx = lx * cosR - ly * sinR;
                                        double ry = lx * sinR + ly * cosR;
                                        outline.Add(new Point2d(anchorX + rx, anchorY + ry));
                                    }
'''
new2 = r'''                                    if (useBaselineMapping)
                                    {
                                        // 墨迹 1:1: GDI 基线为原点(基线GDI), 上翻到 CAD 坐标, 绕锚点旋转
                                        double lx = gpts[j].X * scale;
                                        double ly = (baselineGDI - gpts[j].Y) * scale;
                                        double rx = lx * cosR - ly * sinR;
                                        double ry = lx * sinR + ly * cosR;
                                        outline.Add(new Point2d(anchorX + rx, anchorY + ry));
                                    }
'''
assert old2 in text, "old2 not found"
text = text.replace(old2, new2, 1)

f.write_text(text, encoding="utf-8")
print("墨迹1:1映射已恢复 (原文字不改动)")
