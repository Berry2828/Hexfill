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
        private class HjoinSegment
        {
            public Point2d Start;
            public Point2d End;
            public ObjectId SourceId;
            public bool Removed;
            public bool InteriorOnLeft;
        }

        private class HjoinPlan
        {
            public int SourceCount;
            public int SkippedLockedCount;
            public HashSet<ObjectId> MergeSourceIds = new HashSet<ObjectId>();
            public List<HjoinLoop> ValidLoops = new List<HjoinLoop>();
        }

        private class HjoinLoop
        {
            public List<Point2d> Points = new List<Point2d>();
            public HashSet<ObjectId> SourceIds = new HashSet<ObjectId>();
            public ObjectId TemplateId = ObjectId.Null;
        }

        private class HjoinOverlapRange
        {
            public double Start;
            public double End;
        }

        private class HjoinJunction
        {
            public Point2d First;
            public Point2d Second;
        }

        [CommandMethod("HJOIN")]
        public void HJOIN()
        {
            Document document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            Database database = document.Database;
            Editor editor = document.Editor;

            try
            {
                PromptSelectionOptions options = new PromptSelectionOptions();
                options.MessageForAdding = "\n选择或框选要聚合的闭合聚合线: ";
                SelectionFilter filter = new SelectionFilter(new TypedValue[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
                PromptSelectionResult selection = editor.GetSelection(options, filter);
                if (selection.Status != PromptStatus.OK || selection.Value == null) return;

                // 去重：同一聚合线被重复选中时只处理一次，避免产出退化环
                ObjectId[] rawIds = selection.Value.GetObjectIds();
                var uniqueIds = new List<ObjectId>(rawIds.Length);
                var seenIds = new HashSet<ObjectId>();
                for (int i = 0; i < rawIds.Length; i++)
                {
                    if (seenIds.Add(rawIds[i]))
                        uniqueIds.Add(rawIds[i]);
                }

                HjoinPlan plan;
                using (Transaction tr = database.TransactionManager.StartTransaction())
                {
                    plan = BuildHjoinPlan(tr, uniqueIds.ToArray());
                    tr.Commit();
                }

                if (plan.SourceCount < 2)
                {
                    editor.WriteMessage("\nHJOIN：至少需要选择 2 条可聚合的闭合轻量聚合线。");
                    return;
                }

                if (plan.MergeSourceIds.Count == 0 || plan.ValidLoops.Count == 0)
                {
                    editor.WriteMessage("\nHJOIN：未发现可聚合的相邻边界或足够接近的重合边，未修改原图。");
                    return;
                }

                int createdCount;
                using (Transaction tr = database.TransactionManager.StartTransaction())
                {
                    createdCount = ExecuteHjoinPlan(database, tr, plan);
                    tr.Commit();
                }

                editor.WriteMessage(string.Format("\nHJOIN：聚合 {0} 条闭合聚合线，生成 {1} 条，跳过锁定图层 {2} 条；距离 {3}mm。", plan.MergeSourceIds.Count, createdCount, plan.SkippedLockedCount, HsetFormat(HsetHjoinMergeTolerance)));
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage(string.Format("\nHJOIN错误：{0}", ex.Message));
            }
        }

        private static HjoinPlan BuildHjoinPlan(Transaction tr, ObjectId[] selectedIds)
        {
            HjoinPlan plan = new HjoinPlan();
            List<Polyline> sources = new List<Polyline>();
            List<ObjectId> sourceIds = new List<ObjectId>();
            foreach (ObjectId id in selectedIds)
            {
                Polyline polyline = tr.GetObject(id, OpenMode.ForRead, false) as Polyline;
                if (polyline == null || polyline.IsErased)
                    continue;
                if (!HjoinCanUsePolyline(polyline))
                    continue;
                if (HjoinLayerIsLocked(tr, polyline))
                {
                    plan.SkippedLockedCount++;
                    continue;
                }
                sources.Add(polyline);
                sourceIds.Add(id);
            }

            plan.SourceCount = sources.Count;
            if (sources.Count < 2) return plan;

            List<HjoinSegment> segments = new List<HjoinSegment>();
            for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
                AddHjoinSegments(segments, sources[sourceIndex], sourceIds[sourceIndex]);

            List<int>[] candidateLists = BuildHjoinCandidateLists(segments, HsetHjoinMergeTolerance);
            Dictionary<ObjectId, HashSet<ObjectId>> sourceLinks = BuildHjoinSourceLinks(segments, HsetHjoinMergeTolerance, candidateLists);
            int removedSharedCount = MarkHjoinInternalSegments(segments, HsetHjoinMergeTolerance, candidateLists);
            if (removedSharedCount == 0) return plan;

            HashSet<ObjectId> candidateSourceIds = new HashSet<ObjectId>();
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].Removed && sourceLinks.ContainsKey(segments[i].SourceId)
                    && sourceLinks[segments[i].SourceId].Count > 0)
                    candidateSourceIds.Add(segments[i].SourceId);
            }

            List<HjoinSegment> mergeSegments = new List<HjoinSegment>();
            for (int i = 0; i < segments.Count; i++)
            {
                if (candidateSourceIds.Contains(segments[i].SourceId))
                    mergeSegments.Add(segments[i]);
            }

            // Match endpoints only through the shared seams that were actually removed.
            // A broad proximity walk can jump into a nearby hole or leave a seam behind.
            List<HjoinLoop> loops = BuildHjoinLoops(mergeSegments, 0.000001);
            for (int i = 0; i < loops.Count; i++)
            {
                List<Point2d> clean = CleanHjoinLoop(loops[i].Points, 0.000001);
                if (clean.Count >= 3 && Math.Abs(HjoinArea(clean)) > 0.000000000001)
                {
                    loops[i].Points = clean;
                    plan.ValidLoops.Add(loops[i]);
                    plan.MergeSourceIds.UnionWith(ExpandHjoinSourceGroup(loops[i].SourceIds, sourceLinks));
                }
            }

            return plan;
        }

        private static int ExecuteHjoinPlan(Database database, Transaction tr, HjoinPlan plan)
        {
            int createdCount = 0;
            BlockTableRecord space = (BlockTableRecord)tr.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
            Dictionary<ObjectId, Polyline> templateCache = new Dictionary<ObjectId, Polyline>();
            foreach (HjoinLoop loop in plan.ValidLoops)
            {
                Polyline template = null;
                if (!loop.TemplateId.IsNull)
                {
                    if (!templateCache.TryGetValue(loop.TemplateId, out template))
                    {
                        template = tr.GetObject(loop.TemplateId, OpenMode.ForRead, false) as Polyline;
                        templateCache[loop.TemplateId] = template;
                    }
                }
                Polyline result = new Polyline();
                result.SetDatabaseDefaults();
                if (template != null)
                    result.SetPropertiesFrom(template);
                for (int i = 0; i < loop.Points.Count; i++)
                    result.AddVertexAt(i, loop.Points[i], 0.0, 0.0, 0.0);
                result.Closed = true;
                space.AppendEntity(result);
                tr.AddNewlyCreatedDBObject(result, true);
                createdCount++;
            }

            foreach (ObjectId sourceId in plan.MergeSourceIds)
            {
                Entity entity = tr.GetObject(sourceId, OpenMode.ForWrite, false) as Entity;
                if (entity != null && !entity.IsErased)
                    entity.Erase();
            }

            return createdCount;
        }

        private static bool HjoinCanUsePolyline(Polyline polyline)
        {
            return polyline != null && polyline.Closed && polyline.NumberOfVertices >= 3;
        }

        private static bool HjoinLayerIsLocked(Transaction tr, Entity entity)
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

        private static void AddHjoinSegments(List<HjoinSegment> segments, Polyline polyline, ObjectId sourceId)
        {
            List<Point2d> sampledPoints = SampleHplitPolyline(polyline, false);
            int count = sampledPoints.Count;
            if (count < 3)
                return;
            bool interiorOnLeft = HplitSignedArea(sampledPoints) > 0;
            if (!interiorOnLeft) sampledPoints.Reverse();

            for (int i = 0; i < count; i++)
            {
                Point2d start = sampledPoints[i];
                Point2d end = sampledPoints[(i + 1) % count];
                if (start.GetDistanceTo(end) <= 0.000001)
                    continue;
                segments.Add(new HjoinSegment
                {
                    Start = start,
                    End = end,
                    SourceId = sourceId,
                    Removed = false,
                    InteriorOnLeft = true
                });
            }
        }

        private static int MarkHjoinInternalSegments(List<HjoinSegment> segments, double tolerance, List<int>[] candidateLists)
        {
            List<HjoinSegment> originalSegments = new List<HjoinSegment>(segments);
            List<HjoinSegment> splitSegments = new List<HjoinSegment>();
            bool anyOverlap = false;
            bool anyTooShort = false;
            List<HjoinJunction> junctions = new List<HjoinJunction>();

            for (int i = 0; i < originalSegments.Count; i++)
            {
                HjoinSegment segment = originalSegments[i];
                Vector2d segmentVector = segment.End - segment.Start;
                double segmentLength = segmentVector.Length;
                if (segmentLength <= 0.000001)
                {
                    anyTooShort = true;
                    continue;
                }

                List<double> splitParameters = new List<double> { 0.0, 1.0 };
                List<HjoinOverlapRange> overlapRanges = new List<HjoinOverlapRange>();
                for (int candidateIndex = 0; candidateIndex < candidateLists[i].Count; candidateIndex++)
                {
                    int j = candidateLists[i][candidateIndex];

                    double overlapStart;
                    double overlapEnd;
                    if (!TryGetHjoinOverlapParameters(segment, originalSegments[j], tolerance, out overlapStart, out overlapEnd))
                        continue;

                    anyOverlap = true;
                    if (j > i)
                    {
                        HjoinSegment other = originalSegments[j];
                        foreach (double parameter in new[] { overlapStart, overlapEnd })
                        {
                            Point2d first = HjoinPointAtParameter(segment.Start, segment.End, parameter);
                            Vector2d otherVector = other.End - other.Start;
                            double otherParameter = ((first.X - other.Start.X) * otherVector.X
                                + (first.Y - other.Start.Y) * otherVector.Y) / (otherVector.Length * otherVector.Length);
                            Point2d second = HjoinPointAtParameter(other.Start, other.End, Math.Max(0, Math.Min(1, otherParameter)));
                            junctions.Add(new HjoinJunction { First = first, Second = second });
                        }
                    }
                    AddHjoinSplitParameter(splitParameters, overlapStart);
                    AddHjoinSplitParameter(splitParameters, overlapEnd);
                    overlapRanges.Add(new HjoinOverlapRange { Start = overlapStart, End = overlapEnd });
                }

                splitParameters.Sort();
                for (int parameterIndex = 0; parameterIndex < splitParameters.Count - 1; parameterIndex++)
                {
                    double startParameter = splitParameters[parameterIndex];
                    double endParameter = splitParameters[parameterIndex + 1];
                    if ((endParameter - startParameter) * segmentLength <= 0.000001)
                        continue;

                    HjoinSegment piece = new HjoinSegment
                    {
                        Start = HjoinPointAtParameter(segment.Start, segment.End, startParameter),
                        End = HjoinPointAtParameter(segment.Start, segment.End, endParameter),
                        SourceId = segment.SourceId,
                        InteriorOnLeft = segment.InteriorOnLeft,
                        Removed = IsHjoinInternalParameter((startParameter + endParameter) / 2.0, overlapRanges)
                    };
                    splitSegments.Add(piece);
                }
            }

            // 既无重叠也无超短段时，重建结果与输入完全一致，直接跳过整表重建
            if (!anyOverlap && !anyTooShort)
                return 0;

            segments.Clear();
            segments.AddRange(splitSegments);
            SnapHjoinJunctions(segments, junctions);
            foreach (HjoinSegment piece in segments)
            {
                // Three-way gap junctions can collapse a residual seam fragment to
                // a single snapped vertex. It is not an open boundary to be traced.
                if (piece.Start.GetDistanceTo(piece.End) <= 0.000001) piece.Removed = true;
            }

            int removed = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].Removed)
                    removed++;
            }
            return removed;
        }

        private static Dictionary<ObjectId, HashSet<ObjectId>> BuildHjoinSourceLinks(List<HjoinSegment> segments, double tolerance, List<int>[] candidateLists)
        {
            Dictionary<ObjectId, HashSet<ObjectId>> links = new Dictionary<ObjectId, HashSet<ObjectId>>();
            for (int i = 0; i < segments.Count; i++)
            {
                if (!links.ContainsKey(segments[i].SourceId))
                    links[segments[i].SourceId] = new HashSet<ObjectId>();
                for (int candidateIndex = 0; candidateIndex < candidateLists[i].Count; candidateIndex++)
                {
                    int j = candidateLists[i][candidateIndex];
                    if (j <= i) continue;
                    if (segments[i].SourceId == segments[j].SourceId)
                        continue;
                    double overlapStart;
                    double overlapEnd;
                    if (!TryGetHjoinOverlapParameters(segments[i], segments[j], tolerance, out overlapStart, out overlapEnd))
                        continue;
                    if (!links.ContainsKey(segments[j].SourceId))
                        links[segments[j].SourceId] = new HashSet<ObjectId>();
                    links[segments[i].SourceId].Add(segments[j].SourceId);
                    links[segments[j].SourceId].Add(segments[i].SourceId);
                }
            }
            return links;
        }

        private static List<int>[] BuildHjoinCandidateLists(List<HjoinSegment> segments, double tolerance)
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
                HjoinSegment segment = segments[i];
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

            return candidates;
        }

        private static HashSet<ObjectId> ExpandHjoinSourceGroup(
            HashSet<ObjectId> sourceIds,
            Dictionary<ObjectId, HashSet<ObjectId>> links)
        {
            HashSet<ObjectId> result = new HashSet<ObjectId>(sourceIds);
            Queue<ObjectId> pending = new Queue<ObjectId>(sourceIds);
            while (pending.Count > 0)
            {
                ObjectId sourceId = pending.Dequeue();
                HashSet<ObjectId> neighbors;
                if (!links.TryGetValue(sourceId, out neighbors))
                    continue;
                foreach (ObjectId neighbor in neighbors)
                {
                    if (result.Add(neighbor))
                        pending.Enqueue(neighbor);
                }
            }
            return result;
        }

        private static bool TryGetHjoinOverlapParameters(HjoinSegment segment, HjoinSegment candidate, double tolerance, out double startParameter, out double endParameter)
        {
            startParameter = 0.0;
            endParameter = 0.0;

            Vector2d direction = segment.End - segment.Start;
            double length = direction.Length;
            if (length <= 0.000001) return false;
            direction = direction / length;

            Vector2d candidateDirection = candidate.End - candidate.Start;
            double candidateLength = candidateDirection.Length;
            if (candidateLength <= 0.000001) return false;
            candidateDirection = candidateDirection / candidateLength;

            double parallel = Math.Abs(direction.X * candidateDirection.Y - direction.Y * candidateDirection.X);
            if (parallel > 0.01) return false;
            // Only opposite interior sides form a removable shared boundary. Equal or
            // nested outlines can have coincident edges but must not be cut apart.
            double interiorDot = (direction.X * candidateDirection.X + direction.Y * candidateDirection.Y)
                * (segment.InteriorOnLeft ? 1.0 : -1.0) * (candidate.InteriorOnLeft ? 1.0 : -1.0);
            if (interiorDot >= 0.0) return false;
            // The other boundary must lie outside this polygon. Opposite edges of a
            // thin solid stroke are not a seam; an open slit may be a same-source seam.
            Vector2d offset = candidate.Start - segment.Start;
            double side = (direction.X * offset.Y - direction.Y * offset.X)
                * (segment.InteriorOnLeft ? 1.0 : -1.0);
            if (side > 0.000001) return false;
            if (HjoinPointLineDistance(candidate.Start, segment.Start, direction) > tolerance) return false;
            if (HjoinPointLineDistance(candidate.End, segment.Start, direction) > tolerance) return false;

            double candidateStart = HjoinProjection(candidate.Start, segment.Start, direction);
            double candidateEnd = HjoinProjection(candidate.End, segment.Start, direction);
            if (candidateEnd < candidateStart)
            {
                double temporary = candidateStart;
                candidateStart = candidateEnd;
                candidateEnd = temporary;
            }

            double overlapStart = Math.Max(0.0, candidateStart);
            double overlapEnd = Math.Min(length, candidateEnd);
            if (overlapEnd - overlapStart <= Math.Max(tolerance, 0.000001))
                return false;

            startParameter = overlapStart / length;
            endParameter = overlapEnd / length;
            return true;
        }

        private static void AddHjoinSplitParameter(List<double> parameters, double value)
        {
            value = Math.Max(0.0, Math.Min(1.0, value));
            for (int i = 0; i < parameters.Count; i++)
            {
                if (Math.Abs(parameters[i] - value) <= 0.00000001)
                    return;
            }
            parameters.Add(value);
        }

        private static bool IsHjoinInternalParameter(double parameter, List<HjoinOverlapRange> overlapRanges)
        {
            for (int i = 0; i < overlapRanges.Count; i++)
            {
                if (parameter >= overlapRanges[i].Start - 0.00000001 && parameter <= overlapRanges[i].End + 0.00000001)
                    return true;
            }
            return false;
        }

        private static Point2d HjoinPointAtParameter(Point2d start, Point2d end, double parameter)
        {
            return new Point2d(
                start.X + (end.X - start.X) * parameter,
                start.Y + (end.Y - start.Y) * parameter);
        }

        private static void SnapHjoinJunctions(List<HjoinSegment> segments, List<HjoinJunction> junctions)
        {
            const double epsilon = 0.000001;
            int count = segments.Count * 2;
            int[] parent = new int[count];
            Point2d[] points = new Point2d[count];
            Dictionary<long, List<int>> nodes = new Dictionary<long, List<int>>();
            for (int i = 0; i < count; i++)
            {
                parent[i] = i;
                points[i] = (i % 2 == 0) ? segments[i / 2].Start : segments[i / 2].End;
                long x = (long)Math.Floor(points[i].X / epsilon), y = (long)Math.Floor(points[i].Y / epsilon);
                for (long xx = x - 1; xx <= x + 1; xx++) for (long yy = y - 1; yy <= y + 1; yy++)
                {
                    List<int> bucket;
                    if (!nodes.TryGetValue(HjoinGridKey(xx, yy), out bucket)) continue;
                    foreach (int j in bucket) if (points[i].GetDistanceTo(points[j]) <= epsilon)
                        parent[HjoinRoot(parent, i)] = HjoinRoot(parent, j);
                }
                long key = HjoinGridKey(x, y);
                List<int> own;
                if (!nodes.TryGetValue(key, out own)) nodes[key] = own = new List<int>();
                own.Add(i);
            }
            foreach (HjoinJunction junction in junctions)
            {
                int first = FindHjoinNode(points, nodes, junction.First, epsilon);
                int second = FindHjoinNode(points, nodes, junction.Second, epsilon);
                if (first < 0 || second < 0)
                    throw new InvalidOperationException("共享边端点不完整，已保留原轮廓。");
                parent[HjoinRoot(parent, first)] = HjoinRoot(parent, second);
            }
            // Midpoints keep the two sides of a small gap aligned without favoring
            // selection order. Sum local deltas to preserve large-coordinate precision.
            Point2d[] origins = new Point2d[count];
            double[] dx = new double[count], dy = new double[count];
            int[] weights = new int[count];
            for (int i = 0; i < count; i++)
            {
                int root = HjoinRoot(parent, i);
                if (weights[root] == 0) origins[root] = points[i];
                dx[root] += points[i].X - origins[root].X;
                dy[root] += points[i].Y - origins[root].Y;
                weights[root]++;
            }
            for (int i = 0; i < count; i++)
            {
                int root = HjoinRoot(parent, i);
                Point2d point = new Point2d(origins[root].X + dx[root] / weights[root], origins[root].Y + dy[root] / weights[root]);
                if (i % 2 == 0) segments[i / 2].Start = point; else segments[i / 2].End = point;
            }
        }

        private static int HjoinRoot(int[] parent, int index)
        {
            while (parent[index] != index) { parent[index] = parent[parent[index]]; index = parent[index]; }
            return index;
        }

        private static int FindHjoinNode(Point2d[] points, Dictionary<long, List<int>> nodes, Point2d point, double epsilon)
        {
            long x = (long)Math.Floor(point.X / epsilon), y = (long)Math.Floor(point.Y / epsilon);
            for (long xx = x - 1; xx <= x + 1; xx++) for (long yy = y - 1; yy <= y + 1; yy++)
            {
                List<int> bucket;
                if (!nodes.TryGetValue(HjoinGridKey(xx, yy), out bucket)) continue;
                foreach (int i in bucket) if (point.GetDistanceTo(points[i]) <= epsilon) return i;
            }
            return -1;
        }

        private static List<HjoinLoop> BuildHjoinLoops(List<HjoinSegment> segments, double tolerance)
        {
            List<HjoinLoop> loops = new List<HjoinLoop>();
            bool[] used = new bool[segments.Count];
            double gridSize = Math.Max(tolerance, 0.000001);
            Dictionary<long, List<int>> endpointIndex = BuildHjoinEndpointIndex(segments, gridSize);
            for (int i = 0; i < segments.Count; i++)
            {
                if (used[i] || segments[i].Removed) continue;
                used[i] = true;
                List<Point2d> loop = new List<Point2d> { segments[i].Start, segments[i].End };
                HashSet<ObjectId> loopSourceIds = new HashSet<ObjectId> { segments[i].SourceId };

                int guard = 0;
                while (guard < segments.Count + 5)
                {
                    guard++;
                    if (loop.Count >= 3 && loop[loop.Count - 1].GetDistanceTo(loop[0]) <= tolerance)
                    {
                        loop[loop.Count - 1] = loop[0];
                        break;
                    }

                    int nextIndex;
                    bool reverse;
                    if (!FindNextHjoinSegment(segments, used, endpointIndex, gridSize, loop[loop.Count - 1], tolerance, out nextIndex, out reverse))
                        break;

                    used[nextIndex] = true;
                    loopSourceIds.Add(segments[nextIndex].SourceId);
                    Point2d nextPoint = reverse ? segments[nextIndex].Start : segments[nextIndex].End;
                    loop.Add(nextPoint);
                }

                if (loop.Count >= 4 && loop[loop.Count - 1].GetDistanceTo(loop[0]) <= tolerance)
                {
                    loops.Add(new HjoinLoop
                    {
                        Points = loop,
                        SourceIds = loopSourceIds,
                        TemplateId = segments[i].SourceId
                    });
                }
                else
                {
                    // Never delete a source group if even one remaining boundary failed to close.
                    return new List<HjoinLoop>();
                }
            }
            return loops;
        }

        private static bool FindNextHjoinSegment(List<HjoinSegment> segments, bool[] used, Dictionary<long, List<int>> endpointIndex, double gridSize, Point2d current, double tolerance, out int index, out bool reverse)
        {
            index = -1;
            reverse = false;
            double bestDistance = double.MaxValue;
            long cellX = (long)Math.Floor(current.X / gridSize);
            long cellY = (long)Math.Floor(current.Y / gridSize);
            HashSet<int> nearby = new HashSet<int>();
            for (long x = cellX - 1; x <= cellX + 1; x++)
            {
                for (long y = cellY - 1; y <= cellY + 1; y++)
                {
                    List<int> bucket;
                    if (!endpointIndex.TryGetValue(HjoinGridKey(x, y), out bucket)) continue;
                    for (int bucketIndex = 0; bucketIndex < bucket.Count; bucketIndex++)
                        nearby.Add(bucket[bucketIndex]);
                }
            }
            List<int> orderedNearby = new List<int>(nearby);
            orderedNearby.Sort();
            foreach (int i in orderedNearby)
            {
                if (used[i] || segments[i].Removed) continue;
                double startDistance = current.GetDistanceTo(segments[i].Start);
                if (startDistance <= tolerance && startDistance < bestDistance)
                {
                    index = i;
                    reverse = false;
                    bestDistance = startDistance;
                }
            }
            return index >= 0;
        }

        private static Dictionary<long, List<int>> BuildHjoinEndpointIndex(List<HjoinSegment> segments, double gridSize)
        {
            Dictionary<long, List<int>> index = new Dictionary<long, List<int>>();
            for (int i = 0; i < segments.Count; i++)
            {
                AddHjoinEndpoint(index, segments[i].Start, gridSize, i);
                AddHjoinEndpoint(index, segments[i].End, gridSize, i);
            }
            return index;
        }

        private static void AddHjoinEndpoint(Dictionary<long, List<int>> index, Point2d point, double gridSize, int segmentIndex)
        {
            long x = (long)Math.Floor(point.X / gridSize);
            long y = (long)Math.Floor(point.Y / gridSize);
            long key = HjoinGridKey(x, y);
            List<int> bucket;
            if (!index.TryGetValue(key, out bucket))
            {
                bucket = new List<int>();
                index[key] = bucket;
            }
            bucket.Add(segmentIndex);
        }

        private static long HjoinGridKey(long x, long y)
        {
            return unchecked((x << 32) ^ (y & 0xffffffffL));
        }

        private static List<Point2d> CleanHjoinLoop(List<Point2d> points, double tolerance)
        {
            const double geometryTolerance = 0.000001;
            List<Point2d> clean = new List<Point2d>();
            for (int i = 0; i < points.Count; i++)
            {
                if (clean.Count == 0 || clean[clean.Count - 1].GetDistanceTo(points[i]) > geometryTolerance)
                    clean.Add(points[i]);
            }

            if (clean.Count > 1 && clean[clean.Count - 1].GetDistanceTo(clean[0]) <= tolerance)
                clean.RemoveAt(clean.Count - 1);

            bool changed = true;
            while (changed && clean.Count >= 3)
            {
                changed = false;
                for (int i = 0; i < clean.Count; i++)
                {
                    Point2d previous = clean[(i - 1 + clean.Count) % clean.Count];
                    Point2d current = clean[i];
                    Point2d next = clean[(i + 1) % clean.Count];
                    Vector2d a = current - previous;
                    Vector2d b = next - current;
                    if (a.Length <= geometryTolerance || b.Length <= geometryTolerance)
                    {
                        clean.RemoveAt(i);
                        changed = true;
                        break;
                    }
                    double cross = Math.Abs(a.X * b.Y - a.Y * b.X);
                    if (cross <= geometryTolerance * Math.Max(a.Length + b.Length, 1.0))
                    {
                        clean.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }

            return clean;
        }

        private static double HjoinPointLineDistance(Point2d point, Point2d origin, Vector2d direction)
        {
            Vector2d toPoint = point - origin;
            return Math.Abs(toPoint.X * direction.Y - toPoint.Y * direction.X);
        }

        private static double HjoinProjection(Point2d point, Point2d origin, Vector2d direction)
        {
            Vector2d toPoint = point - origin;
            return toPoint.X * direction.X + toPoint.Y * direction.Y;
        }

        private static double HjoinArea(List<Point2d> points)
        {
            return HplitSignedArea(points);
        }
    }
}
