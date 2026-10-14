// Test-only AutoCAD host harness. Clipper2 is an independent oracle, never a production dependency.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Clipper2Lib;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

public class PolygonRegressionChecks
{
    private static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
    private static Type type;
    private static object plugin;
    private static StreamWriter log;
    private static PathsD expected;
    private static ObjectId original;
    private static string splitName;
    private static bool shouldSplit;
    private static int expectedCount;
    private static PathD sourcePath;
    private static ObjectId sentinel;
    private static int checks, failures;
    private static Database Db { get { return Application.DocumentManager.MdiActiveDocument.Database; } }
    private static object Call(string name, params object[] args)
    {
        try { return type.GetMethod(name, Flags).Invoke(plugin, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static void Check(string name, bool ok, string detail = "")
    {
        checks++; if (!ok) failures++;
        log.WriteLine((ok ? "PASS " : "FAIL ") + name + " " + detail); log.Flush();
    }
    private static PathD Path(params double[] xy)
    {
        var p = new PathD(); for (int i=0; i<xy.Length; i+=2) p.Add(new PointD(xy[i],xy[i+1])); return p;
    }
    private static PathD Rect(double x, double y, double w, double h) { return Path(x,y,x+w,y,x+w,y+h,x,y+h); }
    private static PathD Variant(PathD p, int v)
    {
        var q = new PathD(p); if (v==1) q.Reverse();
        if (v==2) q = new PathD(q.Select(t => new PointD(t.x+100000000,t.y-100000000)));
        return q;
    }
    private static Polyline Poly(PathD p)
    {
        var poly = new Polyline(); poly.SetDatabaseDefaults();
        foreach (var t in p) poly.AddVertexAt(poly.NumberOfVertices,new Point2d(t.x,t.y),0,0,0);
        poly.Closed=true; poly.ColorIndex=3; return poly;
    }
    private static PathD Points(Polyline poly)
    {
        // Production sampling is used only to flatten CAD arcs, never to compute expected straight geometry.
        var pts=(List<Point2d>)Call("SampleHplitPolyline",poly,false);
        return new PathD(pts.Select(p=>new PointD(p.X,p.Y)));
    }
    private static ObjectId Add(Transaction tr, Polyline p)
    {
        var space=(BlockTableRecord)tr.GetObject(Db.CurrentSpaceId,OpenMode.ForWrite);
        ObjectId id=space.AppendEntity(p); tr.AddNewlyCreatedDBObject(p,true); return id;
    }
    private static void Clear()
    {
        using(var tr=Db.TransactionManager.StartTransaction())
        {
            var layers=(LayerTable)tr.GetObject(Db.LayerTableId,OpenMode.ForRead);
            foreach(ObjectId id in layers) ((LayerTableRecord)tr.GetObject(id,OpenMode.ForWrite)).IsLocked=false;
            var space=(BlockTableRecord)tr.GetObject(Db.CurrentSpaceId,OpenMode.ForRead);
            foreach(ObjectId id in space) ((Entity)tr.GetObject(id,OpenMode.ForWrite)).Erase();
            tr.Commit();
        }
    }
    private static PathsD Read()
    {
        var result=new PathsD();
        using(var tr=Db.TransactionManager.StartTransaction())
        {
            var space=(BlockTableRecord)tr.GetObject(Db.CurrentSpaceId,OpenMode.ForRead);
            foreach(ObjectId id in space)
            {
                var p=tr.GetObject(id,OpenMode.ForRead) as Polyline;
                if(p!=null) { Check(splitName+"-closed",p.Closed); Check(splitName+"-properties",p.ColorIndex==3); result.Add(Points(p)); }
            }
        }
        return result;
    }
    private static bool Simple(PathD p)
    {
        for(int i=0;i<p.Count;i++)
        {
            if(double.IsNaN(p[i].x)||double.IsInfinity(p[i].x)||double.IsNaN(p[i].y)||double.IsInfinity(p[i].y)) return false;
            var a=p[i]; var b=p[(i+1)%p.Count];
            for(int j=i+1;j<p.Count;j++)
            {
                if(j==i+1 || (i==0 && j==p.Count-1)) continue;
                var c=p[j]; var d=p[(j+1)%p.Count];
                double x1=b.x-a.x,y1=b.y-a.y,x2=d.x-c.x,y2=d.y-c.y;
                double den=x1*y2-y1*x2;
                if(Math.Abs(den)<1e-12) continue;
                double t=((c.x-a.x)*y2-(c.y-a.y)*x2)/den, u=((c.x-a.x)*y1-(c.y-a.y)*x1)/den;
                if(t>1e-8 && t<1-1e-8 && u>1e-8 && u<1-1e-8) return false;
            }
        }
        return true;
    }
    private static double Area(PathsD p) { return Math.Abs(Clipper.Area(Clipper.Union(p,null,FillRule.EvenOdd,6))); }
    private static void Compare(string name, PathsD actual, PathsD reference, int count, double tolerance=0.002)
    {
        Check(name+"-count",actual.Count==count,"actual="+actual.Count+" expected="+count);
        Check(name+"-simple",actual.All(Simple));
        double delta=Area(Clipper.Xor(actual,reference,FillRule.EvenOdd,6));
        Check(name+"-xor-area",delta<=tolerance,"delta="+delta.ToString("R",CultureInfo.InvariantCulture));
        Check(name+"-area",Math.Abs(Area(actual)-Area(reference))<=tolerance);
        if(delta>tolerance)
        {
            log.WriteLine("ACTUAL "+name+" "+string.Join(" | ",actual.Select(p=>string.Join(";",p.Select(t=>t.x.ToString("R",CultureInfo.InvariantCulture)+","+t.y.ToString("R",CultureInfo.InvariantCulture))))));
            log.WriteLine("EXPECTED "+name+" "+string.Join(" | ",reference.Select(p=>string.Join(";",p.Select(t=>t.x.ToString("R",CultureInfo.InvariantCulture)+","+t.y.ToString("R",CultureInfo.InvariantCulture))))));
        }
    }
    private static PathsD Union(PathsD p)
    {
        var oriented=new PathsD();
        foreach(var path in p) { var q=new PathD(path); if(Clipper.Area(q)<0) q.Reverse(); oriented.Add(q); }
        return Clipper.Union(oriented,null,FillRule.NonZero,6);
    }
    private static List<Point2d> CadPoints(PathD p) { return p.Select(t=>new Point2d(t.x,t.y)).ToList(); }

    private static ObjectId Layer(Transaction tr, string name)
    {
        var lt=(LayerTable)tr.GetObject(Db.LayerTableId,OpenMode.ForRead);
        if(lt.Has(name)) return lt[name];
        lt.UpgradeOpen(); var layer=new LayerTableRecord {Name=name}; var id=lt.Add(layer); tr.AddNewlyCreatedDBObject(layer,true); return id;
    }
    private static void TransactionChecks()
    {
        Clear(); var ids=new List<ObjectId>(); ObjectId layer;
        using(var tr=Db.TransactionManager.StartTransaction())
        {
            layer=Layer(tr,"HF_GEOMETRY_LOCK");
            ids.Add(Add(tr,Poly(Rect(0,0,10,10)))); ids.Add(Add(tr,Poly(Rect(10,0,10,10))));
            var p=Poly(Rect(50,0,10,10)); p.LayerId=layer; ids.Add(Add(tr,p));
            ((LayerTableRecord)tr.GetObject(layer,OpenMode.ForWrite)).IsLocked=true; tr.Commit();
        }
        using(var tr=Db.TransactionManager.StartTransaction())
        {
            var plan=Call("BuildHjoinPlan",tr,ids.ToArray());
            Check("HJOIN-locked-skipped",(int)plan.GetType().GetField("SkippedLockedCount").GetValue(plan)==1);
            Call("ExecuteHjoinPlan",Db,tr,plan); tr.Commit();
        }
        Check("HJOIN-locked-original-kept",!ids[2].IsErased);
        splitName="HJOIN-locked"; Compare(splitName,Read(),new PathsD{Rect(0,0,20,10),Rect(50,0,10,10)},2);
        Clear(); ids.Clear(); object pending;
        using(var tr=Db.TransactionManager.StartTransaction())
        {
            var p=Poly(Rect(0,0,10,10)); p.LayerId=layer; ids.Add(Add(tr,p)); ids.Add(Add(tr,Poly(Rect(10,0,10,10)))); tr.Commit();
        }
        using(var tr=Db.TransactionManager.StartTransaction()) { pending=Call("BuildHjoinPlan",tr,ids.ToArray()); }
        using(var tr=Db.TransactionManager.StartTransaction()) { ((LayerTableRecord)tr.GetObject(layer,OpenMode.ForWrite)).IsLocked=true; tr.Commit(); }
        bool failed=false;
        using(var tr=Db.TransactionManager.StartTransaction())
        {
            try { Call("ExecuteHjoinPlan",Db,tr,pending); tr.Commit(); }
            catch { failed=true; tr.Abort(); }
        }
        Check("HJOIN-write-failure",failed); Check("HJOIN-rollback-keeps-sources",ids.All(id=>!id.IsErased));
        splitName="HJOIN-rollback"; Compare(splitName,Read(),new PathsD{Rect(0,0,10,10),Rect(10,0,10,10)},2);

        if(type.GetMethod("AppendHofOffsetEntities",Flags)==null) return; // previous-release audit
        Clear(); var input=Path(0,0,8,0,8,3,12,3,12,0,20,0,20,8,12,8,12,5,8,5,8,8,0,8);
        ObjectId source;
        using(var tr=Db.TransactionManager.StartTransaction()) { source=Add(tr,Poly(input)); tr.Commit(); }
        using(var tr=Db.TransactionManager.StartTransaction())
        {
            var p=(Polyline)tr.GetObject(source,OpenMode.ForRead);
            var offsets=(List<Entity>)Call("GetOffsetEntities",tr,p,1.5,false);
            try { Call("AppendHofOffsetEntities",tr,p,offsets,"HF_OFFSET_TARGET"); tr.Commit(); }
            finally { foreach(var e in offsets) e.Dispose(); }
        }
        using(var tr=Db.TransactionManager.StartTransaction())
        {
            var ms=(BlockTableRecord)tr.GetObject(Db.CurrentSpaceId,OpenMode.ForRead);
            var results=ms.Cast<ObjectId>().Where(id=>id!=source).Select(id=>(Polyline)tr.GetObject(id,OpenMode.ForRead)).ToList();
            Check("HOF-database-all-contours",results.Count==2); Check("HOF-target-layer-properties",results.All(p=>p.Layer=="HF_OFFSET_TARGET" && p.ColorIndex==3));
        }
        Check("HOF-original-preserved",!source.IsErased);
    }

    [CommandMethod("HGEOINIT")]
    public void Init()
    {
        type=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="HexFill").GetType("HexFill.HexFill");
        plugin=Activator.CreateInstance(type); type.GetField("db",Flags).SetValue(plugin,Db);
        type.GetField("ed",Flags).SetValue(plugin,Application.DocumentManager.MdiActiveDocument.Editor);
        log=new StreamWriter(Application.DocumentManager.MdiActiveDocument.Editor.GetString("Result path: ").StringResult);
        checks=failures=0;
    }
    private static void JoinCase(string name, PathsD inputs, PathsD reference, int resultCount, int mergeCount, double tolerance=0.002)
    {
        try
        {
            Clear(); var ids=new List<ObjectId>();
            using(var tr=Db.TransactionManager.StartTransaction()) { foreach(var p in inputs) ids.Add(Add(tr,Poly(p))); tr.Commit(); }
            using(var tr=Db.TransactionManager.StartTransaction())
            {
                var plan=Call("BuildHjoinPlan",tr,ids.ToArray());
                // HashSet<T> implements ICollection<T>, not the non-generic ICollection.
                int mergedCount=(int)plan.GetType().GetField("MergeSourceIds",Flags).GetValue(plan).GetType().GetProperty("Count").GetValue(plan.GetType().GetField("MergeSourceIds",Flags).GetValue(plan),null);
                Check(name+"-sources",mergedCount==mergeCount,"actual="+mergedCount+" expected="+mergeCount);
                if(mergedCount>0) Call("ExecuteHjoinPlan",Db,tr,plan);
                tr.Commit();
            }
            splitName=name; Compare(name,Read(),reference,resultCount,tolerance);
        }
        catch(System.Exception ex) { Check(name+"-exception",false,ex.GetType().Name+":"+ex.Message); }
    }
    private static void OffsetCase(string name, PathD input, double distance, bool expand)
    {
        try
        {
            Clear(); var refs=Clipper.InflatePaths(new PathsD{input},expand?distance:-distance,JoinType.Miter,EndType.Polygon,1000000,6);
            using(var tr=Db.TransactionManager.StartTransaction())
            using(var p=Poly(input))
            {
                var actual=new PathsD();
                // Baseline adapter supports 2.2.0.3; the fixed version returns all offset entities.
                if(type.GetMethod("GetOffsetEntities",Flags)!=null)
                {
                    var entities=(List<Entity>)Call("GetOffsetEntities",tr,p,distance,expand);
                    try { foreach(var e in entities) { Check(name+"-polyline",e is Polyline); if(e is Polyline) actual.Add(Points((Polyline)e)); } }
                    finally { foreach(var e in entities) e.Dispose(); }
                }
                else
                {
                    object[] args={tr,p,distance,expand,null}; Call("GetOffsetValue",args);
                    using(var e=(Entity)args[4]) { if(e is Polyline) actual.Add(Points((Polyline)e)); }
                }
                Compare(name,actual,refs,refs.Count,0.004);
            }
        }
        catch(System.Exception ex) { Check(name+"-exception",false,ex.GetType().Name+":"+ex.Message); }
    }
    [CommandMethod("HGEOUNIT")]
    public void Unit()
    {
        type.GetField("HsetHjoinMergeTolerance",Flags).SetValue(null,0.2);
        for(int v=0;v<3;v++)
        {
            var sets=new Dictionary<string,PathsD>{
                {"adjacent",new PathsD{Rect(0,0,10,10),Rect(10,0,10,10)}},
                {"partial-edge",new PathsD{Rect(0,0,10,20),Rect(10,5,10,10)}},
                {"frame-hole",new PathsD{Rect(0,0,30,5),Rect(0,25,30,5),Rect(0,5,5,20),Rect(25,5,5,20)}},
                // Scaled from upstream TestPolytreeHoles10 (#973): a frame with two holes.
                {"upstream-973",new PathsD{Rect(0,0,79.53,.94),Rect(0,33.36,79.53,.94),Rect(78.47,.94,1.06,32.42),Rect(0,.94,.94,32.42),Rect(29.29,.94,1.06,32.42)}},
                {"collinear",new PathsD{Path(0,0,5,0,5,0,10,0,10,10,0,10),Rect(10,0,10,10)}},
                {"notch",new PathsD{Path(0,0,20,0,20,20,10,20,10,10,0,10),Rect(0,10,10,10)}}
            };
            foreach(var pair in sets)
            {
                var p=new PathsD(pair.Value.Select(t=>Variant(t,v))); if(v==1) p[0].Reverse();
                var reference=Union(p); JoinCase("HJOIN-"+pair.Key+"-v"+v,p,reference,reference.Count,p.Count);
            }
        }
        JoinCase("HJOIN-point-touch",new PathsD{Rect(0,0,10,10),Rect(10,10,10,10)},new PathsD{Rect(0,0,10,10),Rect(10,10,10,10)},2,0);
        JoinCase("HJOIN-disjoint",new PathsD{Rect(0,0,10,10),Rect(30,0,10,10)},new PathsD{Rect(0,0,10,10),Rect(30,0,10,10)},2,0);
        // Shared edges on the same side are not an adjacency union; these inputs must stay intact.
        JoinCase("HJOIN-same-side-overlap",new PathsD{Rect(0,0,20,10),Rect(5,0,10,10)},new PathsD{Rect(0,0,20,10),Rect(5,0,10,10)},2,0);
        JoinCase("HJOIN-duplicate",new PathsD{Rect(0,0,10,10),Rect(0,0,10,10)},new PathsD{Rect(0,0,10,10),Rect(0,0,10,10)},2,0);
        JoinCase("HJOIN-small-gap",new PathsD{Rect(0,0,10,10),Rect(10.1,0,10,10)},new PathsD{Rect(0,0,20.1,10)},1,2,1.01);
        // Screenshot regression: two narrow seams, one opening into an inner hole.
        // Scale the screenshot coordinates so both seams lie within the 0.2mm setting.
        var cap=Path(29,304,388,304,388,194,105,194,105,261,45,261,45,253,29,253);
        var body=Path(29,309,388,309,388,318,378,341,369,341,369,372,418,372,425,357,508,357,
            508,524,382,524,382,481,348,481,348,428,334,428,318,391,256,391,256,424,228,424,
            228,452,176,452,176,587,382,587,382,527,508,527,508,701,425,701,425,696,
            365,696,388,741,388,798,109,798,104,784,47,784,42,798,29,798);
        var screenshot=new PathsD{cap,body,Rect(421,172,87,132),Rect(422,755,86,43)};
        var bands=new PathsD(screenshot){Rect(29,304,359,5),Rect(382,524,126,3)};
        for(int v=0;v<3;v++)
        {
            Func<PathD,PathD> scale=p=>new PathD(p.Select(t=>new PointD(t.x/30.0,(800-t.y)/30.0)));
            var input=new PathsD(screenshot.Select(p=>Variant(scale(p),v)));
            var reference=Union(new PathsD(bands.Select(p=>Variant(scale(p),v))));
            JoinCase("HJOIN-screenshot-hole-v"+v,input,reference,4,2,0.002);
            input.Reverse();
            JoinCase("HJOIN-screenshot-selection-reversed-v"+v,input,reference,4,2,0.002);
        }
        type.GetField("HsetHjoinMergeTolerance",Flags).SetValue(null,0.01);
        var tinyHole=new PathsD{Rect(0,0,10,4.975),Rect(0,5.025,10,4.975),Rect(0,4.975,4.975,.05),Rect(5.025,4.975,4.975,.05)};
        JoinCase("HJOIN-small-enclosed-hole",tinyHole,Union(tinyHole),2,4,0.000001);
        type.GetField("HsetHjoinMergeTolerance",Flags).SetValue(null,0.2);
        var dumbbell=Path(0,0,8,0,8,3,12,3,12,0,20,0,20,8,12,8,12,5,8,5,8,8,0,8);
        var offsetSets=new Dictionary<string,PathD>{
            {"rectangle",Rect(0,0,20,20)}, {"concave-L",Path(0,0,20,0,20,8,8,8,8,20,0,20)},
            {"narrow-neck",dumbbell}, {"near-collinear",Path(0,0,10,.01,20,0,20,20,0,20,0,0)}
        };
        foreach(var pair in offsetSets) for(int v=0;v<3;v++) foreach(bool expand in new[]{false,true})
            OffsetCase("HOF-"+pair.Key+"-v"+v+"-"+(expand?"out":"in"),Variant(pair.Value,v),pair.Key=="narrow-neck"?1.5:1,expand);
        for(int v=0;v<3;v++) OffsetCase("HOF-collapse-v"+v,Variant(Rect(0,0,10,10),v),6,false);
        TransactionChecks();
    }
    private static void SplitFixture(int n, out PathD input, out PathD cut, out bool split)
    {
        input=Rect(0,0,20,20); cut=Path(-5,10,25,10); split=true;
        switch(n)
        {
            case 1: input=Path(0,0,20,0,20,8,8,8,8,20,0,20); cut=Path(4,-5,4,25); break;
            case 2: input=Path(0,0,20,0,20,20,15,20,15,5,5,5,5,20,0,20); cut=Path(-5,10,25,10); break;
            case 3: input=Path(0,0,20,0,20,20,16,20,16,4,12,4,12,20,8,20,8,4,4,4,4,20,0,20); cut=Path(-5,10,25,10); break;
            case 4: input=Rect(0,0,100,100); cut=Path(0,70,100,70,0,30,100,30); break;
            case 5: input=Rect(0,0,100,100); cut=Path(-10,70,110,70,-10,30,110,30); break;
            case 6: cut=Path(-5,-5,25,25); break;
            case 7: cut=Path(-5,5,5,-5); split=false; break;
            case 8: cut=Path(-5,0,25,0); split=false; break;
            case 9: cut=Path(5,5,15,15); split=false; break;
            case 10: input=Path(0,0,10,0,10,0,20,0,20,20,0,20,0,0); break;
            case 11: cut=Path(-5,10,10,10,10,10,25,10); break;
        }
    }
    [CommandMethod("HGEOSET")]
    public void SplitSetup()
    {
        var ed=Application.DocumentManager.MdiActiveDocument.Editor;
        int id=ed.GetInteger("Case: ").Value; int n=id%100, v=id/100;
        Clear(); PathD input,cut; SplitFixture(n,out input,out cut,out shouldSplit);
        input=Variant(input,v); cut=Variant(cut,v);
        sourcePath=input;
        type.GetField("HsetHplitCutSpacing",Flags).SetValue(null,0.1);
        using(var tr=Db.TransactionManager.StartTransaction())
        {
            original=Add(tr,Poly(input));
            var ms=(BlockTableRecord)tr.GetObject(Db.CurrentSpaceId,OpenMode.ForWrite);
            var circle=new Circle(new Point3d(-1000,-1000,0),Vector3d.ZAxis,2);
            sentinel=ms.AppendEntity(circle); tr.AddNewlyCreatedDBObject(circle,true); tr.Commit();
        }
        splitName="HPLIT-case"+n+"-v"+v;
        var strip=Clipper.InflatePaths(new PathsD{cut},.05,JoinType.Miter,EndType.Butt,1000000,6);
        expected=shouldSplit?Clipper.Difference(new PathsD{input},strip,FillRule.NonZero,6):new PathsD{input}; expectedCount=expected.Count;
        Check(splitName+"-oracle",shouldSplit?expectedCount>=2:expectedCount==1);
    }
    [CommandMethod("HGEOASSERT")]
    public void SplitAssert()
    {
        try
        {
            var actual=Read(); Compare(splitName,actual,expected,expectedCount,0.005); Check(splitName+"-original",original.IsErased==shouldSplit);
            using(var tr=Db.TransactionManager.StartTransaction())
            {
                var circle=(Circle)tr.GetObject(sentinel,OpenMode.ForRead);
                Check(splitName+"-unrelated-kept",circle.Radius==2 && circle.Center==new Point3d(-1000,-1000,0));
            }
        }
        catch(System.Exception ex) { Check(splitName+"-exception",false,ex.Message); }
    }
    [CommandMethod("HGEOUNDO")]
    public void UndoAssert()
    {
        Check("HPLIT-undo-original-restored",!original.IsErased);
        Compare("HPLIT-undo",Read(),new PathsD{sourcePath},1);
    }
    [CommandMethod("HGEODONE")]
    public void Done() { log.WriteLine("SUMMARY checks="+checks+" failures="+failures); log.Dispose(); }
    [CommandMethod("HGEOEXPORT")]
    public void Export()
    {
        string filename=Application.DocumentManager.MdiActiveDocument.Editor.GetString("Export path: ").StringResult;
        using(var writer=new StreamWriter(filename))
        using(var tr=Db.TransactionManager.StartTransaction())
        {
            var space=(BlockTableRecord)tr.GetObject(Db.CurrentSpaceId,OpenMode.ForRead);
            foreach(ObjectId id in space)
            {
                var p=tr.GetObject(id,OpenMode.ForRead) as Polyline; if(p==null) continue;
                var xy=new List<string>(); for(int i=0;i<p.NumberOfVertices;i++) { var point=p.GetPoint2dAt(i); xy.Add(point.X.ToString("R",CultureInfo.InvariantCulture)+","+point.Y.ToString("R",CultureInfo.InvariantCulture)); }
                writer.WriteLine((p.Closed?"1":"0")+"|"+p.Area.ToString("R",CultureInfo.InvariantCulture)+"|"+p.Layer+"|"+string.Join(";",xy));
            }
        }
    }
}
