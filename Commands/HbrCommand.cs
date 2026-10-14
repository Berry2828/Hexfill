using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Exception = System.Exception;

namespace HexFill
{
    public partial class HexFill
    {
        [CommandMethod("HBR")]
        public void HBR()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            db = doc.Database;
            ed = doc.Editor;

            try
            {
                var filter = new SelectionFilter(new[] { new TypedValue(0, REGION_FILTER) });
                var selRes = ed.GetSelection(filter);
                if (selRes.Status != PromptStatus.OK)
                    return;

                ObjectId[] regionIds = selRes.Value.GetObjectIds();

                var rootNumbers = new List<string>();
                var rootSet = new HashSet<string>();
                var rootLeafIds = new Dictionary<string, List<ObjectId>>();
                var skippedEntityIds = new HashSet<ObjectId>();

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    EnsureRegAppsRegistered(tr, new[] { GGE_NUM_APP, GGE_ISLEAF_APP, HCLN_CLEARED_APP });

                    foreach (ObjectId id in regionIds)
                    {
                        try
                        {
                            var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null)
                            {
                                skippedEntityIds.Add(id);
                                continue;
                            }
                            if (IsCircleInDeleteRange(ent))
                                continue;
                            if (!(ent is Curve curve) || !IsCurveClosed(curve))
                            {
                                skippedEntityIds.Add(id);
                                continue;
                            }

                            if (!IsActiveNumberedLeaf(ent))
                                continue;

                            string num = GetEntityNumber(ent);
                            string rootNum = GetRootNumber(num);
                            if (!rootLeafIds.TryGetValue(rootNum, out var leafIds))
                            {
                                leafIds = new List<ObjectId>();
                                rootLeafIds[rootNum] = leafIds;
                            }
                            leafIds.Add(id);

                            if (rootSet.Add(rootNum))
                                rootNumbers.Add(rootNum);
                        }
                        catch (Exception)
                        {
                            skippedEntityIds.Add(id);
                        }
                    }
                    tr.Commit();
                }

                if (rootNumbers.Count == 0)
                {
                    string skipNote = skippedEntityIds.Count > 0
                        ? $" 已跳过 {skippedEntityIds.Count} 个异常或失效区域。"
                        : string.Empty;
                    ed.WriteMessage($"\n没有找到叶子区域，请先使用HNM命令编号。{skipNote}");
                    return;
                }

                rootNumbers.Sort(StringComparer.OrdinalIgnoreCase);
                var allCircles = CollectAllCirclesInRadiusRange();
                var rootDirection = new Dictionary<string, string>();

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (string rootNum in rootNumbers)
                    {
                        int yCount = 0;
                        int xCount = 0;

                        if (rootLeafIds.TryGetValue(rootNum, out var leafIds))
                        {
                            foreach (ObjectId leafId in leafIds)
                            {
                                try
                                {
                                    var ent = tr.GetObject(leafId, OpenMode.ForRead) as Entity;
                                    if (!(ent is Curve curve) || !IsCurveClosed(curve))
                                    {
                                        skippedEntityIds.Add(leafId);
                                        continue;
                                    }

                                    var centers = CollectCircleCentersInRegion(
                                        allCircles,
                                        ent.GeometricExtents,
                                        curve);
                                    if (centers.Count < 3)
                                        continue;

                                    if (DetectHoneycombDirection(centers))
                                        yCount++;
                                    else
                                        xCount++;
                                }
                                catch (Exception)
                                {
                                    skippedEntityIds.Add(leafId);
                                }
                            }
                        }

                        rootDirection[rootNum] = yCount > xCount ? "Y" : "X";
                    }
                    tr.Commit();
                }

                string filename = BuildCsvPath("Brightness");
                // UTF-8 BOM 让新旧 Excel 都能正确识别中文表头；所有字段统一按 CSV 规则写出，
                // HAD 端同时兼容 Excel 另存为时产生的分号/制表符分隔格式。
                using (var file = new StreamWriter(filename, false, new UTF8Encoding(true)))
                {
                    file.WriteLine("编号,当前亮度,目标亮度,排列方向");
                    foreach (string rootNum in rootNumbers)
                    {
                        string direction = rootDirection.TryGetValue(rootNum, out var value)
                            ? value
                            : "X";
                        file.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "\"{0}\",,,\"{1}\"", rootNum.Replace("\"", "\"\""), direction));
                    }
                }

                _lastHbrExportDirectory = Path.GetDirectoryName(filename);
                SaveLastHbrExportDirectory(_lastHbrExportDirectory);

                string skippedMessage = skippedEntityIds.Count > 0
                    ? $"；跳过 {skippedEntityIds.Count} 个异常或失效区域"
                    : string.Empty;
                ed.WriteMessage($"\nHBR：已导出 {rootNumbers.Count} 个根区域到 {filename}{skippedMessage}；填写亮度后使用 HAD 导入。");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n错误: {ex.Message}");
            }
        }

        // 说明：BuildCsvPath 共享方法位于 HexFill.cs（Full 与 Lite 均可编译）
    }
}
