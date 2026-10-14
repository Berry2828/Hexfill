# -*- coding: utf-8 -*-
"""MTEXT: 解析内嵌 \\W/\\H 格式码, 按显示比例渲染"""
from pathlib import Path

f = Path(r"C:\Users\allen\Documents\Hexfill 2\Commands\HtcCommand.cs")
text = f.read_text(encoding="utf-8")

# 1. 在 MTEXT 分支应用内嵌格式因子
old1 = """                            if (textStyle != null)
                            {
                                fontFileName = textStyle.FileName;
                                bigFontFileName = textStyle.BigFontFileName;
                                typeFaceName = textStyle.Font.TypeFace;
                                fontBold = textStyle.Font.Bold;
                                fontItalic = textStyle.Font.Italic;
                                if (isMText)
                                {
                                    widthFactor = textStyle.XScale;
                                    oblique = textStyle.ObliquingAngle;
                                }
                                // 固定字高样式: 实体高度为 0 时以样式固定高度为准
                                if (textHeight <= 1e-6 && textStyle.TextSize > 1e-6)
                                    textHeight = textStyle.TextSize;
                            }
                        }
"""
new1 = """                            if (textStyle != null)
                            {
                                fontFileName = textStyle.FileName;
                                bigFontFileName = textStyle.BigFontFileName;
                                typeFaceName = textStyle.Font.TypeFace;
                                fontBold = textStyle.Font.Bold;
                                fontItalic = textStyle.Font.Italic;
                                if (isMText)
                                {
                                    widthFactor = textStyle.XScale;
                                    oblique = textStyle.ObliquingAngle;
                                    // 内嵌格式码 (\\W 宽度因子 / \\H 字高): AutoCAD 显示按码缩放,
                                    // 渲染必须同步, 否则圆与显示文字宽高不一致
                                    double inlineW, inlineH;
                                    bool inlineHRelative;
                                    ParseMTextInlineFactors(textString, out inlineW, out inlineH, out inlineHRelative);
                                    if (inlineW > 1e-6) widthFactor = inlineW;
                                    if (inlineH > 1e-6)
                                        textHeight = inlineHRelative ? textHeight * inlineH : inlineH;
                                }
                                // 固定字高样式: 实体高度为 0 时以样式固定高度为准
                                if (textHeight <= 1e-6 && textStyle.TextSize > 1e-6)
                                    textHeight = textStyle.TextSize;
                            }
                        }
"""
assert old1 in text, "style block not found"
text = text.replace(old1, new1, 1)

# 2. 新增解析函数 (放在 IsMicrosoftYaheiFont 前)
anchor = """        /// <summary>
        /// 是否为微软雅黑字体（TypeFace=Microsoft YaHei 或字体文件名为 msyh*）
        /// </summary>"""
helper = """        /// <summary>
        /// 解析 MTEXT 内嵌格式码中的宽度因子 \\W2.87; 与字高 \\H1.2x;(相对) / \\H1.2;(绝对)。
        /// 这些码会使 AutoCAD 显示的文字宽/高与字高不一致, 渲染必须同步解析。
        /// </summary>
        private static void ParseMTextInlineFactors(string contents,
            out double widthFactor, out double heightFactor, out bool heightIsRelative)
        {
            widthFactor = 0.0;
            heightFactor = 0.0;
            heightIsRelative = false;
            if (string.IsNullOrEmpty(contents)) return;
            var m = System.Text.RegularExpressions.Regex.Match(contents, @"\\W([0-9.]+);");
            if (m.Success)
                double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out widthFactor);
            var mh = System.Text.RegularExpressions.Regex.Match(contents, @"\\H([0-9.]+)x;");
            if (mh.Success)
            {
                double.TryParse(mh.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out heightFactor);
                heightIsRelative = true;
                return;
            }
            mh = System.Text.RegularExpressions.Regex.Match(contents, @"\\H([0-9.]+);");
            if (mh.Success)
                double.TryParse(mh.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out heightFactor);
        }

        /// <summary>
        /// 是否为微软雅黑字体（TypeFace=Microsoft YaHei 或字体文件名为 msyh*）
        /// </summary>"""
assert anchor in text, "anchor not found"
text = text.replace(anchor, helper, 1)

f.write_text(text, encoding="utf-8")
print("MTEXT 内嵌格式码解析已加入")
