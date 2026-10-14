# -*- coding: utf-8 -*-
"""HTC 映射改为: 高度=字高精确拟合 + 插入点锚定 (不依赖 GeometricExtents 中心)"""
from pathlib import Path

f = Path(r"C:\Users\allen\Documents\Hexfill 2\Commands\HtcCommand.cs")
text = f.read_text(encoding="utf-8")

# ---- Region A: 替换整个映射计算块 ----
start_marker = "                    double scale = 0.0;"
end_marker = '                    ed.WriteMessage(string.Format('
si = text.index(start_marker)
ei = text.index(end_marker, si)

new_mapping = '''                    double scale = 0.0;
                    double anchorX = 0.0;
                    double anchorY = 0.0;
                    bool useBaselineMapping = true;

                    // ===== 统一映射 (2026-08-19 用户要求) =====
                    // 轮廓高度 = 字高 (精确一致), 宽度等比不变形;
                    // 锚点 = 文字插入点 / 显示框左下, 与文字位置严格一致。
                    if (!isMText && textHeight > 1e-6)
                    {
                        scale = textHeight / gdH;
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
                    else if (isMText && textHeight > 1e-6)
                    {
                        // MTEXT: 高度拟合显示框, 锚在显示框左下
                        scale = extH / gdH;
                        anchorX = ext.MinPoint.X;
                        anchorY = ext.MinPoint.Y;
                    }
                    else
                    {
                        // 异常兜底: 显示框左下锚定
                        scale = textHeight > 1e-6 ? textHeight / gdH : extH / gdH;
                        anchorX = ext.MinPoint.X;
                        anchorY = ext.MinPoint.Y;
                    }

'''
text = text[:si] + new_mapping + text[ei:]

# ---- Region B: 替换点映射分支 (墨迹左下角锚定) ----
old_branch = '''                                    if (useBaselineMapping)
                                    {
                                        // DBText 1:1：局部坐标（基线起点为原点，Y 上正）→ 旋转 → 平移锚点
                                        double lx = gpts[j].X * scale;
                                        double ly = (baselineGDI - gpts[j].Y) * scale;
                                        double rx = lx * cosR - ly * sinR;
                                        double ry = lx * sinR + ly * cosR;
                                        outline.Add(new Point2d(anchorX + rx, anchorY + ry));
                                    }
'''
new_branch = '''                                    if (useBaselineMapping)
                                    {
                                        // 墨迹左下角 (minX, maxY) 锚定到锚点; Y 上翻; 再绕锚点旋转
                                        double lx = (gpts[j].X - minX) * scale;
                                        double ly = (maxY - gpts[j].Y) * scale;
                                        double rx = lx * cosR - ly * sinR;
                                        double ry = lx * sinR + ly * cosR;
                                        outline.Add(new Point2d(anchorX + rx, anchorY + ry));
                                    }
'''
assert old_branch in text, "point mapping branch not found"
text = text.replace(old_branch, new_branch, 1)

f.write_text(text, encoding="utf-8")
print("HTC 映射已替换为 插入点锚定 + 高度精确拟合")
