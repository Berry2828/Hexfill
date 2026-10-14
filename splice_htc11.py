# -*- coding: utf-8 -*-
"""宽度基准改用 AutoCAD 自测的 MText.ActualWidth; 高度=字高"""
from pathlib import Path

f = Path(r"C:\Users\allen\Documents\Hexfill 2\Commands\HtcCommand.cs")
text = f.read_text(encoding="utf-8")

# 1. 映射块: scaleX 用目标宽度, scaleY 用字高
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

                    // ===== 映射 (2026-08-19 终版) =====
                    // 高度: 精确 = 字高 (scaleY = 字高/墨迹高)
                    // 宽度: MTEXT 用 AutoCAD 自测的实际显示宽度 (ActualWidth), 精确对齐;
                    //       DBText 与高度等比。
                    scaleY = textHeight / gdH;
                    if (isMText && targetWidthMM > 1e-6)
                        scaleX = targetWidthMM / gdW;
                    else
                        scaleX = scaleY;
                    scale = scaleY;
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
                        anchorY = ext.MaxPoint.Y - (baselineGDI - minY) * scaleY;
                    }

'''
text = text[:si] + new_block + text[ei:]

# 2. 点映射: 分轴缩放
old2 = r'''                                    if (useBaselineMapping)
                                    {
                                        // 墨迹 1:1: GDI 基线为原点(基线GDI), 上翻到 CAD 坐标, 绕锚点旋转
                                        double lx = gpts[j].X * scale;
                                        double ly = (baselineGDI - gpts[j].Y) * scale;
                                        double rx = lx * cosR - ly * sinR;
                                        double ry = lx * sinR + ly * cosR;
                                        outline.Add(new Point2d(anchorX + rx, anchorY + ry));
                                    }
'''
new2 = r'''                                    if (useBaselineMapping)
                                    {
                                        // 分轴缩放: X 用 scaleX (宽度对齐), Y 用 scaleY (高度=字高); 绕锚点旋转
                                        double lx = gpts[j].X * scaleX;
                                        double ly = (baselineGDI - gpts[j].Y) * scaleY;
                                        double rx = lx * cosR - ly * sinR;
                                        double ry = lx * sinR + ly * cosR;
                                        outline.Add(new Point2d(anchorX + rx, anchorY + ry));
                                    }
'''
assert old2 in text, "old2 not found"
text = text.replace(old2, new2, 1)

# 3. 签名加参数
old3 = r'''            Point3d position, Point3d alignmentPoint, double fitDistance)'''
new3 = r'''            Point3d position, Point3d alignmentPoint, double fitDistance, double targetWidthMM)'''
assert old3 in text, "old3 not found"
text = text.replace(old3, new3, 1)

# 4. 调用点: 计算 targetWidthMM 并传入
old4 = r'''                        // 统一按微软雅黑渲染字形轮廓
                        List<Polyline> outlines = BuildTextOutlines(
                            textString, ext, rotation, fontFileName, bigFontFileName,
                            typeFaceName, fontBold, fontItalic, isMText, textHeight,
                            lineSpacingFactor, widthFactor, oblique, attachment,
                            hMode, vMode, position, alignmentPoint, fitDistance);
'''
new4 = r'''                        // MTEXT 实际显示宽度 (AutoCAD 自测, 与可见文字严格一致)
                        double targetWidthMM = -1.0;
                        if (isMText)
                        {
                            try { targetWidthMM = ((MText)ent).ActualWidth; }
                            catch { targetWidthMM = -1.0; }
                        }
                        ed.WriteMessage("\\n[HTC调试] 实际显示宽度=" + (targetWidthMM > 0 ? targetWidthMM.ToString("F3") : "N/A"));

                        // 统一按微软雅黑渲染字形轮廓
                        List<Polyline> outlines = BuildTextOutlines(
                            textString, ext, rotation, fontFileName, bigFontFileName,
                            typeFaceName, fontBold, fontItalic, isMText, textHeight,
                            lineSpacingFactor, widthFactor, oblique, attachment,
                            hMode, vMode, position, alignmentPoint, fitDistance, targetWidthMM);
'''
assert old4 in text, "old4 not found"
text = text.replace(old4, new4, 1)

f.write_text(text, encoding="utf-8")
print("ActualWidth 对齐已加入")
