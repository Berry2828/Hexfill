# -*- coding: utf-8 -*-
"""在 dcd2285 好版本上, 用唯一标记替换映射块为逐维盒拟合"""
from pathlib import Path

f = Path(r"C:\Users\allen\Documents\Hexfill 2\Commands\HtcCommand.cs")
text = f.read_text(encoding="utf-8")

start_marker = "                    double scale = 0.0;"
end_marker = '                    ed.WriteMessage(string.Format(\n                        "\\n[HTC调试] 字高='
si = text.index(start_marker)
ei = text.index(end_marker)

new_block = '''                    double scale = 0.0;
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

# 点映射分支: 改用 scaleX/scaleY
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
assert old2 in text, "point mapping not found"
text = text.replace(old2, new2, 1)

# 对象诊断插入
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
assert old3 in text, "diag anchor not found"
text = text.replace(old3, new3, 1)

f.write_text(text, encoding="utf-8")

# 括号平衡检查
depth = 0
for ch in text:
    if ch == '{': depth += 1
    elif ch == '}': depth -= 1
print("括号平衡深度:", depth, "(应为0)")
print("替换完成")
