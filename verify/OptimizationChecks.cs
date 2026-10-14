using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

public class OptimizationChecks
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private object plugin;
    private Type type;
    private StreamWriter output;

    private object Call(string method, params object[] args)
    {
        try { return type.GetMethod(method, Private).Invoke(plugin, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    private void Setup()
    {
        type = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "HexFill").GetType("HexFill.HexFill");
        plugin = Activator.CreateInstance(type);
        var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        type.GetField("db", Private).SetValue(plugin, doc.Database);
        type.GetField("ed", Private).SetValue(plugin, doc.Editor);
        var filename = doc.Editor.GetString("Result path: ").StringResult;
        output = new StreamWriter(filename);
    }

    private static Polyline Rectangle(double x, double y, double w, double h)
    {
        var pl = new Polyline();
        pl.AddVertexAt(0, new Point2d(x,y), 0,0,0);
        pl.AddVertexAt(1, new Point2d(x+w,y), 0,0,0);
        pl.AddVertexAt(2, new Point2d(x+w,y+h), 0,0,0);
        pl.AddVertexAt(3, new Point2d(x,y+h), 0,0,0);
        pl.Closed = true;
        return pl;
    }

    private void Points(string name, List<Point3d> points)
    {
        output.WriteLine("CASE " + name + " " + points.Count);
        foreach (Point3d point in points)
            output.WriteLine(point.X.ToString("R", CultureInfo.InvariantCulture)+","+point.Y.ToString("R", CultureInfo.InvariantCulture));
    }

    [CommandMethod("HOPTGEOM")]
    public void Geometry()
    {
        Setup();
        using (output)
        {
            using (var poly = Rectangle(-2,3,10,7))
            using (var circle = new Circle(new Point3d(3,4,0), Vector3d.ZAxis, 5))
            using (var ellipse = new Ellipse(new Point3d(3,4,0), Vector3d.ZAxis, new Vector3d(6,2,0), 0.4, 0, Math.PI*2))
            {
                foreach (Entity shape in new Entity[] { poly, circle, ellipse })
                    foreach (bool direction in new[] { false, true })
                        Points(shape.GetType().Name+direction, (List<Point3d>)Call("GenerateHexGridStrict", shape, 0.3, direction, 0.015));
            }
            var outlines = new List<Polyline> {
                Rectangle(0,0,8,8), Rectangle(2,2,4,4),
                Rectangle(10,0,2,2), Rectangle(10,5,2,2), Rectangle(15,2,0.03,3)
            };
            try
            {
                foreach (bool direction in new[] { false,true })
                    Points("Glyph"+direction, (List<Point3d>)Call("GenerateHexGridText", outlines, 0.06, direction, 0.015));
            }
            finally { foreach (var poly in outlines) poly.Dispose(); }

            var allocatedMethod = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Public | BindingFlags.Static);
            Func<long> allocated = allocatedMethod == null ? (Func<long>)(() => -1)
                : (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), allocatedMethod);
            using (var poly = Rectangle(0,0,100,50))
            {
                Call("GenerateHexGridStrict", poly, 1.0, false, 0.015); // JIT warm-up
                for (int repeat=0; repeat<3; repeat++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    long before = allocated();
                    var timer = Stopwatch.StartNew();
                    var points = (List<Point3d>)Call("GenerateHexGridStrict", poly, 0.2, false, 0.015);
                    timer.Stop();
                    long bytes = allocated();
                    output.WriteLine("PERF,"+points.Count+","+timer.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)+","+(before < 0 ? -1 : bytes-before));
                }
            }
        }
    }

    private void Check(string name, bool ok)
    {
        output.WriteLine((ok ? "PASS " : "FAIL ")+name);
        output.Flush();
        if (!ok) throw new InvalidOperationException(name);
    }

    private static string Sequence(Database db, ObjectId id)
    {
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
            foreach (ObjectId attId in br.AttributeCollection)
            {
                var att = (AttributeReference)tr.GetObject(attId, OpenMode.ForRead);
                if (att.Tag == "SEQ") return att.TextString;
            }
        }
        return "";
    }

    [CommandMethod("HOPTBAL")]
    public void Balloons()
    {
        Setup();
        var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        var db = doc.Database;
        ObjectId model = SymbolUtilityServices.GetBlockModelSpaceId(db);
        using (output)
        {
            Call("RegisterBalloonEvents", doc);
            try
            {
                var balloons = new List<ObjectId>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var ms = (BlockTableRecord)tr.GetObject(model, OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                        if (tr.GetObject(id, OpenMode.ForRead) is BlockReference) balloons.Add(id);
                    Check("three-balloons-fixture", balloons.Count == 3);
                    ((BlockReference)tr.GetObject(balloons[0], OpenMode.ForWrite)).Erase();
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    ((LayerTableRecord)tr.GetObject(lt["球标"], OpenMode.ForWrite)).IsLocked = true;
                    tr.Commit();
                }
                var pending = (IDictionary)type.GetField("_pendingBalloonRenumberDatabases", Private).GetValue(plugin);
                Check("erased-event-queues-source-space", pending.Count == 1);
                db.TileMode = false;
                Check("layout-changed-before-idle", db.CurrentSpaceId != model);
                Call("OnIdleRenumberBalloons", null, EventArgs.Empty);
                Check("locked-renumber-keeps-request", pending.Count == 1);
                Check("locked-renumber-rolls-back-sequences", Sequence(db, balloons[1]) == "2" && Sequence(db, balloons[2]) == "3");
                object request = pending[db];
                var requestType = request.GetType();
                for (int i=0; i<2; i++)
                {
                    requestType.GetField("NextAttemptUtc").SetValue(request, DateTime.MinValue);
                    Call("OnIdleRenumberBalloons", null, EventArgs.Empty);
                }
                Check("three-failures-retain-request", pending.Count == 1 && (int)requestType.GetField("Failures").GetValue(request) == 3);
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    ((LayerTableRecord)tr.GetObject(lt["球标"], OpenMode.ForWrite)).IsLocked = false;
                    tr.Commit();
                }
                Check("layer-unlock-rearms-retry", (int)requestType.GetField("Failures").GetValue(request) == 0);
                Call("OnIdleRenumberBalloons", null, EventArgs.Empty);
                Check("successful-retry-clears-request", pending.Count == 0);
                Check("source-model-renumbered-despite-layout-switch", Sequence(db, balloons[1]) == "1" && Sequence(db, balloons[2]) == "2");
                db.TileMode = true;
                // Pending work in another document must not overwrite command context.
                var sentinel = new Database(true, true);
                try
                {
                    type.GetField("db", Private).SetValue(plugin, sentinel);
                    Call("QueueBalloonRenumber", db, model);
                    Call("OnIdleRenumberBalloons", null, EventArgs.Empty);
                    Check("idle-restores-original-database-context", ReferenceEquals(type.GetField("db", Private).GetValue(plugin), sentinel));
                }
                finally { type.GetField("db", Private).SetValue(plugin, db); sentinel.Dispose(); }
            }
            finally { Call("UnregisterBalloonEvents", doc); }
        }
    }
}
