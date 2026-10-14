# -*- coding: utf-8 -*-
from pathlib import Path

f = Path(r"C:\Users\allen\Documents\Hexfill 2\Commands\HtcCommand.cs")
text = f.read_text(encoding="utf-8")

# ---- 1. 替换整个映射块为逐维盒拟合 ----
si = text.index("                    double scale = 0.0;")
ei = text.index('                    ed.WriteMessage(string.Format(')
new_block = r'''                    double scale = 0.0;
                    double scaleX = 0.0, scaleY = 0.0;
                    double anchorX = 0.0;
                    double anchorY = 0.0;
                    bool useBaselineMapping = true;

                    // ===== 盒映射 (最终方案 2026-08-19) =====
                    // 圆轮廓与文字显示范围(GeometricExtents)严格一致:
                    // 宽度/高度分别拟合显示框, 墨迹左下角锚定显示框左下角(不居中、不再偏移)。
                    {
                        double extW = ext.MaxPoint.X - ext.MinPoint.X;
                        double extH = ext.MaxPoint.Y - ext.MinPoint.Y;
                        double rotW = gdW * Math.Abs(cosR) + gdH * Math.Abs(sinR);
                        double rotH = gdW * Math.Abs(sinR) + gdH * Math.Abs(cosR);
                        if (rotW < 1e-6 || rotH < 1e-6) return outlines;
                        scaleX = extW / rotW;
                        scaleY = extH / rotH;
                        scale = (scaleX + scaleY) / 2.0;
                        anchorX = ext.MinPoint.X;
                        anchorY = ext.MinPoint.Y;
                    }

'''
text = text[:si] + new_block + text[ei:]

# ---- 2. 替换点映射分支 ----
old2 = r'''                                    if (useBaselineMapping)
                                    {
                                        // 墨迹左下角 (minX, maxY) 锚定到锚点; Y 上翻; Fit/Align 拉伸; 再绕锚点旋转
                                        double lx = (gpts[j].X - minX) * scale * stretchX;
                                        double ly = (maxY - gpts[j].Y) * scale * stretchY;
                                        double rx = lx * cosR - ly * sinR;
                                        double ry = lx * sinR + ly * cosR;
                                        outline.Add(new Point2d(anchorX + rx, anchorY + ry));
                                    }
'''
new2 = r'''                                    if (useBaselineMapping)
                                    {
                                        // 墨迹左下角锚定显示框左下角; 宽高分别拟合显示框; 旋转时绕墨迹角旋转
                                        double lx = (gpts[j].X - minX) * scaleX;
                                        double ly = (maxY - gpts[j].Y) * scaleY;
                                        double rx = lx * cosR - ly * sinR;
                                        double ry = lx * sinR + ly * cosR;
                                        outline.Add(new Point2d(anchorX + rx, anchorY + ry));
                                    }
'''
assert old2 in text, "old2 not found"
text = text.replace(old2, new2, 1)

# ---- 3. 对象诊断输出 (插在调试轮廓数之前) ----
old3 = r'''                        // 统一按微软雅黑渲染字形轮廓
                        List<Polyline> outlines = BuildTextOutlines('''
new3 = r'''                        // 对象诊断
                        ed.WriteMessage("\\n[HTC调试] 对象类型=" + ent.GetType().Name);
                        if (ent is MText mtDbg)
                        {
                            ed.WriteMessage("\\n[HTC调试] MText 框宽=" + mtDbg.Width + " 附着=" + mtDbg.Attachment + " 位置=" + mtDbg.Location);
                            string rawDbg = mtDbg.Contents ?? "";
                            ed.WriteMessage("\\n[HTC调试] 内容前120字=" + (rawDbg.Length > 120 ? rawDbg.Substring(0, 120) : rawDbg));
                        }
                        else if (ent is DBText dtDbg)
                        {
                            ed.WriteMessage("\\n[HTC调试] DBText Pos=" + dtDbg.Position + " AlignPt=" + dtDbg.AlignmentPoint
                                + " HMode=" + dtDbg.HorizontalMode + " VMode=" + dtDbg.VerticalMode
                                + " WF=" + dtDbg.WidthFactor + " H=" + dtDbg.Height);
                        }
                        ed.WriteMessage("\\n[HTC调试] 样式字体=" + (fontFileName ?? "") + " TypeFace=" + (typeFaceName ?? ""));

                        // 统一按微软雅黑渲染字形轮廓
                        List<Polyline> outlines = BuildTextOutlines('''
assert old3 in text, "old3 not found"
text = text.replace(old3, new3, 1)

f.write_text(text, encoding="utf-8")
print("逐维盒拟合 + 对象诊断 已完成")
