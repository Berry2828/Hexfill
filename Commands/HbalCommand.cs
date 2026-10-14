using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Exception = System.Exception;

namespace HexFill
{
    // HBAL 球标命令 + 删除后自动重编号（从 HexFill.cs 拆出的独立 partial 文件，行为不变）
    public partial class HexFill
    {
        // ================================================================
        // HBAL - 球标生成命令
        // 点击放置带序号圆标，数字自动递增，删除后自动重新排序
        // 球标以块（圆+属性文字）形式存在，单独图层"球标"
        // ================================================================
        private const string BALLOON_LAYER = "球标";
        private const string BALLOON_BLOCK_NAME = "H_BALLOON_DEF";
        private const string BALLOON_XDATA_APP = "H_Balloon";
        private const double BALLOON_CIRCLE_RADIUS = 2.5;
        private const double BALLOON_TEXT_HEIGHT = 1.75;
        private class BalloonRenumberRequest
        {
            public readonly HashSet<ObjectId> Spaces = new HashSet<ObjectId>();
            public int Failures;
            public DateTime NextAttemptUtc;
        }
        private readonly Dictionary<Database, BalloonRenumberRequest> _pendingBalloonRenumberDatabases
            = new Dictionary<Database, BalloonRenumberRequest>();

        [CommandMethod("HBAL")]
        public void HBAL()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            db = doc.Database;
            ed = doc.Editor;

            try
            {
                GetOrCreateLayer(BALLOON_LAYER);

                ObjectId blockId;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    blockId = GetOrCreateBalloonBlock(tr);
                    tr.Commit();
                }

                int nextSeq = GetMaxBalloonSeq() + 1;
                int startSeq = nextSeq;
                ed.WriteMessage($"\n点击放置球标（编号从 {nextSeq} 开始），Enter/右键结束");

                while (true)
                {
                    var pointOptions = new PromptPointOptions("\n点击放置位置，或输入 Done 完成: ");
                    pointOptions.AllowNone = true;
                    pointOptions.Keywords.Add("Done");
                    var ptRes = ed.GetPoint(pointOptions);
                    if (ptRes.Status == PromptStatus.Keyword && string.Equals(ptRes.StringResult, "Done", StringComparison.OrdinalIgnoreCase)) break;
                    if (ptRes.Status != PromptStatus.OK) break;

                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        CreateBalloonInstance(tr, ptRes.Value, nextSeq);
                        tr.Commit();
                    }

                    nextSeq++;
                }

                ed.WriteMessage($"\nHBAL：生成 {nextSeq - startSeq} 个球标。");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n错误: {ex.Message}");
            }
        }

        private void RegisterBalloonEvents(Document doc)
        {
            if (doc == null || doc.Database == null) return;
            doc.Database.ObjectErased -= OnBalloonObjectErased;
            doc.Database.ObjectErased += OnBalloonObjectErased;
            doc.Database.ObjectModified -= OnBalloonLayerModified;
            doc.Database.ObjectModified += OnBalloonLayerModified;
        }

        private void UnregisterBalloonEvents(Document doc)
        {
            if (doc == null || doc.Database == null) return;
            doc.Database.ObjectErased -= OnBalloonObjectErased;
            doc.Database.ObjectModified -= OnBalloonLayerModified;
        }

        private void QueueBalloonRenumber(Database sourceDatabase, ObjectId spaceId)
        {
            BalloonRenumberRequest request;
            if (!_pendingBalloonRenumberDatabases.TryGetValue(sourceDatabase, out request))
            {
                request = new BalloonRenumberRequest();
                _pendingBalloonRenumberDatabases.Add(sourceDatabase, request);
            }
            request.Spaces.Add(spaceId);
            request.Failures = 0;
            request.NextAttemptUtc = DateTime.MinValue;
            ScheduleBalloonRenumber();
        }

        private void ScheduleBalloonRenumber()
        {
            Autodesk.AutoCAD.ApplicationServices.Application.Idle -= OnIdleRenumberBalloons;
            if (_pendingBalloonRenumberDatabases.Values.Any(request => request.Failures < 3))
                Autodesk.AutoCAD.ApplicationServices.Application.Idle += OnIdleRenumberBalloons;
        }

        private void OnBalloonLayerModified(object sender, ObjectEventArgs e)
        {
            if (!(e.DBObject is LayerTableRecord)) return;
            BalloonRenumberRequest request;
            if (_pendingBalloonRenumberDatabases.TryGetValue((Database)sender, out request))
            {
                request.Failures = 0;
                request.NextAttemptUtc = DateTime.MinValue;
                ScheduleBalloonRenumber();
            }
        }

        private void OnBalloonObjectErased(object sender, ObjectErasedEventArgs e)
        {
            try
            {
                if (e == null || !e.Erased) return;
                var br = e.DBObject as BlockReference;
                if (br == null || br.Layer != BALLOON_LAYER) return;
                Database sourceDatabase = sender as Database ?? br.Database;
                if (sourceDatabase == null) return;

                // 去重：同一批删除只触发一次重编号
                QueueBalloonRenumber(sourceDatabase, br.OwnerId);
            }
            catch (Exception ex)
            {
                var activeDocument = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                activeDocument?.Editor.WriteMessage("\nHBAL：无法登记重编号任务：" + ex.Message);
            }
        }

        private void OnIdleRenumberBalloons(object sender, EventArgs e)
        {
            Autodesk.AutoCAD.ApplicationServices.Application.Idle -= OnIdleRenumberBalloons;
            Database previousDatabase = db;
            Editor previousEditor = ed;
            try
            {
                var pendingDatabases = _pendingBalloonRenumberDatabases.ToArray();
                var documentManager = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager;
                foreach (var pending in pendingDatabases)
                {
                    Database pendingDatabase = pending.Key;
                    BalloonRenumberRequest request = pending.Value;
                    if (request.Failures >= 3 || request.NextAttemptUtc > DateTime.UtcNow) continue;
                    Document targetDocument = null;
                    try
                    {
                        foreach (Document document in documentManager)
                        {
                            if (document != null && document.Database == pendingDatabase)
                            {
                                targetDocument = document;
                                break;
                            }
                        }
                        if (targetDocument == null)
                        {
                            _pendingBalloonRenumberDatabases.Remove(pendingDatabase);
                            continue;
                        }

                        using (targetDocument.LockDocument())
                        {
                            db = targetDocument.Database;
                            ed = targetDocument.Editor;
                            if (db == null || ed == null)
                                continue;
                            foreach (ObjectId spaceId in request.Spaces.ToArray())
                            {
                                RenumberBalloons(spaceId);
                                request.Spaces.Remove(spaceId);
                            }
                            _pendingBalloonRenumberDatabases.Remove(pendingDatabase);
                        }
                    }
                    catch (Exception ex)
                    {
                        request.Failures++;
                        request.NextAttemptUtc = DateTime.UtcNow.AddSeconds(1);
                        if (request.Failures == 1 || request.Failures == 3)
                            targetDocument?.Editor.WriteMessage(request.Failures < 3
                                ? "\nHBAL：自动重编号暂未完成，将重试：" + ex.Message
                                : "\nHBAL：自动重编号暂停，任务已保留；解锁图层后重试：" + ex.Message);
                    }
                }
            }
            finally
            {
                db = previousDatabase;
                ed = previousEditor;
                ScheduleBalloonRenumber();
            }
        }

        private void RenumberBalloons(ObjectId spaceId)
        {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    if (!bt.Has(BALLOON_BLOCK_NAME)) { tr.Commit(); return; }

                    var blockId = bt[BALLOON_BLOCK_NAME];
                    var ms = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);

                    // 收集所有球标 BlockReference，按 XData 原始序号排序
                    var balloons = new List<Tuple<int, ObjectId>>();
                    RXClass blockRefClass = RXClass.GetClass(typeof(BlockReference));
                    foreach (ObjectId id in ms)
                    {
                        if (!id.ObjectClass.IsDerivedFrom(blockRefClass)) continue;
                        var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                        if (br == null || br.Layer != BALLOON_LAYER) continue;
                        if (br.BlockTableRecord != blockId) continue;

                        string seqStr = GetXDataString(br, BALLOON_XDATA_APP);
                        if (int.TryParse(seqStr, out int seq))
                            balloons.Add(Tuple.Create(seq, id));
                    }

                    balloons.Sort((a, b) => a.Item1.CompareTo(b.Item1));

                    int newSeq = 1;
                    foreach (var b in balloons)
                    {
                        var br = tr.GetObject(b.Item2, OpenMode.ForRead) as BlockReference;
                        if (br == null) continue;

                        string expected = newSeq.ToString();
                        bool attributeMatches = false;
                        foreach (ObjectId attId in br.AttributeCollection)
                        {
                            var att = tr.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                            if (att != null && att.Tag == "SEQ")
                            { attributeMatches = att.TextString == expected; break; }
                        }
                        if (b.Item1 == newSeq && attributeMatches) { newSeq++; continue; }
                        br.UpgradeOpen();

                        // 更新属性文字
                        foreach (ObjectId attId in br.AttributeCollection)
                        {
                            var att = tr.GetObject(attId, OpenMode.ForWrite) as AttributeReference;
                            if (att != null && att.Tag == "SEQ")
                            {
                                att.TextString = newSeq.ToString();
                                break;
                            }
                        }

                        SetXDataString(br, BALLOON_XDATA_APP, newSeq.ToString(), tr);
                        newSeq++;
                    }

                    tr.Commit();
                    if (balloons.Count > 0)
                        ed.WriteMessage($"\n球标重新排序完成：{balloons.Count} 个");
                }
        }

        private ObjectId GetOrCreateBalloonBlock(Transaction tr)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (bt.Has(BALLOON_BLOCK_NAME))
                return bt[BALLOON_BLOCK_NAME];

            bt.UpgradeOpen();
            var btr = new BlockTableRecord
            {
                Name = BALLOON_BLOCK_NAME
            };

            var circle = new Circle(new Point3d(0, 0, 0), Vector3d.ZAxis, BALLOON_CIRCLE_RADIUS);
            btr.AppendEntity(circle);

            var attDef = new AttributeDefinition
            {
                Position = new Point3d(0, 0, 0),
                Height = BALLOON_TEXT_HEIGHT,
                TextString = "1",
                Tag = "SEQ",
                Prompt = "序号",
                HorizontalMode = TextHorizontalMode.TextCenter,
                VerticalMode = TextVerticalMode.TextVerticalMid,
                AlignmentPoint = new Point3d(0, 0, 0)
            };
            btr.AppendEntity(attDef);

            bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);
            return btr.ObjectId;
        }

        private void CreateBalloonInstance(Transaction tr, Point3d position, int sequence)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var blockId = bt[BALLOON_BLOCK_NAME];
            var ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

            var br = new BlockReference(position, blockId)
            {
                Layer = BALLOON_LAYER
            };
            ms.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);

            var att = new AttributeReference
            {
                Position = position,
                Height = BALLOON_TEXT_HEIGHT,
                TextString = sequence.ToString(),
                Tag = "SEQ",
                HorizontalMode = TextHorizontalMode.TextCenter,
                VerticalMode = TextVerticalMode.TextVerticalMid,
                AlignmentPoint = position,
                Layer = BALLOON_LAYER
            };
            br.AttributeCollection.AppendAttribute(att);
            tr.AddNewlyCreatedDBObject(att, true);

            SetXDataString(br, BALLOON_XDATA_APP, sequence.ToString(), tr);
        }

        private int GetMaxBalloonSeq()
        {
            int maxSeq = 0;
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return 0;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                if (!bt.Has(BALLOON_BLOCK_NAME)) { tr.Commit(); return 0; }

                var blockId = bt[BALLOON_BLOCK_NAME];
                var ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

                RXClass blockRefClass = RXClass.GetClass(typeof(BlockReference));
                foreach (ObjectId id in ms)
                {
                    if (!id.ObjectClass.IsDerivedFrom(blockRefClass)) continue;
                    var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null || br.Layer != BALLOON_LAYER) continue;
                    if (br.BlockTableRecord != blockId) continue;

                    string seqStr = GetXDataString(br, BALLOON_XDATA_APP);
                    if (int.TryParse(seqStr, out int seq) && seq > maxSeq)
                        maxSeq = seq;
                }
                tr.Commit();
            }
            return maxSeq;
        }
    }
}
