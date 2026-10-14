# -*- coding: utf-8 -*-
from pathlib import Path

f = Path(r"C:\Users\allen\Documents\Hexfill 2\Commands\HtcCommand.cs")
text = f.read_text(encoding="utf-8")

old0 = r'''                    bool useBaselineMapping = true;
'''
new0 = r'''                    bool useBaselineMapping = true;
                    double stretchX = 1.0, stretchY = 1.0;
'''
assert old0 in text, "old0"
text = text.replace(old0, new0, 1)

old1 = r'''                    if (!isMText && textHeight > 1e-6)
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
'''
new1 = r'''                    if (!isMText && textHeight > 1e-6)
                    {
                        scale = textHeight / gdH;
                        double offXEm = 0.0, offYEm = 0.0;
                        bool isFit = hMode == TextHorizontalMode.TextFit;
                        bool isAlign = hMode == TextHorizontalMode.TextAlign;
                        bool isDefaultAlign = hMode == TextHorizontalMode.TextLeft &&
                                              vMode == TextVerticalMode.TextBase;
                        // Fit/Align: AutoCAD 按两对齐点距离拉伸文字(原 WidthFactor 被忽略)
                        Point3d pivot = (isDefaultAlign || isFit || isAlign) ? position : alignmentPoint;
                        if ((isFit || isAlign) && fitDistance > 1e-6)
                        {
                            double advanceEm = MeasureTextAdvance(renderText, fontFamily, fontStyle, format);
                            if (advanceEm <= 1e-6) advanceEm = gdW;
                            double advanceMM = advanceEm * (textHeight / 1000.0);
                            if (advanceMM > 1e-6)
                            {
                                double s = fitDistance / advanceMM;
                                stretchX = s;
                                if (isAlign) stretchY = s;
                            }
                        }
                        if (!isDefaultAlign && !isFit && !isAlign)
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
'''
assert old1 in text, "old1"
text = text.replace(old1, new1, 1)

old2 = r'''                                    if (useBaselineMapping)
                                    {
                                        // 墨迹左下角 (minX, maxY) 锚定到锚点; Y 上翻; 再绕锚点旋转
                                        double lx = (gpts[j].X - minX) * scale;
                                        double ly = (maxY - gpts[j].Y) * scale;
                                        double rx = lx * cosR - ly * sinR;
                                        double ry = lx * sinR + ly * cosR;
                                        outline.Add(new Point2d(anchorX + rx, anchorY + ry));
                                    }
'''
new2 = r'''                                    if (useBaselineMapping)
                                    {
                                        // 墨迹左下角 (minX, maxY) 锚定到锚点; Y 上翻; Fit/Align 拉伸; 再绕锚点旋转
                                        double lx = (gpts[j].X - minX) * scale * stretchX;
                                        double ly = (maxY - gpts[j].Y) * scale * stretchY;
                                        double rx = lx * cosR - ly * sinR;
                                        double ry = lx * sinR + ly * cosR;
                                        outline.Add(new Point2d(anchorX + rx, anchorY + ry));
                                    }
'''
assert old2 in text, "old2"
text = text.replace(old2, new2, 1)

old3 = r'''            TextHorizontalMode hMode, TextVerticalMode vMode,
            Point3d position, Point3d alignmentPoint)'''
new3 = r'''            TextHorizontalMode hMode, TextVerticalMode vMode,
            Point3d position, Point3d alignmentPoint, double fitDistance)'''
assert old3 in text, "old3"
text = text.replace(old3, new3, 1)

old4 = r'''                        // 统一按微软雅黑渲染字形轮廓
                        List<Polyline> outlines = BuildTextOutlines(
                            textString, ext, rotation, fontFileName, bigFontFileName,
                            typeFaceName, fontBold, fontItalic, isMText, textHeight,
                            lineSpacingFactor, widthFactor, oblique, attachment,
                            hMode, vMode, position, alignmentPoint);
'''
new4 = r'''                        // Fit/Align 两点拉伸距离 (非此类文字为 -1)
                        double fitDistance = -1.0;
                        if (!isMText && (hMode == TextHorizontalMode.TextFit ||
                                         hMode == TextHorizontalMode.TextAlign))
                        {
                            fitDistance = Point2dDistance(
                                new Point2d(position.X, position.Y),
                                new Point2d(alignmentPoint.X, alignmentPoint.Y));
                        }

                        // 统一按微软雅黑渲染字形轮廓
                        List<Polyline> outlines = BuildTextOutlines(
                            textString, ext, rotation, fontFileName, bigFontFileName,
                            typeFaceName, fontBold, fontItalic, isMText, textHeight,
                            lineSpacingFactor, widthFactor, oblique, attachment,
                            hMode, vMode, position, alignmentPoint, fitDistance);
'''
assert old4 in text, "old4"
text = text.replace(old4, new4, 1)

f.write_text(text, encoding="utf-8")
print("Fit/Align 拉伸已加入")
