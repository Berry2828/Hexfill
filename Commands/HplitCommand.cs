using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace HexFill
{
    public partial class HexFill
    {
        [CommandMethod("HPLIT")]
        public void HPLIT()
        {
            Document document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            Database database = document.Database;
            Editor editor = document.Editor;

            try
            {
                List<ObjectId> polylineIds = PromptHplitClosedPolylines(editor, database);
                if (polylineIds.Count == 0) return;

                List<Point2d> cutPoints = PromptHplitCutPoints(editor);
                if (cutPoints.Count < 2)
                {
                    editor.WriteMessage("\n至少需要选择 2 个分割点。");
                    return;
                }

                int splitSourceCount = 0;
                int skippedSourceCount = 0;
                int createdPolylineCount = 0;

                for (int sourceIndex = 0; sourceIndex < polylineIds.Count; sourceIndex++)
                {
                    try
                    {
                        int createdForSource = 0;
                        using (Transaction tr = database.TransactionManager.StartTransaction())
                        {
                            Polyline source = tr.GetObject(polylineIds[sourceIndex], OpenMode.ForRead) as Polyline;
                            if (source == null || !source.Closed || HplitLayerIsLocked(tr, source) || !HplitPathMayOverlap(source, cutPoints))
                            {
                                skippedSourceCount++;
                                continue;
                            }

                            List<Point2d> boundary = GetHplitBoundary(source);
                            if (!HplitRawPathCrossesPolygon(cutPoints, boundary))
                            {
                                skippedSourceCount++;
                                continue;
                            }

                            List<List<Point2d>> resultPolygons = new List<List<Point2d>>();
                            try
                            {
                                resultPolygons = SplitHplitPolygonsByRegion(source, cutPoints);
                                List<List<Point2d>> boundaryZigzagPolygons;
                                if (TrySplitHplitBoundaryZigzag(boundary, cutPoints, out boundaryZigzagPolygons) && boundaryZigzagPolygons.Count > resultPolygons.Count)
                                    resultPolygons = boundaryZigzagPolygons;
                                if (resultPolygons.Count < 2)
                                    resultPolygons = SplitHplitPolygons(boundary, cutPoints);
                            }
                            catch
                            {
                                resultPolygons = new List<List<Point2d>>();
                            }

                            if (resultPolygons.Count < 2)
                            {
                                skippedSourceCount++;
                                continue;
                            }

                            BlockTableRecord resultSpace = (BlockTableRecord)tr.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                            for (int i = 0; i < resultPolygons.Count; i++)
                            {
                                Polyline newPolyline = CreateHplitPolyline(resultPolygons[i], source);
                                resultSpace.AppendEntity(newPolyline);
                                tr.AddNewlyCreatedDBObject(newPolyline, true);
                                createdForSource++;
                            }

                            source.UpgradeOpen();
                            source.Erase();
                            tr.Commit();
                        }

                        splitSourceCount++;
                        createdPolylineCount += createdForSource;
                    }
                    catch
                    {
                        skippedSourceCount++;
                    }
                }

                if (splitSourceCount == 0)
                    editor.WriteMessage(string.Format("\nHPLIT：未找到被分割路径完整贯穿的闭合聚合线，已跳过 {0} 条。", skippedSourceCount));
                else
                {
                    editor.WriteMessage(string.Format("\nHPLIT：已切割 {0} 条闭合聚合线，生成 {1} 条新闭合聚合线，跳过 {2} 条，分割间距={3}mm。", splitSourceCount, createdPolylineCount, skippedSourceCount, HsetFormat(HsetHplitCutSpacing)));
                }
                return;
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage(string.Format("\nHPLIT错误：{0}", ex.Message));
            }
        }

        private class HplitHit
        {
            public Point2d Point;
            public double PathParam;
            public int PathSegment;
            public double PathFraction;
            public int BoundaryEdge;
            public double BoundaryFraction;
        }

        private class HplitClip
        {
            public HplitHit Start;
            public HplitHit End;
            public List<Point2d> Points;
        }

        private class HplitCandidate
        {
            public List<Point2d> Points;
            public double Side;
            public double Area;
        }

        private class HplitCurvePiece
        {
            public List<Point2d> Points;
            public bool Used;
        }

        private static List<ObjectId> PromptHplitClosedPolylines(Editor editor, Database database)
        {
            SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
            PromptSelectionResult selection = null;

            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                List<ObjectId> impliedIds = FindHplitClosedPolylines(database, implied.Value.GetObjectIds());
                if (impliedIds.Count > 0)
                    return impliedIds;
            }

            PromptSelectionOptions options = new PromptSelectionOptions();
            options.MessageForAdding = "\n选择或框选闭合聚合线: ";
            selection = editor.GetSelection(options, filter);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return new List<ObjectId>();

            List<ObjectId> selectedIds = FindHplitClosedPolylines(database, selection.Value.GetObjectIds());
            if (selectedIds.Count == 0)
                editor.WriteMessage("\n未找到闭合轻量聚合线。");
            return selectedIds;
        }

        private static List<ObjectId> FindHplitClosedPolylines(Database database, ObjectId[] objectIds)
        {
            List<ObjectId> result = new List<ObjectId>();
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId objectId in objectIds)
                {
                    Polyline polyline = tr.GetObject(objectId, OpenMode.ForRead) as Polyline;
                    if (polyline != null && polyline.Closed)
                    {
                        if (!result.Contains(objectId))
                            result.Add(objectId);
                    }
                }
                tr.Commit();
            }
            return result;
        }

        private static List<Point2d> PromptHplitCutPoints(Editor editor)
        {
            List<Point2d> points = new List<Point2d>();
            Point3d lastPoint = Point3d.Origin;

            while (true)
            {
                PromptPointOptions options = new PromptPointOptions(points.Count == 0 ? "\n指定分割路径第一个点: " : "\n指定下一个分割点，按 Enter 完成: ");
                options.AllowNone = true;
                if (points.Count > 0)
                {
                    options.UseBasePoint = true;
                    options.BasePoint = lastPoint;
                }

                PromptPointResult result = editor.GetPoint(options);
                if (result.Status == PromptStatus.None || result.Status == PromptStatus.Cancel) break;
                if (result.Status != PromptStatus.OK) return new List<Point2d>();

                Point2d point = new Point2d(result.Value.X, result.Value.Y);
                if (points.Count == 0 || HplitDistance(points[points.Count - 1], point) > 0.0001)
                {
                    points.Add(point);
                    lastPoint = result.Value;
                }
            }

            return points;
        }

        private static List<Point2d> GetHplitBoundary(Polyline polyline)
        {
            List<Point2d> boundary = SampleHplitPolyline(polyline, false);

            if (boundary.Count > 1 && HplitDistance(boundary[0], boundary[boundary.Count - 1]) <= 0.0001)
                boundary.RemoveAt(boundary.Count - 1);
            if (boundary.Count < 3)
                throw new InvalidOperationException("所选聚合线顶点少于 3 个。");
            return boundary;
        }

        private static bool HplitLayerIsLocked(Transaction tr, Entity entity)
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

        private static bool HplitPathMayOverlap(Entity source, List<Point2d> cutPoints)
        {
            try
            {
                Extents3d extents = source.GeometricExtents;
                double padding = Math.Max(HsetHplitHalfCutSpacing, 0.000001);
                double minX = cutPoints[0].X;
                double maxX = cutPoints[0].X;
                double minY = cutPoints[0].Y;
                double maxY = cutPoints[0].Y;
                for (int i = 1; i < cutPoints.Count; i++)
                {
                    minX = Math.Min(minX, cutPoints[i].X);
                    maxX = Math.Max(maxX, cutPoints[i].X);
                    minY = Math.Min(minY, cutPoints[i].Y);
                    maxY = Math.Max(maxY, cutPoints[i].Y);
                }
                return maxX + padding >= extents.MinPoint.X &&
                       minX - padding <= extents.MaxPoint.X &&
                       maxY + padding >= extents.MinPoint.Y &&
                       minY - padding <= extents.MaxPoint.Y;
            }
            catch
            {
                // fail-closed：判定失败时视为路径与区域不重叠，不进入分割流程，
                // 与 HplitLayerIsLocked 系列的失败保守策略保持一致。
                return false;
            }
        }

        private static List<Point2d> SampleHplitPolyline(Polyline polyline, bool includeFinalPoint)
        {
            List<Point2d> points = new List<Point2d>();
            if (polyline == null || polyline.NumberOfVertices < 2) return points;

            int segmentCount = polyline.Closed ? polyline.NumberOfVertices : Math.Max(0, polyline.NumberOfVertices - 1);
            for (int segment = 0; segment < segmentCount; segment++)
            {
                int subdivisions = GetHplitPolylineSegmentSubdivisions(polyline, segment);
                if (subdivisions == 1)
                {
                    // 直线段（无弧）：采样点就是段起点，直接取顶点，跳过参数映射
                    Point2d vertex = polyline.GetPoint2dAt(segment);
                    AddHplitPoint(points, vertex, 0.0001);
                }
                else
                {
                    for (int step = 0; step < subdivisions; step++)
                    {
                        double parameter = segment + step / (double)subdivisions;
                        Point3d sample = polyline.GetPointAtParameter(parameter);
                        AddHplitPoint(points, new Point2d(sample.X, sample.Y), 0.0001);
                    }
                }
            }

            if (includeFinalPoint && segmentCount > 0)
            {
                Point2d finalPoint = polyline.Closed ? polyline.GetPoint2dAt(0) : polyline.GetPoint2dAt(polyline.NumberOfVertices - 1);
                AddHplitPoint(points, finalPoint, 0.0001);
            }

            return points;
        }

        private static int GetHplitPolylineSegmentSubdivisions(Polyline polyline, int segment)
        {
            double bulge = Math.Abs(polyline.GetBulgeAt(segment));
            if (bulge <= 1e-9) return 1;

            Point2d start = polyline.GetPoint2dAt(segment);
            Point2d end = polyline.GetPoint2dAt((segment + 1) % polyline.NumberOfVertices);
            double chord = Math.Max(HplitDistance(start, end), 0.0001);
            double angle = Math.Abs(4.0 * Math.Atan(bulge));
            double radius = chord / Math.Max(2.0 * Math.Sin(angle / 2.0), 1e-9);
            double arcLength = Math.Abs(radius * angle);
            int byLength = (int)Math.Ceiling(arcLength / 1.0);
            int byAngle = (int)Math.Ceiling(angle / (Math.PI / 16.0));
            return Math.Min(96, Math.Max(8, Math.Max(byLength, byAngle)));
        }

        private static List<List<Point2d>> SplitHplitPolygonsByRegion(Polyline source, List<Point2d> cutPoints)
        {
            List<List<Point2d>> result = new List<List<Point2d>>();
            Polyline cutter = null;
            DBObjectCollection sourceRegions = null;
            DBObjectCollection cutterRegions = null;

            try
            {
                cutter = CreateHplitCutterPolyline(source, cutPoints);

                DBObjectCollection sourceCurves = new DBObjectCollection();
                sourceCurves.Add(source);
                sourceRegions = Region.CreateFromCurves(sourceCurves);

                DBObjectCollection cutterCurves = new DBObjectCollection();
                cutterCurves.Add(cutter);
                cutterRegions = Region.CreateFromCurves(cutterCurves);

                if (sourceRegions == null || sourceRegions.Count == 0 || cutterRegions == null || cutterRegions.Count == 0)
                    return result;

                for (int sourceIndex = 0; sourceIndex < sourceRegions.Count; sourceIndex++)
                {
                    Region sourceRegion = sourceRegions[sourceIndex] as Region;
                    if (sourceRegion == null) continue;

                    for (int cutterIndex = 0; cutterIndex < cutterRegions.Count; cutterIndex++)
                    {
                        Region cutterRegion = cutterRegions[cutterIndex] as Region;
                        if (cutterRegion == null) continue;

                        Region cutterClone = null;
                        try
                        {
                            cutterClone = cutterRegion.Clone() as Region;
                            if (cutterClone != null)
                                sourceRegion.BooleanOperation(BooleanOperationType.BoolSubtract, cutterClone);
                        }
                        finally
                        {
                            DisposeHplitDBObject(cutterClone);
                        }
                    }

                    result.AddRange(ExtractHplitRegionLoops(sourceRegion));
                }

                result = FilterHplitOuterLoops(result);
                if (result.Count < 2) return new List<List<Point2d>>();
                return result;
            }
            catch
            {
                return new List<List<Point2d>>();
            }
            finally
            {
                DisposeHplitDBObjectCollection(sourceRegions);
                DisposeHplitDBObjectCollection(cutterRegions);
                DisposeHplitDBObject(cutter);
            }
        }

        private static Polyline CreateHplitCutterPolyline(Polyline source, List<Point2d> cutPoints)
        {
            List<Point2d> centerPath = CleanHplitPoints(cutPoints, false);
            double halfSpacing = HsetHplitHalfCutSpacing;
            List<Point2d> leftOffset = BuildHplitOffsetPath(centerPath, halfSpacing);
            List<Point2d> rightOffset = BuildHplitOffsetPath(centerPath, -halfSpacing);
            if (leftOffset.Count < 2 || rightOffset.Count < 2)
                throw new InvalidOperationException("无法生成 HPLIT 分割带。");

            List<Point2d> cutterPoints = new List<Point2d>();
            for (int i = 0; i < leftOffset.Count; i++)
                AddHplitPoint(cutterPoints, leftOffset[i], 0.0001);
            for (int i = rightOffset.Count - 1; i >= 0; i--)
                AddHplitPoint(cutterPoints, rightOffset[i], 0.0001);

            cutterPoints = CleanHplitPoints(cutterPoints, true);
            if (cutterPoints.Count < 3)
                throw new InvalidOperationException("分割带顶点少于 3 个。");

            Polyline cutter = new Polyline();
            cutter.SetDatabaseDefaults();
            for (int i = 0; i < cutterPoints.Count; i++)
                cutter.AddVertexAt(i, cutterPoints[i], 0.0, 0.0, 0.0);
            cutter.Closed = true;
            return cutter;
        }

        private static List<List<Point2d>> ExtractHplitRegionLoops(Region region)
        {
            List<List<Point2d>> loops = new List<List<Point2d>>();
            DBObjectCollection exploded = new DBObjectCollection();
            try
            {
                region.Explode(exploded);
                List<HplitCurvePiece> pieces = new List<HplitCurvePiece>();

                for (int i = 0; i < exploded.Count; i++)
                {
                    Region nested = exploded[i] as Region;
                    if (nested != null)
                    {
                        loops.AddRange(ExtractHplitRegionLoops(nested));
                        continue;
                    }
                    Curve curve = exploded[i] as Curve;
                    if (curve == null) continue;

                    List<Point2d> points = SampleHplitCurve(curve);
                    if (points.Count < 2) continue;

                    if (HplitDistance(points[0], points[points.Count - 1]) <= 0.01)
                    {
                        points = CleanHplitPoints(points, true);
                        if (points.Count >= 3)
                            loops.Add(points);
                    }
                    else
                    {
                        HplitCurvePiece piece = new HplitCurvePiece();
                        piece.Points = points;
                        pieces.Add(piece);
                    }
                }

                loops.AddRange(BuildHplitLoopsFromPieces(pieces, 0.01));
                return loops;
            }
            finally
            {
                DisposeHplitDBObjectCollection(exploded);
            }
        }

        private static List<Point2d> SampleHplitCurve(Curve curve)
        {
            Polyline polyline = curve as Polyline;
            if (polyline != null)
                return SampleHplitPolyline(polyline, true);

            List<Point2d> points = new List<Point2d>();
            int subdivisions = GetHplitCurveSubdivisions(curve);
            double startParam = curve.StartParam;
            double endParam = curve.EndParam;

            for (int i = 0; i <= subdivisions; i++)
            {
                double parameter = startParam + (endParam - startParam) * i / subdivisions;
                Point3d sample = curve.GetPointAtParameter(parameter);
                AddHplitPoint(points, new Point2d(sample.X, sample.Y), 0.0001);
            }
            return points;
        }

        private static int GetHplitCurveSubdivisions(Curve curve)
        {
            if (curve is Line) return 1;

            double length = 0.0;
            try
            {
                length = Math.Abs(curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam));
            }
            catch
            {
                try
                {
                    Point3d start = curve.GetPointAtParameter(curve.StartParam);
                    Point3d end = curve.GetPointAtParameter(curve.EndParam);
                    length = start.DistanceTo(end);
                }
                catch
                {
                    length = 16.0;
                }
            }

            return Math.Min(256, Math.Max(8, (int)Math.Ceiling(length / 1.0)));
        }

        private static List<List<Point2d>> BuildHplitLoopsFromPieces(List<HplitCurvePiece> pieces, double tolerance)
        {
            List<List<Point2d>> loops = new List<List<Point2d>>();
            for (int i = 0; i < pieces.Count; i++)
            {
                if (pieces[i].Used || pieces[i].Points.Count < 2) continue;

                pieces[i].Used = true;
                List<Point2d> loop = new List<Point2d>(pieces[i].Points);
                bool closed = false;

                int guard = 0;
                while (guard < pieces.Count + 4)
                {
                    guard++;
                    if (loop.Count >= 3 && HplitDistance(loop[loop.Count - 1], loop[0]) <= tolerance)
                    {
                        loop.RemoveAt(loop.Count - 1);
                        closed = true;
                        break;
                    }

                    bool reverse;
                    int nextIndex = FindHplitNextPiece(pieces, loop[loop.Count - 1], tolerance, out reverse);
                    if (nextIndex < 0) break;

                    pieces[nextIndex].Used = true;
                    List<Point2d> nextPoints = new List<Point2d>(pieces[nextIndex].Points);
                    if (reverse) nextPoints.Reverse();
                    for (int pointIndex = 1; pointIndex < nextPoints.Count; pointIndex++)
                        AddHplitPoint(loop, nextPoints[pointIndex], 0.0001);
                }

                if (!closed && loop.Count >= 3 && HplitDistance(loop[loop.Count - 1], loop[0]) <= tolerance)
                {
                    loop.RemoveAt(loop.Count - 1);
                    closed = true;
                }

                loop = CleanHplitPoints(loop, true);
                if (closed && loop.Count >= 3 && Math.Abs(HplitSignedArea(loop)) > 0.01)
                    loops.Add(loop);
            }
            return loops;
        }

        private static int FindHplitNextPiece(List<HplitCurvePiece> pieces, Point2d point, double tolerance, out bool reverse)
        {
            reverse = false;
            int bestIndex = -1;
            double bestDistance = tolerance;

            for (int i = 0; i < pieces.Count; i++)
            {
                if (pieces[i].Used || pieces[i].Points.Count < 2) continue;
                double startDistance = HplitDistance(point, pieces[i].Points[0]);
                if (startDistance <= bestDistance)
                {
                    bestDistance = startDistance;
                    bestIndex = i;
                    reverse = false;
                }

                double endDistance = HplitDistance(point, pieces[i].Points[pieces[i].Points.Count - 1]);
                if (endDistance <= bestDistance)
                {
                    bestDistance = endDistance;
                    bestIndex = i;
                    reverse = true;
                }
            }
            return bestIndex;
        }

        private static List<List<Point2d>> FilterHplitOuterLoops(List<List<Point2d>> loops)
        {
            List<List<Point2d>> cleaned = new List<List<Point2d>>();
            List<double> areas = new List<double>();

            for (int i = 0; i < loops.Count; i++)
            {
                List<Point2d> loop = CleanHplitPoints(loops[i], true);
                double area = Math.Abs(HplitSignedArea(loop));
                if (loop.Count >= 3 && area > 0.01)
                {
                    cleaned.Add(loop);
                    areas.Add(area);
                }
            }

            List<List<Point2d>> result = new List<List<Point2d>>();
            for (int i = 0; i < cleaned.Count; i++)
            {
                Point2d probe = HplitPolygonCentroid(cleaned[i]);
                bool contained = false;
                for (int j = 0; j < cleaned.Count; j++)
                {
                    if (i == j || areas[j] <= areas[i] + 0.01) continue;
                    if (HplitPointInsidePolygon(probe, cleaned[j], 0.001) || HplitPointInsidePolygon(cleaned[i][0], cleaned[j], 0.001))
                    {
                        contained = true;
                        break;
                    }
                }
                if (!contained)
                    result.Add(cleaned[i]);
            }
            return result;
        }

        private static void DisposeHplitDBObjectCollection(DBObjectCollection objects)
        {
            if (objects == null) return;
            for (int i = 0; i < objects.Count; i++)
                DisposeHplitDBObject(objects[i]);
        }

        private static void DisposeHplitDBObject(DBObject dbObject)
        {
            if (dbObject == null) return;
            try
            {
                dbObject.Dispose();
            }
            catch
            {
            }
        }

        private static bool TrySplitHplitBoundaryZigzag(List<Point2d> boundary, List<Point2d> cutPoints, out List<List<Point2d>> polygons)
        {
            polygons = new List<List<Point2d>>();
            List<Point2d> points = CleanHplitPoints(cutPoints, false);
            if (boundary == null || boundary.Count != 4 || points.Count != 4) return false;

            double minX = boundary[0].X;
            double maxX = boundary[0].X;
            double minY = boundary[0].Y;
            double maxY = boundary[0].Y;
            for (int i = 1; i < boundary.Count; i++)
            {
                minX = Math.Min(minX, boundary[i].X);
                maxX = Math.Max(maxX, boundary[i].X);
                minY = Math.Min(minY, boundary[i].Y);
                maxY = Math.Max(maxY, boundary[i].Y);
            }

            for (int i = 0; i < boundary.Count; i++)
            {
                bool corner = (HplitCloseTo(boundary[i].X, minX) || HplitCloseTo(boundary[i].X, maxX)) && (HplitCloseTo(boundary[i].Y, minY) || HplitCloseTo(boundary[i].Y, maxY));
                if (!corner) return false;
            }

            Point2d firstStart = points[0];
            Point2d firstEnd = points[1];
            Point2d secondStart = points[2];
            Point2d secondEnd = points[3];
            if (!HplitCloseTo(firstStart.Y, firstEnd.Y) || !HplitCloseTo(secondStart.Y, secondEnd.Y)) return false;
            if (!HplitSpansHplitRectangle(firstStart, firstEnd, minX, maxX) || !HplitSpansHplitRectangle(secondStart, secondEnd, minX, maxX)) return false;
            if (HplitCloseTo(firstEnd.Y, secondStart.Y) || HplitCloseTo(firstEnd.X, secondStart.X)) return false;

            double upperY = Math.Max(firstEnd.Y, secondStart.Y);
            double lowerY = Math.Min(firstEnd.Y, secondStart.Y);
            double halfSpacing = HsetHplitHalfCutSpacing;
            double topInsideY = upperY - halfSpacing;
            double bottomInsideY = lowerY + halfSpacing;
            if (bottomInsideY >= topInsideY || upperY + halfSpacing >= maxY || lowerY - halfSpacing <= minY) return false;

            Point2d upperPoint = HplitCloseTo(firstEnd.Y, upperY) ? firstEnd : secondStart;
            Point2d lowerPoint = HplitCloseTo(firstEnd.Y, lowerY) ? firstEnd : secondStart;
            Vector2d direction = HplitNormalize(HplitVector(upperPoint, lowerPoint));
            Vector2d normal = new Vector2d(-direction.Y, direction.X);
            Point2d plusStart = HplitMove(upperPoint, normal, halfSpacing);
            Point2d plusEnd = HplitMove(lowerPoint, normal, halfSpacing);
            Point2d minusStart = HplitMove(upperPoint, normal, -halfSpacing);
            Point2d minusEnd = HplitMove(lowerPoint, normal, -halfSpacing);

            List<Point2d> top = new List<Point2d>();
            top.Add(new Point2d(minX, upperY + halfSpacing));
            top.Add(new Point2d(maxX, upperY + halfSpacing));
            top.Add(new Point2d(maxX, maxY));
            top.Add(new Point2d(minX, maxY));

            List<Point2d> bottom = new List<Point2d>();
            bottom.Add(new Point2d(minX, minY));
            bottom.Add(new Point2d(maxX, minY));
            bottom.Add(new Point2d(maxX, lowerY - halfSpacing));
            bottom.Add(new Point2d(minX, lowerY - halfSpacing));

            List<Point2d> middleA;
            List<Point2d> middleB;
            if (HplitCloseTo(upperPoint.X, maxX) && HplitCloseTo(lowerPoint.X, minX))
            {
                middleA = BuildHplitBoundaryZigzagPiece(minX, topInsideY, minusStart, minusEnd, minX, true, bottomInsideY, topInsideY, minX, maxX);
                middleB = BuildHplitBoundaryZigzagPiece(maxX, bottomInsideY, plusStart, plusEnd, maxX, false, bottomInsideY, topInsideY, minX, maxX);
            }
            else if (HplitCloseTo(upperPoint.X, minX) && HplitCloseTo(lowerPoint.X, maxX))
            {
                middleA = BuildHplitBoundaryZigzagPiece(maxX, topInsideY, minusStart, minusEnd, maxX, true, bottomInsideY, topInsideY, minX, maxX);
                middleB = BuildHplitBoundaryZigzagPiece(minX, bottomInsideY, plusStart, plusEnd, minX, false, bottomInsideY, topInsideY, minX, maxX);
            }
            else
            {
                return false;
            }

            if (middleA == null || middleB == null) return false;
            polygons.Add(CleanHplitPoints(top, true));
            polygons.Add(CleanHplitPoints(bottom, true));
            polygons.Add(CleanHplitPoints(middleA, true));
            polygons.Add(CleanHplitPoints(middleB, true));
            return polygons.Count == 4;
        }

        private static List<Point2d> BuildHplitBoundaryZigzagPiece(double sideX, double horizontalY, Point2d lineStart, Point2d lineEnd, double intersectX, bool useTop, double bottomInsideY, double topInsideY, double minX, double maxX)
        {
            Point2d horizontalHit;
            Point2d sideHit;
            if (!TryHplitAtY(lineStart, lineEnd, horizontalY, out horizontalHit) || !TryHplitAtX(lineStart, lineEnd, intersectX, out sideHit)) return null;
            List<Point2d> result = new List<Point2d>();
            result.Add(new Point2d(sideX, horizontalY));
            result.Add(new Point2d(HplitClamp(horizontalHit.X, minX, maxX), horizontalY));
            result.Add(new Point2d(sideX, HplitClamp(sideHit.Y, bottomInsideY, topInsideY)));
            return Math.Abs(HplitSignedArea(result)) > 0.01 ? result : null;
        }

        private static bool TryHplitAtY(Point2d start, Point2d end, double y, out Point2d point)
        {
            point = Point2d.Origin;
            double dy = end.Y - start.Y;
            if (Math.Abs(dy) < 1e-9) return false;
            double fraction = (y - start.Y) / dy;
            point = new Point2d(start.X + (end.X - start.X) * fraction, y);
            return true;
        }

        private static bool TryHplitAtX(Point2d start, Point2d end, double x, out Point2d point)
        {
            point = Point2d.Origin;
            double dx = end.X - start.X;
            if (Math.Abs(dx) < 1e-9) return false;
            double fraction = (x - start.X) / dx;
            point = new Point2d(x, start.Y + (end.Y - start.Y) * fraction);
            return true;
        }

        private static bool HplitSpansHplitRectangle(Point2d start, Point2d end, double minX, double maxX)
        {
            return (HplitCloseTo(start.X, minX) && HplitCloseTo(end.X, maxX)) || (HplitCloseTo(start.X, maxX) && HplitCloseTo(end.X, minX));
        }

        private static bool HplitCloseTo(double left, double right)
        {
            return Math.Abs(left - right) <= 0.0001;
        }

        private static List<List<Point2d>> SplitHplitPolygons(List<Point2d> boundary, List<Point2d> cutPoints)
        {
            List<List<Point2d>> polygons = new List<List<Point2d>>();
            polygons.Add(CleanHplitPoints(boundary, true));
            bool anySegmentSplit = false;

            for (int i = 0; i < cutPoints.Count - 1; i++)
            {
                List<Point2d> segment = new List<Point2d>();
                segment.Add(cutPoints[i]);
                segment.Add(cutPoints[i + 1]);

                List<List<Point2d>> nextPolygons = new List<List<Point2d>>();
                for (int polygonIndex = 0; polygonIndex < polygons.Count; polygonIndex++)
                {
                    List<List<Point2d>> split;
                    if (TrySplitHplitPolygon(polygons[polygonIndex], segment, true, out split))
                    {
                        nextPolygons.AddRange(split);
                        anySegmentSplit = true;
                    }
                    else
                    {
                        nextPolygons.Add(polygons[polygonIndex]);
                    }
                }
                polygons = nextPolygons;
            }

            if (anySegmentSplit && polygons.Count >= 2)
                return polygons;

            List<List<Point2d>> wholePathSplit;
            if (TrySplitHplitPolygon(boundary, cutPoints, true, out wholePathSplit))
                return wholePathSplit;

            return new List<List<Point2d>>();
        }

        private static bool TrySplitHplitPolygon(List<Point2d> polygon, List<Point2d> cutPoints, bool requireRawCrossing, out List<List<Point2d>> splitPolygons)
        {
            splitPolygons = new List<List<Point2d>>();
            if (cutPoints == null || cutPoints.Count < 2) return false;
            if (requireRawCrossing && !HplitRawPathCrossesPolygon(cutPoints, polygon)) return false;

            List<Point2d> centerPath = CleanHplitPoints(cutPoints, false);
            double halfSpacing = HsetHplitHalfCutSpacing;
            List<Point2d> leftOffset = BuildHplitOffsetPath(centerPath, halfSpacing);
            List<Point2d> rightOffset = BuildHplitOffsetPath(centerPath, -halfSpacing);

            HplitClip leftClip = ClipHplitOffsetPath(leftOffset, polygon);
            HplitClip rightClip = ClipHplitOffsetPath(rightOffset, polygon);
            if (leftClip == null || rightClip == null) return false;

            HplitCandidate leftPart = SelectHplitCandidate(leftClip, polygon, centerPath, 1.0);
            HplitCandidate rightPart = SelectHplitCandidate(rightClip, polygon, centerPath, -1.0);
            if (leftPart == null || rightPart == null) return false;

            double parentArea = Math.Abs(HplitSignedArea(polygon));
            if (leftPart.Area < 0.01 || rightPart.Area < 0.01) return false;
            if (leftPart.Area >= parentArea - 0.01 || rightPart.Area >= parentArea - 0.01) return false;

            splitPolygons.Add(leftPart.Points);
            splitPolygons.Add(rightPart.Points);
            return true;
        }

        private static bool HplitRawPathCrossesPolygon(List<Point2d> path, List<Point2d> polygon)
        {
            List<Point2d> hits = new List<Point2d>();
            for (int pathIndex = 0; pathIndex < path.Count - 1; pathIndex++)
            {
                Point2d pathStart = path[pathIndex];
                Point2d pathEnd = path[pathIndex + 1];
                if (HplitDistance(pathStart, pathEnd) < 1e-9) continue;

                for (int edgeIndex = 0; edgeIndex < polygon.Count; edgeIndex++)
                {
                    double pathFraction;
                    double boundaryFraction;
                    Point2d hitPoint;
                    if (HplitSegmentIntersection(pathStart, pathEnd, polygon[edgeIndex], polygon[(edgeIndex + 1) % polygon.Count], out pathFraction, out boundaryFraction, out hitPoint))
                        AddHplitPointAnywhere(hits, hitPoint, 0.0001);
                }
            }
            return hits.Count >= 2;
        }

        private static void AddHplitPointAnywhere(List<Point2d> points, Point2d point, double tolerance)
        {
            for (int i = 0; i < points.Count; i++)
            {
                if (HplitDistance(points[i], point) <= tolerance)
                    return;
            }
            points.Add(point);
        }

        private static List<Point2d> BuildHplitOffsetPath(List<Point2d> centerPath, double offset)
        {
            List<Point2d> path = CleanHplitPoints(centerPath, false);
            if (path.Count < 2) return path;

            List<Vector2d> normals = new List<Vector2d>();
            for (int i = 0; i < path.Count - 1; i++)
            {
                Vector2d direction = HplitNormalize(HplitVector(path[i], path[i + 1]));
                normals.Add(new Vector2d(-direction.Y, direction.X));
            }

            List<Point2d> offsetPath = new List<Point2d>();
            offsetPath.Add(HplitMove(path[0], normals[0], offset));

            for (int i = 1; i < path.Count - 1; i++)
            {
                Point2d previousA = HplitMove(path[i - 1], normals[i - 1], offset);
                Point2d previousB = HplitMove(path[i], normals[i - 1], offset);
                Point2d nextA = HplitMove(path[i], normals[i], offset);
                Point2d nextB = HplitMove(path[i + 1], normals[i], offset);

                Point2d intersection;
                if (HplitLineIntersection(previousA, previousB, nextA, nextB, out intersection))
                    offsetPath.Add(intersection);
                else
                    offsetPath.Add(nextA);
            }

            offsetPath.Add(HplitMove(path[path.Count - 1], normals[normals.Count - 1], offset));
            return CleanHplitPoints(offsetPath, false);
        }

        private static HplitClip ClipHplitOffsetPath(List<Point2d> offsetPath, List<Point2d> boundary)
        {
            if (offsetPath == null || offsetPath.Count < 2) return null;

            List<HplitHit> hits = new List<HplitHit>();
            for (int pathIndex = 0; pathIndex < offsetPath.Count - 1; pathIndex++)
            {
                Point2d pathStart = offsetPath[pathIndex];
                Point2d pathEnd = offsetPath[pathIndex + 1];
                if (HplitDistance(pathStart, pathEnd) < 1e-9) continue;

                for (int edgeIndex = 0; edgeIndex < boundary.Count; edgeIndex++)
                {
                    Point2d edgeStart = boundary[edgeIndex];
                    Point2d edgeEnd = boundary[(edgeIndex + 1) % boundary.Count];
                    double pathFraction;
                    double boundaryFraction;
                    Point2d hitPoint;
                    if (HplitSegmentIntersection(pathStart, pathEnd, edgeStart, edgeEnd, out pathFraction, out boundaryFraction, out hitPoint))
                    {
                        HplitHit hit = new HplitHit();
                        hit.Point = hitPoint;
                        hit.PathSegment = pathIndex;
                        hit.PathFraction = pathFraction;
                        hit.PathParam = pathIndex + pathFraction;
                        hit.BoundaryEdge = edgeIndex;
                        hit.BoundaryFraction = boundaryFraction;
                        AddHplitHit(hits, hit);
                    }
                }
            }

            if (hits.Count < 2) return null;
            hits.Sort(delegate (HplitHit a, HplitHit b) { return a.PathParam.CompareTo(b.PathParam); });

            HplitHit bestStart = null;
            HplitHit bestEnd = null;
            double bestSpan = -1.0;
            for (int i = 0; i < hits.Count - 1; i++)
            {
                if (HplitDistance(hits[i].Point, hits[i + 1].Point) < 0.0001) continue;
                double midParam = (hits[i].PathParam + hits[i + 1].PathParam) / 2.0;
                Point2d midPoint = HplitPointAtPathParam(offsetPath, midParam);
                if (HplitPointInsidePolygon(midPoint, boundary, 0.0001))
                {
                    double span = hits[i + 1].PathParam - hits[i].PathParam;
                    if (span > bestSpan)
                    {
                        bestSpan = span;
                        bestStart = hits[i];
                        bestEnd = hits[i + 1];
                    }
                }
            }

            if (bestStart == null || bestEnd == null) return null;

            HplitClip clip = new HplitClip();
            clip.Start = bestStart;
            clip.End = bestEnd;
            clip.Points = BuildHplitClippedPath(offsetPath, bestStart, bestEnd);
            if (clip.Points.Count < 2) return null;
            return clip;
        }

        private static void AddHplitHit(List<HplitHit> hits, HplitHit hit)
        {
            for (int i = 0; i < hits.Count; i++)
            {
                if (HplitDistance(hits[i].Point, hit.Point) < 0.0001 || Math.Abs(hits[i].PathParam - hit.PathParam) < 1e-7)
                    return;
            }
            hits.Add(hit);
        }

        private static List<Point2d> BuildHplitClippedPath(List<Point2d> offsetPath, HplitHit start, HplitHit end)
        {
            List<Point2d> result = new List<Point2d>();
            AddHplitPoint(result, start.Point, 0.0001);

            for (int vertexIndex = start.PathSegment + 1; vertexIndex <= end.PathSegment; vertexIndex++)
            {
                if (vertexIndex > 0 && vertexIndex < offsetPath.Count)
                {
                    double vertexParam = vertexIndex;
                    if (vertexParam > start.PathParam + 1e-7 && vertexParam < end.PathParam - 1e-7)
                        AddHplitPoint(result, offsetPath[vertexIndex], 0.0001);
                }
            }

            AddHplitPoint(result, end.Point, 0.0001);
            return result;
        }

        private static HplitCandidate SelectHplitCandidate(HplitClip clip, List<Point2d> boundary, List<Point2d> centerPath, double desiredSide)
        {
            List<Point2d> firstBoundary = BuildHplitBoundaryPath(boundary, clip.End, clip.Start);
            List<Point2d> secondBoundary = BuildHplitBoundaryPath(boundary, clip.Start, clip.End);
            secondBoundary.Reverse();

            HplitCandidate first = MakeHplitCandidate(clip.Points, firstBoundary, centerPath);
            HplitCandidate second = MakeHplitCandidate(clip.Points, secondBoundary, centerPath);
            if (first == null) return second;
            if (second == null) return first;

            if (desiredSide >= 0)
                return first.Side >= second.Side ? first : second;
            return first.Side <= second.Side ? first : second;
        }

        private static HplitCandidate MakeHplitCandidate(List<Point2d> offsetPath, List<Point2d> boundaryPath, List<Point2d> centerPath)
        {
            List<Point2d> points = new List<Point2d>();
            for (int i = 0; i < offsetPath.Count; i++)
                AddHplitPoint(points, offsetPath[i], 0.0001);
            for (int i = 1; i < boundaryPath.Count; i++)
                AddHplitPoint(points, boundaryPath[i], 0.0001);

            points = CleanHplitPoints(points, true);
            if (points.Count < 3) return null;

            double area = Math.Abs(HplitSignedArea(points));
            if (area < 0.0001) return null;

            HplitCandidate candidate = new HplitCandidate();
            candidate.Points = points;
            candidate.Area = area;
            candidate.Side = HplitSideOfCut(HplitPolygonCentroid(points), centerPath);
            return candidate;
        }

        private static List<Point2d> BuildHplitBoundaryPath(List<Point2d> boundary, HplitHit start, HplitHit end)
        {
            List<Point2d> result = new List<Point2d>();
            AddHplitPoint(result, start.Point, 0.0001);

            int vertexCount = boundary.Count;
            double startParam = start.BoundaryEdge + start.BoundaryFraction;
            double endParam = end.BoundaryEdge + end.BoundaryFraction;
            while (endParam <= startParam + 1e-8)
                endParam += vertexCount;

            double vertexParam = Math.Floor(startParam) + 1.0;
            int guard = 0;
            while (vertexParam < endParam - 1e-8 && guard < vertexCount + 2)
            {
                int vertexIndex = ((int)Math.Round(vertexParam)) % vertexCount;
                AddHplitPoint(result, boundary[vertexIndex], 0.0001);
                vertexParam += 1.0;
                guard++;
            }

            AddHplitPoint(result, end.Point, 0.0001);
            return result;
        }

        private static Polyline CreateHplitPolyline(List<Point2d> points, Polyline source)
        {
            List<Point2d> cleanPoints = CleanHplitPoints(points, true);
            if (cleanPoints.Count < 3)
                throw new InvalidOperationException("分割结果顶点少于 3 个。");

            Polyline polyline = new Polyline();
            polyline.SetDatabaseDefaults();
            polyline.SetPropertiesFrom(source);
            for (int i = 0; i < cleanPoints.Count; i++)
                polyline.AddVertexAt(i, cleanPoints[i], 0.0, 0.0, 0.0);
            polyline.Closed = true;
            return polyline;
        }

        private static List<Point2d> CleanHplitPoints(List<Point2d> points, bool closed)
        {
            List<Point2d> clean = new List<Point2d>();
            if (points == null) return clean;

            for (int i = 0; i < points.Count; i++)
                AddHplitPoint(clean, points[i], 0.0001);

            if (closed && clean.Count > 1 && HplitDistance(clean[0], clean[clean.Count - 1]) <= 0.0001)
                clean.RemoveAt(clean.Count - 1);
            return clean;
        }

        private static void AddHplitPoint(List<Point2d> points, Point2d point, double tolerance)
        {
            // 平方距离比较，避免热路径上的开方
            if (points.Count == 0 || HplitDistanceSquared(points[points.Count - 1], point) > tolerance * tolerance)
                points.Add(point);
        }

        private static bool HplitSegmentIntersection(Point2d a1, Point2d a2, Point2d b1, Point2d b2, out double t, out double u, out Point2d hit)
        {
            Vector2d r = HplitVector(a1, a2);
            Vector2d s = HplitVector(b1, b2);
            Vector2d qp = HplitVector(a1, b1);
            double denominator = HplitCross(r, s);
            t = 0.0;
            u = 0.0;
            hit = Point2d.Origin;
            if (Math.Abs(denominator) < 1e-9) return false;

            t = HplitCross(qp, s) / denominator;
            u = HplitCross(qp, r) / denominator;
            // 端点容差按线段长度换算为“绝对距离 ≈ 1e-6mm”，避免固定参数容差
            // 在长线段上把仅擦过端点附近的路径误判为交点。
            double lengthA = Math.Max(Math.Sqrt(r.X * r.X + r.Y * r.Y), 1e-9);
            double lengthB = Math.Max(Math.Sqrt(s.X * s.X + s.Y * s.Y), 1e-9);
            double epsilonA = 1e-6 / lengthA;
            double epsilonB = 1e-6 / lengthB;
            if (t < -epsilonA || t > 1.0 + epsilonA || u < -epsilonB || u > 1.0 + epsilonB) return false;

            t = HplitClamp(t, 0.0, 1.0);
            u = HplitClamp(u, 0.0, 1.0);
            hit = new Point2d(a1.X + r.X * t, a1.Y + r.Y * t);
            return true;
        }

        private static bool HplitLineIntersection(Point2d a1, Point2d a2, Point2d b1, Point2d b2, out Point2d hit)
        {
            Vector2d r = HplitVector(a1, a2);
            Vector2d s = HplitVector(b1, b2);
            double denominator = HplitCross(r, s);
            hit = Point2d.Origin;
            if (Math.Abs(denominator) < 1e-9) return false;

            Vector2d qp = HplitVector(a1, b1);
            double t = HplitCross(qp, s) / denominator;
            hit = new Point2d(a1.X + r.X * t, a1.Y + r.Y * t);
            return true;
        }

        private static Point2d HplitPointAtPathParam(List<Point2d> path, double param)
        {
            if (path.Count == 0) return Point2d.Origin;
            if (path.Count == 1) return path[0];

            int segment = (int)Math.Floor(param);
            if (segment < 0) segment = 0;
            if (segment >= path.Count - 1) segment = path.Count - 2;
            double fraction = HplitClamp(param - segment, 0.0, 1.0);
            return new Point2d(path[segment].X + (path[segment + 1].X - path[segment].X) * fraction, path[segment].Y + (path[segment + 1].Y - path[segment].Y) * fraction);
        }

        private static bool HplitPointInsidePolygon(Point2d point, List<Point2d> polygon, double tolerance)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                Point2d a = polygon[j];
                Point2d b = polygon[i];
                if (HplitPointOnSegment(point, a, b, tolerance)) return true;

                bool crosses = ((a.Y > point.Y) != (b.Y > point.Y));
                if (crosses)
                {
                    double x = (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
                    if (point.X < x) inside = !inside;
                }
            }
            return inside;
        }

        private static bool HplitPointOnSegment(Point2d point, Point2d a, Point2d b, double tolerance)
        {
            Vector2d ab = HplitVector(a, b);
            Vector2d ap = HplitVector(a, point);
            double lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
            if (lengthSquared < 1e-12) return HplitDistance(point, a) <= tolerance;
            double cross = Math.Abs(HplitCross(ab, ap));
            if (cross > tolerance * Math.Sqrt(lengthSquared)) return false;
            // 沿线段方向用“投影比例”做判定：dot 是长度²量纲，不能与长度量纲的
            // tolerance 直接比较（旧实现短边容差被放大、长边被压缩）。
            double length = Math.Sqrt(lengthSquared);
            double projection = (ap.X * ab.X + ap.Y * ab.Y) / lengthSquared;
            double projectionTolerance = length > 1e-12 ? tolerance / length : 0.0;
            return projection >= -projectionTolerance && projection <= 1.0 + projectionTolerance;
        }

        private static double HplitSignedArea(List<Point2d> points)
        {
            if (points == null || points.Count < 3) return 0.0;
            Point2d origin = points[0];
            double area = 0.0;
            for (int i = 0; i < points.Count; i++)
            {
                Point2d a = points[i];
                Point2d b = points[(i + 1) % points.Count];
                area += (a.X - origin.X) * (b.Y - origin.Y) - (b.X - origin.X) * (a.Y - origin.Y);
            }
            return area / 2.0;
        }

        private static Point2d HplitPolygonCentroid(List<Point2d> points)
        {
            Point2d origin = points[0];
            double crossSum = 0.0;
            double cx = 0.0;
            double cy = 0.0;

            for (int i = 0; i < points.Count; i++)
            {
                Point2d a = points[i];
                Point2d b = points[(i + 1) % points.Count];
                double ax = a.X - origin.X, ay = a.Y - origin.Y;
                double bx = b.X - origin.X, by = b.Y - origin.Y;
                double cross = ax * by - bx * ay;
                crossSum += cross;
                cx += (ax + bx) * cross;
                cy += (ay + by) * cross;
            }

            if (Math.Abs(crossSum) > 1e-9)
                return new Point2d(origin.X + cx / (3.0 * crossSum), origin.Y + cy / (3.0 * crossSum));

            double sx = 0.0;
            double sy = 0.0;
            for (int i = 0; i < points.Count; i++)
            {
                sx += points[i].X;
                sy += points[i].Y;
            }
            return new Point2d(sx / points.Count, sy / points.Count);
        }

        private static double HplitSideOfCut(Point2d point, List<Point2d> centerPath)
        {
            double bestDistance = double.MaxValue;
            double bestSide = 0.0;
            for (int i = 0; i < centerPath.Count - 1; i++)
            {
                Point2d a = centerPath[i];
                Point2d b = centerPath[i + 1];
                Vector2d ab = HplitVector(a, b);
                double lengthSquared = ab.X * ab.X + ab.Y * ab.Y;
                if (lengthSquared < 1e-12) continue;

                double t = ((point.X - a.X) * ab.X + (point.Y - a.Y) * ab.Y) / lengthSquared;
                t = HplitClamp(t, 0.0, 1.0);
                Point2d closest = new Point2d(a.X + ab.X * t, a.Y + ab.Y * t);
                double distance = HplitDistanceSquared(point, closest);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestSide = HplitCross(ab, HplitVector(closest, point)) / Math.Sqrt(lengthSquared);
                }
            }
            return bestSide;
        }

        private static Vector2d HplitVector(Point2d from, Point2d to)
        {
            return new Vector2d(to.X - from.X, to.Y - from.Y);
        }

        private static Vector2d HplitNormalize(Vector2d vector)
        {
            double length = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y);
            if (length < 1e-12) return new Vector2d(1.0, 0.0);
            return new Vector2d(vector.X / length, vector.Y / length);
        }

        private static Point2d HplitMove(Point2d point, Vector2d direction, double distance)
        {
            return new Point2d(point.X + direction.X * distance, point.Y + direction.Y * distance);
        }

        private static double HplitCross(Vector2d a, Vector2d b)
        {
            return a.X * b.Y - a.Y * b.X;
        }

        private static double HplitDistance(Point2d a, Point2d b)
        {
            return Math.Sqrt(HplitDistanceSquared(a, b));
        }

        private static double HplitDistanceSquared(Point2d a, Point2d b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        private static double HplitClamp(double value, double minimum, double maximum)
        {
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }
    }
}
