using System;
using System.Collections.Generic;
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
        private const string HcheckMarkerLayer = "HCHK_图形健康标识";
        private const double HcheckGeometryTolerance = 0.001;

        private class HcheckIssue
        {
            public string Message;
            public Point3d Position;
            public double Size;
        }

        private struct HcheckSegment
        {
            public Point2d Start;
            public Point2d End;
            public ObjectId SourceId;
            public int SegmentIndex;
            public int SegmentCount;
        }

        [CommandMethod("HCHK")]
        public void HCHK()
        {
            Document document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            Database database = document.Database;
            Editor editor = document.Editor;
            db = database;
            ed = editor;

            try
            {
                PromptSelectionOptions options = new PromptSelectionOptions();
                options.MessageForAdding = "\n选择或框选要做健康检查的聚合线、线段和编号文字: ";
                SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE,LINE,TEXT,MTEXT") });
                PromptSelectionResult selection = editor.GetSelection(options, filter);
                if (selection.Status != PromptStatus.OK || selection.Value == null) return;

                ObjectId[] selectedIds = selection.Value.GetObjectIds();
                List<HcheckIssue> issues = new List<HcheckIssue>();
                List<HcheckSegment> segments = new List<HcheckSegment>();

                using (Transaction tr = database.TransactionManager.StartTransaction())
                {
                    ClearHcheckMarkers(database, tr);
                    HashSet<ObjectId> referencedNumberTextIds = GetHcheckReferencedNumberTextIds(database, tr);
                    ObjectId numberLayerId;
                    bool hasNumberLayer = TryGetExistingLayerId(tr, GGE_NUM_LAYER, out numberLayerId);

                    for (int i = 0; i < selectedIds.Length; i++)
                    {
                        Entity entity = tr.GetObject(selectedIds[i], OpenMode.ForRead, false) as Entity;
                        if (entity == null || entity.IsErased) continue;
                        if (string.Equals(entity.Layer, HcheckMarkerLayer, StringComparison.OrdinalIgnoreCase)) continue;

                        Polyline polyline = entity as Polyline;
                        if (polyline != null)
                        {
                            CheckHcheckPolyline(polyline, selectedIds[i], issues, segments);
                            CheckHcheckEntityNumberHandle(database, tr, entity, issues);
                            continue;
                        }

                        Line line = entity as Line;
                        if (line != null)
                        {
                            CheckHcheckLine(line, selectedIds[i], issues, segments);
                            CheckHcheckEntityNumberHandle(database, tr, entity, issues);
                            continue;
                        }

                        DBText text = entity as DBText;
                        if (text != null && hasNumberLayer && text.LayerId == numberLayerId && !referencedNumberTextIds.Contains(selectedIds[i]))
                        {
                            issues.Add(new HcheckIssue
                            {
                                Message = "孤立编号文字，可用 HNM 或 HCLN 清理",
                                Position = text.Position,
                                Size = 2.0
                            });
                            continue;
                        }

                        MText mtext = entity as MText;
                        if (mtext != null && hasNumberLayer && mtext.LayerId == numberLayerId && !referencedNumberTextIds.Contains(selectedIds[i]))
                        {
                            issues.Add(new HcheckIssue
                            {
                                Message = "孤立编号文字，可用 HNM 或 HCLN 清理",
                                Position = mtext.Location,
                                Size = 2.0
                            });
                        }
                    }

                    CheckHcheckSegmentPairs(segments, issues);

                    if (issues.Count > 0)
                    {
                        ObjectId markerLayerId = GetOrCreateHcheckLayer(database, tr);
                        BlockTableRecord space = (BlockTableRecord)tr.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                        for (int i = 0; i < issues.Count; i++)
                            CreateHcheckMarker(space, tr, markerLayerId, issues[i], i + 1);
                    }

                    tr.Commit();
                }

                if (issues.Count == 0)
                {
                    editor.WriteMessage("\nHCHK：未发现健康检查问题，已清理旧标识。");
                    return;
                }

                editor.WriteMessage(string.Format("\nHCHK：发现 {0} 处问题，已标识在图层 {1}。", issues.Count, HcheckMarkerLayer));
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage(string.Format("\nHCHK错误：{0}", ex.Message));
            }
        }

        private void CheckHcheckPolyline(Polyline polyline, ObjectId sourceId, List<HcheckIssue> issues, List<HcheckSegment> segments)
        {
            int vertexCount = polyline.NumberOfVertices;
            if (vertexCount < 2)
            {
                issues.Add(new HcheckIssue { Message = "聚合线顶点少于 2 个", Position = HcheckEntityCenter(polyline), Size = 2.0 });
                return;
            }

            if (!polyline.Closed)
            {
                Point2d start = polyline.GetPoint2dAt(0);
                Point2d end = polyline.GetPoint2dAt(vertexCount - 1);
                double gap = start.GetDistanceTo(end);
                issues.Add(new HcheckIssue
                {
                    Message = gap <= HsetHgapSmallGapTolerance ? string.Format("微小未闭合缺口 {0:0.###}mm，可用 HGAP", gap) : string.Format("明显未闭合缺口 {0:0.###}mm，需人工确认", gap),
                    Position = new Point3d((start.X + end.X) / 2.0, (start.Y + end.Y) / 2.0, 0.0),
                    Size = Math.Max(gap * 0.35, 2.0)
                });
            }

            int segmentCount = polyline.Closed ? vertexCount : vertexCount - 1;
            for (int i = 0; i < segmentCount; i++)
            {
                Point2d start = polyline.GetPoint2dAt(i);
                Point2d end = polyline.GetPoint2dAt((i + 1) % vertexCount);
                if (start.GetDistanceTo(end) <= HcheckGeometryTolerance)
                {
                    issues.Add(new HcheckIssue
                    {
                        Message = "零长度边或重复顶点",
                        Position = new Point3d(start.X, start.Y, 0.0),
                        Size = 2.0
                    });
                }
            }

            List<Point2d> sampledPoints = SampleHplitPolyline(polyline, !polyline.Closed);
            int sampledSegmentCount = polyline.Closed ? sampledPoints.Count : sampledPoints.Count - 1;
            for (int i = 0; i < sampledSegmentCount; i++)
            {
                Point2d start = sampledPoints[i];
                Point2d end = sampledPoints[(i + 1) % sampledPoints.Count];
                if (start.GetDistanceTo(end) <= HcheckGeometryTolerance)
                    continue;
                segments.Add(new HcheckSegment
                {
                    Start = start,
                    End = end,
                    SourceId = sourceId,
                    SegmentIndex = i,
                    SegmentCount = sampledSegmentCount
                });
            }

            CheckHcheckRedundantVertices(polyline, issues);
        }

        private static void CheckHcheckLine(Line line, ObjectId sourceId, List<HcheckIssue> issues, List<HcheckSegment> segments)
        {
            Point2d start = new Point2d(line.StartPoint.X, line.StartPoint.Y);
            Point2d end = new Point2d(line.EndPoint.X, line.EndPoint.Y);
            if (start.GetDistanceTo(end) <= HcheckGeometryTolerance)
            {
                issues.Add(new HcheckIssue { Message = "零长度线段", Position = line.StartPoint, Size = 2.0 });
                return;
            }

            segments.Add(new HcheckSegment
            {
                Start = start,
                End = end,
                SourceId = sourceId,
                SegmentIndex = 0,
                SegmentCount = 1
            });
        }

        private static void CheckHcheckRedundantVertices(Polyline polyline, List<HcheckIssue> issues)
        {
            int count = polyline.NumberOfVertices;
            if (count < 3) return;

            int limit = polyline.Closed ? count : count - 2;
            for (int i = 0; i < limit; i++)
            {
                int index = polyline.Closed ? i : i + 1;
                int incomingSegment = (index - 1 + count) % count;
                int outgoingSegment = index;
                if (Math.Abs(polyline.GetBulgeAt(incomingSegment)) > 1e-9 ||
                    Math.Abs(polyline.GetBulgeAt(outgoingSegment)) > 1e-9)
                    continue;
                Point2d previous = polyline.GetPoint2dAt((index - 1 + count) % count);
                Point2d current = polyline.GetPoint2dAt(index);
                Point2d next = polyline.GetPoint2dAt((index + 1) % count);
                if (previous.GetDistanceTo(current) <= HcheckGeometryTolerance || current.GetDistanceTo(next) <= HcheckGeometryTolerance)
                    continue;
                if (HcheckPointOnSegment(current, previous, next, HcheckGeometryTolerance))
                {
                    issues.Add(new HcheckIssue
                    {
                        Message = "连续共线冗余顶点",
                        Position = new Point3d(current.X, current.Y, 0.0),
                        Size = 2.0
                    });
                }
            }
        }

        private static void CheckHcheckSegmentPairs(List<HcheckSegment> segments, List<HcheckIssue> issues)
        {
            List<int>[] candidateLists = BuildHcheckCandidateLists(segments, HcheckGeometryTolerance);
            for (int i = 0; i < segments.Count; i++)
            {
                for (int candidateIndex = 0; candidateIndex < candidateLists[i].Count; candidateIndex++)
                {
                    int j = candidateLists[i][candidateIndex];
                    if (j <= i) continue;
                    HcheckSegment a = segments[i];
                    HcheckSegment b = segments[j];
                    bool sameSource = a.SourceId == b.SourceId;
                    bool adjacent = sameSource && HcheckSegmentsAdjacent(a, b);

                    Point2d overlapStart;
                    Point2d overlapEnd;
                    if (!adjacent && TryHcheckSegmentOverlap(a.Start, a.End, b.Start, b.End, HcheckGeometryTolerance, out overlapStart, out overlapEnd))
                    {
                        issues.Add(new HcheckIssue
                        {
                            Message = sameSource ? "自身重复/重叠边" : "重复/重叠线段",
                            Position = new Point3d((overlapStart.X + overlapEnd.X) / 2.0, (overlapStart.Y + overlapEnd.Y) / 2.0, 0.0),
                            Size = Math.Max(overlapStart.GetDistanceTo(overlapEnd) * 0.2, 2.0)
                        });
                        continue;
                    }

                    if (sameSource && !adjacent)
                    {
                        Point2d intersection;
                        double ta;
                        double tb;
                        if (TryHcheckSegmentIntersection(a.Start, a.End, b.Start, b.End, out intersection, out ta, out tb))
                        {
                            // ta/tb 是无量纲参数，不能与长度量纲的 HcheckGeometryTolerance 直接比较：
                            // 长线段上端点附近的真实自交因参数极小被旧实现漏报。
                            // 换算为“距端点 ≥ 0.001mm 才算内部交点”。
                            double aLength = a.Start.GetDistanceTo(a.End);
                            double bLength = b.Start.GetDistanceTo(b.End);
                            double taTolerance = aLength > 1e-9 ? HcheckGeometryTolerance / aLength : 0.0;
                            double tbTolerance = bLength > 1e-9 ? HcheckGeometryTolerance / bLength : 0.0;
                            if (ta > taTolerance && ta < 1.0 - taTolerance &&
                                tb > tbTolerance && tb < 1.0 - tbTolerance)
                            {
                                issues.Add(new HcheckIssue
                                {
                                    Message = "聚合线自交",
                                    Position = new Point3d(intersection.X, intersection.Y, 0.0),
                                    Size = 2.0
                                });
                            }
                        }
                    }
                }
            }
        }

        private static List<int>[] BuildHcheckCandidateLists(List<HcheckSegment> segments, double tolerance)
        {
            int count = segments.Count;
            List<int>[] candidates = new List<int>[count];
            double[] minX = new double[count];
            double[] maxX = new double[count];
            double[] minY = new double[count];
            double[] maxY = new double[count];
            for (int i = 0; i < count; i++)
            {
                candidates[i] = new List<int>();
                HcheckSegment segment = segments[i];
                minX[i] = Math.Min(segment.Start.X, segment.End.X);
                maxX[i] = Math.Max(segment.Start.X, segment.End.X);
                minY[i] = Math.Min(segment.Start.Y, segment.End.Y);
                maxY[i] = Math.Max(segment.Start.Y, segment.End.Y);
            }

            List<int> order = new List<int>(count);
            for (int i = 0; i < count; i++)
                order.Add(i);
            order.Sort((left, right) => minX[left].CompareTo(minX[right]));

            for (int orderIndex = 0; orderIndex < order.Count; orderIndex++)
            {
                int i = order[orderIndex];
                double aMinX = minX[i] - tolerance;
                double aMaxX = maxX[i] + tolerance;
                double aMinY = minY[i] - tolerance;
                double aMaxY = maxY[i] + tolerance;
                for (int nextOrderIndex = orderIndex + 1; nextOrderIndex < order.Count; nextOrderIndex++)
                {
                    int j = order[nextOrderIndex];
                    if (minX[j] > aMaxX) break;
                    if (maxX[j] < aMinX || maxY[j] < aMinY || minY[j] > aMaxY) continue;
                    candidates[i].Add(j);
                    candidates[j].Add(i);
                }
            }

            for (int i = 0; i < candidates.Length; i++)
                candidates[i].Sort();
            return candidates;
        }

        private void CheckHcheckEntityNumberHandle(Database database, Transaction tr, Entity entity, List<HcheckIssue> issues)
        {
            string handleStr = GetEntityTextHandle(entity);
            if (string.IsNullOrEmpty(handleStr)) return;

            bool valid = false;
            if (long.TryParse(handleStr, System.Globalization.NumberStyles.HexNumber, null, out long handleLong))
            {
                try
                {
                    ObjectId textId = database.GetObjectId(false, new Handle(handleLong), 0);
                    DBText text = tr.GetObject(textId, OpenMode.ForRead, false) as DBText;
                    valid = text != null && !text.IsErased;
                }
                catch
                {
                    valid = false;
                }
            }

            if (!valid)
            {
                issues.Add(new HcheckIssue
                {
                    Message = "区域编号文字引用失效，可用 HNM 重新编号",
                    Position = HcheckEntityCenter(entity),
                    Size = 2.0
                });
            }
        }

        private HashSet<ObjectId> GetHcheckReferencedNumberTextIds(Database database, Transaction tr)
        {
            HashSet<ObjectId> result = new HashSet<ObjectId>();
            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
            foreach (ObjectId id in modelSpace)
            {
                Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (entity == null || entity.IsErased) continue;

                string handleStr = GetEntityTextHandle(entity);
                if (string.IsNullOrEmpty(handleStr)) continue;
                if (!long.TryParse(handleStr, System.Globalization.NumberStyles.HexNumber, null, out long handleLong)) continue;

                try
                {
                    ObjectId textId = database.GetObjectId(false, new Handle(handleLong), 0);
                    if (!textId.IsNull && textId.IsValid)
                        result.Add(textId);
                }
                catch
                {
                }
            }
            return result;
        }

        private static bool HcheckSegmentsAdjacent(HcheckSegment a, HcheckSegment b)
        {
            if (a.SourceId != b.SourceId) return false;
            if (Math.Abs(a.SegmentIndex - b.SegmentIndex) == 1) return true;
            return a.SegmentCount > 1 && Math.Abs(a.SegmentIndex - b.SegmentIndex) == a.SegmentCount - 1;
        }

        private static bool TryHcheckSegmentOverlap(Point2d a1, Point2d a2, Point2d b1, Point2d b2, double tolerance, out Point2d overlapStart, out Point2d overlapEnd)
        {
            overlapStart = Point2d.Origin;
            overlapEnd = Point2d.Origin;
            Vector2d direction = a2 - a1;
            double length = direction.Length;
            if (length <= tolerance) return false;
            direction = direction / length;

            if (HcheckPointLineDistance(b1, a1, direction) > tolerance || HcheckPointLineDistance(b2, a1, direction) > tolerance)
                return false;

            double b0 = HcheckProjection(b1, a1, direction);
            double b1Projection = HcheckProjection(b2, a1, direction);
            if (b1Projection < b0)
            {
                double tmp = b0;
                b0 = b1Projection;
                b1Projection = tmp;
            }

            double start = Math.Max(0.0, b0);
            double end = Math.Min(length, b1Projection);
            if (end - start <= Math.Max(tolerance, 0.001))
                return false;

            overlapStart = a1 + direction * start;
            overlapEnd = a1 + direction * end;
            return true;
        }

        private static bool TryHcheckSegmentIntersection(Point2d a1, Point2d a2, Point2d b1, Point2d b2, out Point2d hit, out double ta, out double tb)
        {
            hit = Point2d.Origin;
            ta = 0.0;
            tb = 0.0;
            Vector2d r = a2 - a1;
            Vector2d s = b2 - b1;
            double denominator = r.X * s.Y - r.Y * s.X;
            if (Math.Abs(denominator) < 1e-12) return false;

            Vector2d qp = b1 - a1;
            ta = (qp.X * s.Y - qp.Y * s.X) / denominator;
            tb = (qp.X * r.Y - qp.Y * r.X) / denominator;
            if (ta < -HcheckGeometryTolerance || ta > 1.0 + HcheckGeometryTolerance || tb < -HcheckGeometryTolerance || tb > 1.0 + HcheckGeometryTolerance)
                return false;

            hit = new Point2d(a1.X + r.X * ta, a1.Y + r.Y * ta);
            return true;
        }

        private static bool HcheckPointOnSegment(Point2d point, Point2d start, Point2d end, double tolerance)
        {
            Vector2d segment = end - start;
            double lengthSquared = segment.X * segment.X + segment.Y * segment.Y;
            if (lengthSquared <= tolerance * tolerance) return point.GetDistanceTo(start) <= tolerance;
            Vector2d toPoint = point - start;
            double cross = Math.Abs(segment.X * toPoint.Y - segment.Y * toPoint.X);
            if (cross > tolerance * Math.Sqrt(lengthSquared)) return false;
            double dot = toPoint.X * segment.X + toPoint.Y * segment.Y;
            return dot >= -tolerance && dot <= lengthSquared + tolerance;
        }

        private static double HcheckPointLineDistance(Point2d point, Point2d origin, Vector2d direction)
        {
            Vector2d toPoint = point - origin;
            return Math.Abs(toPoint.X * direction.Y - toPoint.Y * direction.X);
        }

        private static double HcheckProjection(Point2d point, Point2d origin, Vector2d direction)
        {
            Vector2d toPoint = point - origin;
            return toPoint.X * direction.X + toPoint.Y * direction.Y;
        }

        private static Point3d HcheckEntityCenter(Entity entity)
        {
            try
            {
                Extents3d extents = entity.GeometricExtents;
                return new Point3d((extents.MinPoint.X + extents.MaxPoint.X) / 2.0, (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0, 0.0);
            }
            catch
            {
                return Point3d.Origin;
            }
        }

        private static void ClearHcheckMarkers(Database database, Transaction tr)
        {
            LayerTable layerTable = (LayerTable)tr.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (!layerTable.Has(HcheckMarkerLayer)) return;

            BlockTableRecord space = (BlockTableRecord)tr.GetObject(database.CurrentSpaceId, OpenMode.ForRead);
            List<ObjectId> markerIds = new List<ObjectId>();
            foreach (ObjectId objectId in space)
            {
                Entity entity = tr.GetObject(objectId, OpenMode.ForRead, false) as Entity;
                if (entity != null && string.Equals(entity.Layer, HcheckMarkerLayer, StringComparison.OrdinalIgnoreCase))
                    markerIds.Add(objectId);
            }

            for (int i = 0; i < markerIds.Count; i++)
            {
                Entity entity = tr.GetObject(markerIds[i], OpenMode.ForWrite, false) as Entity;
                if (entity != null && !entity.IsErased)
                    entity.Erase();
            }
        }

        private static ObjectId GetOrCreateHcheckLayer(Database database, Transaction tr)
        {
            LayerTable layerTable = (LayerTable)tr.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (layerTable.Has(HcheckMarkerLayer))
                return layerTable[HcheckMarkerLayer];

            layerTable.UpgradeOpen();
            LayerTableRecord layer = new LayerTableRecord();
            layer.Name = HcheckMarkerLayer;
            layer.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
            ObjectId layerId = layerTable.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
            return layerId;
        }

        private static void CreateHcheckMarker(BlockTableRecord space, Transaction tr, ObjectId layerId, HcheckIssue issue, int index)
        {
            double size = Math.Max(issue.Size, 1.0);
            Circle circle = new Circle(issue.Position, Vector3d.ZAxis, size);
            circle.LayerId = layerId;
            circle.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
            space.AppendEntity(circle);
            tr.AddNewlyCreatedDBObject(circle, true);

            DBText label = new DBText();
            label.Position = issue.Position + new Vector3d(size * 1.25, size * 1.25, 0.0);
            label.Height = Math.Max(size * 0.35, 1.2);
            label.TextString = string.Format("HCHK{0}: {1}", index, issue.Message);
            label.LayerId = layerId;
            label.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
            space.AppendEntity(label);
            tr.AddNewlyCreatedDBObject(label, true);
        }
    }
}
