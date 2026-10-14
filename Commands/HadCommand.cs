using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Exception = System.Exception;

namespace HexFill
{
    public partial class HexFill
    {
        private class HadLeafRegion
        {
            public ObjectId Id;
            public string Number;
            public string RootNumber;
            public double Spacing;
            public bool VirtualSpacing;
            public bool YDirection;
            public double Radius;
        }

        [CommandMethod("HAD")]
        public void HAD()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            db = doc.Database;
            ed = doc.Editor;

            try
            {
                // ===== 第1步：选择 HBR 导出的 CSV =====
                // 恢复 2026-08-07 V11 的路径联动：优先打开最近一次 HBR 导出目录，
                // AutoCAD 重启后从注册表恢复；目录失效时才回退桌面。
                string initialDirectory = _lastHbrExportDirectory;
                if (string.IsNullOrEmpty(initialDirectory))
                    initialDirectory = GetLastHbrExportDirectory();
                if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
                    initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                string spacingFilename;
                bool filediaRestore = false;
                if (Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("FILEDIA")) == 0)
                {
                    // FILEDIA=0 会禁用文件对话框; HAD 必须选文件, 临时开启并在结束后恢复。
                    Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("FILEDIA", (short)1);
                    filediaRestore = true;
                }
                try
                {
                    spacingFilename = SelectImportCsv("选择亮度数据文件（HBR导出的CSV）", initialDirectory, "HexFillHAD");
                    if (spacingFilename == null)
                    {
                        ed.WriteMessage("\n未选择文件，操作取消。");
                        return;
                    }
                }
                finally
                {
                    if (filediaRestore)
                    {
                        // FILEDIA 是用户硬性要求：命令结束后仍保持 1，避免后续文件对话框失效。
                        Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("FILEDIA", (short)1);
                    }
                }

                // 校验文件名前缀，防止混用（HAD只能使用HBR导出的Brightness_前缀CSV）
                string filePrefix = Path.GetFileNameWithoutExtension(spacingFilename);
                if (filePrefix.IndexOf("Brightness_", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    ed.WriteMessage("\n错误：此CSV不是亮度数据文件（HBR导出），文件名需包含 Brightness_。");
                    return;
                }

                // 读取 Excel 另存后的亮度表：兼容 UTF-8/UTF-16 BOM、系统本地编码和不同分隔符。
                string[] allBrightLines = ReadCsvLinesWithExcelEncoding(spacingFilename);
                if (allBrightLines.Length < 2)
                {
                    ed.WriteMessage("\n错误：亮度文件没有有效表头或数据行。");
                    return;
                }
                var brightLines = allBrightLines.Skip(1).ToList();
                var rootNumbers = new List<string>();
                var rootCurrBright = new Dictionary<string, double>();
                var rootTgtBright = new Dictionary<string, double>();
                var rootCsvDirection = new Dictionary<string, string>();
                int malformedRowCount = 0;
                int invalidNumericRowCount = 0;
                int invalidDirectionCount = 0;
                int duplicateRootCount = 0;

                for (int lineIndex = 0; lineIndex < brightLines.Count; lineIndex++)
                {
                    string line = brightLines[lineIndex];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (HasUnclosedCsvQuote(line))
                    {
                        malformedRowCount++;
                        continue;
                    }

                    var parts = ParseCSVLine(line);
                    int fieldCount = parts.Length;
                    while (fieldCount > 4 && string.IsNullOrEmpty(parts[fieldCount - 1]))
                        fieldCount--;
                    if (fieldCount < 3 || fieldCount > 4)
                    {
                        malformedRowCount++;
                        continue;
                    }

                    string num = NormalizeImportedRootNumber(parts[0]);
                    if (string.IsNullOrEmpty(num))
                    {
                        malformedRowCount++;
                        continue;
                    }

                    // 空亮度字段视为"未填写"（缺失），交给后面的缺失检查给出补充指引；
                    // 只有非空但解析失败的字段才计为非法数值。
                    string currentField = NormalizeCsvField(parts[1]);
                    string targetField = NormalizeCsvField(parts[2]);
                    bool currentBlank = string.IsNullOrEmpty(currentField);
                    bool targetBlank = string.IsNullOrEmpty(targetField);
                    double currBright = 0;
                    double tgtBright = 0;
                    bool currentParsed = !currentBlank && TryParseDoubleAnyCulture(currentField, out currBright);
                    bool targetParsed = !targetBlank && TryParseDoubleAnyCulture(targetField, out tgtBright);
                    bool numericInvalid = (!currentParsed && !currentBlank) ||
                        (!targetParsed && !targetBlank) ||
                        double.IsNaN(currBright) || double.IsInfinity(currBright) ||
                        double.IsNaN(tgtBright) || double.IsInfinity(tgtBright);
                    if (numericInvalid)
                    {
                        invalidNumericRowCount++;
                        continue;
                    }

                    // Duplicate root numbers use the first valid record, preserving CSV order deterministically.
                    if (rootCurrBright.ContainsKey(num))
                    {
                        duplicateRootCount++;
                        continue;
                    }

                    rootNumbers.Add(num);
                    rootCurrBright[num] = currBright;
                    rootTgtBright[num] = tgtBright;
                    if (fieldCount == 4)
                    {
                        string dir = NormalizeCsvField(parts[3]).ToUpperInvariant();
                        if (dir == "X" || dir == "Y")
                            rootCsvDirection[num] = dir;
                        else
                            invalidDirectionCount++;
                    }
                }

                if (malformedRowCount > 0 || invalidNumericRowCount > 0 ||
                    invalidDirectionCount > 0 || duplicateRootCount > 0)
                {
                    var importWarnings = new List<string>();
                    if (malformedRowCount > 0)
                        importWarnings.Add($"坏行 {malformedRowCount} 行");
                    if (invalidNumericRowCount > 0)
                        importWarnings.Add($"非法数值 {invalidNumericRowCount} 行");
                    if (invalidDirectionCount > 0)
                        importWarnings.Add($"非法方向 {invalidDirectionCount} 个（按实测方向）");
                    if (duplicateRootCount > 0)
                        importWarnings.Add($"重复编号 {duplicateRootCount} 个，按首次记录处理");
                    ed.WriteMessage("\nHAD警告：" + string.Join("；", importWarnings) + "。有效记录将继续处理。");
                }

                if (rootNumbers.Count == 0)
                {
                    ed.WriteMessage("\nCSV没有有效数据。");
                    return;
                }

                // 检查亮度数据是否已填写（单次遍历同时统计缺失与有效根数量）
                int missingBright = 0;
                int validRootCount = 0;
                for (int i = 0; i < rootNumbers.Count; i++)
                {
                    string num = rootNumbers[i];
                    bool valid = rootCurrBright[num] > 0 && rootTgtBright[num] > 0;
                    if (valid)
                        validRootCount++;
                    else
                        missingBright++;
                }
                if (missingBright > 0)
                {
                    ed.WriteMessage($"\nHAD：{missingBright} 个根区域亮度无效，已跳过；请补充亮度后重试。");
                }

                if (validRootCount == 0)
                {
                    ed.WriteMessage("\n没有有效的亮度数据，无法计算。请先在CSV中填写亮度值。");
                    return;
                }

                // ===== 第2步：从CAD收集区域并计算新间距 =====
                // 收集有效亮度用于均匀度计算
                var validCurrBright = new List<double>();
                var validTgtBright = new List<double>();

                // 从CAD中收集所有区域，按根区域分组
                var cadFilter = new SelectionFilter(new TypedValue[] { new TypedValue(0, REGION_FILTER) });
                var cadSelRes = ed.GetSelection(cadFilter);
                if (cadSelRes.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n未选择区域，无法计算。");
                    return;
                }
                ObjectId[] cadRegionIds = cadSelRes.Value.GetObjectIds();

                // 收集圆用于间距计算
                var hadAllCircles = CollectAllCirclesInRadiusRange();

                // 按根区域分组CAD中的叶子区域
                var cadLeafRegions = new Dictionary<string, List<HadLeafRegion>>();
                using (var trGroup = db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId rid in cadRegionIds)
                    {
                        var ent = trGroup.GetObject(rid, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        var num = GetEntityNumber(ent);
                        var isLeaf = GetXDataString(ent, GGE_ISLEAF_APP);
                        if (string.IsNullOrEmpty(num) || isLeaf != "1") continue;
                        if (!(ent is Curve curve) || !IsCurveClosed(curve)) continue;

                        string rootNum = GetRootNumber(num);
                        if (!cadLeafRegions.ContainsKey(rootNum))
                            cadLeafRegions[rootNum] = new List<HadLeafRegion>();

                        var ext = ent.GeometricExtents;
                        var circleRecords = CollectCircleRecordsInRegion(hadAllCircles, ext, curve);
                        var centers = new List<Point2d>(circleRecords.Count);
                        for (int circleIndex = 0; circleIndex < circleRecords.Count; circleIndex++)
                            centers.Add(Point3dToPoint2d(circleRecords[circleIndex].Center));
                        double spacing;
                        bool virtualSpacing = false;
                        if (centers.Count >= 2)
                        {
                            spacing = CalculateAverageNearestNeighborSpacing(centers);
                        }
                        else if (centers.Count == 1 && curve is Polyline pl)
                        {
                            var cp = centers[0];
                            double maxDist = 0;
                            for (int i = 0; i < pl.NumberOfVertices; i++)
                            {
                                var vtx = pl.GetPoint3dAt(i);
                                double dist = Point2dDistance(cp, Point3dToPoint2d(vtx));
                                if (dist > maxDist) maxDist = dist;
                            }
                            spacing = maxDist > 0 ? maxDist : -1.0;
                            virtualSpacing = spacing > 0;
                        }
                        else
                        {
                            spacing = -1.0;
                        }

                        cadLeafRegions[rootNum].Add(new HadLeafRegion
                        {
                            Id = rid,
                            Number = num,
                            RootNumber = rootNum,
                            Spacing = spacing,
                            VirtualSpacing = virtualSpacing,
                            YDirection = centers.Count >= 3 && DetectHoneycombDirection(centers),
                            Radius = DetectCircleRadius(circleRecords)
                        });
                    }
                    trGroup.Commit();
                }

                var regionsToProcess = new List<Tuple<ObjectId, double, bool, double, bool>>();
                int skipCount = 0;
                foreach (string rootNum in rootNumbers)
                {
                    double currBright = rootCurrBright[rootNum];
                    double tgtBright = rootTgtBright[rootNum];

                    if (currBright <= 0 || tgtBright <= 0) continue;

                    double brightnessRatio = currBright / tgtBright;

                    // 均匀度统计口径 (v11): 所有 CSV 根区域均计入, 与图纸中是否存在无关
                    validCurrBright.Add(currBright);
                    validTgtBright.Add(tgtBright);

                    // 找到该根区域下所有叶子区域（从CAD实时数据）
                    if (!cadLeafRegions.ContainsKey(rootNum)) continue;
                    var leafItems = cadLeafRegions[rootNum];

                    if (leafItems.Count == 0) continue;

                    double avgOldSpacing = 0;
                    int spacedLeafCount = 0;
                    for (int leafIndex = 0; leafIndex < leafItems.Count; leafIndex++)
                    {
                        double leafSpacing = leafItems[leafIndex].Spacing;
                        if (leafSpacing > 0)
                        {
                            avgOldSpacing += leafSpacing;
                            spacedLeafCount++;
                        }
                    }
                    if (spacedLeafCount > 0)
                        avgOldSpacing /= spacedLeafCount;
                    double avgNewSpacing = 0;
                    int leafCount = 0;

                    foreach (var leaf in leafItems)
                    {
                        double oldSpacing = leaf.Spacing;
                        if (oldSpacing <= 0) continue;
                        // 只有一个圆时，减少亮度则维持不变（已是最少）
                        if (leaf.VirtualSpacing && brightnessRatio >= 1) continue;

                        double newSpacing = oldSpacing * Math.Sqrt(brightnessRatio);

                        // 物理下限：间距不得小于会话级最小间距 (HDIA 控制)
                        double leafMinSpacing = CurrentMinSpacing;
                        if (newSpacing < leafMinSpacing)
                            newSpacing = leafMinSpacing;

                        avgNewSpacing += newSpacing;
                        leafCount++;
                        double tolerance = Math.Max(newSpacing * 0.01, 0.001);
                        bool atMinimum = HadSpacingAtMinimum(newSpacing);
                        bool rebuild = Math.Abs(oldSpacing - newSpacing) > tolerance
                            || (atMinimum && !HadSpacingAtMinimum(oldSpacing));
                        if (!rebuild && !atMinimum)
                        {
                            skipCount++;
                            continue;
                        }
                        bool direction = rootCsvDirection.TryGetValue(rootNum, out string csvDirection)
                            ? csvDirection == "Y"
                            : leaf.YDirection;
                        regionsToProcess.Add(Tuple.Create(leaf.Id, newSpacing, direction, leaf.Radius, rebuild));
                    }
                    if (leafCount > 0) avgNewSpacing /= leafCount;

                }

                // ===== 第3步：均匀度信息 =====
                if (validCurrBright.Count > 0)
                {
                    double minCurr = validCurrBright.Min();
                    double maxCurr = validCurrBright.Max();
                    double currentUniformity = maxCurr > 0 ? minCurr / maxCurr : 0;

                    double minTgt = validTgtBright.Min();
                    double maxTgt = validTgtBright.Max();
                    double estimatedUniformity = maxTgt > 0 ? minTgt / maxTgt : 0;

                    ed.WriteMessage($"\nHAD：均匀度 {currentUniformity:P1}→{estimatedUniformity:P1}（当前 {minCurr:F1}-{maxCurr:F1}，目标 {minTgt:F1}-{maxTgt:F1}）。");
                }

                if (regionsToProcess.Count == 0)
                {
                    ed.WriteMessage("\n所有区域间距已一致，无需处理。");
                    return;
                }

                using (var trInit = db.TransactionManager.StartTransaction())
                {
                    EnsureRegAppsRegistered(trInit, new[] { GGE_NUM_APP, GGE_TEXT_HANDLE_APP, GGE_ISLEAF_APP });
                    trInit.Commit();
                }
                int totalDeleted = 0;
                int totalCircles = 0;
                int processedRegions = 0;
                int failedRegions = 0;
                int minimumRegions = 0;
                int markedCircles = 0;
                for (int i = 0; i < regionsToProcess.Count; i++)
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            var ent = tr.GetObject(regionsToProcess[i].Item1, OpenMode.ForRead) as Entity;
                            if (ent == null) { tr.Commit(); continue; }
                            if (!(ent is Curve curve) || !IsCurveClosed(curve)) { tr.Commit(); continue; }

                            double targetSpacing = regionsToProcess[i].Item2;
                            bool hadYDirection = regionsToProcess[i].Item3;
                            double genRadius = regionsToProcess[i].Item4;

                            if (!regionsToProcess[i].Item5)
                            {
                                var existing = CollectCircleRecordsInRegion(hadAllCircles, ent.GeometricExtents, curve);
                                int marked = MarkHadMinimumCircles(existing, tr);
                                tr.Commit();
                                processedRegions++;
                                minimumRegions++;
                                markedCircles += marked;
                                continue;
                            }

                            int deletedCount;
                            int newCount;
                            List<CircleRecord> removedCircles;
                            List<CircleRecord> createdCircles;
                            if (!TryReplaceCirclesWithGrid(
                                ent, tr, targetSpacing, hadYDirection, genRadius,
                                hadAllCircles, null,
                                out deletedCount, out newCount,
                                out removedCircles, out createdCircles))
                            {
                                ed.WriteMessage($"\n[{i + 1}/{regionsToProcess.Count}] 无法生成新圆，已保留旧圆并标记异常颜色。");
                                tr.Commit();
                                failedRegions++;
                                continue;
                            }

                            int newlyMarked = HadSpacingAtMinimum(targetSpacing)
                                ? MarkHadMinimumCircles(createdCircles, tr) : 0;
                            tr.Commit();
                            processedRegions++;
                            if (HadSpacingAtMinimum(targetSpacing)) minimumRegions++;
                            markedCircles += newlyMarked;
                            totalDeleted += deletedCount;
                            totalCircles += newCount;
                            hadAllCircles.ApplyReplacement(removedCircles, createdCircles);
                            if ((i + 1) % 20 == 0)
                            {
                                ed.UpdateScreen();
                            }
                        }
                        catch (OperationCanceledException ex)
                        {
                            tr.Abort();
                            ed.WriteMessage("\nHAD：" + ex.Message);
                            return;
                        }
                        catch (Exception ex)
                        {
                            failedRegions++;
                            ed.WriteMessage($"\n[{i + 1}/{regionsToProcess.Count}] 更新异常，原圆已回滚保留: {ex.Message}");
                            try { tr.Abort(); } catch (Exception abortEx) { ed?.WriteMessage("\n[警告] 事务回滚失败: " + abortEx.Message); }
                        }
                    }
                }
                ed.UpdateScreen();

                ed.WriteMessage($"\nHAD：处理 {processedRegions} 个区域，跳过 {skipCount} 个，失败 {failedRegions} 个，删除 {totalDeleted} 个旧圆，生成 {totalCircles} 个圆；{minimumRegions} 个区域达到最小间距，{markedCircles} 个网点标红。");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n错误: {ex.Message}");
            }
        }

        private bool HadSpacingAtMinimum(double spacing)
        {
            return spacing <= CurrentMinSpacing + Math.Max(1e-8, CurrentMinSpacing * 1e-6);
        }

        private static int MarkHadMinimumCircles(List<CircleRecord> circles, Transaction tr)
        {
            ValidateCircleRecordsWritable(circles, tr);
            foreach (CircleRecord record in circles)
            {
                Circle circle = (Circle)tr.GetObject(record.Id, OpenMode.ForWrite, false);
                circle.ColorIndex = 1;
            }
            return circles.Count;
        }

        private static string NormalizeImportedRootNumber(string value)
        {
            string result = NormalizeCsvField(value);
            if (result.StartsWith("'")) result = result.Substring(1).Trim();
            if (result.StartsWith("=\"") && result.EndsWith("\"") && result.Length >= 4)
                result = result.Substring(2, result.Length - 3);
            return result.Trim();
        }

        private static bool HasUnclosedCsvQuote(string line)
        {
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] != '"') continue;
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    i++;
                    continue;
                }
                quoted = !quoted;
            }
            return quoted;
        }



    }
}
