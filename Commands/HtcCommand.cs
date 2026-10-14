using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using FontDescriptor = Autodesk.AutoCAD.GraphicsInterface.FontDescriptor;
using Autodesk.AutoCAD.Runtime;
using Exception = System.Exception;

namespace HexFill
{
    public partial class HexFill
    {
        // HTC - 文字转网点命令
        // ================================================================
        // 功能：
        // 1. 框选一个或多个文字对象（TEXT/MTEXT）
        // 2. 圆直径固定 0.03mm、圆心间距固定 0.06mm（用户要求，不受任何设置影响）
        // 3. 统一按微软雅黑渲染字形轮廓（用户要求 2026-08-14，不得改回其他字体）；
        //    只新增圆，不修改原文字对象（TEXT/MTEXT 样式保持原样）
        // 4. 在文字笔画轮廓内部按蜂窝排列填满圆，并要求整个圆留在笔画内
        // ================================================================
        [CommandMethod("HTC")]
﻿        public void HTC()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            db = doc.Database;
            ed = doc.Editor;

            try
            {
                // 框选文字对象（TEXT 单行文字 + MTEXT 多行文字）
                var filter = new SelectionFilter(new TypedValue[] { new TypedValue(0, "TEXT,MTEXT") });
                var selRes = ed.GetSelection(filter);
                if (selRes.Status != PromptStatus.OK) return;

                // 圆直径与间距：固定值，不受 HDIA 等任何设置影响
                // 用户 2026-08-16 要求固定 0.03mm / 0.06mm（2026-08-29 合并评审中用户再次确认；
                // 此前 08-20 误记为 0.1/0.1，已更正）
                double diameter = 0.03;   // 圆直径 0.03mm（半径 0.015mm）
                double spacing = 0.06;    // 圆心间距 0.06mm

                double radius = diameter / 2.0;

                int textCount = 0;
                int totalCircles = 0;
                int failedTexts = 0;

                ObjectId[] textIds = selRes.Value.GetObjectIds();

                // 每个文字独立提交；某个文字失败不影响此前成功或后续文字。
                string firstFailure = null;
                foreach (ObjectId id in textIds)
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            var ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                            if (ent == null) continue;
                            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(ent.OwnerId, OpenMode.ForWrite);

                            List<Polyline> outlines = BuildHtcEntityOutlines(ent, tr);

                            if (outlines == null || outlines.Count == 0)
                            {
                                failedTexts++;
                                continue;
                            }

                            // 字高校验 (用户要求): 每个笔画都必须能容纳一个直径 0.03mm 的圆
                            bool strokeTooThin = false;
                            for (int oi = 0; oi < outlines.Count; oi++)
                            {
                                Extents3d ob = outlines[oi].GeometricExtents;
                                if (ob.MaxPoint.X - ob.MinPoint.X < 2 * radius ||
                                    ob.MaxPoint.Y - ob.MinPoint.Y < 2 * radius)
                                {
                                    strokeTooThin = true;
                                    break;
                                }
                            }
                            if (strokeTooThin)
                            {
                                failedTexts++;
                                foreach (var poly in outlines) poly.Dispose();
                                continue;
                            }

                            // 按奇偶填充规则在全部轮廓上生成蜂窝圆：
                            // 被奇数条轮廓包围的点=笔画（填充），偶数=孔洞（空心），
                            // 如"0"外圈与内圈之间的环带填充、中心空心
                            int generatedCount = 0;
                            try
                            {
                                var gridPoints = GenerateHexGridText(outlines, spacing, true, radius);
                                if (gridPoints.Count == 0)
                                {
                                    failedTexts++;
                                }
                                else
                                {
                                    foreach (var point in gridPoints)
                                    {
                                        if ((generatedCount & 1023) == 0) CheckGenerationCancelled();
                                        generatedCount++;
                                        Point3d center = new Point3d(point.X, point.Y, outlines[0].Elevation);
                                        Circle circle = new Circle(center, Vector3d.ZAxis, radius);
                                        modelSpace.AppendEntity(circle);
                                        tr.AddNewlyCreatedDBObject(circle, true);
                                    }

                                }
                            }
                            finally
                            {
                                foreach (var poly in outlines)
                                    poly.Dispose();
                            }
                            tr.Commit();
                            if (generatedCount > 0)
                            {
                                totalCircles += generatedCount;
                                textCount++;
                            }
                        }
                        catch (OperationCanceledException ex)
                        {
                            tr.Abort();
                            ed.WriteMessage("\nHTC：" + ex.Message);
                            return;
                        }
                        catch (Exception ex)
                        {
                            tr.Abort();
                            failedTexts++;
                            if (firstFailure == null) firstFailure = ex.Message;
                        }
                    }
                }

                if (firstFailure != null)
                    ed.WriteMessage("\nHTC：部分文字失败并保留原状，首个原因：" + firstFailure);

                ed.WriteMessage("\nHTC：处理 {0} 个文字，生成 {1} 个圆，跳过 {2} 个；直径 {3:F3}mm，间距 {4:F3}mm。",
                    textCount, totalCircles, failedTexts, diameter, spacing);
            }
            catch (Exception ex)
            {
                ed.WriteMessage("\nHTC错误：" + ex.Message);
            }
        }


        /// <summary>
        /// 解析 MTEXT 内容，去除全部控制代码（{\f字体; \H高度 \C颜色 \W宽高比 \Q斜切
        /// \A对齐 \T字距 \L/\l \O/\o \K/\k 下划线/上划线/删除线 \S堆叠 \U+XXXX 等），
        /// 只保留原始文字中真正可见的字符——避免生成网点时把 {\fSimSun|b0|i0|c134|p2; 之类
        /// 的代码符号当字面字符渲染进网点（"生成的文字前面出现代码和符号"）。
        /// 分组括号 { } 不渲染；\{ \} \\ 为字面量；\P/\n 为换行；\~ 为不换行空格。
        /// </summary>
        private static string StripMTextControlCodes(string contents)
        {
            if (string.IsNullOrEmpty(contents)) return contents;
            var sb = new System.Text.StringBuilder(contents.Length);
            int i = 0;
            int n = contents.Length;
            while (i < n)
            {
                char c = contents[i];
                if (c == '\\' && i + 1 < n)
                {
                    char next = contents[i + 1];
                    switch (next)
                    {
                        case 'P': case 'p': case 'n':       // 段落/换行
                            sb.Append('\n');
                            i += 2;
                            continue;
                        case '~':                            // 不换行空格
                            sb.Append(' ');
                            i += 2;
                            continue;
                        case 'L': case 'l': case 'O': case 'o': case 'K': case 'k':
                            i += 2;                          // 下划线/上划线/删除线开关：不渲染
                            continue;
                        case '{': sb.Append('{'); i += 2; continue;   // 字面 {
                        case '}': sb.Append('}'); i += 2; continue;   // 字面 }
                        case '\\': sb.Append('\\'); i += 2; continue; // 字面 \
                        case 'S': case 's':                  // 堆叠 \S 分子^分母; 保留可见字符
                        {
                            int semi = contents.IndexOf(';', i + 2);
                            if (semi < 0) { i += 2; continue; }
                            string stack = contents.Substring(i + 2, semi - i - 2);
                            foreach (char ch in stack)
                                sb.Append((ch == '^' || ch == '#') ? ' ' : ch);
                            i = semi + 1;
                            continue;
                        }
                        case 'U': case 'u':                  // Unicode 转义 \U+XXXX
                        {
                            int j = i + 2;
                            if (j < n && contents[j] == '+') j++;
                            string hex = "";
                            while (j < n && hex.Length < 4 && Uri.IsHexDigit(contents[j]))
                            {
                                hex += contents[j];
                                j++;
                            }
                            if (hex.Length > 0)
                            {
                                try { sb.Append((char)Convert.ToInt32(hex, 16)); } catch { }
                                i = j;
                            }
                            else
                            {
                                i += 2;
                            }
                            continue;
                        }
                        default:
                        {
                            // 常规格式代码 \X参数; （\f \H \W \Q \A \C \T 等）→ 整体跳过
                            int semi = contents.IndexOf(';', i + 2);
                            if (semi > i + 2)
                            {
                                string seg = contents.Substring(i + 1, semi - i - 1);
                                if (IsMTextControlSegment(seg))
                                {
                                    i = semi + 1;
                                    continue;
                                }
                            }
                            // 无法识别的转义：只跳过反斜杠本身，保留后一个字符（保守不丢内容）
                            i += 1;
                            continue;
                        }
                    }
                }
                if (c == '{' || c == '}')
                {
                    i += 1;   // 分组括号本身不渲染
                    continue;
                }
                sb.Append(c);
                i += 1;
            }
            return sb.ToString();
        }

        /// <summary>
        /// 判断 MTEXT 控制代码参数段是否只含控制字符（字母/数字/分隔符），
        /// 是则整段按控制代码跳过；否则按普通可见文本保留。
        /// </summary>
        private static bool IsMTextControlSegment(string seg)
        {
            for (int i = 0; i < seg.Length; i++)
            {
                char c = seg[i];
                if (!(char.IsLetterOrDigit(c) || c == '|' || c == '#' || c == '^' ||
                      c == '+' || c == '-' || c == '.' || c == '/' || c == '\\' ||
                      c == ' ' || c == ';'))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 判断文字样式是否为 SHX/大字体（GDI 无法渲染的描边字体，需回退微软雅黑）。
        /// AutoCAD 的 SHX 样式文件名无扩展名（如 "txt"、"romans"），TrueType 样式
        /// 必有 .ttf/.ttc 扩展名或带 TypeFace 名；大字体（BigFont）必为 SHX。
        /// </summary>
        /// <summary>
        /// 解析 MTEXT 内嵌格式码中的宽度因子 \W2.87; 与字高 \H1.2x;(相对) / \H1.2;(绝对)。
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
        /// </summary>
        private static bool IsMicrosoftYaheiFont(string fontFileName, string typeFaceName)
        {
            if (!string.IsNullOrEmpty(typeFaceName) &&
                typeFaceName.IndexOf("YaHei", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (!string.IsNullOrEmpty(fontFileName) &&
                fontFileName.IndexOf("msyh", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        private static bool IsShxFont(string fontFileName, string bigFontFileName, string typeFaceName)
        {
            if (!string.IsNullOrWhiteSpace(bigFontFileName))
                return true;
            string fn = fontFileName ?? "";
            if (fn.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.IsNullOrEmpty(Path.GetExtension(fn)))
            {
                // 无扩展名：SHX 样式通常只有名字；带 TypeFace 的 Font-only TrueType 样式不算 SHX
                if (!string.IsNullOrWhiteSpace(typeFaceName))
                    return false;
                return true;
            }
            return false;
        }

        private List<Polyline> BuildHtcEntityOutlines(Entity source, Transaction tr)
        {
            var result = new List<Polyline>();
            var fragments = new DBObjectCollection();
            try
            {
                if (source is MText)
                {
                    // Let AutoCAD perform wrapping, attachment and inline formatting.
                    // Explode transient fragments only; the original MTEXT stays intact.
                    source.Explode(fragments);
                    foreach (DBObject fragment in fragments)
                    {
                        DBText text = fragment as DBText;
                        if (text != null) result.AddRange(BuildHtcTextOutlines(text, tr));
                    }
                }
                else if (source is DBText)
                    result.AddRange(BuildHtcTextOutlines((DBText)source, tr));
                return result;
            }
            catch
            {
                foreach (Polyline outline in result) outline.Dispose();
                throw;
            }
            finally
            {
                foreach (DBObject fragment in fragments) fragment.Dispose();
                fragments.Dispose();
            }
        }

        private List<Polyline> BuildHtcTextOutlines(DBText source, Transaction tr)
        {
            // Native MTEXT explosion emits whitespace runs between formatting groups.
            // They have a layout advance but no visible ink or usable geometric extents.
            if (string.IsNullOrWhiteSpace(source.TextString)) return new List<Polyline>();
            if (source.Normal.GetAngleTo(Vector3d.ZAxis) > 1e-8)
                throw new InvalidOperationException("HTC 暂不支持非 XY 平面的文字，原文字已保留。");
            using (var local = (DBText)source.Clone())
            {
                double rotation = source.Rotation;
                Point3d pivot = source.Position;
                Matrix3d toLocal = Matrix3d.Rotation(-rotation, Vector3d.ZAxis, pivot);
                local.TransformBy(toLocal);
                Extents3d bounds = local.GeometricExtents;
                TextStyleTableRecord style = tr.GetObject(source.TextStyleId, OpenMode.ForRead, false) as TextStyleTableRecord;
                bool bold = style != null && style.Font.Bold;
                bool italic = style != null && style.Font.Italic;
                string fontFileName = "msyh.ttc";
                string typeFaceName = "Microsoft YaHei";
                var outlines = BuildTextOutlines(local.TextString, bounds, 0, fontFileName, null,
                    typeFaceName, bold, italic, false, local.Height, 1, local.WidthFactor,
                    local.Oblique, AttachmentPoint.TopLeft, local.HorizontalMode, local.VerticalMode,
                    local.Position, local.AlignmentPoint, -1, -1);
                try
                {
                    foreach (Polyline outline in outlines)
                    {
                        if (local.IsMirroredInX || local.IsMirroredInY)
                        {
                            double cx = (bounds.MinPoint.X + bounds.MaxPoint.X) * 0.5;
                            double cy = (bounds.MinPoint.Y + bounds.MaxPoint.Y) * 0.5;
                            for (int i = 0; i < outline.NumberOfVertices; i++)
                            {
                                Point2d p = outline.GetPoint2dAt(i);
                                outline.SetPointAt(i, new Point2d(local.IsMirroredInX ? 2 * cx - p.X : p.X,
                                    local.IsMirroredInY ? 2 * cy - p.Y : p.Y));
                            }
                        }
                        outline.Elevation = local.Position.Z;
                        outline.TransformBy(toLocal.Inverse());
                    }
                    return outlines;
                }
                catch
                {
                    foreach (Polyline outline in outlines) outline.Dispose();
                    throw;
                }
            }
        }

        /// <summary>
        /// 用 TrueType 渲染字形轮廓，返回闭合聚合线列表（CAD 坐标）。
        /// HTC 调用方固定使用微软雅黑；宽度因子与斜切角施加于字形。
        /// 调用方先按 CAD 解析 MTEXT 的实际片段，再消除旋转。
        /// 在未旋转平面中按片段的真实可见范围校准位置、宽高，之后恢复旋转。
        /// 不修改原文字对象，圆直径和间距由 HTC 的固定参数控制。
        /// </summary>
        private List<Polyline> BuildTextOutlines(
            string text, Extents3d ext, double rotation,
            string fontFileName, string bigFontFileName, string typeFaceName,
            bool fontBold, bool fontItalic, bool isMText, double textHeight,
            double lineSpacingFactor, double widthFactor, double oblique,
            AttachmentPoint attachment,
            TextHorizontalMode hMode, TextVerticalMode vMode,
            Point3d position, Point3d alignmentPoint, double fitDistance, double targetWidthMM)
        {
            var outlines = new List<Polyline>();
            System.Drawing.Text.PrivateFontCollection privateFonts = null;
            System.Drawing.FontFamily fontFamily = null;
            try
            {
                if (string.IsNullOrEmpty(text)) return outlines;

                fontFamily = ResolveTextFont(fontFileName, bigFontFileName, typeFaceName, out privateFonts, out _);
                if (fontFamily == null) return outlines;
                System.Drawing.FontStyle fontStyle = GetAvailableFontStyle(fontFamily, fontBold, fontItalic);

                using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                using (var format = new System.Drawing.StringFormat(System.Drawing.StringFormat.GenericDefault))
                {
                    // 去除 MTEXT 控制代码（{\f...; \H \C \P 等），只保留可见文字；
                    // 换行标记 → 换行；GDI 单行渲染后手动按 AutoCAD 行距堆叠
                    string renderText = isMText ? StripMTextControlCodes(text)
                                                : text.Replace("\\n", "\n");
                    format.Alignment = System.Drawing.StringAlignment.Near;
                    format.LineAlignment = System.Drawing.StringAlignment.Near;
                    format.FormatFlags |= System.Drawing.StringFormatFlags.NoClip;

                    // AutoCAD MTEXT 默认基线行距 = 1.6667 × 字高 × 行距因子（AtLeast 风格）；
                    // GDI emSize=1000 与 CAD 字高一一对应
                    double safeLineSpacingFactor = (lineSpacingFactor > 1e-6) ? lineSpacingFactor : 1.0;
                    float lineAdvance = (float)(1000.0 * 1.6666666667 * safeLineSpacingFactor);

                    int pathEmUnits = fontFamily.GetEmHeight(fontStyle);
                    int pathCellAscent = fontFamily.GetCellAscent(fontStyle);
                    double pathBaselineOffset = pathEmUnits > 0
                        ? (double)pathCellAscent / pathEmUnits * 1000.0
                        : 800.0;
                    double safeWidthFactor = (widthFactor > 1e-6) ? widthFactor : 1.0;
                    double obliqueTangent = Math.Tan(oblique);

                    string[] lines = renderText.Split('\n');
                    for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                    {
                        if (lines[lineIndex].Length == 0) continue;
                        using (var linePath = new System.Drawing.Drawing2D.GraphicsPath())
                        {
                            linePath.AddString(lines[lineIndex], fontFamily, (int)fontStyle,
                                1000.0f, new System.Drawing.PointF(0, lineIndex * lineAdvance), format);

                            // AutoCAD 的宽度因子以文字原点缩放，斜切则以每一行自身基线为轴。
                            // GDI 的 Y 轴向下，因此 x' = width*x - tan(oblique)*y + tan(oblique)*baseline。
                            // 旧实现绕整个路径顶边斜切，会让单行整体横移、多行逐行错位。
                            if (Math.Abs(safeWidthFactor - 1.0) > 1e-9 ||
                                Math.Abs(obliqueTangent) > 1e-9)
                            {
                                double lineBaseline = lineIndex * lineAdvance + pathBaselineOffset;
                                using (var transform = new System.Drawing.Drawing2D.Matrix(
                                    (float)safeWidthFactor, 0f, (float)-obliqueTangent, 1f,
                                    (float)(obliqueTangent * lineBaseline), 0f))
                                {
                                    linePath.Transform(transform);
                                }
                            }

                            path.AddPath(linePath, false);
                        }
                    }

                    path.Flatten(null, 0.5f);
                    var gpts = path.PathPoints;
                    byte[] types = path.PathTypes;
                    if (gpts.Length < 3) return outlines;

                    // 整段字形包围盒（GDI 坐标，Y 向下，变换后）
                    float minX = float.MaxValue, maxX = float.MinValue;
                    float minY = float.MaxValue, maxY = float.MinValue;
                    for (int i = 0; i < gpts.Length; i++)
                    {
                        if (gpts[i].X < minX) minX = gpts[i].X;
                        if (gpts[i].X > maxX) maxX = gpts[i].X;
                        if (gpts[i].Y < minY) minY = gpts[i].Y;
                        if (gpts[i].Y > maxY) maxY = gpts[i].Y;
                    }
                    float gdW = maxX - minX;
                    float gdH = maxY - minY;
                    if (gdW < 1e-6 || gdH < 1e-6) return outlines;

                    double cosR = Math.Cos(rotation);
                    double sinR = Math.Sin(rotation);

                    double extW = ext.MaxPoint.X - ext.MinPoint.X;
                    double extH = ext.MaxPoint.Y - ext.MinPoint.Y;

                    double scale = 0.0;
                    double scaleX = 0.0, scaleY = 0.0;
                    double anchorX = 0.0;
                    double anchorY = 0.0;
                    bool useBaselineMapping = true;
                    double baselineGDI = pathBaselineOffset;

                    // ===== 映射 (2026-08-29 修正) =====
                    // 高度: em 缩放 = 字高/1000 (GDI emSize=1000 对应字高, 墨迹高=gdH×字高/1000)。
                    //   此前误用 scaleY=字高/墨迹高 导致圆整体放大 ~1000/gdH 倍 (回归自 319acb8)。
                    // 宽度: MTEXT 用 AutoCAD 自测的实际显示宽度 (ActualWidth), 精确对齐;
                    //       DBText 与高度等比 (em 缩放, 保比例)。
                    scaleY = textHeight / 1000.0;
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

                    float gdCx = (minX + maxX) / 2.0f;
                    float gdCy = (minY + maxY) / 2.0f;

                    // 提取闭合子路径（每个笔画）：闭合点 (types[i] & 0x80) 结束一个子路径
                    int startIdx = 0;
                    for (int i = 0; i <= gpts.Length; i++)
                    {
                        bool isClose = (i < gpts.Length) && ((types[i] & 0x80) != 0);
                        if (isClose || i == gpts.Length)
                        {
                            int endIdx = isClose ? i + 1 : gpts.Length;
                            if (endIdx - startIdx >= 3)
                            {
                                var outline = new List<Point2d>();
                                for (int j = startIdx; j < endIdx; j++)
                                {
                                    if (useBaselineMapping)
                                    {
                                        // 分轴缩放: X 用 scaleX (宽度对齐), Y 用 scaleY (高度=字高); 绕锚点旋转
                                        double lx = gpts[j].X * scaleX;
                                        double ly = (baselineGDI - gpts[j].Y) * scaleY;
                                        double rx = lx * cosR - ly * sinR;
                                        double ry = lx * sinR + ly * cosR;
                                        outline.Add(new Point2d(anchorX + rx, anchorY + ry));
                                    }
                                    else
                                    {
                                        // 包围盒居中：相对字形中心（Y 翻转）→ 旋转 → 自校准比例 → 居中到 ext
                                        double gdx = gpts[j].X - gdCx;
                                        double gdy = gdCy - gpts[j].Y;
                                        double rx = gdx * cosR - gdy * sinR;
                                        double ry = gdx * sinR + gdy * cosR;
                                        outline.Add(new Point2d(anchorX + rx * scale, anchorY + ry * scale));
                                    }
                                }
                                if (outline.Count >= 3)
                                {
                                    var poly = new Polyline();
                                    foreach (var p in outline)
                                        poly.AddVertexAt(poly.NumberOfVertices, p, 0, 0, 0);
                                    poly.Closed = true;
                                    outlines.Add(poly);
                                }
                            }
                            startIdx = endIdx;
                        }
                    }

                    // Calibrate visible ink in the unrotated text plane. CAD text height
                    // and GDI em size use different font metrics; a center shift alone
                    // cannot correct the width/height mismatch. Rotation is restored
                    // by BuildHtcTextOutlines after fitting the native fragment bounds.
                    if (outlines.Count > 0)
                    {
                        Extents3d outlineExtents = outlines[0].GeometricExtents;
                        for (int i = 1; i < outlines.Count; i++)
                            outlineExtents.AddExtents(outlines[i].GeometricExtents);

                        double textCenterX = (ext.MinPoint.X + ext.MaxPoint.X) * 0.5;
                        double textCenterY = (ext.MinPoint.Y + ext.MaxPoint.Y) * 0.5;
                        double outlineCenterX = (outlineExtents.MinPoint.X + outlineExtents.MaxPoint.X) * 0.5;
                        double outlineCenterY = (outlineExtents.MinPoint.Y + outlineExtents.MaxPoint.Y) * 0.5;
                        double fitX = extW / (outlineExtents.MaxPoint.X - outlineExtents.MinPoint.X);
                        double fitY = extH / (outlineExtents.MaxPoint.Y - outlineExtents.MinPoint.Y);
                        foreach (Polyline outline in outlines)
                        {
                            for (int i = 0; i < outline.NumberOfVertices; i++)
                            {
                                Point2d p = outline.GetPoint2dAt(i);
                                outline.SetPointAt(i, new Point2d(textCenterX + (p.X - outlineCenterX) * fitX,
                                    textCenterY + (p.Y - outlineCenterY) * fitY));
                            }
                        }
                    }
                }
            }
            catch
            {
                foreach (Polyline outline in outlines) outline.Dispose();
                throw;
            }
            finally
            {
                if (fontFamily != null) fontFamily.Dispose();
                if (privateFonts != null) privateFonts.Dispose();
            }
            return outlines;
        }

        /// <summary>
        /// 测量文字在 em=1000 下的前进宽度（advance width），用于旋转文字时反推基线起点。
        /// </summary>
        private static double MeasureTextAdvance(
            string text, System.Drawing.FontFamily family,
            System.Drawing.FontStyle style, System.Drawing.StringFormat format)
        {
            try
            {
                using (var bmp = new System.Drawing.Bitmap(1, 1))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                using (var font = new System.Drawing.Font(family, 1000f, style, System.Drawing.GraphicsUnit.Pixel))
                {
                    var size = g.MeasureString(text, font, new System.Drawing.PointF(0, 0), format);
                    return size.Width;
                }
            }
            catch
            {
                return 0.0;
            }
        }

        /// <summary>
        /// 查找或新建独立的微软雅黑文字样式（普通 msyh.ttc / 粗体 msyhbd.ttc）。
        /// 只新建独立样式记录，绝不修改 Standard 等既有共享样式；返回样式 ObjectId。
        /// 仅 SHX/大字体回退路径使用。
        /// </summary>
        private ObjectId EnsureMicrosoftYaheiStyle(Transaction tr, bool bold)
        {
            string fontFile = bold ? "msyhbd.ttc" : "msyh.ttc";
            try
            {
                var styleTable = (SymbolTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead, false);
                foreach (ObjectId sid in styleTable)
                {
                    var rec = tr.GetObject(sid, OpenMode.ForRead, false) as TextStyleTableRecord;
                    if (rec != null && !rec.IsErased &&
                        string.Equals(Path.GetFileName(rec.FileName ?? ""), fontFile,
                            StringComparison.OrdinalIgnoreCase))
                        return sid;
                }
                styleTable.UpgradeOpen();
                string baseName = bold ? "HTC-MicrosoftYaHei-Bold" : "HTC-MicrosoftYaHei";
                string styleName = baseName;
                int suffix = 2;
                while (styleTable.Has(styleName))
                    styleName = baseName + "-" + (suffix++);
                var newRec = new TextStyleTableRecord
                {
                    Name = styleName,
                    FileName = fontFile,
                    Font = new FontDescriptor(
                        "Microsoft YaHei", bold, false, 134, 0)
                };
                styleTable.Add(newRec);
                tr.AddNewlyCreatedDBObject(newRec, true);
                return newRec.Id;
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        /// <summary>
        /// 按文字样式解析 TrueType 字体；SHX/大字体/加载失败一律回退微软雅黑。
        /// fellBackToYahei 输出：是否发生了回退（调用方可据此决定是否需要把文字样式切换为雅黑）。
        /// </summary>
        private static System.Drawing.FontFamily ResolveTextFont(
            string fontFileName, string bigFontFileName, string typeFaceName,
            out System.Drawing.Text.PrivateFontCollection privateFonts,
            out bool fellBackToYahei)
        {
            privateFonts = null;
            fellBackToYahei = true;
            try
            {
                bool isShx = !string.IsNullOrWhiteSpace(bigFontFileName);
                if (!string.IsNullOrEmpty(fontFileName) &&
                    fontFileName.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
                    isShx = true;

                if (!isShx)
                {
                    string resolvedFontFile = ResolveWindowsFontFile(fontFileName);
                    if (!string.IsNullOrEmpty(resolvedFontFile))
                    {
                        privateFonts = new System.Drawing.Text.PrivateFontCollection();
                        privateFonts.AddFontFile(resolvedFontFile);
                        if (privateFonts.Families.Length > 0)
                        {
                            fellBackToYahei = false;
                            return new System.Drawing.FontFamily(privateFonts.Families[0].Name, privateFonts);
                        }
                    }
                    // 无文件名或文件缺失：尝试 TypeFace / 文件名作家族名
                    string familyCandidates = !string.IsNullOrEmpty(typeFaceName)
                        ? typeFaceName
                        : System.IO.Path.GetFileNameWithoutExtension(fontFileName ?? "");
                    if (!string.IsNullOrWhiteSpace(familyCandidates))
                    {
                        try
                        {
                            var family = new System.Drawing.FontFamily(familyCandidates);
                            fellBackToYahei = false;
                            return family;
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
            try { return new System.Drawing.FontFamily("Microsoft YaHei"); }
            catch { return null; }
        }

        /// <summary>
        /// 把样式字体文件名解析为 Windows Fonts 目录下的实际字体文件（大小写不敏感）。
        /// </summary>
        private static string ResolveWindowsFontFile(string fontFileName)
        {
            if (string.IsNullOrWhiteSpace(fontFileName)) return null;
            try
            {
                if (System.IO.File.Exists(fontFileName)) return fontFileName;
                string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                string candidate = System.IO.Path.Combine(fontsDir, fontFileName);
                if (System.IO.File.Exists(candidate)) return candidate;
                foreach (string f in System.IO.Directory.GetFiles(fontsDir))
                {
                    if (string.Equals(System.IO.Path.GetFileName(f), fontFileName,
                        StringComparison.OrdinalIgnoreCase))
                        return f;
                }
            }
            catch
            {
            }
            return null;
        }

        /// <summary>
        /// 选择字体家族中可用的样式（粗/斜组合取最近可用者）。
        /// </summary>
        private static System.Drawing.FontStyle GetAvailableFontStyle(
            System.Drawing.FontFamily family, bool bold, bool italic)
        {
            if (family == null) return System.Drawing.FontStyle.Regular;
            System.Drawing.FontStyle style = System.Drawing.FontStyle.Regular;
            if (bold) style |= System.Drawing.FontStyle.Bold;
            if (italic) style |= System.Drawing.FontStyle.Italic;
            try
            {
                if (family.IsStyleAvailable(style)) return style;
            }
            catch
            {
            }
            if (bold && italic)
            {
                try
                {
                    if (family.IsStyleAvailable(System.Drawing.FontStyle.Bold)) return System.Drawing.FontStyle.Bold;
                    if (family.IsStyleAvailable(System.Drawing.FontStyle.Italic)) return System.Drawing.FontStyle.Italic;
                }
                catch
                {
                }
            }
            return System.Drawing.FontStyle.Regular;
        }

        /// <summary>
        /// 在多个闭合轮廓上按奇偶填充规则生成蜂窝网格点：
        /// 点被奇数条轮廓包围 → 位于字形笔画内（保留）；
        /// 被偶数条轮廓包围 → 位于孔洞（如"0"的中心、"回"的内空），不填充。
        /// 网格数学与 GenerateHexGridStrict(yOffset=true) 一致（9 偏移选点数最多者）。
        /// 轮廓来自文字对象自身字体的渲染几何，按 CAD 显示范围 1:1 映射。
        /// </summary>
        private List<Point3d> GenerateHexGridText(List<Polyline> outlines, double spacing, bool yOffset, double radius)
        {
            if (outlines == null || outlines.Count == 0) return new List<Point3d>();
            if (radius <= 0) radius = CurrentRadius;

            // 合并包围盒
            var ext = outlines[0].GeometricExtents;
            for (int i = 1; i < outlines.Count; i++)
                ext.AddExtents(outlines[i].GeometricExtents);

            // 每轮廓 containment 缓存（含 bbox 快速剪枝）
            var containments = new List<CurveContainment>(outlines.Count);
            for (int i = 0; i < outlines.Count; i++)
            {
                if (outlines[i].NumberOfVertices >= 3)
                    containments.Add(BuildCurveContainment(outlines[i]));
                else
                    containments.Add(null);
            }

            double rowSpacing, colSpacing;
            if (yOffset)
            {
                // Y方向偏移蜂窝：列距更近(sqrt(3)/2)，行距全距，奇数列Y偏移
                colSpacing = spacing * Sqrt3Over2;
                rowSpacing = spacing;
            }
            else
            {
                rowSpacing = spacing * Sqrt3Over2;
                colSpacing = spacing;
            }

            // 可用区域 = boundingbox 内缩 radius
            double availW = (ext.MaxPoint.X - ext.MinPoint.X) - 2 * radius;
            double availH = (ext.MaxPoint.Y - ext.MinPoint.Y) - 2 * radius;
            if (availW <= 0 || availH <= 0) return new List<Point3d>();

            int rows, cols;
            ValidateGridDimensions(availW, availH, rowSpacing, colSpacing, out rows, out cols);

            double totalW = (cols - 1) * colSpacing;
            double totalH = (rows - 1) * rowSpacing;
            double baseOffX = (availW - totalW) / 2.0;
            double baseOffY = (availH - totalH) / 2.0;
            double baseStartX = ext.MinPoint.X + radius + baseOffX;
            double baseStartY = ext.MinPoint.Y + radius + baseOffY;

            double halfRowSpacing = rowSpacing * 0.5;
            double halfColSpacing = colSpacing * 0.5;
            double[] yOffsets = { 0.0, halfRowSpacing, -halfRowSpacing };
            double[] xOffsets = { 0.0, halfColSpacing, -halfColSpacing };

            List<Point3d> bestResult = null;
            var candidate = new List<Point3d>(Math.Min(rows * cols / 2, 65536));
            var rowContainments = new List<CurveContainment>(containments.Count);
            int bestCount = -1;

            foreach (double dyOff in yOffsets)
            {
                foreach (double dxOff in xOffsets)
                {
                    double startX = baseStartX + dxOff;
                    double startY = baseStartY + dyOff;
                    candidate.Clear();

                    for (int r = 0; r < rows; r++)
                    {
                        if ((r & 31) == 0) CheckGenerationCancelled();
                        double y = startY + r * rowSpacing;
                        if (y < ext.MinPoint.Y || y > ext.MaxPoint.Y) continue;

                        // 所有本行候选圆的纵向范围；保留原轮廓顺序和奇偶规则。
                        rowContainments.Clear();
                        double maxY = y + (yOffset ? halfRowSpacing : 0.0);
                        foreach (CurveContainment containment in containments)
                        {
                            if (containment != null &&
                                containment.Extents.MinPoint.Y - radius <= maxY &&
                                containment.Extents.MaxPoint.Y + radius >= y)
                                rowContainments.Add(containment);
                        }
                        if (rowContainments.Count == 0) continue;

                        for (int c = 0; c < cols; c++)
                        {
                            double x = startX + c * colSpacing;
                            double finalY = y;
                            if (yOffset)
                            {
                                finalY += (c % 2 == 1) ? halfRowSpacing : 0.0;
                            }
                            else
                            {
                                x += (r % 2 == 1) ? halfColSpacing : 0.0;
                            }
                            if (x < ext.MinPoint.X || x > ext.MaxPoint.X) continue;

                            var pt2d = new Point2d(x, finalY);

                            // 圆与笔画相交即可 (用户 2026-08-19 确认):
                            // 笔画尖端/边缘处也能放圆, 圆的整体轮廓与文字外缘齐平,
                            // 不再因"整圆必须在笔画内"而整体缩小一圈。
                            if (IsCircleIntersectsGlyph(pt2d, radius, rowContainments))
                                AddGridPoint(candidate, new Point3d(x, finalY, 0));
                        }
                    }

                    if (candidate.Count > bestCount)
                    {
                        bestCount = candidate.Count;
                        var reusable = bestResult;
                        bestResult = candidate;
                        candidate = reusable ?? new List<Point3d>(Math.Min(rows * cols / 2, 65536));
                    }
                }
            }

            return bestResult ?? new List<Point3d>();
        }

        /// <summary>
        /// 圆与字形笔画相交判定: 圆心在笔画内(奇偶填充) 或 圆与任一轮廓边界距离小于半径。
        /// 笔画尖端/边缘也能放圆, 圆的整体轮廓与文字外缘齐平, 不会整体缩小。
        /// </summary>
        private static bool IsCircleIntersectsGlyph(
            Point2d center, double radius, List<CurveContainment> containments)
        {
            int crossings = 0;
            for (int index = 0; index < containments.Count; index++)
            {
                CurveContainment containment = containments[index];
                if (containment == null) continue;
                if (center.X < containment.Extents.MinPoint.X ||
                    center.X > containment.Extents.MaxPoint.X ||
                    center.Y < containment.Extents.MinPoint.Y ||
                    center.Y > containment.Extents.MaxPoint.Y)
                    continue;
                if (containment.Contains(center))
                    crossings++;
            }
            if ((crossings & 1) != 0)
                return true;
            double r2 = radius * radius;
            for (int index = 0; index < containments.Count; index++)
            {
                CurveContainment containment = containments[index];
                if (containment == null || containment.Boundary == null ||
                    containment.Boundary.Count < 2)
                    continue;
                Extents3d extents = containment.Extents;
                if (center.X < extents.MinPoint.X - radius ||
                    center.X > extents.MaxPoint.X + radius ||
                    center.Y < extents.MinPoint.Y - radius ||
                    center.Y > extents.MaxPoint.Y + radius)
                    continue;
                List<Point2d> boundary = containment.Boundary;
                int previous = boundary.Count - 1;
                for (int current = 0; current < boundary.Count; current++)
                {
                    if (DistanceToSegmentSquared(center, boundary[previous], boundary[current]) < r2)
                        return true;
                    previous = current;
                }
            }
            return false;
        }

        /// <summary>
        /// 奇偶填充判定: 圆心是否位于字形笔画内 (被奇数条轮廓包围)。
        /// 仅检查圆心, 不要求整个圆入内 —— 圆的整体轮廓因此与文字外缘齐平。
        /// </summary>
        private static bool IsPointInsideGlyph(
            Point2d center, List<CurveContainment> containments)
        {
            int crossings = 0;
            for (int index = 0; index < containments.Count; index++)
            {
                CurveContainment containment = containments[index];
                if (containment == null) continue;
                if (center.X < containment.Extents.MinPoint.X ||
                    center.X > containment.Extents.MaxPoint.X ||
                    center.Y < containment.Extents.MinPoint.Y ||
                    center.Y > containment.Extents.MaxPoint.Y)
                    continue;
                if (containment.Contains(center))
                    crossings++;
            }
            return (crossings & 1) != 0;
        }

        private static bool IsCircleFullyInsideGlyph(
            Point2d center, double radius, List<CurveContainment> containments)
        {
            int crossings = 0;
            for (int index = 0; index < containments.Count; index++)
            {
                CurveContainment containment = containments[index];
                if (containment == null) continue;
                if (center.X < containment.Extents.MinPoint.X ||
                    center.X > containment.Extents.MaxPoint.X ||
                    center.Y < containment.Extents.MinPoint.Y ||
                    center.Y > containment.Extents.MaxPoint.Y)
                    continue;
                if (containment.Contains(center))
                    crossings++;
            }

            if ((crossings & 1) == 0)
                return false;
            if (radius <= 1e-9)
                return true;

            double clearance = Math.Max(0.0, radius - 1e-7);
            double clearanceSquared = clearance * clearance;
            for (int index = 0; index < containments.Count; index++)
            {
                CurveContainment containment = containments[index];
                if (containment == null || containment.Boundary == null ||
                    containment.Boundary.Count < 2)
                    continue;

                Extents3d extents = containment.Extents;
                if (center.X < extents.MinPoint.X - radius ||
                    center.X > extents.MaxPoint.X + radius ||
                    center.Y < extents.MinPoint.Y - radius ||
                    center.Y > extents.MaxPoint.Y + radius)
                    continue;

                List<Point2d> boundary = containment.Boundary;
                int previous = boundary.Count - 1;
                for (int current = 0; current < boundary.Count; current++)
                {
                    if (DistanceToSegmentSquared(center, boundary[previous], boundary[current]) <
                        clearanceSquared)
                        return false;
                    previous = current;
                }
            }

            return true;
        }

    }
}
