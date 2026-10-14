using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace HexFill
{
    public partial class HexFill
    {
        private const string HgapMarkerLayer = "HGAP_未闭合标识";

        private class HgapOpenPolyline
        {
            public ObjectId Id;
            public Point2d Start;
            public Point2d End;
            public double Gap;
            public bool CanModify;
        }

        private class HgapSegment
        {
            public Point2d Start;
            public Point2d End;
            public ObjectId SourceId;
        }

        private class HgapChain
        {
            public List<Point2d> Points = new List<Point2d>();
            public ObjectId SourceId;
        }

        private class HgapCleanupResult
        {
            public int SourceCount;
            public int CreatedCount;
            public int RemovedOverlapCount;
            public List<ObjectId> CandidateIds = new List<ObjectId>();
        }

        [CommandMethod("HGAP")]
        public void HGAP()
        {
            Document document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            Database database = document.Database;
            Editor editor = document.Editor;

            try
            {
                PromptSelectionOptions options = new PromptSelectionOptions();
                options.MessageForAdding = "\n选择或框选要检查闭合缺口的聚合线: ";
                SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE,LINE") });
                PromptSelectionResult selection = editor.GetSelection(options, filter);
                if (selection.Status != PromptStatus.OK || selection.Value == null) return;

                ObjectId[] selectedIds = selection.Value.GetObjectIds();
                HgapCleanupResult cleanupResult;
                using (Transaction tr = database.TransactionManager.StartTransaction())
                {
                    cleanupResult = CleanupHgapGeometry(database, tr, selectedIds);
                    tr.Commit();
                }

                ObjectId[] candidateIds = cleanupResult.CandidateIds.Count > 0 ? cleanupResult.CandidateIds.ToArray() : selectedIds;
                List<HgapOpenPolyline> openPolylines = GetHgapOpenPolylines(database, candidateIds);
                if (openPolylines.Count == 0)
                {
                    using (Transaction tr = database.TransactionManager.StartTransaction())
                    {
                        ClearHgapMarkers(database, tr);
                        tr.Commit();
                    }
                    editor.WriteMessage("\nHGAP：未发现未闭合聚合线，已清理旧标识。");
                    if (cleanupResult.SourceCount > 0)
                        editor.WriteMessage(string.Format("\nHGAP：已将 {0} 个相连源对象聚合为 {1} 条聚合线，清理 {2} 条重复/重叠线段。", cleanupResult.SourceCount, cleanupResult.CreatedCount, cleanupResult.RemovedOverlapCount));
                    return;
                }

                double autoCloseTolerance = HsetHgapSmallGapTolerance;

                int closedCount = 0;
                int markedCount = 0;
                using (Transaction tr = database.TransactionManager.StartTransaction())
                {
                    ClearHgapMarkers(database, tr);
                    ObjectId markerLayerId = ObjectId.Null;
                    BlockTableRecord space = (BlockTableRecord)tr.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);

                    for (int i = 0; i < openPolylines.Count; i++)
                    {
                        HgapOpenPolyline item = openPolylines[i];
                        bool shouldClose = item.CanModify && item.Gap <= autoCloseTolerance;
                        if (shouldClose)
                        {
                            Polyline polyline = tr.GetObject(item.Id, OpenMode.ForWrite, false) as Polyline;
                            if (polyline != null && !polyline.Closed)
                            {
                                if (TryHgapCleanClosePolyline(polyline, autoCloseTolerance))
                                {
                                    closedCount++;
                                    continue;
                                }
                            }
                        }

                        if (markerLayerId.IsNull)
                            markerLayerId = GetOrCreateHgapLayer(database, tr);
                        CreateHgapMarker(space, tr, markerLayerId, item);
                        markedCount++;
                    }

                    tr.Commit();
                }

                editor.WriteMessage(string.Format("\nHGAP：闭合 {0} 条缺口，保留 {1} 处标识；聚合 {2} 个源对象为 {3} 条聚合线，清理 {4} 条重叠线。", closedCount, markedCount, cleanupResult.SourceCount, cleanupResult.CreatedCount, cleanupResult.RemovedOverlapCount));
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage(string.Format("\nHGAP错误：{0}", ex.Message));
            }
        }

        private static List<HgapOpenPolyline> GetHgapOpenPolylines(Database database, ObjectId[] objectIds)
        {
            List<HgapOpenPolyline> result = new List<HgapOpenPolyline>();
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                for (int i = 0; i < objectIds.Length; i++)
                {
                    Polyline polyline = null;
                    try
                    {
                        polyline = tr.GetObject(objectIds[i], OpenMode.ForRead, false) as Polyline;
                    }
                    catch
                    {
                        continue;
                    }
                    if (polyline == null || polyline.IsErased || polyline.Closed || polyline.NumberOfVertices < 2) continue;

                    Point2d start = polyline.GetPoint2dAt(0);
                    Point2d end = polyline.GetPoint2dAt(polyline.NumberOfVertices - 1);
                    double gap = start.GetDistanceTo(end);
                    result.Add(new HgapOpenPolyline { Id = objectIds[i], Start = start, End = end, Gap = gap, CanModify = !HgapLayerIsLocked(tr, polyline) });
                }
                tr.Commit();
            }
            return result;
        }

        private static HgapCleanupResult CleanupHgapGeometry(Database database, Transaction tr, ObjectId[] objectIds)
        {
            HgapCleanupResult result = new HgapCleanupResult();
            List<HgapSegment> segments = new List<HgapSegment>();
            HashSet<ObjectId> selfOverlapSourceIds = new HashSet<ObjectId>();

            for (int i = 0; i < objectIds.Length; i++)
            {
                Entity entity = tr.GetObject(objectIds[i], OpenMode.ForRead, false) as Entity;
                if (entity == null || entity.IsErased) continue;
                if (HgapLayerIsLocked(tr, entity)) continue;

                Line line = entity as Line;
                if (line != null)
                {
                    AddHgapSegment(segments, new Point2d(line.StartPoint.X, line.StartPoint.Y), new Point2d(line.EndPoint.X, line.EndPoint.Y), objectIds[i]);
                    continue;
                }

                // 聚合线（闭合）：只清理自身顶点问题（零长度边/重复顶点、连续共线冗余顶点），
                // 不拆段、不重建、不参与重复/重叠清理——"重复/重叠聚合线维持不处理"。
                // 有弧段（bulge）的闭合线不清理顶点，避免破坏弧线语义。
                Polyline polyline = entity as Polyline;
                if (polyline == null) continue;
                if (polyline.Closed)
                {
                    if (HgapCanRebuildPolyline(polyline))
                    {
                        polyline.UpgradeOpen();
                        bool closedCleaned = CleanHgapClosedPolylineVertices(polyline, 0.001);
                        if (closedCleaned && polyline.NumberOfVertices < 3)
                        {
                            polyline.Erase();
                            continue;
                        }
                        polyline.DowngradeOpen();
                    }
                    continue;
                }
                if (!HgapCanRebuildPolyline(polyline))
                {
                    continue;
                }

                // 先清理顶点问题：零长度边/重复顶点、连续共线冗余顶点。
                // 清理后不足两个顶点的线视为无效几何，直接删除。
                polyline.UpgradeOpen();
                bool cleaned = CleanHgapPolylineVertices(polyline, 0.001);
                if (cleaned && polyline.NumberOfVertices < 2)
                {
                    polyline.Erase();
                    continue;
                }
                polyline.DowngradeOpen();

                bool hasOverlap = HgapPolylineHasOverlap(polyline, 0.001);
                AddHgapPolylineSegments(segments, polyline, objectIds[i]);
                if (hasOverlap)
                    selfOverlapSourceIds.Add(objectIds[i]);
            }

            if (segments.Count == 0)
            {
                result.CandidateIds.AddRange(objectIds);
                return result;
            }

            HashSet<ObjectId> consumedSourceIds = FindHgapConnectedSourceIds(
                segments, selfOverlapSourceIds, HsetHgapSmallGapTolerance);
            if (consumedSourceIds.Count == 0)
            {
                result.CandidateIds.AddRange(objectIds);
                return result;
            }

            segments = segments.Where(segment => consumedSourceIds.Contains(segment.SourceId)).ToList();
            result.SourceCount = consumedSourceIds.Count;
            int originalSegmentCount = segments.Count;
            List<HgapSegment> mergedSegments = MergeHgapSegments(segments, 0.001);
            result.RemovedOverlapCount = Math.Max(0, originalSegmentCount - mergedSegments.Count);
            List<HgapChain> chains = BuildHgapChains(mergedSegments, HsetHgapSmallGapTolerance);

            BlockTableRecord space = (BlockTableRecord)tr.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
            for (int i = 0; i < chains.Count; i++)
            {
                if (chains[i].Points.Count < 2) continue;
                Polyline polyline = new Polyline();
                polyline.SetDatabaseDefaults();
                Entity sourceEntity = null;
                if (!chains[i].SourceId.IsNull)
                    sourceEntity = tr.GetObject(chains[i].SourceId, OpenMode.ForRead, false) as Entity;
                if (sourceEntity != null)
                    polyline.SetPropertiesFrom(sourceEntity);

                List<Point2d> points = CleanHgapChainPoints(chains[i].Points, 0.001);
                bool closed = points.Count > 2 && points[0].GetDistanceTo(points[points.Count - 1]) <= 0.001;
                if (closed)
                {
                    points.RemoveAt(points.Count - 1);
                    CleanHgapCollinearChainPoints(points, 0.001, true);
                }
                if (points.Count < 2 || (closed && points.Count < 3)) continue;

                for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
                    polyline.AddVertexAt(pointIndex, points[pointIndex], 0.0, 0.0, 0.0);
                polyline.Closed = closed;

                ObjectId newId = space.AppendEntity(polyline);
                tr.AddNewlyCreatedDBObject(polyline, true);
                result.CandidateIds.Add(newId);
                result.CreatedCount++;
            }

            if (result.CreatedCount == 0)
            {
                result.CandidateIds.AddRange(objectIds);
                return result;
            }

            foreach (ObjectId sourceId in consumedSourceIds)
            {
                Entity entity = tr.GetObject(sourceId, OpenMode.ForWrite, false) as Entity;
                if (entity != null && !entity.IsErased)
                    entity.Erase();
            }

            for (int i = 0; i < objectIds.Length; i++)
            {
                if (!consumedSourceIds.Contains(objectIds[i]))
                    result.CandidateIds.Add(objectIds[i]);
            }
            return result;
        }

        private static bool HgapLayerIsLocked(Transaction tr, Entity entity)
        {
            try
            {
                LayerTableRecord layer = tr.GetObject(entity.LayerId, OpenMode.ForRead, false) as LayerTableRecord;
                return layer != null && layer.IsLocked;
            }
            catch
            {
                return true;
            }
        }

        private static HashSet<ObjectId> FindHgapConnectedSourceIds(
            List<HgapSegment> segments,
            HashSet<ObjectId> selfOverlapSourceIds,
            double connectionTolerance)
        {
            HashSet<ObjectId> result = new HashSet<ObjectId>(selfOverlapSourceIds);

            // 结果集合与比较顺序无关，先用按 minX 排序的扫描线预筛选，
            // 把全量 O(n²) 端点/共线比较限制在包围盒相近的段对上。
            // 预筛选 padding 取连接容差与共线合并容差(0.001)的较大值，保证不漏掉任何候选对。
            double padding = Math.Max(connectionTolerance, 0.001);
            int count = segments.Count;
            double[] minX = new double[count];
            double[] maxX = new double[count];
            double[] minY = new double[count];
            double[] maxY = new double[count];
            for (int i = 0; i < count; i++)
            {
                minX[i] = Math.Min(segments[i].Start.X, segments[i].End.X);
                maxX[i] = Math.Max(segments[i].Start.X, segments[i].End.X);
                minY[i] = Math.Min(segments[i].Start.Y, segments[i].End.Y);
                maxY[i] = Math.Max(segments[i].Start.Y, segments[i].End.Y);
            }

            List<int> order = new List<int>(count);
            for (int i = 0; i < count; i++)
                order.Add(i);
            order.Sort((left, right) => minX[left].CompareTo(minX[right]));

            for (int orderIndex = 0; orderIndex < count; orderIndex++)
            {
                int i = order[orderIndex];
                double aMinX = minX[i] - padding;
                double aMaxX = maxX[i] + padding;
                double aMinY = minY[i] - padding;
                double aMaxY = maxY[i] + padding;
                for (int nextOrderIndex = orderIndex + 1; nextOrderIndex < count; nextOrderIndex++)
                {
                    int j = order[nextOrderIndex];
                    if (minX[j] > aMaxX) break;
                    if (maxX[j] < aMinX || maxY[j] < aMinY || minY[j] > aMaxY) continue;
                    if (segments[i].SourceId == segments[j].SourceId)
                        continue;
                    if (!HgapSegmentsConnect(segments[i], segments[j], connectionTolerance))
                        continue;
                    result.Add(segments[i].SourceId);
                    result.Add(segments[j].SourceId);
                }
            }
            return result;
        }

        private static bool HgapSegmentsConnect(HgapSegment first, HgapSegment second, double tolerance)
        {
            if (first.Start.GetDistanceTo(second.Start) <= tolerance ||
                first.Start.GetDistanceTo(second.End) <= tolerance ||
                first.End.GetDistanceTo(second.Start) <= tolerance ||
                first.End.GetDistanceTo(second.End) <= tolerance)
                return true;

            HgapSegment merged;
            return TryMergeHgapSegment(first, second, 0.001, out merged);
        }

        private static bool HgapCanRebuildPolyline(Polyline polyline)
        {
            if (polyline.NumberOfVertices < 2) return false;
            int segmentCount = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
            for (int i = 0; i < segmentCount; i++)
            {
                if (Math.Abs(polyline.GetBulgeAt(i)) > 1e-9)
                    return false;
            }
            return true;
        }

        private static bool HgapPolylineHasOverlap(Polyline polyline, double tolerance)
        {
            List<HgapSegment> segments = new List<HgapSegment>();
            AddHgapPolylineSegments(segments, polyline, ObjectId.Null);
            for (int i = 0; i < segments.Count; i++)
            {
                for (int j = i + 1; j < segments.Count; j++)
                {
                    HgapSegment merged;
                    if (TryMergeHgapSegment(segments[i], segments[j], tolerance, out merged))
                        return true;
                }
            }
            return false;
        }

        private static void AddHgapPolylineSegments(List<HgapSegment> segments, Polyline polyline, ObjectId sourceId)
        {
            int segmentCount = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
            for (int i = 0; i < segmentCount; i++)
                AddHgapSegment(segments, polyline.GetPoint2dAt(i), polyline.GetPoint2dAt((i + 1) % polyline.NumberOfVertices), sourceId);
        }

        private static void AddHgapSegment(List<HgapSegment> segments, Point2d start, Point2d end, ObjectId sourceId)
        {
            if (start.GetDistanceTo(end) <= 0.000001) return;
            segments.Add(new HgapSegment { Start = start, End = end, SourceId = sourceId });
        }

        private static List<HgapSegment> MergeHgapSegments(List<HgapSegment> segments, double tolerance)
        {
            List<HgapSegment> merged = new List<HgapSegment>();
            for (int i = 0; i < segments.Count; i++)
            {
                HgapSegment current = segments[i];
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    for (int j = 0; j < merged.Count; j++)
                    {
                        HgapSegment combined;
                        if (TryMergeHgapSegment(merged[j], current, tolerance, out combined))
                        {
                            current = combined;
                            merged.RemoveAt(j);
                            changed = true;
                            break;
                        }
                    }
                }
                merged.Add(current);
            }
            return merged;
        }

        private static bool TryMergeHgapSegment(HgapSegment a, HgapSegment b, double tolerance, out HgapSegment merged)
        {
            merged = null;
            Vector2d direction = a.End - a.Start;
            double length = direction.Length;
            if (length <= 0.000001) return false;
            direction = direction / length;

            if (!HgapPointOnLine(b.Start, a.Start, direction, tolerance) || !HgapPointOnLine(b.End, a.Start, direction, tolerance))
                return false;

            double a0 = 0.0;
            double a1 = length;
            double b0 = HgapProjection(b.Start, a.Start, direction);
            double b1 = HgapProjection(b.End, a.Start, direction);
            if (b1 < b0)
            {
                double tmp = b0;
                b0 = b1;
                b1 = tmp;
            }

            if (Math.Min(a1, b1) - Math.Max(a0, b0) <= tolerance)
                return false;

            double min = Math.Min(a0, b0);
            double max = Math.Max(a1, b1);
            merged = new HgapSegment
            {
                Start = a.Start + direction * min,
                End = a.Start + direction * max,
                SourceId = a.SourceId
            };
            return true;
        }

        private static bool HgapPointOnLine(Point2d point, Point2d linePoint, Vector2d direction, double tolerance)
        {
            Vector2d toPoint = point - linePoint;
            return Math.Abs(toPoint.X * direction.Y - toPoint.Y * direction.X) <= tolerance;
        }

        private static double HgapProjection(Point2d point, Point2d origin, Vector2d direction)
        {
            Vector2d toPoint = point - origin;
            return toPoint.X * direction.X + toPoint.Y * direction.Y;
        }

        private static List<HgapChain> BuildHgapChains(List<HgapSegment> segments, double tolerance)
        {
            List<HgapChain> chains = new List<HgapChain>();
            bool[] used = new bool[segments.Count];
            for (int i = 0; i < segments.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                HgapChain chain = new HgapChain();
                chain.SourceId = segments[i].SourceId;
                chain.Points.Add(segments[i].Start);
                chain.Points.Add(segments[i].End);

                bool changed = true;
                while (changed)
                {
                    changed = false;
                    for (int j = 0; j < segments.Count; j++)
                    {
                        if (used[j]) continue;
                        if (TryAppendHgapSegment(chain.Points, segments[j], tolerance))
                        {
                            used[j] = true;
                            changed = true;
                            break;
                        }
                    }
                }
                chains.Add(chain);
            }
            return chains;
        }

        private static bool TryAppendHgapSegment(List<Point2d> points, HgapSegment segment, double tolerance)
        {
            Point2d first = points[0];
            Point2d last = points[points.Count - 1];
            if (last.GetDistanceTo(segment.Start) <= tolerance)
            {
                points.Add(segment.Start);
                points.Add(segment.End);
                return true;
            }
            if (last.GetDistanceTo(segment.End) <= tolerance)
            {
                points.Add(segment.End);
                points.Add(segment.Start);
                return true;
            }
            if (first.GetDistanceTo(segment.End) <= tolerance)
            {
                points.Insert(0, segment.End);
                points.Insert(0, segment.Start);
                return true;
            }
            if (first.GetDistanceTo(segment.Start) <= tolerance)
            {
                points.Insert(0, segment.Start);
                points.Insert(0, segment.End);
                return true;
            }
            return false;
        }

        private static List<Point2d> CleanHgapChainPoints(List<Point2d> points, double tolerance)
        {
            List<Point2d> clean = new List<Point2d>();
            for (int i = 0; i < points.Count; i++)
            {
                if (clean.Count == 0 || clean[clean.Count - 1].GetDistanceTo(points[i]) > tolerance)
                    clean.Add(points[i]);
            }
            CleanHgapCollinearChainPoints(clean, tolerance, false);
            return clean;
        }

        // 清理单条开放线的顶点问题：零长度边/重复顶点（相邻点重合）、连续共线冗余顶点。
        // 返回是否有顶点被删除；调用方需在清理后检查 NumberOfVertices < 2 时删除该线。
        private static bool CleanHgapPolylineVertices(Polyline polyline, double tolerance)
        {
            bool changed = false;
            for (int i = polyline.NumberOfVertices - 1; i >= 1; i--)
            {
                if (polyline.GetPoint2dAt(i).GetDistanceTo(polyline.GetPoint2dAt(i - 1)) <= tolerance)
                {
                    polyline.RemoveVertexAt(i);
                    changed = true;
                }
            }
            while (true)
            {
                bool progress = false;
                for (int i = 1; i < polyline.NumberOfVertices - 1; i++)
                {
                    if (HgapIsRedundantCollinearVertex(
                        polyline.GetPoint2dAt(i - 1), polyline.GetPoint2dAt(i), polyline.GetPoint2dAt(i + 1), tolerance))
                    {
                        polyline.RemoveVertexAt(i);
                        progress = true;
                        changed = true;
                        break;
                    }
                }
                if (!progress) break;
            }
            return changed;
        }

        // 清理闭合聚合线的顶点问题：零长度边/重复顶点（相邻点重合，含首尾环绕）、
        // 连续共线冗余顶点（按闭合环处理，含首尾环绕）。
        // 调用方保证该线无弧段（bulge）；清理后少于 3 个顶点的闭合环视为无效，由调用方删除。
        // 注意：不能把闭合 LWPOLYLINE 删到 3 个顶点以下（AutoCAD 抛 eDegenerateGeometry），
        // 因此写回用 SetPointAt 覆盖 + 尾部删除，保持顶点数 >= 3。
        private static bool CleanHgapClosedPolylineVertices(Polyline polyline, double tolerance)
        {
            List<Point2d> points = new List<Point2d>();
            for (int i = 0; i < polyline.NumberOfVertices; i++)
                points.Add(polyline.GetPoint2dAt(i));
            bool changed = false;

            // 1) 零长度边/重复顶点：相邻点重合即删（含最后一个点与首点重合的环绕情况）。
            for (int i = points.Count - 1; i >= 0; i--)
            {
                if (points.Count < 3) break;
                int next = (i + 1) % points.Count;
                if (points[i].GetDistanceTo(points[next]) <= tolerance)
                {
                    points.RemoveAt(i);
                    changed = true;
                }
            }

            // 2) 连续共线冗余顶点：按闭合环清理（首尾环绕也能删）。
            int before = points.Count;
            CleanHgapCollinearChainPoints(points, tolerance, true);
            if (points.Count < before)
                changed = true;

            if (!changed) return false;

            if (points.Count >= 3)
            {
                // 写回：前 points.Count 个顶点覆盖，删除尾部多余顶点。
                for (int i = 0; i < points.Count; i++)
                    polyline.SetPointAt(i, points[i]);
                while (polyline.NumberOfVertices > points.Count)
                    polyline.RemoveVertexAt(polyline.NumberOfVertices - 1);
            }
            else
            {
                // 共线清理后闭合环不足 3 点：无效几何。删尾到无法再删为止
                // （RemoveVertexAt 对闭合线删到 <3 会抛 eDegenerateGeometry，吞掉即可），
                // 调用方依 NumberOfVertices < 3 删除整条线。
                while (polyline.NumberOfVertices > 2)
                {
                    try { polyline.RemoveVertexAt(polyline.NumberOfVertices - 1); }
                    catch (System.Exception) { break; }
                }
            }
            return true;
        }

        // 移除连续共线冗余顶点。closed=true 时按闭合环处理（含首尾环绕），
        // closed=false 时按开放折线处理（首尾点不参与中间点判定）。
        private static void CleanHgapCollinearChainPoints(List<Point2d> points, double tolerance, bool closed)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                int count = points.Count;
                if (count < 3) return;
                for (int i = 0; i < count; i++)
                {
                    if (!closed && (i == 0 || i == count - 1)) continue;
                    Point2d prev = points[(i - 1 + count) % count];
                    Point2d next = points[(i + 1) % count];
                    if (HgapIsRedundantCollinearVertex(prev, points[i], next, tolerance))
                    {
                        points.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }
        }

        // 判定中间点 b 是否为 a→c 连线上的冗余共线顶点（三点共线且 b 落在 a、c 之间）。
        // 垂直距离与投影区间双重判定，避免把“折返点”（a 与 c 重合或 b 超出线段范围）误删。
        private static bool HgapIsRedundantCollinearVertex(Point2d a, Point2d b, Point2d c, double tolerance)
        {
            Vector2d ac = c - a;
            double acLength = ac.Length;
            if (acLength <= tolerance) return false;

            double cross = Math.Abs((b.X - a.X) * ac.Y - (b.Y - a.Y) * ac.X);
            if (cross > tolerance * acLength) return false;

            double projection = ((b.X - a.X) * ac.X + (b.Y - a.Y) * ac.Y) / (acLength * acLength);
            return projection >= -1e-9 && projection <= 1.0 + 1e-9;
        }

        private static ObjectId GetOrCreateHgapLayer(Database database, Transaction tr)
        {
            LayerTable layerTable = (LayerTable)tr.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (layerTable.Has(HgapMarkerLayer))
                return layerTable[HgapMarkerLayer];

            layerTable.UpgradeOpen();
            LayerTableRecord layer = new LayerTableRecord();
            layer.Name = HgapMarkerLayer;
            layer.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
            ObjectId layerId = layerTable.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
            return layerId;
        }

        private static bool TryHgapCleanClosePolyline(Polyline polyline, double tolerance)
        {
            int vertexCount = polyline.NumberOfVertices;
            if (vertexCount < 4) return false;

            Point2d startPoint = polyline.GetPoint2dAt(0);
            Point2d endPoint = polyline.GetPoint2dAt(vertexCount - 1);
            if (startPoint.GetDistanceTo(endPoint) > tolerance)
                return false;

            Point2d cleanCorner;
            if (TryGetHgapCleanCorner(polyline, tolerance, out cleanCorner))
            {
                CloseHgapPolylineAt(polyline, cleanCorner);
                return true;
            }

            // 先闭合再设置末段 bulge：开放多段线的有效段索引为 0..N-2，
            // 对 N-1 调用 SetBulgeAt 会越界抛异常（兜底闭合路径必失败）。
            // 闭合后 N-1 才是合法段索引，bulge 置 0 保证闭合边为直线。
            int lastIndex = polyline.NumberOfVertices - 1;
            polyline.Closed = true;
            polyline.SetBulgeAt(lastIndex, 0.0);
            return true;
        }

        private static void CloseHgapPolylineAt(Polyline polyline, Point2d closePoint)
        {
            int lastIndex = polyline.NumberOfVertices - 1;
            polyline.SetPointAt(0, closePoint);
            polyline.SetPointAt(lastIndex, closePoint);
            if (polyline.NumberOfVertices > 3)
                polyline.RemoveVertexAt(lastIndex);
            polyline.Closed = true;
        }

        private static bool TryGetHgapCleanCorner(Polyline polyline, double tolerance, out Point2d corner)
        {
            corner = Point2d.Origin;
            int vertexCount = polyline.NumberOfVertices;
            if (vertexCount < 3) return false;
            if (Math.Abs(polyline.GetBulgeAt(0)) > 1e-9 || Math.Abs(polyline.GetBulgeAt(vertexCount - 2)) > 1e-9)
                return false;

            Point2d first = polyline.GetPoint2dAt(0);
            Point2d second = polyline.GetPoint2dAt(1);
            Point2d beforeLast = polyline.GetPoint2dAt(vertexCount - 2);
            Point2d last = polyline.GetPoint2dAt(vertexCount - 1);

            if (!TryHgapLineIntersection(first, second, beforeLast, last, out corner))
                return false;

            return first.GetDistanceTo(corner) <= tolerance && last.GetDistanceTo(corner) <= tolerance;
        }

        private static bool TryHgapLineIntersection(Point2d a1, Point2d a2, Point2d b1, Point2d b2, out Point2d intersection)
        {
            intersection = Point2d.Origin;
            Vector2d r = a2 - a1;
            Vector2d s = b2 - b1;
            double denominator = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(denominator) < 1e-12)
                return false;

            Vector2d qp = b1 - a1;
            double t = (qp.X * s.Y - qp.Y * s.X) / denominator;
            intersection = new Point2d(a1.X + r.X * t, a1.Y + r.Y * t);
            return true;
        }

        private static void ClearHgapMarkers(Database database, Transaction tr)
        {
            LayerTable layerTable = (LayerTable)tr.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (!layerTable.Has(HgapMarkerLayer)) return;

            BlockTableRecord space = (BlockTableRecord)tr.GetObject(database.CurrentSpaceId, OpenMode.ForRead);
            List<ObjectId> markerIds = new List<ObjectId>();
            foreach (ObjectId objectId in space)
            {
                Entity entity = tr.GetObject(objectId, OpenMode.ForRead, false) as Entity;
                if (entity != null && string.Equals(entity.Layer, HgapMarkerLayer, StringComparison.OrdinalIgnoreCase))
                    markerIds.Add(objectId);
            }

            for (int i = 0; i < markerIds.Count; i++)
            {
                Entity entity = tr.GetObject(markerIds[i], OpenMode.ForWrite, false) as Entity;
                if (entity != null && !entity.IsErased)
                    entity.Erase();
            }
        }

        private static void CreateHgapMarker(BlockTableRecord space, Transaction tr, ObjectId layerId, HgapOpenPolyline item)
        {
            Point3d start = new Point3d(item.Start.X, item.Start.Y, 0.0);
            Point3d end = new Point3d(item.End.X, item.End.Y, 0.0);
            Point3d center = new Point3d((item.Start.X + item.End.X) / 2.0, (item.Start.Y + item.End.Y) / 2.0, 0.0);
            double size = Math.Max(item.Gap * 0.35, 2.0);

            Line gapLine = new Line(start, end);
            gapLine.LayerId = layerId;
            gapLine.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
            space.AppendEntity(gapLine);
            tr.AddNewlyCreatedDBObject(gapLine, true);

            Circle circle = new Circle(center, Vector3d.ZAxis, size);
            circle.LayerId = layerId;
            circle.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
            space.AppendEntity(circle);
            tr.AddNewlyCreatedDBObject(circle, true);

            DBText label = new DBText();
            label.Position = center + new Vector3d(size * 1.2, size * 1.2, 0.0);
            label.Height = Math.Max(size * 0.45, 1.2);
            label.TextString = string.Format("HGAP {0:0.###}mm", item.Gap);
            label.LayerId = layerId;
            label.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
            space.AppendEntity(label);
            tr.AddNewlyCreatedDBObject(label, true);
        }
    }
}
