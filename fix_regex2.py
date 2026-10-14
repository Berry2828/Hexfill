# -*- coding: utf-8 -*-
"""用 chr(92) 逐字符构造, 重写 ParseMTextInlineFactors (正则必须双反斜杠)"""
from pathlib import Path

BS = chr(92)  # 反斜杠
f = Path(r"C:\Users\allen\Documents\Hexfill 2\Commands\HtcCommand.cs")
text = f.read_text(encoding="utf-8")

new_func = (
    "        /// <summary>\n"
    "        /// 解析 MTEXT 内嵌格式码中的宽度因子 " + BS + "W2.87; 与字高 " + BS + "H1.2x;(相对) / " + BS + "H1.2;(绝对)。\n"
    "        /// 这些码会使 AutoCAD 显示的文字宽/高与字高不一致, 渲染必须同步解析。\n"
    "        /// </summary>\n"
    "        private static void ParseMTextInlineFactors(string contents,\n"
    "            out double widthFactor, out double heightFactor, out bool heightIsRelative)\n"
    "        {\n"
    "            widthFactor = 0.0;\n"
    "            heightFactor = 0.0;\n"
    "            heightIsRelative = false;\n"
    "            if (string.IsNullOrEmpty(contents)) return;\n"
    '            var m = System.Text.RegularExpressions.Regex.Match(contents, @"' + BS + BS + 'W([0-9.]+);");\n'
    "            if (m.Success)\n"
    "                double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,\n"
    "                    System.Globalization.CultureInfo.InvariantCulture, out widthFactor);\n"
    '            var mh = System.Text.RegularExpressions.Regex.Match(contents, @"' + BS + BS + 'H([0-9.]+)x;");\n'
    "            if (mh.Success)\n"
    "            {\n"
    "                double.TryParse(mh.Groups[1].Value, System.Globalization.NumberStyles.Float,\n"
    "                    System.Globalization.CultureInfo.InvariantCulture, out heightFactor);\n"
    "                heightIsRelative = true;\n"
    "                return;\n"
    "            }\n"
    '            mh = System.Text.RegularExpressions.Regex.Match(contents, @"' + BS + BS + 'H([0-9.]+);");\n'
    "            if (mh.Success)\n"
    "                double.TryParse(mh.Groups[1].Value, System.Globalization.NumberStyles.Float,\n"
    "                    System.Globalization.CultureInfo.InvariantCulture, out heightFactor);\n"
    "        }\n\n"
)

start_marker = "        /// <summary>\n        /// 解析 MTEXT 内嵌格式码中的宽度因子"
end_marker = "        /// <summary>\n        /// 是否为微软雅黑字体"
si = text.index(start_marker)
ei = text.index(end_marker)
text = text[:si] + new_func + text[ei:]
f.write_text(text, encoding="utf-8")

# 立即验证
text2 = f.read_text(encoding="utf-8")
for line in text2.splitlines():
    if "Regex.Match(contents" in line:
        print(repr(line))
