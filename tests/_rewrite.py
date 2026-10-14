with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "r", encoding="utf-8-sig") as f:
    lines = f.readlines()

new_hplit = """        [CommandMethod("HPLIT")]
        public void HPLIT()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            db = doc.Database;
            ed = doc.Editor;

            try
            {
                // Step 1: Select closed polyline (supports SCR/PickFirst + window)
                var selFilter = new SelectionFilter(new[] { new TypedValue(0, "LWPOLYLINE") });
                PromptSelectionResult selRes = default(PromptSelectionResult);

                var impliedRes = ed.SelectImplied();
                bool usedImplied = false;
                if (impliedRes.Status == PromptStatus.OK && impliedRes.Value.Count > 0)
                {
                    var selIds = impliedRes.Value.GetObjectIds();
                    using (var trCheck = db.TransactionManager.StartTransaction())
                    {
                        foreach (ObjectId id in selIds)
                        {
                            if (trCheck.GetObject(id, OpenMode.ForRead) is Polyline plChk && plChk.Closed)
                            { usedImplied = true; break; }
                        }
                        trCheck.Commit();
                    }
                    if (usedImplied) selRes = impliedRes;
                }

                if (!usedImplied)
                {
                    var selOpts = new PromptSelectionOptions();
                    selOpts.MessageForAdding = "\\nSelect closed polylines: ";
                    selRes = ed.GetSelection(selOpts, selFilter);
                }
                if (selRes.Status != PromptStatus.OK) return;

                ObjectId plId = ObjectId.Null;
                using (var trCheck = db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in selRes.Value.GetObjectIds())
                    {
                        var candidate = trCheck.GetObject(id, OpenMode.ForRead) as Polyline;
                        if (candidate != null && candidate.Closed) { plId = id; break; }
                    }
                    trCheck.Commit();
                }

                if (plId == ObjectId.Null)
                {
                    ed.WriteMessage("\\nError: no closed polyline found in selection");
                    return;
                }

                // Step 2: Pick multiple points forming the cut polyline (Enter to finish)
                var cutPts = new List<Point3d>();
                while (true)
                {
                    string prompt = cutPts.Count == 0
                        ? "\\nPick cut polyline point (Enter to finish): "
                        : "\\nPick next point (Enter to finish): ";
                    var ppo = new PromptPointOptions(prompt);
                    ppo.AllowNone = true;
                    if (cutPts.Count > 0)
                    {
                        ppo.UseBasePoint = true;
                        ppo.BasePoint = cutPts[cutPts.Count - 1];
                    }
                    var ppr = ed.GetPoint(ppo);
                    if (ppr.Status == PromptStatus.None || ppr.Status == PromptStatus.Cancel) break;
                    if (ppr.Status != PromptStatus.OK) return;
                    cutPts.Add(ppr.Value);
                }

                if (cutPts.Count < 2)
                {
                    ed.WriteMessage("\\nError: need at least 2 points for cut polyline");
                    return;
                }

                double totalCutLen = 0;
                for (int i = 1; i < cutPts.Count; i++)
                    totalCutLen += cutPts[i].DistanceTo(cutPts[i - 1]);
                if (totalCutLen < 0.01)
                {
                    ed.WriteMessage("\\nError: cut polyline too short");
                    return;
                }

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var pl = tr.GetObject(plId, OpenMode.ForRead) as Polyline;
                    if (pl == null || !pl.Closed)
                    {
                        ed.WriteMessage("\\nError: select a closed polyline");
                        return;
                    }

                    // Build cut polyline and offset by +/-0.05mm (gap=0.1mm)
                    double halfGap = 0.05;
                    var cutPl = new Polyline();
                    for (int i = 0; i < cutPts.Count; i++)
                        cutPl.AddVertexAt(i, new Point2d(cutPts[i].X, cutPts[i].Y), 0, 0, 0);

                    var offsetPlus = cutPl.GetOffsetCurves(halfGap);
                    var offsetMinus = cutPl.GetOffsetCurves(-halfGap);
                    cutPl.Dispose();

                    if (offsetPlus.Count == 0 || offsetMinus.Count == 0)
                    {
                        foreach (DBObject o in offsetPlus) o.Dispose();
                        foreach (DBObject o in offsetMinus) o.Dispose();
                        ed.WriteMessage("\\nError: cannot offset cut polyline");
                        return;
                    }

                    var cutLine1 = offsetPlus[0] as Curve;
                    var cutLine2 = offsetMinus[0] as Curve;
                    if (cutLine1 == null || cutLine2 == null)
                    {
                        foreach (DBObject o in offsetPlus) o.Dispose();
                        foreach (DBObject o in offsetMinus) o.Dispose();
                        ed.WriteMessage("\\nError: offset produced non-curve result");
                        return;
                    }

                    // Reference lines for side classification (use first segment direction)
                    Point3d firstPt = cutPts[0];
                    Point3d secondPt = cutPts[1];
                    Vector3d firstDir = (secondPt - firstPt).GetNormal();
                    Vector3d firstPerp = new Vector3d(-firstDir.Y, firstDir.X, 0);
                    Point3d line1RefP1 = firstPt + firstPerp * halfGap;
                    Point3d line1RefP2 = firstPt + firstPerp * halfGap + firstDir;
                    Point3d line2RefP1 = firstPt - firstPerp * halfGap;
                    Point3d line2RefP2 = firstPt - firstPerp * halfGap + firstDir;

                    // Find intersections
                    var allHits = new List<(Point3d Pt, double Param, int LineId)>();
                    int nVerts = pl.NumberOfVertices;
                    for (int i = 0; i < nVerts; i++)
                    {
                        Point3d vi = pl.GetPoint3dAt(i);
                        Point3d vj = pl.GetPoint3dAt((i + 1) % nVerts);
                        if (vi.DistanceTo(vj) < 1e-9) continue;

                        var pts = new Point3dCollection();
                        using (var edge = new Line(vi, vj))
                        {
                            cutLine1.IntersectWith(edge, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);
                            foreach (Point3d p in pts)
                            {
                                Point3d onCurve = pl.GetClosestPointTo(p, false);
                                allHits.Add((onCurve, pl.GetParameterAtPoint(onCurve), 1));
                            }
                        }
                        pts.Clear();
                        using (var edge2 = new Line(vi, vj))
                        {
                            cutLine2.IntersectWith(edge2, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);
                            foreach (Point3d p in pts)
                            {
                                Point3d onCurve = pl.GetClosestPointTo(p, false);
                                allHits.Add((onCurve, pl.GetParameterAtPoint(onCurve), 2));
                            }
                        }
                    }

                    cutLine1.Dispose();
                    cutLine2.Dispose();
                    foreach (DBObject o in offsetPlus) { if (o != cutLine1) o.Dispose(); }
                    foreach (DBObject o in offsetMinus) { if (o != cutLine2) o.Dispose(); }

                    // Deduplicate
                    var uniqueHits = new List<(Point3d Pt, double Param, int LineId)>();
                    foreach (var hit in allHits)
                    {
                        bool dup = false;
                        foreach (var uh in uniqueHits)
                            if (hit.Pt.DistanceTo(uh.Pt) < 0.001) { dup = true; break; }
                        if (!dup) uniqueHits.Add(hit);
                    }

                    int l1c = uniqueHits.Count(h => h.LineId == 1);
                    int l2c = uniqueHits.Count(h => h.LineId == 2);
                    if (l1c < 2 || l2c < 2)
                    {
                        ed.WriteMessage(String.Format("\\nError: not enough intersections (L1:{0}, L2:{1})", l1c, l2c));
                        return;
                    }

                    uniqueHits.Sort((a, b) => a.Param.CompareTo(b.Param));

                    var splitPts = new Point3dCollection();
                    foreach (var hit in uniqueHits) splitPts.Add(hit.Pt);
                    var splitCurves = pl.GetSplitCurves(splitPts);

                    var l1Verts = new List<Point3d>();
                    var l2Verts = new List<Point3d>();

                    for (int i = 0; i < splitCurves.Count; i++)
                    {
                        var seg = splitCurves[i] as Curve;
                        if (seg == null) continue;

                        Point3d s = seg.StartPoint;
                        Point3d e = seg.EndPoint;
                        bool sOn1 = PointOnLine(s, line1RefP1, line1RefP2, 0.01);
                        bool eOn1 = PointOnLine(e, line1RefP1, line1RefP2, 0.01);
                        bool sOn2 = PointOnLine(s, line2RefP1, line2RefP2, 0.01);
                        bool eOn2 = PointOnLine(e, line2RefP1, line2RefP2, 0.01);

                        if (sOn1 && eOn1)
                            CollectSegVerts(seg, l1Verts);
                        else if (sOn2 && eOn2)
                            CollectSegVerts(seg, l2Verts);
                    }

                    foreach (Curve c in splitCurves) c.Dispose();

                    if (l1Verts.Count < 2 || l2Verts.Count < 2)
                    {
                        ed.WriteMessage(String.Format("\\nError: cannot split (L1:{0}v, L2:{1}v)", l1Verts.Count, l2Verts.Count));
                        return;
                    }

                    var bt2 = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var btr2 = (BlockTableRecord)tr.GetObject(bt2[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    var newPl1 = new Polyline();
                    for (int i = 0; i < l1Verts.Count; i++)
                        newPl1.AddVertexAt(i, new Point2d(l1Verts[i].X, l1Verts[i].Y), 0, 0, 0);
                    newPl1.Closed = true;
                    newPl1.Layer = pl.Layer;
                    newPl1.Color = pl.Color;
                    btr2.AppendEntity(newPl1);
                    tr.AddNewlyCreatedDBObject(newPl1, true);

                    var newPl2 = new Polyline();
                    for (int i = 0; i < l2Verts.Count; i++)
                        newPl2.AddVertexAt(i, new Point2d(l2Verts[i].X, l2Verts[i].Y), 0, 0, 0);
                    newPl2.Closed = true;
                    newPl2.Layer = pl.Layer;
                    newPl2.Color = pl.Color;
                    btr2.AppendEntity(newPl2);
                    tr.AddNewlyCreatedDBObject(newPl2, true);

                    pl.UpgradeOpen();
                    pl.Erase();
                    tr.Commit();

                    ed.WriteMessage(String.Format("\\nHPLIT: split into 2 polylines (L:{0}v, R:{1}v)",
                        newPl1.NumberOfVertices, newPl2.NumberOfVertices));
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage(String.Format("\\nHPLIT error: {0}", ex.Message));
            }
        }
"""

lines[3247:3522] = [new_hplit + "\n"]

with open(r"C:\Users\allen\Documents\Hexfill 2\HexFill.cs", "w", encoding="utf-8-sig") as f:
    f.writelines(lines)

print(f"Replaced lines 3248-3522 with clean HPLIT ({len(new_hplit.splitlines())} lines)")
