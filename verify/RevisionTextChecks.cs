// Real AutoCAD text measurements. This helper is never included in the plugin.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

public class RevisionTextChecks
{
    private static BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    [CommandMethod("HREVTEST")]
    public void Test()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        var type = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "HexFill").GetType("HexFill.HexFill");
        var plugin = Activator.CreateInstance(type);
        type.GetField("db", flags).SetValue(plugin, doc.Database);
        type.GetField("ed", flags).SetValue(plugin, doc.Editor);
        var method = type.GetMethod("BuildHtcEntityOutlines", flags);
        string path = doc.Editor.GetString("Results: ").StringResult;
        using (var log = new StreamWriter(path))
        {
            Transaction tr = doc.Database.TransactionManager.StartTransaction();
            try
            {
            log.AutoFlush = true;
            log.WriteLine("START text measurements");
            var styles = (TextStyleTable)tr.GetObject(doc.Database.TextStyleTableId, OpenMode.ForRead);
            styles.UpgradeOpen();
            var style = new TextStyleTableRecord { Name = "REV_YAHEI", FileName = "msyh.ttc" };
            style.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor("Microsoft YaHei", false, false, 134, 34);
            ObjectId styleId = styles.Add(style); tr.AddNewlyCreatedDBObject(style, true);
            // Native MTEXT layout needs a committed font style, just like real commands.
            tr.Commit(); tr.Dispose(); tr = doc.Database.TransactionManager.StartTransaction();
            var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
            int checks = 0, failures = 0;
            Action<string, bool, string> check = (name, ok, detail) => { checks++; if (!ok) failures++; log.WriteLine((ok ? "PASS " : "FAIL ") + name + " " + detail); };
            int caseIndex = 0;
            foreach (double rotation in new[] { 0.0, Math.PI/6, Math.PI/4, Math.PI/2, Math.PI*1.5 })
            foreach (double width in new[] { 0.65, 1.0, 1.35 })
            foreach (int alignment in new[] { 0, 1, 2, 3, 4 })
            {
                using (var text = new DBText { TextStyleId = styleId, TextString = "V234061L_DEUS_0A_DE_0.02_6V014_20261014",
                    Position = new Point3d(25, 30, 7.5), Height = 1.2, Rotation = rotation, WidthFactor = width, Oblique = 0.12 })
                {
                    log.WriteLine("RUN text "+caseIndex+" rotation="+rotation+" alignment="+alignment);
                    if (alignment == 1) { text.HorizontalMode = TextHorizontalMode.TextCenter; text.VerticalMode = TextVerticalMode.TextVerticalMid; text.AlignmentPoint = text.Position; }
                    if (alignment == 2) { text.HorizontalMode = TextHorizontalMode.TextRight; text.VerticalMode = TextVerticalMode.TextTop; text.AlignmentPoint = text.Position; }
                    if (alignment == 3) { text.HorizontalMode = TextHorizontalMode.TextFit; text.AlignmentPoint = text.Position + new Vector3d(Math.Cos(rotation)*28, Math.Sin(rotation)*28, 0); }
                    if (alignment == 4) { text.IsMirroredInX = true; text.IsMirroredInY = true; }
                    space.AppendEntity(text); tr.AddNewlyCreatedDBObject(text, true); text.AdjustAlignment(doc.Database);
                    string before = text.TextString; double h = text.Height, originalRotation = text.Rotation;
                    var outlines = (List<Polyline>)method.Invoke(plugin, new object[] { text, tr });
                    try
                    {
                        Matrix3d local = Matrix3d.Rotation(-rotation, Vector3d.ZAxis, text.Position);
                        using (var copy = (DBText)text.Clone())
                        {
                            copy.TransformBy(local);
                            foreach (var p in outlines) p.TransformBy(local);
                            double error = ExtentError(Bounds(outlines), copy.GeometricExtents);
                            check("HTC-text-local-bounds-"+caseIndex, error < 1e-6, "error="+error);
                            check("HTC-text-preserved-"+caseIndex, text.TextString == before && text.Height == h && text.Rotation == originalRotation, "");
                        }
                    }
                    finally { foreach (var p in outlines) p.Dispose(); }
                    caseIndex++;
                }
            }
            foreach (double rotation in new[] { 0.0, Math.PI/4, Math.PI/2 })
            foreach (AttachmentPoint attachment in new[] { AttachmentPoint.TopLeft, AttachmentPoint.MiddleCenter, AttachmentPoint.BottomRight })
            {
                using (var text = new MText { TextStyleId = styleId, TextHeight = 1.2, Width = 6,
                    Location = new Point3d(15, 40, 7.5), Rotation = rotation, Attachment = attachment,
                    Contents = "ABCD EFGH IJKL MNOP\\P{\\H1.5x;第二行} {\\W0.7;XYZ}" })
                {
                    log.WriteLine("RUN mtext "+caseIndex);
                    space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
                    space.AppendEntity(text); tr.AddNewlyCreatedDBObject(text, true);
                    log.WriteLine("RUN mtext appended");
                    tr.Commit(); tr.Dispose(); tr = doc.Database.TransactionManager.StartTransaction();
                    log.WriteLine("RUN mtext committed");
                    // Reopen the committed entity before asking the native layout engine.
                    var committedText = (MText)tr.GetObject(text.ObjectId, OpenMode.ForRead);
                    log.WriteLine("RUN mtext reopened");
                    var fragments = new DBObjectCollection(); committedText.Explode(fragments);
                    log.WriteLine("RUN mtext exploded "+fragments.Count);
                    var outlines = (List<Polyline>)method.Invoke(plugin, new object[] { committedText, tr });
                    try
                    {
                        Matrix3d local = Matrix3d.Rotation(-rotation, Vector3d.ZAxis, text.Location);
                        foreach (DBObject fragment in fragments) if (fragment is DBText) ((DBText)fragment).TransformBy(local);
                        foreach (var p in outlines) p.TransformBy(local);
                        var native = fragments.Cast<DBObject>().OfType<DBText>().Where(f => !string.IsNullOrWhiteSpace(f.TextString)).ToList();
                        Extents3d expected = native[0].GeometricExtents;
                        foreach (var fragment in native.Skip(1)) expected.AddExtents(fragment.GeometricExtents);
                        double error = ExtentError(Bounds(outlines), expected);
                        check("HTC-mtext-layout-"+caseIndex, error < 1e-6, "error="+error+" fragments="+native.Count);
                        check("HTC-mtext-format-preserved-"+caseIndex, text.Contents.Contains("\\H1.5x;") && text.Width == 6 && text.Attachment == attachment, "");
                    }
                    finally { foreach (var p in outlines) p.Dispose(); foreach (DBObject f in fragments) f.Dispose(); fragments.Dispose(); }
                    caseIndex++;
                }
            }
            log.WriteLine("SUMMARY checks="+checks+" failures="+failures);
            tr.Abort();
            }
            finally { tr.Dispose(); }
        }
    }
    private static Extents3d Bounds(List<Polyline> paths)
    {
        if (paths.Count == 0) throw new InvalidOperationException("No outlines");
        Extents3d bounds = paths[0].GeometricExtents;
        foreach (var p in paths.Skip(1)) bounds.AddExtents(p.GeometricExtents);
        return bounds;
    }

    private static ObjectId fixtureText;
    private const string FixtureContent = "V234061L_DEUS_0A_DE_0.02_6V014_20261014";
    [CommandMethod("HREVSETHTC")]
    public void SetHtcFixture()
    {
        var db=Application.DocumentManager.MdiActiveDocument.Database;
        using(var tr=db.TransactionManager.StartTransaction())
        {
            var space=(BlockTableRecord)tr.GetObject(db.CurrentSpaceId,OpenMode.ForWrite);
            var text=new DBText(); text.SetDatabaseDefaults();
            text.TextString=FixtureContent; text.Position=new Point3d(10,20,7.5);
            text.Height=1.2; text.WidthFactor=.8; text.Rotation=Math.PI/6;
            fixtureText=space.AppendEntity(text); tr.AddNewlyCreatedDBObject(text,true); tr.Commit();
        }
    }
    [CommandMethod("HREVASSERTHTC")]
    public void AssertHtcFixture()
    {
        var doc=Application.DocumentManager.MdiActiveDocument;
        string filename=doc.Editor.GetString("Result: ").StringResult;
        using(var log=new StreamWriter(filename))
        using(var tr=doc.Database.TransactionManager.StartTransaction())
        {
            var text=(DBText)tr.GetObject(fixtureText,OpenMode.ForRead);
            var space=(BlockTableRecord)tr.GetObject(text.OwnerId,OpenMode.ForRead);
            var circles=new List<Circle>();
            foreach(ObjectId id in space) { var c=tr.GetObject(id,OpenMode.ForRead) as Circle; if(c!=null) circles.Add(c); }
            Action<string,bool> check=(name,ok)=>log.WriteLine((ok?"PASS ":"FAIL ")+name);
            check("HTC-actual-original-preserved",text.TextString==FixtureContent && text.Position==new Point3d(10,20,7.5)
                && text.Height==1.2 && text.WidthFactor==.8 && text.Rotation==Math.PI/6);
            check("HTC-actual-dot-count",circles.Count>100);
            check("HTC-actual-elevation",circles.All(c=>Math.Abs(c.Center.Z-7.5)<1e-9));
            check("HTC-actual-diameter",circles.All(c=>Math.Abs(c.Radius-.015)<1e-9));
            Matrix3d local=Matrix3d.Rotation(-text.Rotation,Vector3d.ZAxis,text.Position);
            using(var copy=(DBText)text.Clone())
            {
                copy.TransformBy(local); Extents3d expected=copy.GeometricExtents;
                var points=circles.Select(c=>c.Center.TransformBy(local)).ToList();
                var actual=new Extents3d(new Point3d(points.Min(p=>p.X)-.015,points.Min(p=>p.Y)-.015,7.5),
                    new Point3d(points.Max(p=>p.X)+.015,points.Max(p=>p.Y)+.015,7.5));
                check("HTC-actual-boundary-contained",actual.MinPoint.X>=expected.MinPoint.X-1e-6
                    && actual.MinPoint.Y>=expected.MinPoint.Y-1e-6 && actual.MaxPoint.X<=expected.MaxPoint.X+1e-6
                    && actual.MaxPoint.Y<=expected.MaxPoint.Y+1e-6);
                check("HTC-actual-boundary-grid-tolerance",ExtentError(actual,expected)<.12);
            }
        }
    }
    private static double ExtentError(Extents3d actual, Extents3d expected)
    {
        return Math.Max(actual.MinPoint.DistanceTo(expected.MinPoint), actual.MaxPoint.DistanceTo(expected.MaxPoint));
    }
}
