using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using FontDescriptor = Autodesk.AutoCAD.GraphicsInterface.FontDescriptor;
using Autodesk.AutoCAD.Runtime;
using Exception = System.Exception;

namespace HexFill
{
    // 实现IExtensionApplication接口，支持注册表方式自动加载
    public partial class HexFill : IExtensionApplication
    {
        // ---------- IExtensionApplication 实现 ----------
        public void Initialize()
        {
            // 自动为所有已打开的文档注册球标删除事件
            var dm = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager;
            foreach (Document doc in dm)
            {
                RegisterBalloonEvents(doc);
            }
            // 监听新文档打开，自动注册球标事件
            dm.DocumentCreated += OnDocumentCreated;
            dm.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
        }

        public void Terminate()
        {
            var dm = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager;
            Autodesk.AutoCAD.ApplicationServices.Application.Idle -= OnIdleRenumberBalloons;
            dm.DocumentCreated -= OnDocumentCreated;
            dm.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            foreach (Document doc in dm)
            {
                UnregisterBalloonEvents(doc);
            }
            _pendingBalloonRenumberDatabases.Clear();
            _registeredAppsByDb.Clear();
        }

        private void OnDocumentCreated(object sender, DocumentCollectionEventArgs e)
        {
            if (e.Document != null)
                RegisterBalloonEvents(e.Document);
        }

        private void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            if (e.Document == null)
                return;

            Database documentDatabase = null;
            try
            {
                documentDatabase = e.Document.Database;
                UnregisterBalloonEvents(e.Document);
            }
            catch
            {
            }

            if (documentDatabase != null)
            {
                _pendingBalloonRenumberDatabases.Remove(documentDatabase);
                _registeredAppsByDb.Remove(documentDatabase);
            }

            if (_pendingBalloonRenumberDatabases.Count == 0)
                Autodesk.AutoCAD.ApplicationServices.Application.Idle -= OnIdleRenumberBalloons;
        }

        // ---------- 静态事件，供UI使用 ----------
        public static event Action<Dictionary<string, int>, int> SelectionCompleted; // 参数：类型统计字典，总对象数
        public static event Action<int> NumberingCompleted; // 参数：叶子区域总数

        // 已注册的 XData 应用名缓存仅作诊断记录；正确性始终以当前数据库 RegAppTable 为准。
        // Undo/PURGE 可能删除数据库记录，但不会回滚进程内缓存，因此禁止用缓存跳过 Has 检查。
        private readonly Dictionary<Database, HashSet<string>> _registeredAppsByDb = new Dictionary<Database, HashSet<string>>();

        private HashSet<string> GetRegisteredAppsForDb()
        {
            if (!_registeredAppsByDb.TryGetValue(db, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _registeredAppsByDb[db] = set;
            }
            return set;
        }

        private static bool _hgrYDirection = false;

        // ===== 统一常量（所有命令共享） =====
        private const double Diameter = 0.06;           // 生成圆直径 (mm) = 0.06mm
        private const double Radius = Diameter / 2.0;    // 生成圆半径 = 0.03mm
        private const double MinCircleSpacing = 0.1;    // 圆心与圆心之间最小距离 (mm)
        private const double MinDeleteRadius = 0.01;    // 删除判定-半径下限 (mm)（直径 0.02mm）
        private const double MaxDeleteRadius = 0.1;     // 删除判定-半径上限 (mm)（直径 0.2mm）
        private const double Sqrt3Over2 = 0.8660254037844386; // sqrt(3)/2，六边形行间距系数
        private const int NearestNeighborBruteForceThreshold = 1024;
        private const double PositionGroupThreshold = 2.0;    // SortByPosition 分组阈值 (mm)

        // 编号相关常量
        private const string GGE_NUM_LAYER = "H_NUM";
        private const string GGE_NUM_APP = "H_Num";
        private const string GGE_TEXT_HANDLE_APP = "H_TextHandle";
        private const string GGE_ISLEAF_APP = "H_IsLeaf"; // 叶子节点标记
        private const string HCLN_CLEARED_APP = "H_Clr"; // HCLN清除标记

        // HDIA - 切换生成圆直径（0.03mm ↔ 0.06mm），仅当前CAD会话生效
        private static double _currentDiameter = 0.03;      // 当前圆直径，默认0.03mm
        private static string _lastHbrExportDirectory;
        private static string _lastHexExportDirectory;
        private double CurrentRadius { get { return _currentDiameter / 2.0; } }
        private double CurrentMinSpacing
        {
            get { return Math.Abs(_currentDiameter - 0.06) < 0.001 ? 0.14 : 0.1; }
        }

        /// <summary>
        /// CSV 数值解析：兼容 Excel 在不同区域设置下保存的 1.25、1,25、1,234.56 和 1.234,56。
        /// 不使用 NumberStyles.Any 直接按 InvariantCulture 解析，避免把 1,25 误认成千分位 125。
        /// </summary>
        private static bool TryParseDoubleAnyCulture(string text, out double value)
        {
            value = 0;
            string normalized = NormalizeCsvField(text).Replace(" ", "").Replace("\u00A0", "");
            if (normalized.StartsWith("'")) normalized = normalized.Substring(1);
            if (normalized.StartsWith("=\"") && normalized.EndsWith("\"") && normalized.Length >= 4)
                normalized = normalized.Substring(2, normalized.Length - 3);
            if (string.IsNullOrEmpty(normalized)) return false;

            int lastDot = normalized.LastIndexOf('.');
            int lastComma = normalized.LastIndexOf(',');
            if (lastDot >= 0 && lastComma >= 0)
            {
                // 最后出现的分隔符视作小数点，另一种视作千分位。
                if (lastComma > lastDot)
                    normalized = normalized.Replace(".", "").Replace(',', '.');
                else
                    normalized = normalized.Replace(",", "");
            }
            else if (lastComma >= 0)
            {
                // HBR/HAD 的亮度值允许小于 1，单个逗号一律按小数点处理。
                normalized = normalized.Replace(',', '.');
            }

            return double.TryParse(normalized, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value) && IsFinite(value);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private const long MaxGridCandidatePoints = 10000000;
        private const int MaxGeneratedCircles = 1000000;

        private static void ValidateGridDimensions(double width, double height,
            double rowSpacing, double colSpacing, out int rows, out int cols)
        {
            rows = cols = 0;
            if (!IsFinite(width) || !IsFinite(height) ||
                !IsFinite(rowSpacing) || !IsFinite(colSpacing) || rowSpacing <= 0 || colSpacing <= 0)
                throw new InvalidOperationException("网格尺寸和间距必须为有限正数。");
            if (width <= 0 || height <= 0) return;
            double rowCount = Math.Floor(height / rowSpacing) + 1;
            double colCount = Math.Floor(width / colSpacing) + 1;
            if (!IsFinite(rowCount) || !IsFinite(colCount) ||
                rowCount > int.MaxValue || colCount > int.MaxValue ||
                rowCount * colCount > MaxGridCandidatePoints)
                throw new InvalidOperationException("网格规模过大（单对象最多 1000 万个候选点），请增大间距或缩小处理范围；原对象已保留。");
            rows = (int)rowCount;
            cols = (int)colCount;
        }

        private static void AddGridPoint(List<Point3d> points, Point3d point)
        {
            if (points.Count >= MaxGeneratedCircles)
                throw new InvalidOperationException("单对象生成圆超过 100 万个，请增大间距或缩小处理范围；原对象已保留。");
            points.Add(point);
        }

        private static void CheckGenerationCancelled()
        {
            if (HostApplicationServices.Current.UserBreak())
                throw new OperationCanceledException("操作已取消；当前对象已回滚保留。");
        }

        [CommandMethod("HDIA")]
        public void HDIA()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            db = doc.Database;
            ed = doc.Editor;
            if (Math.Abs(_currentDiameter - 0.03) < 0.001)
                _currentDiameter = 0.06;
            else
                _currentDiameter = 0.03;
            ed.WriteMessage($"\n当前圆直径: {_currentDiameter}mm, 半径: {CurrentRadius}mm, 最小间距: {CurrentMinSpacing}mm");
        }

        private Database db;
        private Editor ed;

        private string SelectImportCsv(string title, string directory, string dialogName)
        {
            directory = Path.GetFullPath(directory);
            // Core Console/scripts require a command-line prompt. In the desktop host,
            // force the export folder: ordinary InitialDirectory loses to CAD dialog history.
            int active = Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("CMDACTIVE"));
            if ((active & (4 | 32)) != 0 || string.Equals(System.Diagnostics.Process.GetCurrentProcess().ProcessName,
                "accoreconsole", StringComparison.OrdinalIgnoreCase))
            {
                var options = new PromptOpenFileOptions(title)
                {
                    Filter = "CSV Files (*.csv)|*.csv",
                    InitialDirectory = directory
                };
                var result = ed.GetFileNameForOpen(options);
                return result.Status == PromptStatus.OK ? result.StringResult : null;
            }
            return ShowImportCsvDialog(title, directory, dialogName);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string ShowImportCsvDialog(string title, string directory, string dialogName)
        {
            // Keep desktop-only GUI types out of the Core Console prompt's JIT path.
            var flags = Autodesk.AutoCAD.Windows.OpenFileDialog.OpenFileDialogFlags.DefaultIsFolder
                | Autodesk.AutoCAD.Windows.OpenFileDialog.OpenFileDialogFlags.ForceDefaultFolder;
            var dialog = new Autodesk.AutoCAD.Windows.OpenFileDialog(title, directory, "csv", dialogName, flags);
            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.Filename : null;
        }

        private static string GetLastHbrExportDirectory()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\HexFill"))
                {
                    return key?.GetValue("LastHbrExportDirectory") as string;
                }
            }
            catch
            {
                return null;
            }
        }

        private static void SaveLastHbrExportDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\HexFill"))
                {
                    key?.SetValue("LastHbrExportDirectory", directory);
                }
            }
            catch
            {
            }
        }

        private static string GetLastHexExportDirectory()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\HexFill"))
                {
                    return key?.GetValue("LastHexExportDirectory") as string;
                }
            }
            catch
            {
                return null;
            }
        }

        private static void SaveLastHexExportDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\HexFill"))
                {
                    key?.SetValue("LastHexExportDirectory", directory);
                }
            }
            catch
            {
            }
        }

        // ---------- 新增：类型名映射为中文 ----------
        private string MapTypeNameToChinese(string engName)
        {
            switch (engName)
            {
                case "Circle": return "圆";
                case "Polyline":
                case "Polyline2d":
                case "Polyline3d": return "多段线";
                case "Line": return "直线";
                case "Ellipse": return "椭圆";
                case "Spline": return "样条曲线";
                default: return engName;
            }
        }

        // ---------- 静态工具方法 ----------
        private static double Point2dDistance(Point2d a, Point2d b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static Point2d Point3dToPoint2d(Point3d pt)
        {
            return new Point2d(pt.X, pt.Y);
        }

        private static double GetEllipseRotationAngle(Ellipse ell)
        {
            return Math.Atan2(ell.MajorAxis.Y, ell.MajorAxis.X);
        }

        /// <summary>
        /// 检测已有圆的蜂窝排列方向
        /// X方向偏移中每个圆有水平邻居(|dy|≈0, |dx|≈s)
        /// Y方向偏移中每个圆有垂直邻居(|dx|≈0, |dy|≈s)
        /// 通过统计水平近邻和垂直近邻的数量来判定方向
        /// </summary>
        private static bool DetectHoneycombDirection(List<Point2d> centers)
        {
            if (centers.Count < 5) return false;

            // 圆数量大时均匀抽样，最多取 200 个点即可准确判定方向
            var pts = centers;
            int maxSample = 200;
            if (centers.Count > maxSample)
            {
                pts = new List<Point2d>(maxSample);
                double step = (double)(centers.Count - 1) / (maxSample - 1);
                for (int i = 0; i < maxSample; i++)
                    pts.Add(centers[(int)(i * step)]);
            }

            // 找最小间距 s
            double s = double.MaxValue;
            for (int i = 0; i < pts.Count; i++)
                for (int j = i + 1; j < pts.Count; j++)
                {
                    double dx = pts[i].X - pts[j].X;
                    double dy = pts[i].Y - pts[j].Y;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist > 0 && dist < s)
                        s = dist;
                }

            if (s <= 0 || s == double.MaxValue) return false;

            // 统计水平近邻(|dy|≈0, |dx|≈s) 和垂直近邻(|dx|≈0, |dy|≈s)
            double tol = s * 0.3;
            int hCount = 0, vCount = 0;

            for (int i = 0; i < pts.Count; i++)
                for (int j = i + 1; j < pts.Count; j++)
                {
                    double dx = Math.Abs(pts[i].X - pts[j].X);
                    double dy = Math.Abs(pts[i].Y - pts[j].Y);
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist > s * 0.8 && dist < s * 1.2)
                    {
                        if (dy < tol && dx > tol) hCount++; // 水平邻居
                        if (dx < tol && dy > tol) vCount++; // 垂直邻居
                    }
                }

            // Y方向偏移时垂直邻居多于水平邻居
            return vCount > hCount;
        }

        // ---------- 公共辅助函数 ----------

        /// <summary>
        /// 判断点是否在曲线内。
        /// - Circle / Ellipse：精确数学判断
        /// - 其他曲线（含含弧的多段线）：使用 IntersectWith 精确射线法，
        ///   避免参数采样在弧线段上漏交点导致圆生成在边界外的问题。
        /// </summary>
        /// <summary>
        /// 检查子曲线是否被父曲线包含（鲁棒版，支持共享边界的分割区域）
        /// 综合判定策略，不依赖 IntersectWith 硬性判定：
        /// 1. 面积比较：子面积必须小于父面积
        /// 2. 边界框检查：子bbox在父bbox内（允许容差）
        /// 3. 质心检查：子中心点在父区域内部
        /// 4. 边界采样：沿子区域边界均匀采样多点，>80%在父内部即判定包含
        /// </summary>
        private bool IsFullyContained(Curve child, Curve parent)
        {
            return IsFullyContained(child, parent, null, null);
        }

        /// <summary>
        /// IsFullyContained 的缓存版本：同一批分析中对同一曲线只做一次边界采样/
        /// 包含判定器构建（两者都是曲线的纯函数，批量分析阶段曲线不会被修改）。
        /// 缓存仅在真正通过面积/包围盒预检后才按需填充，保持与原实现一致的惰性语义。
        /// </summary>
        private bool IsFullyContained(
            Curve child, Curve parent,
            Dictionary<ObjectId, List<Point2d>> samplesCache,
            Dictionary<ObjectId, CurveContainment> containmentCache)
        {
            if (!IsCurveClosed(child) || !IsCurveClosed(parent)) return false;

            try
            {
                // 1. 面积比较（快速排除）
                double childArea = child.Area;
                double parentArea = parent.Area;
                if (childArea >= parentArea) return false;

                // 2. 边界框检查（允许微小容差，处理坐标精度误差）
                var childExt = child.GeometricExtents;
                var parentExt = parent.GeometricExtents;
                double bboxTol = 0.001; // 0.001mm 容差
                if (childExt.MinPoint.X < parentExt.MinPoint.X - bboxTol ||
                    childExt.MaxPoint.X > parentExt.MaxPoint.X + bboxTol ||
                    childExt.MinPoint.Y < parentExt.MinPoint.Y - bboxTol ||
                    childExt.MaxPoint.Y > parentExt.MaxPoint.Y + bboxTol)
                    return false;

                // 3. 边界采样：所有点都必须位于父区域内部或父边界上。
                var samplePoints = GetOrComputeBoundarySamples(samplesCache, child);
                if (samplePoints.Count == 0) return false;
                CurveContainment parentContainment = GetOrComputeCurveContainment(containmentCache, parent);
                if (parentContainment == null) return false;

                foreach (var pt in samplePoints)
                {
                    if (!parentContainment.Contains(pt))
                    {
                        var point3d = new Point3d(pt.X, pt.Y, 0);
                        if (DistanceToCurve(point3d, parent) > bboxTol)
                            return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取子曲线的边界采样点（可选缓存）。采样异常时退回到顶点采样，
        /// 与 SampleBoundaryPoints 内部的失败回退路径保持一致。
        /// </summary>
        private List<Point2d> GetOrComputeBoundarySamples(
            Dictionary<ObjectId, List<Point2d>> cache, Curve curve)
        {
            List<Point2d> samples;
            if (cache != null && cache.TryGetValue(curve.ObjectId, out samples))
                return samples;

            try
            {
                samples = SampleBoundaryPoints(curve);
            }
            catch
            {
                samples = GetCurveVertices(curve);
            }
            if (cache != null)
                cache[curve.ObjectId] = samples;
            return samples;
        }

        /// <summary>
        /// 构建曲线的包含判定器（可选缓存）。构建失败返回 null，
        /// 与原实现中异常被捕获后整体返回 false 的语义一致。
        /// </summary>
        private CurveContainment GetOrComputeCurveContainment(
            Dictionary<ObjectId, CurveContainment> cache, Curve curve)
        {
            CurveContainment containment;
            if (cache != null && cache.TryGetValue(curve.ObjectId, out containment) && containment != null)
                return containment;

            try
            {
                containment = BuildCurveContainment(curve);
            }
            catch
            {
                containment = null;
            }
            if (cache != null)
                cache[curve.ObjectId] = containment;
            return containment;
        }

        /// <summary>
        /// 沿曲线边界均匀采样多个点（支持所有曲线类型，含弧段）
        /// 使用参数空间均匀采样，确保弧段中点也被检查
        /// </summary>
        private List<Point2d> SampleBoundaryPoints(Curve curve)
        {
            var points = new List<Point2d>();
            try
            {
                double startParam = curve.StartParam;
                double endParam = curve.EndParam;
                double paramRange = endParam - startParam;
                if (paramRange <= 0) return GetCurveVertices(curve);

                // 均匀采样 32 个点（覆盖整个边界，包括弧段）
                int numSamples = 32;
                for (int i = 0; i < numSamples; i++)
                {
                    double t = startParam + paramRange * i / numSamples;
                    var pt = curve.GetPointAtParameter(t);
                    points.Add(new Point2d(pt.X, pt.Y));
                }

                // 同时加入顶点（确保关键拐点被检查）
                var verts = GetCurveVertices(curve);
                foreach (var v in verts)
                {
                    if (!points.Contains(v))
                        points.Add(v);
                }
            }
            catch
            {
                // 退回到顶点采样
                points = GetCurveVertices(curve);
            }
            return points;
        }

        /// <summary>
        /// 获取闭合曲线的采样顶点（用于包含判断）
        /// </summary>
                private List<Point2d> GetCurveVertices(Curve curve)
        {
            var verts = new List<Point2d>();
            try
            {
                if (curve is Circle circle)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        double angle = i * Math.PI / 4.0;
                        verts.Add(new Point2d(
                            circle.Center.X + circle.Radius * Math.Cos(angle),
                            circle.Center.Y + circle.Radius * Math.Sin(angle)));
                    }
                }
                else if (curve is Ellipse ellipse)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        double angle = i * Math.PI / 4.0;
                        verts.Add(new Point2d(
                            ellipse.Center.X + ellipse.MajorRadius * Math.Cos(angle),
                            ellipse.Center.Y + ellipse.MinorRadius * Math.Sin(angle)));
                    }
                }
                else if (curve is Polyline pl)
                {
                    var polylineVertices = new List<Point2d>();
                    for (int i = 0; i < pl.NumberOfVertices; i++)
                    {
                        var pt = pl.GetPoint3dAt(i);
                        var vertex = new Point2d(pt.X, pt.Y);
                        polylineVertices.Add(vertex);
                        verts.Add(vertex);
                    }

                    for (int i = 0; i < polylineVertices.Count; i++)
                    {
                        foreach (double fraction in new[] { 0.25, 0.5, 0.75 })
                        {
                            var sample = pl.GetPointAtParameter(i + fraction);
                            verts.Add(new Point2d(sample.X, sample.Y));
                        }
                    }
                }
                else if (curve is Spline spline)
                {
                    // 采样样条曲线：起点+终点+中间6个点
                    verts.Add(new Point2d(spline.StartPoint.X, spline.StartPoint.Y));
                    double start = spline.StartParam;
                    double end = spline.EndParam;
                    for (int i = 1; i <= 6; i++)
                    {
                        double t = start + (end - start) * i / 7.0;
                        var pt = spline.GetPointAtParameter(t);
                        verts.Add(new Point2d(pt.X, pt.Y));
                    }
                    verts.Add(new Point2d(spline.EndPoint.X, spline.EndPoint.Y));
                }
                else
                {
                    // 通用曲线：使用边界框的4个角点 + 中点
                    var ext = curve.GeometricExtents;
                    double cx = (ext.MinPoint.X + ext.MaxPoint.X) / 2;
                    double cy = (ext.MinPoint.Y + ext.MaxPoint.Y) / 2;
                    verts.Add(new Point2d(ext.MinPoint.X, ext.MinPoint.Y));
                    verts.Add(new Point2d(ext.MaxPoint.X, ext.MinPoint.Y));
                    verts.Add(new Point2d(ext.MaxPoint.X, ext.MaxPoint.Y));
                    verts.Add(new Point2d(ext.MinPoint.X, ext.MaxPoint.Y));
                    verts.Add(new Point2d(cx, cy));
                    // 加上起点和终点
                    verts.Add(new Point2d(curve.StartPoint.X, curve.StartPoint.Y));
                    verts.Add(new Point2d(curve.EndPoint.X, curve.EndPoint.Y));
                }
            }
            catch { }
            return verts;
        }

        private bool PointInPolygon(Point2d pt, Curve curve)
        {
            try
            {
                if (!IsCurveClosed(curve)) return false;
                return BuildCurveContainment(curve).Contains(pt);
            }
            catch
            {
                return false;
            }
        }

        private List<Point2d> BuildOrderedBoundary(Curve curve)
        {
            var points = new List<Point2d>();
            if (curve is Polyline polyline)
            {
                int segmentCount = polyline.Closed
                    ? polyline.NumberOfVertices
                    : Math.Max(0, polyline.NumberOfVertices - 1);
                for (int segment = 0; segment < segmentCount; segment++)
                {
                    int subdivisions = Math.Abs(polyline.GetBulgeAt(segment)) > 1e-10 ? 16 : 1;
                    for (int step = 0; step < subdivisions; step++)
                    {
                        double parameter = segment + step / (double)subdivisions;
                        var sample = polyline.GetPointAtParameter(parameter);
                        points.Add(new Point2d(sample.X, sample.Y));
                    }
                }
                return points;
            }

            double startParameter = curve.StartParam;
            double endParameter = curve.EndParam;
            double parameterRange = endParameter - startParameter;
            if (parameterRange <= 0)
                return points;

            int sampleCount = Math.Min(256, Math.Max(64, (int)Math.Ceiling(parameterRange * 16)));
            for (int index = 0; index < sampleCount; index++)
            {
                double parameter = startParameter + parameterRange * index / sampleCount;
                var sample = curve.GetPointAtParameter(parameter);
                points.Add(new Point2d(sample.X, sample.Y));
            }
            return points;
        }

        private static bool PointInVertexLoop(Point2d point, List<Point2d> vertices, double tolerance)
        {
            if (vertices == null || vertices.Count < 3)
                return false;

            double toleranceSquared = tolerance * tolerance;
            bool inside = false;
            int previous = vertices.Count - 1;

            for (int current = 0; current < vertices.Count; current++)
            {
                Point2d start = vertices[previous];
                Point2d end = vertices[current];
                if (DistanceToSegmentSquared(point, start, end) <= toleranceSquared)
                    return true;

                bool crosses = (start.Y > point.Y) != (end.Y > point.Y);
                if (crosses)
                {
                    double crossingX = (end.X - start.X) * (point.Y - start.Y)
                        / (end.Y - start.Y) + start.X;
                    if (point.X < crossingX)
                        inside = !inside;
                }
                previous = current;
            }
            return inside;
        }

        private static double DistanceToSegmentSquared(Point2d point, Point2d start, Point2d end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared <= double.Epsilon)
            {
                double px = point.X - start.X;
                double py = point.Y - start.Y;
                return px * px + py * py;
            }

            double projection = ((point.X - start.X) * dx + (point.Y - start.Y) * dy)
                / lengthSquared;
            projection = Math.Max(0, Math.Min(1, projection));
            double closestX = start.X + projection * dx;
            double closestY = start.Y + projection * dy;
            double distanceX = point.X - closestX;
            double distanceY = point.Y - closestY;
            return distanceX * distanceX + distanceY * distanceY;
        }

        /// <summary>
        /// 判断曲线是否封闭
        /// </summary>
        private bool IsCurveClosed(Curve curve)
        {
            if (curve.Closed) return true;
            if (curve is Polyline pl)
            {
                if (pl.Closed) return true;
                if (pl.NumberOfVertices < 2) return false;
                var start = pl.GetPoint3dAt(0);
                var end = pl.GetPoint3dAt(pl.NumberOfVertices - 1);
                return start.DistanceTo(end) < Tolerance.Global.EqualPoint;
            }
            try
            {
                var start = curve.StartPoint;
                var end = curve.EndPoint;
                return start.DistanceTo(end) < Tolerance.Global.EqualPoint;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取点到曲线的最短2D距离
        /// </summary>
        private double DistanceToCurve(Point3d pt, Curve curve)
        {
            var closest = curve.GetClosestPointTo(pt, false);
            double dx = pt.X - closest.X;
            double dy = pt.Y - closest.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// 获取区域的中心点
        /// </summary>
        private Point3d GetRegionCenter(Entity ent)
        {
            if (ent is Circle cir) return cir.Center;
            if (ent is Ellipse ell) return ell.Center;
            var ext = ent.GeometricExtents;
            return new Point3d((ext.MinPoint.X + ext.MaxPoint.X) / 2,
                               (ext.MinPoint.Y + ext.MaxPoint.Y) / 2, 0);
        }

        /// <summary>
        /// 获取区域的面积
        /// </summary>
        private double GetArea(Entity ent)
        {
            if (ent is Curve curve && IsCurveClosed(curve)) return curve.Area;
            return 0;
        }

        /// <summary>
        /// 读取扩展数据字符串
        /// </summary>
        private string GetXDataString(Entity ent, string appName)
        {
            ResultBuffer rb = ent.GetXDataForApplication(appName);
            if (rb == null) return null;
            try
            {
                foreach (TypedValue tv in rb)
                    if (tv.TypeCode == 1000 && tv.Value is string str)
                        return str;
                return null;
            }
            finally
            {
                // GetXDataForApplication 每次返回新分配的缓冲区，必须释放，避免原生内存泄漏
                rb.Dispose();
            }
        }

        /// <summary>
        /// 设置扩展数据字符串（自动注册应用程序名，带本地缓存避免重复查表）
        /// 保留其他 appName 的现有 XData，仅更新/删除指定 appName 的条目
        /// </summary>
        private void SetXDataString(Entity ent, string appName, string value, Transaction tr)
        {
            var registeredApps = GetRegisteredAppsForDb();
            RegAppTable regAppTable = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (!regAppTable.Has(appName))
            {
                regAppTable.UpgradeOpen();
                var regAppRec = new RegAppTableRecord { Name = appName };
                regAppTable.Add(regAppRec);
                tr.AddNewlyCreatedDBObject(regAppRec, true);
            }
            registeredApps.Add(appName);

            ent.UpgradeOpen();

            // 保留其他 appName 的 XData，仅更新/删除指定 appName 的条目
            var newRb = new ResultBuffer();
            bool hasNewXData = false;
            var existingRb = ent.XData;
            if (existingRb != null)
            {
                try
                {
                    bool skipCurrentApp = false;
                    bool insideValidAppSection = false;
                    foreach (TypedValue tv in existingRb)
                    {
                        if (tv.TypeCode == 1001)
                        {
                            string existingAppName = tv.Value as string;
                            skipCurrentApp = string.Equals(existingAppName, appName, StringComparison.Ordinal);
                            insideValidAppSection = !string.IsNullOrEmpty(existingAppName);
                        }
                        if (insideValidAppSection && !skipCurrentApp)
                        {
                            newRb.Add(tv);
                            hasNewXData = true;
                        }
                    }
                }
                finally
                {
                    // XData 属性读取返回新缓冲区，复制完毕后释放，避免原生内存泄漏
                    existingRb.Dispose();
                }
            }

            // 添加新的 XData 条目（value 非空时添加，为空/null 则相当于删除该条目）
            if (!string.IsNullOrEmpty(value))
            {
                newRb.Add(new TypedValue(1001, appName));
                newRb.Add(new TypedValue(1000, value));
                hasNewXData = true;
            }

            try
            {
                // 空 ResultBuffer 不是合法的 DXF XData 序列，会触发 eBadDxfSequence。
                // 删除最后一组 XData 时必须赋 null；仍有数据时首项必定是 1001 应用名。
                ent.XData = hasNewXData ? newRb : null;
            }
            finally
            {
                // XData 设置器把数据复制进实体后，调用方持有的缓冲区即可释放
                newRb.Dispose();
            }
        }

        // ---------- 图层与文字处理 ----------

        private ObjectId GetOrCreateLayer(string layerName)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var id = GetOrCreateLayer(tr, layerName);
                tr.Commit();
                return id;
            }
        }

        /// <summary>
        /// 在指定事务内获取或创建图层（避免在已有事务内嵌套开启新事务，提高中望CAD兼容性）
        /// </summary>
        private ObjectId GetOrCreateLayer(Transaction tr, string layerName)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(layerName))
            {
                return lt[layerName];
            }
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord
            {
                Name = layerName,
                Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByLayer, 7)
            };
            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            return ltr.ObjectId;
        }

        private ObjectId CreateNumberText(Point3d pt, string numberStr, Transaction tr)
        {
            var layerId = GetOrCreateLayer(tr, GGE_NUM_LAYER);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

            var text = new DBText
            {
                Position = pt,
                Height = 0.2,  // 改为0.2（原为0.5）
                TextString = numberStr,
                LayerId = layerId,
                HorizontalMode = TextHorizontalMode.TextCenter,
                VerticalMode = TextVerticalMode.TextVerticalMid,
                AlignmentPoint = pt
            };
            ObjectId textId = ms.AppendEntity(text);
            tr.AddNewlyCreatedDBObject(text, true);
            return textId;
        }

        private string GetEntityNumber(Entity ent)
        {
            return GetXDataString(ent, GGE_NUM_APP);
        }

        private bool IsActiveNumberedLeaf(Entity ent)
        {
            if (ent == null || ent.IsErased)
                return false;

            if (string.IsNullOrEmpty(GetEntityNumber(ent)))
                return false;

            if (GetXDataString(ent, GGE_ISLEAF_APP) != "1")
                return false;

            return GetXDataString(ent, HCLN_CLEARED_APP) != "1";
        }

        private void SetEntityNumber(Entity ent, string numStr, Transaction tr)
        {
            SetXDataString(ent, GGE_NUM_APP, numStr, tr);
        }

        private string GetEntityTextHandle(Entity ent)
        {
            return GetXDataString(ent, GGE_TEXT_HANDLE_APP);
        }

        private void SetEntityTextHandle(Entity ent, string handle, Transaction tr)
        {
            SetXDataString(ent, GGE_TEXT_HANDLE_APP, handle, tr);
        }

        private void DeleteEntityText(Entity ent, Transaction tr)
        {
            var handleStr = GetEntityTextHandle(ent);
            if (!string.IsNullOrEmpty(handleStr) && long.TryParse(handleStr, System.Globalization.NumberStyles.HexNumber, null, out long handleLong))
            {
                try
                {
                    var id = db.GetObjectId(false, new Handle(handleLong), 0);
                    if (!id.IsNull && id.IsValid)
                    {
                        var text = tr.GetObject(id, OpenMode.ForRead) as DBText;
                        if (text != null && !text.IsErased)
                        {
                            text.UpgradeOpen();
                            text.Erase();
                        }
                    }
                }
                catch
                {
                    // 忽略无效句柄
                }
            }
            SetEntityTextHandle(ent, null, tr);
        }

        /// <summary>
        /// HCLN专用：写入H_Clr标记（替代物理删除XData，绕过AutoCAD 2018 XData删除bug）
        /// </summary>
        private void MarkEntityClearedByHcln(Entity ent, Transaction tr)
        {
            // Missing handles are harmless; failure to erase existing text must roll back this region.
            string handleStr = GetEntityTextHandle(ent);
            long handleValue;
            ObjectId textId;
            if (long.TryParse(handleStr, System.Globalization.NumberStyles.HexNumber, null, out handleValue)
                && db.TryGetObjectId(new Handle(handleValue), out textId)
                && !textId.IsNull && textId.IsValid && !textId.IsErased)
            {
                var text = tr.GetObject(textId, OpenMode.ForRead, false) as DBText;
                if (text != null)
                {
                    text.UpgradeOpen();
                    text.Erase();
                }
            }
            SetEntityTextHandle(ent, null, tr);
            SetEntityNumber(ent, null, tr);
            SetXDataString(ent, GGE_ISLEAF_APP, null, tr);
            SetXDataString(ent, HCLN_CLEARED_APP, "1", tr);
        }

        private void DeleteEntityNumberCompletely(Entity ent, Transaction tr)
        {
            DeleteEntityText(ent, tr);
            ent.UpgradeOpen();
            // 只清除编号相关的XData，保留其他可能的XData
            SetEntityNumber(ent, null, tr);
            SetXDataString(ent, GGE_TEXT_HANDLE_APP, null, tr);
            SetXDataString(ent, GGE_ISLEAF_APP, null, tr);
        }

        private bool TryGetExistingLayerId(Transaction tr, string layerName, out ObjectId layerId)
        {
            layerId = ObjectId.Null;
            if (string.IsNullOrEmpty(layerName))
                return false;

            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(layerName))
                return false;

            layerId = lt[layerName];
            return true;
        }

        private int CleanupOrphanNumberTexts(Transaction tr, IEnumerable<ObjectId> scopeIds,
            bool restrictHandleCleanupToScope = false)
        {
            ObjectId numberLayerId;
            bool hasNumberLayer = TryGetExistingLayerId(tr, GGE_NUM_LAYER, out numberLayerId);

            var cleanupScopes = BuildNumberCleanupScopes(tr, scopeIds);
            if (cleanupScopes.Count == 0)
                return 0;
            var handleCleanupIds = restrictHandleCleanupToScope ? new HashSet<ObjectId>(scopeIds) : null;

            var referencedTextIds = new HashSet<ObjectId>();
            var staleHandleIds = new List<ObjectId>();
            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

            foreach (ObjectId id in modelSpace)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (ent == null || ent.IsErased)
                    continue;

                string handleStr = GetEntityTextHandle(ent);
                if (string.IsNullOrEmpty(handleStr))
                    continue;

                if (!long.TryParse(handleStr, System.Globalization.NumberStyles.HexNumber, null, out long handleLong))
                {
                    staleHandleIds.Add(id);
                    continue;
                }

                try
                {
                    ObjectId textId = db.GetObjectId(false, new Handle(handleLong), 0);
                    if (textId.IsNull || !textId.IsValid)
                    {
                        staleHandleIds.Add(id);
                        continue;
                    }

                    DBText text = tr.GetObject(textId, OpenMode.ForRead, false) as DBText;
                    if (text == null || text.IsErased)
                    {
                        staleHandleIds.Add(id);
                        continue;
                    }

                    if (!hasNumberLayer || text.LayerId != numberLayerId)
                    {
                        staleHandleIds.Add(id);
                        continue;
                    }

                    referencedTextIds.Add(textId);
                }
                catch
                {
                    staleHandleIds.Add(id);
                }
            }

            foreach (ObjectId entityId in staleHandleIds)
            {
                if (handleCleanupIds != null && !handleCleanupIds.Contains(entityId))
                    continue;
                Entity ent = tr.GetObject(entityId, OpenMode.ForWrite, false) as Entity;
                if (ent == null || ent.IsErased)
                    continue;

                SetEntityTextHandle(ent, null, tr);
            }

            if (!hasNumberLayer)
                return 0;

            int removedCount = 0;
            foreach (ObjectId id in modelSpace)
            {
                DBText text = tr.GetObject(id, OpenMode.ForRead, false) as DBText;
                if (text == null || text.IsErased)
                    continue;
                if (text.LayerId != numberLayerId)
                    continue;
                if (referencedTextIds.Contains(id))
                    continue;
                if (!IsPointInAnyNumberCleanupScope(text.Position, cleanupScopes, 1.0))
                    continue;

                text.UpgradeOpen();
                text.Erase();
                removedCount++;
            }

            return removedCount;
        }

        private List<Extents3d> BuildNumberCleanupScopes(Transaction tr, IEnumerable<ObjectId> scopeIds)
        {
            var scopes = new List<Extents3d>();
            if (scopeIds == null)
                return scopes;

            foreach (ObjectId id in scopeIds)
            {
                try
                {
                    Entity ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (ent == null || ent.IsErased)
                        continue;
                    scopes.Add(ent.GeometricExtents);
                }
                catch
                {
                }
            }
            return scopes;
        }

        private bool IsPointInAnyNumberCleanupScope(Point3d point, List<Extents3d> scopes, double tolerance)
        {
            for (int i = 0; i < scopes.Count; i++)
            {
                Extents3d ext = scopes[i];
                if (point.X >= ext.MinPoint.X - tolerance && point.X <= ext.MaxPoint.X + tolerance &&
                    point.Y >= ext.MinPoint.Y - tolerance && point.Y <= ext.MaxPoint.Y + tolerance)
                    return true;
            }
            return false;
        }

        private void UpdateEntityText(Entity ent, string numStr, Transaction tr)
        {
            var center = GetRegionCenter(ent);
            string handleStr = GetEntityTextHandle(ent);
            bool textUpdated = false;

            if (!string.IsNullOrEmpty(handleStr) && long.TryParse(handleStr, System.Globalization.NumberStyles.HexNumber, null, out long handleLong))
            {
                try
                {
                    var id = db.GetObjectId(false, new Handle(handleLong), 0);
                    if (!id.IsNull && id.IsValid)
                    {
                        var text = tr.GetObject(id, OpenMode.ForRead) as DBText;
                        if (text != null && !text.IsErased)
                        {
                            text.UpgradeOpen();
                            text.TextString = numStr;
                            textUpdated = true;
                        }
                    }
                }
                catch { }
            }

            if (!textUpdated)
            {
                ObjectId newTextId = CreateNumberText(center, numStr, tr);
                string newHandle = newTextId.Handle.ToString();
                SetEntityTextHandle(ent, newHandle, tr);
            }
        }

        private string[] ParseCSVLine(string line)
        {
            if (line == null) return new string[0];
            char delimiter = DetectCsvDelimiter(line);
            var fields = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                }
                else if (ch == delimiter && !quoted)
                {
                    fields.Add(NormalizeCsvField(field.ToString()));
                    field.Clear();
                }
                else
                {
                    field.Append(ch);
                }
            }
            fields.Add(NormalizeCsvField(field.ToString()));
            return fields.ToArray();
        }

        private static char DetectCsvDelimiter(string line)
        {
            int comma = CountCsvDelimiter(line, ',');
            int semicolon = CountCsvDelimiter(line, ';');
            int tab = CountCsvDelimiter(line, '\t');
            if (tab >= comma && tab >= semicolon && tab > 0) return '\t';
            if (semicolon > comma) return ';';
            return ',';
        }

        private static int CountCsvDelimiter(string line, char delimiter)
        {
            int count = 0;
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"') i++;
                    else quoted = !quoted;
                }
                else if (line[i] == delimiter && !quoted) count++;
            }
            return count;
        }

        private static string NormalizeCsvField(string value)
        {
            if (value == null) return "";
            return value.Trim().Trim('\uFEFF').Trim('\u200B').Trim();
        }

        private static string[] ReadCsvLinesWithExcelEncoding(string filename)
        {
            byte[] bytes = File.ReadAllBytes(filename);
            Encoding encoding = Encoding.UTF8;
            int offset = 0;
            if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 &&
                bytes[2] == 0xFE && bytes[3] == 0xFF)
            {
                encoding = Encoding.BigEndianUnicode;
                offset = 4;
            }
            else if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE &&
                     bytes[2] == 0x00 && bytes[3] == 0x00)
            {
                encoding = new UTF32Encoding(false, true);
                offset = 4;
            }
            else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encoding = new UTF8Encoding(false, true);
                offset = 3;
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encoding = Encoding.Unicode;
                offset = 2;
            }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encoding = Encoding.BigEndianUnicode;
                offset = 2;
            }
            else
            {
                // Excel 的“CSV（逗号分隔）”在部分旧版本/本地化系统中会使用 ANSI。
                // 先尝试 UTF-8 严格解码，失败时使用当前 Windows ANSI 代码页。
                try
                {
                    encoding = new UTF8Encoding(false, true);
                    string utf8 = encoding.GetString(bytes);
                    return SplitCsvLines(utf8);
                }
                catch (DecoderFallbackException)
                {
                    encoding = Encoding.Default;
                }
            }

            string content = encoding.GetString(bytes, offset, bytes.Length - offset);
            return SplitCsvLines(content);
        }

        private static string[] SplitCsvLines(string content)
        {
            if (string.IsNullOrEmpty(content)) return new string[0];
            return content.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        // 从编号中提取根区域编号（如 "101_01" -> "101"，"102" -> "102"）
        private string GetRootNumber(string num)
        {
            return num.Contains("_") ? num.Substring(0, num.IndexOf('_')) : num;
        }

        // ---------- HNM 专用辅助类 ----------

        private class EntityInfo
        {
            public ObjectId EntityId { get; set; }
            public string ObjType { get; set; }
            public Point3d Center { get; set; }
            public double Area { get; set; }
        }

        // ---------- HNM 专用辅助方法 ----------

        private string IntToTwoDigit(int num)
        {
            return num < 10 ? $"0{num}" : num.ToString();
        }

        private List<EntityInfo> SortByPosition(List<EntityInfo> list)
        {
            if (list.Count <= 1) return list;

            // 先按X值排序
            var sortedByX = list.OrderBy(e => e.Center.X).ToList();

            // 分组阈值
            double threshold = PositionGroupThreshold;

            // 按X值相近分组（X值差异在2mm以内的为一组）
            var groups = new List<List<EntityInfo>>();
            var currentGroup = new List<EntityInfo> { sortedByX[0] };

            for (int i = 1; i < sortedByX.Count; i++)
            {
                double prevX = currentGroup[0].Center.X;
                double currX = sortedByX[i].Center.X;

                if (currX - prevX <= threshold)
                {
                    currentGroup.Add(sortedByX[i]);
                }
                else
                {
                    groups.Add(currentGroup);
                    currentGroup = new List<EntityInfo> { sortedByX[i] };
                }
            }
            groups.Add(currentGroup);

            // 每组内按Y值排序（Y降序，从上到下）
            var result = new List<EntityInfo>();
            foreach (var group in groups)
            {
                var sortedGroup = group.OrderByDescending(e => e.Center.Y).ToList();
                result.AddRange(sortedGroup);
            }

            return result;
        }

        private void CollectAllDescendants(
            ObjectId parentId,
            Dictionary<ObjectId, List<ObjectId>> childrenMap,
            List<ObjectId> descendants,
            HashSet<ObjectId> visited,
            int depth = 0)
        {
            const int maxDepth = 50;
            if (depth > maxDepth || !childrenMap.TryGetValue(parentId, out var childIds))
                return;

            foreach (ObjectId childId in childIds)
            {
                if (!visited.Add(childId))
                    continue;
                descendants.Add(childId);
                CollectAllDescendants(childId, childrenMap, descendants, visited, depth + 1);
            }
        }

        // ---------- 命令实现 ----------



        [CommandMethod("HNM")]
        public void HNM()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            db = doc.Database;
            ed = doc.Editor;

            var oldCmdecho = Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("CMDECHO");
            Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("CMDECHO", 0);

            try
            {
                // 获取起始编号
                PromptIntegerOptions optStartNum = new PromptIntegerOptions("\n请输入起始编号 <1>: ");
                optStartNum.AllowNegative = false;
                optStartNum.AllowZero = false;
                optStartNum.DefaultValue = 1;
                PromptIntegerResult resStartNum = ed.GetInteger(optStartNum);
                if (resStartNum.Status == PromptStatus.Cancel)
                    return;
                if (resStartNum.Status != PromptStatus.OK && resStartNum.Status != PromptStatus.None)
                    return;
                int startNum = (resStartNum.Status == PromptStatus.OK) ? resStartNum.Value : 1;

                // 选择闭合区域
                PromptSelectionOptions selOpts = new PromptSelectionOptions();
                selOpts.MessageForAdding = "\n选择所有要编号的闭合区域（包括外层和内层）: ";
                SelectionFilter filter = new SelectionFilter(new[] {
                    new TypedValue(0, "LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE,SPLINE")
                });
                PromptSelectionResult selRes = ed.GetSelection(selOpts, filter);

                if (selRes.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n未选择任何区域。");
                    return;
                }

                // 第一步：只读取旧编号；清除和重编号在同一事务内完成，避免中途失败丢失编号。
                ObjectId[] ids = selRes.Value.GetObjectIds();
                int clearedCount = 0;
                var oldNums = new Dictionary<ObjectId, string>();
                var hclnClearedIds = new HashSet<ObjectId>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in ids)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent != null)
                        {
                            var num = GetEntityNumber(ent);
                            var hclnCleared = GetXDataString(ent, HCLN_CLEARED_APP);
                            if (hclnCleared == "1")
                            {
                                hclnClearedIds.Add(id);
                                if (!string.IsNullOrEmpty(num))
                                    clearedCount++;
                            }
                            else if (!string.IsNullOrEmpty(num))
                            {
                                // 记录旧编号（根区域编号，不含"_"的）
                                if (!num.Contains("_"))
                                    oldNums[id] = num;
                                clearedCount++;
                            }
                        }
                    }
                    tr.Commit();
                }
                int orphanTextCount = 0;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    EnsureRegAppsRegistered(tr, new[]
                    {
                        GGE_NUM_APP,
                        GGE_TEXT_HANDLE_APP,
                        GGE_ISLEAF_APP,
                        HCLN_CLEARED_APP
                    });

                    // 收集实体信息 + 统计类型（合并为单次遍历）
                    List<EntityInfo> entsInfo = new List<EntityInfo>();
                    var typeCounts = new Dictionary<string, int>();

                    for (int i = 0; i < ids.Length; i++)
                    {
                        Entity ent = tr.GetObject(ids[i], OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        // 过滤掉生成的圆（半径 0.01~0.1mm），与其他命令判定范围保持一致
                        if (IsCircleInDeleteRange(ent)) continue;
                        if (!(ent is Curve regionCurve) || !IsCurveClosed(regionCurve)) continue;

                        string objType = ent.GetType().Name;
                        string typeNameCn = MapTypeNameToChinese(objType);
                        if (typeCounts.ContainsKey(typeNameCn))
                            typeCounts[typeNameCn]++;
                        else
                            typeCounts[typeNameCn] = 1;

                        Point3d center = GetRegionCenter(ent);
                        double area = GetArea(ent);
                        entsInfo.Add(new EntityInfo
                        {
                            EntityId = ids[i],
                            ObjType = objType,
                            Center = center,
                            Area = area
                        });
                    }

                    SelectionCompleted?.Invoke(typeCounts, ids.Length);

                    if (entsInfo.Count == 0)
                    {
                        ed.WriteMessage("\n没有有效的区域可编号。");
                        tr.Commit();
                        return;
                    }

                    foreach (ObjectId id in ids)
                    {
                        // 先只读打开并检查是否有编号需要清除，避免对所有选中实体做无谓的写升级
                        Entity numberedEntity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (numberedEntity == null || numberedEntity.IsErased)
                            continue;
                        string number = GetEntityNumber(numberedEntity);
                        if (!string.IsNullOrEmpty(number) || hclnClearedIds.Contains(id))
                            DeleteEntityNumberCompletely(numberedEntity, tr);
                    }

                    // 预建 EntityInfo 查找字典
                    var entsInfoMap = new Dictionary<ObjectId, EntityInfo>();
                    foreach (var info in entsInfo)
                        entsInfoMap[info.EntityId] = info;

                    // ========== 包含关系分析（新版鲁棒判定） ==========

                    // 按面积从大到小排序
                    entsInfo = entsInfo.OrderByDescending(e => e.Area).ToList();

                    // 预先获取所有实体的边界框和实体对象缓存
                    var entExtents = new Dictionary<ObjectId, Extents3d>();
                    var entityCache = new Dictionary<ObjectId, Entity>();
                    foreach (var e in entsInfo)
                    {
                        var ent = tr.GetObject(e.EntityId, OpenMode.ForRead) as Entity;
                        if (ent != null)
                        {
                            entExtents[e.EntityId] = ent.GeometricExtents;
                            entityCache[e.EntityId] = ent;
                        }
                    }

                    Dictionary<ObjectId, ObjectId> parentMap = new Dictionary<ObjectId, ObjectId>();

                    // 批量包含分析期间复用边界采样与包含判定器缓存：
                    // 同一曲线只采样/构建一次，避免对每个候选父子对重复采样。
                    var samplesCache = new Dictionary<ObjectId, List<Point2d>>();
                    var containmentCache = new Dictionary<ObjectId, CurveContainment>();

                    for (int i = 0; i < entsInfo.Count; i++)
                    {
                        ObjectId aEntId = entsInfo[i].EntityId;
                        var aExt = entExtents[aEntId];
                        if (!entityCache.TryGetValue(aEntId, out var aEntity) || !(aEntity is Curve aCurve))
                        {
                            parentMap[aEntId] = ObjectId.Null;
                            continue;
                        }
                        ObjectId parentId = ObjectId.Null;
                        double minParentArea = double.MaxValue;

                        // 只检查面积比自己大的区域（j < i 因为已按面积降序排列）
                        for (int j = 0; j < i; j++)
                        {
                            ObjectId bEntId = entsInfo[j].EntityId;
                            var bExt = entExtents[bEntId];

                            // 优化：先检查边界框是否重叠
                            if (aExt.MaxPoint.X < bExt.MinPoint.X || aExt.MinPoint.X > bExt.MaxPoint.X ||
                                aExt.MaxPoint.Y < bExt.MinPoint.Y || aExt.MinPoint.Y > bExt.MaxPoint.Y)
                            {
                                continue;
                            }

                            Entity bEnt;
                            if (!entityCache.TryGetValue(bEntId, out bEnt) || bEnt == null) continue;

                            if (bEnt is Curve bCurve && IsFullyContained(aCurve, bCurve, samplesCache, containmentCache))
                            {
                                double candidateArea = entsInfo[j].Area;
                                if (candidateArea < minParentArea)
                                {
                                    minParentArea = candidateArea;
                                    parentId = bEntId;
                                }
                            }
                        }
                        parentMap[aEntId] = parentId;
                    }

                    // 已经拥有简单父级编号的区域保持为根，除非 HCLN 已将其标记清除。
                    // 这样后续扩大选择范围时，既有父级编号不会被新出现的外框覆盖。
                    foreach (ObjectId anchoredRoot in oldNums.Keys)
                    {
                        if (parentMap.ContainsKey(anchoredRoot))
                            parentMap[anchoredRoot] = ObjectId.Null;
                    }

                    // ========== 检测并修正循环包含 ==========
                    bool adjusted = true;
                    int cycleSafety = 0;
                    while (adjusted && cycleSafety < 100)
                    {
                        adjusted = false;
                        cycleSafety++;
                        foreach (var item in parentMap.ToList())
                        {
                            ObjectId child = item.Key;
                            ObjectId parent = item.Value;
                            if (parent == ObjectId.Null) continue;

                            var visited = new HashSet<ObjectId> { child };
                            ObjectId current = parent;
                            bool hasCycle = false;
                            int depth = 0;
                            while (parentMap.ContainsKey(current) && parentMap[current] != ObjectId.Null && depth < 50)
                            {
                                if (current == child || !visited.Add(current))
                                {
                                    hasCycle = true;
                                    break;
                                }
                                current = parentMap[current];
                                depth++;
                            }
                            if (!hasCycle && current == child) hasCycle = true;

                            if (hasCycle)
                            {
                                // 在环中找到面积最小的节点，断开其父链接（让它成为根）
                                var cycleNodes = new List<ObjectId>();
                                ObjectId c = child;
                                int cd = 0;
                                while (cd < 50)
                                {
                                    cycleNodes.Add(c);
                                    if (!parentMap.ContainsKey(c) || parentMap[c] == ObjectId.Null) break;
                                    c = parentMap[c];
                                    if (c == child) break;
                                    cd++;
                                }
                                ObjectId largestNode = child;
                                double largestArea = double.MinValue;
                                foreach (var node in cycleNodes)
                                {
                                    EntityInfo info;
                                    if (entsInfoMap.TryGetValue(node, out info) && info.Area > largestArea)
                                    {
                                        largestArea = info.Area;
                                        largestNode = node;
                                    }
                                }
                                parentMap[largestNode] = ObjectId.Null;
                                adjusted = true;
                                break;
                            }
                        }
                    }

                    // ========== 构建子节点映射 ==========
                    Dictionary<ObjectId, List<ObjectId>> childrenMap = new Dictionary<ObjectId, List<ObjectId>>();
                    foreach (var item in parentMap)
                    {
                        ObjectId child = item.Key;
                        ObjectId parent = item.Value;

                        if (parent != ObjectId.Null)
                        {
                            if (!childrenMap.ContainsKey(parent))
                                childrenMap[parent] = new List<ObjectId>();
                            childrenMap[parent].Add(child);
                        }
                    }

                    // ========== 检查是否有嵌套关系 ==========
                    bool hasNesting = parentMap.Any(p => p.Value != ObjectId.Null);
                    if (!hasNesting)
                    {
                        ed.WriteMessage("\n[注意] 没有嵌套关系，所选区域不编号。");
                    }

                    // ========== 扁平化编号（CollectAllDescendants） ==========
                    // 找出根节点（无父级）并按位置排序
                    List<ObjectId> allRoots = parentMap.Where(p => p.Value == ObjectId.Null).Select(p => p.Key).ToList();
                    List<EntityInfo> allRootInfo = allRoots
                        .Select(r => { EntityInfo info; entsInfoMap.TryGetValue(r, out info); return info; })
                        .Where(e => e != null)
                        .ToList();
                    allRootInfo = SortByPosition(allRootInfo);
                    List<ObjectId> rootsSorted = allRootInfo.Select(e => e.EntityId).ToList();

                    var numberableRoots = rootsSorted
                        .Where(r => childrenMap.ContainsKey(r) && childrenMap[r].Count > 0)
                        .ToList();
                    int skippedRootCount = rootsSorted.Count - numberableRoots.Count;
                    if (numberableRoots.Count == 0)
                        ed.WriteMessage("\n[注意] 所选根区域都没有子区域，本次不生成编号。");

                    var usedNums = new HashSet<string>();
                    int nextNewNum = startNum;
                    int leafCount = 0;
                    int numberedCount = 0;
                    var numberedEntities = new HashSet<ObjectId>();

                    foreach (ObjectId root in numberableRoots)
                    {
                        string rootNum;
                        if (oldNums.TryGetValue(root, out string oldNum) && !usedNums.Contains(oldNum))
                        {
                            rootNum = oldNum;
                        }
                        else
                        {
                            while (usedNums.Contains(nextNewNum.ToString())) nextNewNum++;
                            rootNum = nextNewNum.ToString();
                            nextNewNum++;
                        }
                        usedNums.Add(rootNum);

                        var rootEntity = tr.GetObject(root, OpenMode.ForWrite) as Entity;
                        if (rootEntity == null || !numberedEntities.Add(root))
                            continue;

                        SetEntityNumber(rootEntity, rootNum, tr);
                        numberedCount++;

                        var descendants = new List<ObjectId>();
                        CollectAllDescendants(root, childrenMap, descendants, numberedEntities);
                        var sortedDescendants = SortByPosition(descendants
                            .Select(id => entsInfoMap.TryGetValue(id, out var info) ? info : null)
                            .Where(info => info != null)
                            .ToList());

                        if (sortedDescendants.Count == 0)
                        {
                            SetXDataString(rootEntity, GGE_ISLEAF_APP, "1", tr);
                            UpdateEntityText(rootEntity, rootNum, tr);
                            leafCount++;
                            continue;
                        }

                        SetXDataString(rootEntity, GGE_ISLEAF_APP, "0", tr);
                        DeleteEntityText(rootEntity, tr);

                        for (int index = 0; index < sortedDescendants.Count; index++)
                        {
                            var descendantInfo = sortedDescendants[index];
                            var descendant = tr.GetObject(descendantInfo.EntityId, OpenMode.ForWrite) as Entity;
                            if (descendant == null)
                                continue;

                            string childNumber = $"{rootNum}_{IntToTwoDigit(index + 1)}";
                            SetEntityNumber(descendant, childNumber, tr);
                            numberedCount++;

                            bool isLeaf = !childrenMap.TryGetValue(descendantInfo.EntityId, out var children)
                                || children.Count == 0;
                            SetXDataString(descendant, GGE_ISLEAF_APP, isLeaf ? "1" : "0", tr);
                            if (isLeaf)
                            {
                                UpdateEntityText(descendant, childNumber, tr);
                                leafCount++;
                            }
                            else
                            {
                                DeleteEntityText(descendant, tr);
                            }
                        }
                    }

                    ed.WriteMessage($"\nHNM：编号 {numberedCount} 个区域（叶子 {leafCount} 个），根区域 {numberableRoots.Count} 个，跳过 {skippedRootCount} 个无子区域根区域。");

                    // 仅在编号流程成功完成后消费 HCLN 清除标记。
                    foreach (ObjectId clearedId in hclnClearedIds)
                    {
                        if (!entsInfoMap.ContainsKey(clearedId))
                            continue;
                        var clearedEntity = tr.GetObject(clearedId, OpenMode.ForWrite) as Entity;
                        if (clearedEntity != null)
                            SetXDataString(clearedEntity, HCLN_CLEARED_APP, "0", tr);
                    }

                    // ========== 触发编号完成事件 ==========
                    NumberingCompleted?.Invoke(leafCount);

                    tr.Commit();
                }

                if (clearedCount > 0)
                    ed.WriteMessage($"\nHNM：重新处理 {clearedCount} 个已清理区域。");
                using (var trCleanup = db.TransactionManager.StartTransaction())
                {
                    orphanTextCount = CleanupOrphanNumberTexts(trCleanup, ids);
                    trCleanup.Commit();
                }
                if (orphanTextCount > 0)
                    ed.WriteMessage($"\nHNM：清理 {orphanTextCount} 个残留编号文本。");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n错误: {ex.Message}");
            }
            finally
            {
                Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("CMDECHO", oldCmdecho);
            }
        }



        [CommandMethod("HGR")]
        public void HGR()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            db = doc.Database;
            ed = doc.Editor;

            try
            {
                var filter = new SelectionFilter(new TypedValue[] { new TypedValue(0, REGION_FILTER) });
                var selRes = ed.GetSelection(filter);
                if (selRes.Status != PromptStatus.OK) return;

                // 间距输入 + R切换方向（有网点时自动检测方向，无网点时使用手动设置的方向）
                double spacing;
                string dirLabel = _hgrYDirection ? "Y" : "X";
                while (true)
                {
                    var strOpts = new PromptStringOptions(
                        $"\n输入每个区域内圆的间距(mm) 或[R切换方向] (当前:{dirLabel}方向, 圆直径{_currentDiameter}mm, 默认{CurrentMinSpacing:F1}mm): ");
                    strOpts.AllowSpaces = false;
                    strOpts.DefaultValue = CurrentMinSpacing.ToString("F1");
                    strOpts.UseDefaultValue = true;
                    var strRes = ed.GetString(strOpts);
                    if (strRes.Status != PromptStatus.OK) return;

                    string input = (strRes.StringResult ?? "").Trim();

                    if (input.Equals("R", StringComparison.OrdinalIgnoreCase))
                    {
                        var dirOpts = new PromptKeywordOptions("\n选择蜂窝排列方向 [X/Y]: ");
                        dirOpts.Keywords.Add("X");
                        dirOpts.Keywords.Add("Y");
                        dirOpts.AllowNone = false;
                        var dirRes = ed.GetKeywords(dirOpts);
                        if (dirRes.Status == PromptStatus.OK)
                        {
                            _hgrYDirection = dirRes.StringResult == "Y";
                            dirLabel = dirRes.StringResult;
                        }
                        continue;
                    }

                    if (double.TryParse(input, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double val) && IsFinite(val) && val > 0)
                    {
                        spacing = val;
                        break;
                    }

                    ed.WriteMessage("\n无效输入，请输入正数数值或输入 R 切换方向。");
                }

                // 间距下限：max(Diameter, CurrentMinSpacing)
                double minSpacing = CurrentMinSpacing;
                if (spacing < minSpacing)
                {
                    spacing = minSpacing;
                    ed.WriteMessage("\n[提示] 间距小于最小允许值，已调整为 " + spacing + "mm");
                }

                int totalCircles = 0;
                int regionCount = 0;
                int skippedCircles = 0;
                int failedRegions = 0;
                double lastGenDiameter = 0;
                CircleSpatialIndex circleIndex = CollectAllCirclesInRadiusRange();

                foreach (ObjectId id in selRes.Value.GetObjectIds())
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null) continue;

                            // 过滤掉圆判定范围内的圆（半径 0.01~0.1mm）
                            if (IsCircleInDeleteRange(ent))
                            {
                                skippedCircles++;
                                continue;
                            }

                            if (!(ent is Curve curve) || !IsCurveClosed(curve))
                            {
                                continue;
                            }


                            // 先收集区域内现有圆的圆心，用于检测排列方向
                            var existingCircles = CollectCircleRecordsInRegion(circleIndex, ent.GeometricExtents, curve);
                            bool useYDirection = _hgrYDirection; // 默认使用手动设置的方向
                            if (existingCircles.Count >= 3)
                            {
                                var centers = existingCircles.Select(c => Point3dToPoint2d(c.Center)).ToList();
                                useYDirection = DetectHoneycombDirection(centers);
                            }

                            // 检测现有圆的半径，用于生成新圆时保持直径一致
                            double genRadius = DetectCircleRadius(existingCircles);
                            lastGenDiameter = genRadius * 2;

                            // 物理下限：间距不得小于会话级最小间距 (HDIA 控制)
                            double effectiveSpacing = spacing;
                            if (effectiveSpacing < CurrentMinSpacing)
                                effectiveSpacing = CurrentMinSpacing;

                            int deletedCount;
                            int actualCnt;
                            List<CircleRecord> removedCircles;
                            List<CircleRecord> createdCircles;
                            if (!TryReplaceCirclesWithGrid(
                                ent, tr, effectiveSpacing, useYDirection, genRadius, circleIndex, existingCircles,
                                out deletedCount, out actualCnt,
                                out removedCircles, out createdCircles))
                            {
                                ed.WriteMessage($"\n[警告] 当前区域无法按目标间距生成圆，已保留并标记 {removedCircles.Count} 个旧圆。");
                                failedRegions++;
                                tr.Commit();
                                continue;
                            }

                            tr.Commit();
                            regionCount++;
                            circleIndex.ApplyReplacement(removedCircles, createdCircles);
                            totalCircles += actualCnt;
                        }
                        catch (OperationCanceledException ex)
                        {
                            tr.Abort();
                            ed.WriteMessage("\nHGR：" + ex.Message);
                            return;
                        }
                        catch (Exception ex)
                        {
                            tr.Abort();
                            failedRegions++;
                            ed.WriteMessage("\nHGR：当前区域失败，旧圆已回滚保留：" + ex.Message);
                        }
                    }
                }

                ed.WriteMessage($"\nHGR：处理 {regionCount} 个区域，生成 {totalCircles} 个圆，失败 {failedRegions} 个区域，忽略 {skippedCircles} 个非目标圆；间距 {spacing}mm，圆直径 {lastGenDiameter:F4}mm。");
            }
            catch (Exception ex)
            {
                ed.WriteMessage("\n错误: " + ex.Message);
            }
        }
        // HRS - 密度调整命令
        // ================================================================
        // 功能：
        // 1. 选择一个或多个闭合区域（支持框选聚合线和圆形）
        // 2. 输入密度调整百分比（正数增加圆数量 / 负数减少）
        // 3. 读取区域内已有圆的平均最近邻间距
        // 4. 按公式 新间距 = 原间距 × √(100/(100+P)) 计算新间距（圆数量变化 = 1+P/100）
        // 5. 删除原圆，按新间距生成严格六边形网格圆
        // ================================================================
        // 统一参数：
        //   - 圆判定：半径 0.01~0.1mm（直径 0.02~0.2mm），不检查编号
        //   - 选择对象：LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE,SPLINE
        //   - 搜索范围：按区域宽高 1 倍扩展
        //   - 删除判定：仅圆心在区域内（射线法）
        //   - 网格生成：严格居中六角网格（内缩 Radius = 0.03mm），仅射线法过滤
        //   - 间距下限：max(Diameter, MinCircleSpacing) = 0.1mm
        //   - 生成圆半径：Radius = 0.03mm（直径 0.06mm）
        //   - 少于2个圆的区域跳过
        // ================================================================

        [CommandMethod("HRS")]
        public void HRS()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            db = doc.Database;
            ed = doc.Editor;

            try
            {
                // 选择闭合区域（统一过滤器，程序会自动过滤掉圆判定范围内的圆）
                var filter = new SelectionFilter(new TypedValue[] {
                    new TypedValue(0, REGION_FILTER)
                });
                var selRes = ed.GetSelection(filter);
                if (selRes.Status != PromptStatus.OK) return;

                // 输入百分比（正数=亮度比<1→更密，负数=亮度比>1→更疏）
                var intOpts = new PromptIntegerOptions("\n输入百分比调整密度 (正数增加圆数量，负数减少): ");
                intOpts.AllowNegative = true;
                intOpts.AllowZero = true;
                intOpts.UseDefaultValue = true;
                intOpts.DefaultValue = 10;
                var intRes = ed.GetInteger(intOpts);
                if (intRes.Status != PromptStatus.OK) return;
                int percent = intRes.Value;

                if (percent <= -100)
                {
                    ed.WriteMessage($"\n百分比 {percent}% 将清除所有圆（不生成新圆）");
                }

                int skippedCircles;
                ObjectId[] regionIds = CollectSelectedClosedRegionIds(
                    selRes.Value.GetObjectIds(), out skippedCircles, skipParentRegions: true).ToArray();
                int totalRegions = regionIds.Length;
                int totalOriginalCircles = 0;
                int totalNewCircles = 0;
                int skippedRegions = 0;
                int processedRegions = 0;
                int failedRegions = 0;
                if (totalRegions == 0)
                {
                    ed.WriteMessage("\n未找到可处理的闭合区域。");
                    return;
                }
                CircleSpatialIndex circleIndex = CollectAllCirclesInRadiusRange();

                for (int i = 0; i < regionIds.Length; i++)
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            var ent = tr.GetObject(regionIds[i], OpenMode.ForRead) as Entity;
                            if (ent == null) { tr.Commit(); continue; }

                            // 必须是闭合曲线
                            if (!(ent is Curve curve) || !IsCurveClosed(curve))
                            {
                                tr.Commit(); continue;
                            }

                            // ── 收集区域内符合条件的圆（半径 0.01~0.1mm，不检查编号，射线法判定）──
                            var circleData = CollectCircleRecordsInRegion(circleIndex, ent.GeometricExtents, curve);
                            var circleCenters = circleData.Select(d => Point3dToPoint2d(d.Center)).ToList();

                            int insideCount = circleCenters.Count;

                            // 少于 2 个圆时：0 个跳过，1 个则视方向决定
                            if (insideCount == 0)
                            {
                                skippedRegions++;
                                tr.Commit();
                                continue;
                            }

                            if (insideCount == 1)
                            {
                                if (percent < 0 && percent > -100)
                                {
                                    skippedRegions++;
                                    tr.Commit();
                                    continue;
                                }
                            }


                            // ── 计算原始间距 ──
                            double originalSpacing;
                            if (insideCount >= 2)
                            {
                                originalSpacing = CalculateAverageNearestNeighborSpacing(circleCenters);
                            }
                            else
                            {
                                // insideCount == 1：取圆心到聚合线最远端点的距离作为虚拟间距
                                var cp = circleCenters[0];
                                double maxDist = 0;
                                if (curve is Polyline pl)
                                {
                                    for (int vi = 0; vi < pl.NumberOfVertices; vi++)
                                    {
                                        var vtx = pl.GetPoint3dAt(vi);
                                        double dist = Point2dDistance(cp, Point3dToPoint2d(vtx));
                                        if (dist > maxDist) maxDist = dist;
                                    }
                                }
                                originalSpacing = maxDist > 0 ? maxDist : 0.09;
                            }
                            if (originalSpacing <= 0)
                            {
                                originalSpacing = 0.09;
                            }

                            // 检测已有圆的蜂窝排列方向
                            bool hrsYDirection = insideCount >= 3 && DetectHoneycombDirection(circleCenters);

                            // 检测现有圆的半径，用于生成新圆时保持直径一致
                            double genRadius = DetectCircleRadius(circleData);

                            if (percent <= -100)
                            {
                                EraseCircleRecords(circleData, tr);
                                tr.Commit();
                                processedRegions++;
                                totalOriginalCircles += insideCount;
                                circleIndex.Remove(circleData);
                                if ((i + 1) % 20 == 0)
                                    ed.UpdateScreen();
                                continue;
                            }

                            // ── 计算亮度比：统一公式 ratio = 100/(100+P) ──
                            // P正数→ratio<1→间距缩小(更密)  P负数→ratio>1→间距放大(更疏)
                            // 圆数量变化倍率 = 1/ratio = 1+P/100（直观对称）
                            double ratio = 100.0 / (100.0 + percent);
                            double newSpacing = originalSpacing * Math.Sqrt(ratio);

                            // 物理下限：间距不得小于会话级最小间距 (HDIA 控制)
                            double minSpacing = CurrentMinSpacing;
                            if (newSpacing < minSpacing)
                            {
                                newSpacing = minSpacing;
                                ed.WriteMessage($"\n警告：新间距小于最小允许值，已调整为 {newSpacing:F4} mm");
                            }

                            int deletedCount;
                            int newCount;
                            List<CircleRecord> removedCircles;
                            List<CircleRecord> createdCircles;
                            if (!TryReplaceCirclesWithGrid(
                                ent, tr, newSpacing, hrsYDirection, genRadius, circleIndex, circleData,
                                out deletedCount, out newCount,
                                out removedCircles, out createdCircles))
                            {
                                failedRegions++;
                                tr.Commit();
                                continue;
                            }

                            tr.Commit();
                            processedRegions++;
                            totalOriginalCircles += insideCount;
                            totalNewCircles += newCount;
                            circleIndex.ApplyReplacement(removedCircles, createdCircles);
                            if ((i + 1) % 20 == 0)
                                ed.UpdateScreen();
                        }
                        catch (OperationCanceledException ex)
                        {
                            tr.Abort();
                            ed.WriteMessage("\nHRS：" + ex.Message);
                            return;
                        }
                        catch (Exception ex)
                        {
                            failedRegions++;
                            ed.WriteMessage($"\n  [处理第{i + 1}个区域异常: {ex.Message}]");
                            try { tr.Abort(); } catch (Exception abortEx) { ed?.WriteMessage("\n[警告] 事务回滚失败: " + abortEx.Message); }
                        }
                    }
                }
                ed.UpdateScreen();

                ed.WriteMessage($"\nHRS：处理 {processedRegions} 个区域，圆数量 {totalOriginalCircles}→{totalNewCircles}，跳过 {skippedRegions} 个区域，失败 {failedRegions} 个区域，调整 {percent}%，忽略 {skippedCircles} 个非目标圆。");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n错误: {ex.Message}");
            }
        }

        // HRD - 旋转排列方向命令
        // ================================================================
        // 功能：选中闭合区域，保持间距不变，翻转蜂窝排列方向（X→Y 或 Y→X）
        // ================================================================
        [CommandMethod("HRD")]
        public void HRD()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            db = doc.Database;
            ed = doc.Editor;

            try
            {
                var filter = new SelectionFilter(new TypedValue[] { new TypedValue(0, REGION_FILTER) });
                var selRes = ed.GetSelection(filter);
                if (selRes.Status != PromptStatus.OK) return;

                int skippedCircles;
                ObjectId[] regionIds = CollectSelectedClosedRegionIds(
                    selRes.Value.GetObjectIds(), out skippedCircles).ToArray();
                if (regionIds.Length == 0)
                {
                    ed.WriteMessage("\n未找到可处理的闭合区域。");
                    return;
                }
                int processedRegions = 0;
                int totalOriginalCircles = 0;
                int totalNewCircles = 0;
                int skippedRegions = 0;
                int failedRegions = 0;
                CircleSpatialIndex circleIndex = CollectAllCirclesInRadiusRange();

                foreach (ObjectId id in regionIds)
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null) { tr.Commit(); continue; }
                            if (!(ent is Curve curve) || !IsCurveClosed(curve)) { tr.Commit(); continue; }

                            var circleData = CollectCircleRecordsInRegion(circleIndex, ent.GeometricExtents, curve);
                            var circleCenters = circleData.Select(d => Point3dToPoint2d(d.Center)).ToList();

                            if (circleCenters.Count < 2)
                            {
                                skippedRegions++;
                                tr.Commit();
                                continue;
                            }


                            double spacing = CalculateAverageNearestNeighborSpacing(circleCenters);
                            bool currentYDir = DetectHoneycombDirection(circleCenters);
                            bool newYDir = !currentYDir;

                            // 检测现有圆的半径，用于生成新圆时保持直径一致
                            double genRadius = DetectCircleRadius(circleData);

                            int deletedCount;
                            int newCount;
                            List<CircleRecord> removedCircles;
                            List<CircleRecord> createdCircles;
                            if (!TryReplaceCirclesWithGrid(
                                ent, tr, spacing, newYDir, genRadius, circleIndex, circleData,
                                out deletedCount, out newCount,
                                out removedCircles, out createdCircles))
                            {
                                failedRegions++;
                                ed.WriteMessage($"\n当前区域旋转后无法生成圆，已保留并标记 {removedCircles.Count} 个旧圆。");
                                tr.Commit();
                                continue;
                            }

                            tr.Commit();
                            processedRegions++;
                            totalOriginalCircles += circleCenters.Count;
                            totalNewCircles += newCount;
                            circleIndex.ApplyReplacement(removedCircles, createdCircles);
                        }
                        catch (OperationCanceledException ex)
                        {
                            tr.Abort();
                            ed.WriteMessage("\nHRD：" + ex.Message);
                            return;
                        }
                        catch (Exception ex)
                        {
                            failedRegions++;
                            ed.WriteMessage($"\n处理异常: {ex.Message}");
                            try { tr.Abort(); } catch (Exception abortEx) { ed?.WriteMessage("\n[警告] 事务回滚失败: " + abortEx.Message); }
                        }
                    }
                }
                ed.UpdateScreen();

                ed.WriteMessage($"\nHRD：处理 {processedRegions} 个区域，圆数量 {totalOriginalCircles}→{totalNewCircles}，跳过 {skippedRegions} 个区域，失败 {failedRegions} 个区域，忽略 {skippedCircles} 个非目标圆。");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 计算点集的平均最近邻间距（用于 HRS 命令）
        /// </summary>
        private double CalculateAverageNearestNeighborSpacing(List<Point2d> points)
        {
            int n = points.Count;
            if (n < 2) return 0;
            if (n <= NearestNeighborBruteForceThreshold)
                return CalculateAverageNearestNeighborSpacingBruteForce(points);

            var indexedPoints = new List<NearestPoint>(n);
            for (int i = 0; i < n; i++)
                indexedPoints.Add(new NearestPoint { Point = points[i], Index = i });
            NearestPointNode root = BuildNearestPointTree(indexedPoints, 0);
            double sumDist = 0;
            for (int i = 0; i < n; i++)
            {
                double bestSquared = double.MaxValue;
                FindNearestPoint(root, points[i], i, ref bestSquared);
                if (bestSquared < double.MaxValue)
                    sumDist += Math.Sqrt(bestSquared);
            }

            return sumDist / n;
        }

        private static double CalculateAverageNearestNeighborSpacingBruteForce(List<Point2d> points)
        {
            double sumDistance = 0;
            for (int i = 0; i < points.Count; i++)
            {
                double bestSquared = double.MaxValue;
                for (int j = 0; j < points.Count; j++)
                {
                    if (i == j) continue;
                    double dx = points[i].X - points[j].X;
                    double dy = points[i].Y - points[j].Y;
                    double distanceSquared = dx * dx + dy * dy;
                    if (distanceSquared < bestSquared)
                        bestSquared = distanceSquared;
                }
                sumDistance += Math.Sqrt(bestSquared);
            }
            return sumDistance / points.Count;
        }

        private class NearestPoint
        {
            public Point2d Point;
            public int Index;
        }

        private class NearestPointNode
        {
            public NearestPoint Value;
            public int Axis;
            public NearestPointNode Left;
            public NearestPointNode Right;
        }

        private static NearestPointNode BuildNearestPointTree(List<NearestPoint> points, int depth)
        {
            if (points.Count == 0) return null;
            int axis = depth & 1;
            points.Sort((left, right) => axis == 0
                ? left.Point.X.CompareTo(right.Point.X)
                : left.Point.Y.CompareTo(right.Point.Y));
            int middle = points.Count / 2;
            return new NearestPointNode
            {
                Value = points[middle],
                Axis = axis,
                Left = BuildNearestPointTree(points.GetRange(0, middle), depth + 1),
                Right = BuildNearestPointTree(points.GetRange(middle + 1, points.Count - middle - 1), depth + 1)
            };
        }

        private static void FindNearestPoint(NearestPointNode node, Point2d target, int targetIndex, ref double bestSquared)
        {
            if (node == null) return;
            double dx = target.X - node.Value.Point.X;
            double dy = target.Y - node.Value.Point.Y;
            double distanceSquared = dx * dx + dy * dy;
            if (node.Value.Index != targetIndex && distanceSquared < bestSquared)
                bestSquared = distanceSquared;

            double axisDelta = node.Axis == 0 ? dx : dy;
            NearestPointNode near = axisDelta <= 0 ? node.Left : node.Right;
            NearestPointNode far = axisDelta <= 0 ? node.Right : node.Left;
            FindNearestPoint(near, target, targetIndex, ref bestSquared);
            if (axisDelta * axisDelta < bestSquared)
                FindNearestPoint(far, target, targetIndex, ref bestSquared);
        }

        // ================================================================
        // 统一辅助方法
        // ================================================================

        // ===== 统一辅助方法 =====
        // 圆判定：半径 MinDeleteRadius ~ MaxDeleteRadius（0.01 ~ 0.1mm），不检查编号
        // 搜索范围：区域宽高 1 倍扩展
        // 删除判定：仅圆心在区域内（射线法）
        // 网格生成：GenerateHexGridStrict（内缩 Radius 居中）
        // 间距下限：max(Diameter, MinCircleSpacing) = max(0.06, 0.1) = 0.1mm

        // 统一选择过滤器（LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE,SPLINE）
        private const string REGION_FILTER = "LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE,SPLINE";

        /// <summary>
        /// 收集选择集中的闭合区域 ObjectId。
        /// 跳过圆判定范围内的圆；可选跳过"有编号但非叶子"的父/中间层区域
        /// （无编号的普通区域始终保留，仅过滤编号层级中的父区域）。
        /// </summary>
        private List<ObjectId> CollectSelectedClosedRegionIds(
            ObjectId[] selectedIds, out int skippedCircleCount, bool skipParentRegions = false)
        {
            var regionIds = new List<ObjectId>(selectedIds.Length);
            skippedCircleCount = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                for (int i = 0; i < selectedIds.Length; i++)
                {
                    try
                    {
                        var entity = tr.GetObject(selectedIds[i], OpenMode.ForRead, false) as Entity;
                        if (entity == null) continue;
                        if (IsCircleInDeleteRange(entity))
                        {
                            skippedCircleCount++;
                            continue;
                        }
                        var curve = entity as Curve;
                        if (curve != null && IsCurveClosed(curve))
                        {
                            if (skipParentRegions)
                            {
                                // 有编号但非叶子（H_IsLeaf != "1"）→ 父/中间层区域，跳过，
                                // 避免整块大区域重排造成的卡顿与父/子重复处理。
                                // 无编号的普通区域不受影响，照常处理。
                                string num = GetEntityNumber(entity);
                                if (!string.IsNullOrEmpty(num))
                                {
                                    string isLeaf = GetXDataString(entity, GGE_ISLEAF_APP);
                                    if (isLeaf != "1") continue;
                                }
                            }
                            regionIds.Add(selectedIds[i]);
                        }
                    }
                    catch
                    {
                    }
                }
                tr.Commit();
            }
            return regionIds;
        }

        /// <summary>
        /// 判断实体是否在圆判定范围内（半径 0.01~0.1mm 的圆）
        /// 用于选择对象时过滤掉符合圆判定范围内的圆
        /// </summary>
        private bool IsCircleInDeleteRange(Entity ent)
        {
            if (ent is Circle circle)
            {
                return circle.Radius >= MinDeleteRadius && circle.Radius <= MaxDeleteRadius;
            }
            return false;
        }

        private bool IsEntityVisiblyRed(Entity entity, Transaction tr)
        {
            Autodesk.AutoCAD.Colors.Color color = entity.Color;
            if (entity.ColorIndex == 256)
            {
                LayerTableRecord layer = tr.GetObject(entity.LayerId, OpenMode.ForRead, false) as LayerTableRecord;
                if (layer != null)
                    color = layer.Color;
            }
            if (color != null && color.ColorIndex == 1)
                return true;
            try
            {
                System.Drawing.Color value = color.ColorValue;
                return value.R >= 200 && value.G <= 80 && value.B <= 80;
            }
            catch
            {
                return false;
            }
        }

        private class CurveContainment
        {
            private const double BoundaryTolerance = 1e-7;
            public Extents3d Extents;
            public int Kind;
            public Point2d Center;
            public double Radius;
            public double MajorRadius;
            public double MinorRadius;
            public double CosAngle;
            public double SinAngle;
            public List<Point2d> Boundary;

            public bool Contains(Point2d point)
            {
                if (point.X < Extents.MinPoint.X - BoundaryTolerance ||
                    point.X > Extents.MaxPoint.X + BoundaryTolerance ||
                    point.Y < Extents.MinPoint.Y - BoundaryTolerance ||
                    point.Y > Extents.MaxPoint.Y + BoundaryTolerance)
                    return false;

                if (Kind == 1)
                    return Point2dDistance(point, Center) <= Radius + Tolerance.Global.EqualPoint;
                if (Kind == 2)
                {
                    double dx = point.X - Center.X;
                    double dy = point.Y - Center.Y;
                    double xr = dx * CosAngle + dy * SinAngle;
                    double yr = -dx * SinAngle + dy * CosAngle;
                    return (xr * xr) / (MajorRadius * MajorRadius) +
                           (yr * yr) / (MinorRadius * MinorRadius) <= 1.0 + Tolerance.Global.EqualPoint;
                }
                return PointInVertexLoop(point, Boundary, BoundaryTolerance);
            }
        }

        private CurveContainment BuildCurveContainment(Curve curve)
        {
            var containment = new CurveContainment { Extents = curve.GeometricExtents };
            Circle circle = curve as Circle;
            if (circle != null)
            {
                containment.Kind = 1;
                containment.Center = Point3dToPoint2d(circle.Center);
                containment.Radius = circle.Radius;
                return containment;
            }

            Ellipse ellipse = curve as Ellipse;
            if (ellipse != null)
            {
                containment.Kind = 2;
                containment.Center = Point3dToPoint2d(ellipse.Center);
                containment.MajorRadius = ellipse.MajorRadius;
                containment.MinorRadius = ellipse.MinorRadius;
                double rotation = GetEllipseRotationAngle(ellipse);
                containment.CosAngle = Math.Cos(rotation);
                containment.SinAngle = Math.Sin(rotation);
                return containment;
            }

            containment.Kind = 3;
            containment.Boundary = BuildOrderedBoundary(curve);
            return containment;
        }

        private bool TryReplaceCirclesWithGrid(
            Entity region,
            Transaction tr,
            double spacing,
            bool yDirection,
            double radius,
            CircleSpatialIndex circleIndex,
            List<CircleRecord> knownCircles,
            out int deletedCount,
            out int createdCount,
            out List<CircleRecord> removedCircles,
            out List<CircleRecord> createdCircles)
        {
            deletedCount = 0;
            createdCount = 0;
            removedCircles = knownCircles ?? CollectCircleRecordsInRegion(
                circleIndex, region.GeometricExtents, region as Curve);
            createdCircles = new List<CircleRecord>();
            ValidateCircleRecordsWritable(removedCircles, tr);
            List<Point3d> gridPoints = GenerateHexGridStrict(region, spacing, yDirection, radius);
            if (gridPoints.Count == 0)
            {
                MarkCircleRecordsForGenerationFailure(removedCircles, tr);
                return false;
            }

            deletedCount = EraseCircleRecords(removedCircles, tr);
            BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
            foreach (Point3d point in gridPoints)
            {
                if ((createdCircles.Count & 1023) == 0) CheckGenerationCancelled();
                Circle circle = new Circle(point, Vector3d.ZAxis, radius);
                modelSpace.AppendEntity(circle);
                tr.AddNewlyCreatedDBObject(circle, true);
                createdCircles.Add(new CircleRecord { Center = point, Radius = radius, Id = circle.ObjectId, Active = true });
            }
            createdCount = gridPoints.Count;
            return true;
        }

        private class CircleRecord
        {
            public Point3d Center;
            public double Radius;
            public ObjectId Id;
            public bool Active;
        }

        private class CircleSpatialIndex
        {
            private readonly double cellSize;
            private readonly Dictionary<long, List<CircleRecord>> cells = new Dictionary<long, List<CircleRecord>>();
            private readonly List<CircleRecord> records = new List<CircleRecord>();
            private long minCellX = long.MaxValue;
            private long maxCellX = long.MinValue;
            private long minCellY = long.MaxValue;
            private long maxCellY = long.MinValue;

            public CircleSpatialIndex(List<CircleRecord> circles)
            {
                cellSize = CalculateCellSize(circles);
                for (int i = 0; i < circles.Count; i++)
                    Add(circles[i]);
            }

            public List<CircleRecord> Query(Extents3d extents)
            {
                var result = new List<CircleRecord>();
                if (cells.Count == 0) return result;
                long startX = Math.Max(ToCell(extents.MinPoint.X), minCellX);
                long endX = Math.Min(ToCell(extents.MaxPoint.X), maxCellX);
                long startY = Math.Max(ToCell(extents.MinPoint.Y), minCellY);
                long endY = Math.Min(ToCell(extents.MaxPoint.Y), maxCellY);
                if (startX > endX || startY > endY) return result;

                double coveredCellCount = (double)(endX - startX + 1) * (endY - startY + 1);
                if (coveredCellCount > cells.Count * 4.0)
                {
                    for (int i = 0; i < records.Count; i++)
                    {
                        CircleRecord circle = records[i];
                        if (!circle.Active) continue;
                        if (circle.Center.X < extents.MinPoint.X || circle.Center.X > extents.MaxPoint.X ||
                            circle.Center.Y < extents.MinPoint.Y || circle.Center.Y > extents.MaxPoint.Y)
                            continue;
                        result.Add(circle);
                    }
                    return result;
                }

                for (long x = startX; x <= endX; x++)
                {
                    for (long y = startY; y <= endY; y++)
                    {
                        List<CircleRecord> bucket;
                        if (!cells.TryGetValue(GridKey(x, y), out bucket)) continue;
                        for (int i = 0; i < bucket.Count; i++)
                        {
                            CircleRecord circle = bucket[i];
                            if (!circle.Active) continue;
                            if (circle.Center.X < extents.MinPoint.X || circle.Center.X > extents.MaxPoint.X ||
                                circle.Center.Y < extents.MinPoint.Y || circle.Center.Y > extents.MaxPoint.Y)
                                continue;
                            result.Add(circle);
                        }
                    }
                }
                return result;
            }

            public void ApplyReplacement(List<CircleRecord> removed, List<CircleRecord> added)
            {
                for (int i = 0; i < removed.Count; i++)
                    removed[i].Active = false;
                for (int i = 0; i < added.Count; i++)
                    Add(added[i]);
            }

            public void Remove(List<CircleRecord> removed)
            {
                for (int i = 0; i < removed.Count; i++)
                    removed[i].Active = false;
            }

            private void Add(CircleRecord circle)
            {
                long x = ToCell(circle.Center.X);
                long y = ToCell(circle.Center.Y);
                long key = GridKey(x, y);
                List<CircleRecord> bucket;
                if (!cells.TryGetValue(key, out bucket))
                {
                    bucket = new List<CircleRecord>();
                    cells[key] = bucket;
                }
                circle.Active = true;
                bucket.Add(circle);
                records.Add(circle);
                minCellX = Math.Min(minCellX, x);
                maxCellX = Math.Max(maxCellX, x);
                minCellY = Math.Min(minCellY, y);
                maxCellY = Math.Max(maxCellY, y);
            }

            private long ToCell(double coordinate)
            {
                return (long)Math.Floor(coordinate / cellSize);
            }

            private static long GridKey(long x, long y)
            {
                return unchecked((x << 32) ^ (y & 0xffffffffL));
            }

            private static double CalculateCellSize(List<CircleRecord> circles)
            {
                if (circles.Count < 2) return 1.0;
                double minX = circles[0].Center.X;
                double maxX = minX;
                double minY = circles[0].Center.Y;
                double maxY = minY;
                for (int i = 1; i < circles.Count; i++)
                {
                    minX = Math.Min(minX, circles[i].Center.X);
                    maxX = Math.Max(maxX, circles[i].Center.X);
                    minY = Math.Min(minY, circles[i].Center.Y);
                    maxY = Math.Max(maxY, circles[i].Center.Y);
                }
                double width = Math.Max(maxX - minX, 0.000001);
                double height = Math.Max(maxY - minY, 0.000001);
                double areaSize = Math.Sqrt(width * height * 64.0 / circles.Count);
                double lineSize = Math.Max(width, height) / Math.Max(Math.Sqrt(circles.Count / 64.0), 1.0);
                double size = width > 0.001 && height > 0.001 ? areaSize : lineSize;
                return Math.Max(0.25, Math.Min(size, 100.0));
            }
        }

        /// <summary>
        /// 收集全图中半径在 MinDeleteRadius~MaxDeleteRadius 范围内的圆
        /// </summary>
        private CircleSpatialIndex CollectAllCirclesInRadiusRange()
        {
            var result = new List<CircleRecord>();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                RXClass circleClass = RXClass.GetClass(typeof(Circle));
                BlockTableRecord modelSpace = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    if (!id.ObjectClass.IsDerivedFrom(circleClass)) continue;
                    var circle = tr.GetObject(id, OpenMode.ForRead) as Circle;
                    if (circle != null
                        && circle.Radius >= MinDeleteRadius
                        && circle.Radius <= MaxDeleteRadius)
                    {
                        result.Add(new CircleRecord { Center = circle.Center, Radius = circle.Radius, Id = id, Active = true });
                    }
                }
                tr.Commit();
            }
            return new CircleSpatialIndex(result);
        }

        /// <summary>
        /// 从全图圆列表中，收集指定区域内的圆心（边界框预筛选 + 精确多边形判断）
        /// </summary>
        private List<Point2d> CollectCircleCentersInRegion(
            CircleSpatialIndex allCircles, Extents3d ext, Curve curve)
        {
            var centers = new List<Point2d>();
            List<CircleRecord> records = CollectCircleRecordsInRegion(allCircles, ext, curve);
            for (int i = 0; i < records.Count; i++)
                centers.Add(Point3dToPoint2d(records[i].Center));
            return centers;
        }

        private List<CircleRecord> CollectCircleRecordsInRegion(
            CircleSpatialIndex allCircles, Extents3d ext, Curve curve)
        {
            var result = new List<CircleRecord>();
            if (allCircles == null || curve == null) return result;
            CurveContainment containment = BuildCurveContainment(curve);
            List<CircleRecord> candidates = allCircles.Query(ext);
            for (int i = 0; i < candidates.Count; i++)
            {
                CircleRecord circle = candidates[i];
                if (containment.Contains(Point3dToPoint2d(circle.Center)))
                    result.Add(circle);
            }
            return result;
        }

        private double DetectCircleRadius(List<CircleRecord> circles)
        {
            if (circles == null || circles.Count == 0) return CurrentRadius;
            var radii = new List<double>(circles.Count);
            for (int i = 0; i < circles.Count; i++)
                radii.Add(circles[i].Radius);
            radii.Sort();
            return radii[radii.Count / 2];
        }

        private static void ValidateCircleRecordsWritable(List<CircleRecord> circles, Transaction tr)
        {
            foreach (CircleRecord record in circles)
            {
                Circle circle = tr.GetObject(record.Id, OpenMode.ForWrite, false) as Circle;
                if (circle == null || circle.IsErased)
                    throw new InvalidOperationException("旧圆已失效，无法完整替换，请重新执行命令。");
            }
        }

        private int EraseCircleRecords(List<CircleRecord> circles, Transaction tr)
        {
            ValidateCircleRecordsWritable(circles, tr);
            int erased = 0;
            for (int i = 0; i < circles.Count; i++)
            {
                Circle circle = tr.GetObject(circles[i].Id, OpenMode.ForWrite, false) as Circle;
                if (circle == null || circle.IsErased)
                    throw new InvalidOperationException("旧圆已失效，无法完整替换，请重新执行命令。");
                // OpenMode.ForWrite/Erase 失败必须传出，交给区域事务整体回滚。
                circle.Erase();
                erased++;
            }
            return erased;
        }

        private int MarkCircleRecordsForGenerationFailure(List<CircleRecord> circles, Transaction tr)
        {
            int marked = 0;
            for (int i = 0; i < circles.Count; i++)
            {
                try
                {
                    Circle circle = tr.GetObject(circles[i].Id, OpenMode.ForWrite, false) as Circle;
                    if (circle == null || circle.IsErased) continue;
                    bool alreadyRed = IsEntityVisiblyRed(circle, tr);
                    circle.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                        Autodesk.AutoCAD.Colors.ColorMethod.ByAci,
                        alreadyRed ? (short)6 : (short)1);
                    marked++;
                }
                catch
                {
                }
            }
            return marked;
        }

        /// <summary>
        /// 确保指定的 RegApp 名称已注册。
        /// Undo/PURGE 后进程缓存可能过期，因此每次都查询当前数据库注册表。
        /// </summary>
        private void EnsureRegAppsRegistered(Transaction tr, string[] appNames)
        {
            RegAppTable regAppTable = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            var registeredApps = GetRegisteredAppsForDb();
            foreach (string appName in appNames)
            {
                if (!regAppTable.Has(appName))
                {
                    regAppTable.UpgradeOpen();
                    var regAppRec = new RegAppTableRecord { Name = appName };
                    regAppTable.Add(regAppRec);
                    tr.AddNewlyCreatedDBObject(regAppRec, true);
                }
                registeredApps.Add(appName);
            }
        }

        /// <summary>
        /// 严格蜂窝网格生成（自适应偏移优化）
        /// 可用区域 = boundingbox 内缩 genRadius，网格居中，仅保留精确判定在内的点
        ///
        /// 优化策略：在 Y/X 方向各尝试 3 种起始偏移（共 9 种组合），选覆盖率最高的方案
        /// </summary>
        /// <param name="genRadius">生成圆半径，默认0表示使用 CurrentRadius</param>
        private List<Point3d> GenerateHexGridStrict(Entity ent, double spacing, bool yOffset = false, double genRadius = 0)
        {
            if (genRadius <= 0) genRadius = CurrentRadius;
            var curve = ent as Curve;
            if (curve == null) return new List<Point3d>();

            var ext = ent.GeometricExtents;
            CurveContainment containment = BuildCurveContainment(curve);
            double rowSpacing, colSpacing;
            if (yOffset)
            {
                // Y方向偏移蜂窝：列距更近(sqrt(3)/2)，行距全距，奇数列Y偏移
                colSpacing = spacing * Sqrt3Over2;
                rowSpacing = spacing;
            }
            else
            {
                // X方向偏移蜂窝：行距更近(sqrt(3)/2)，列距全距，奇数行X偏移（原行为）
                rowSpacing = spacing * Sqrt3Over2;
                colSpacing = spacing;
            }

            // 可用区域 = boundingbox 内缩 genRadius
            double availW = (ext.MaxPoint.X - ext.MinPoint.X) - 2 * genRadius;
            double availH = (ext.MaxPoint.Y - ext.MinPoint.Y) - 2 * genRadius;

            if (availW <= 0 || availH <= 0) return new List<Point3d>();

            int rows, cols;
            ValidateGridDimensions(availW, availH, rowSpacing, colSpacing, out rows, out cols);

            // 基础居中偏移
            double totalW = (cols - 1) * colSpacing;
            double totalH = (rows - 1) * rowSpacing;
            double baseOffX = (availW - totalW) / 2.0;
            double baseOffY = (availH - totalH) / 2.0;
            double baseStartX = ext.MinPoint.X + genRadius + baseOffX;
            double baseStartY = ext.MinPoint.Y + genRadius + baseOffY;

            // 方案A：尝试 3x3 = 9 种偏移组合，选覆盖率最高的
            double halfRowSpacing = rowSpacing * 0.5;
            double halfColSpacing = colSpacing * 0.5;
            double[] yOffsets = { 0.0, halfRowSpacing, -halfRowSpacing };
            double[] xOffsets = { 0.0, halfColSpacing, -halfColSpacing };

            List<Point3d> bestResult = null;
            var candidate = new List<Point3d>(Math.Min(rows * cols / 2, 65536));
            int bestCount = -1;
            double bestCenteredDist = double.MaxValue;
            double centerX = (ext.MinPoint.X + ext.MaxPoint.X) / 2;
            double centerY = (ext.MinPoint.Y + ext.MaxPoint.Y) / 2;

            foreach (double dy in yOffsets)
            {
                foreach (double dx in xOffsets)
                {
                    double startX = baseStartX + dx;
                    double startY = baseStartY + dy;
                    candidate.Clear();

                    for (int r = 0; r < rows; r++)
                    {
                        if ((r & 31) == 0) CheckGenerationCancelled();
                        double y = startY + r * rowSpacing;
                        // bbox 行级预筛选：y 超出包围盒直接跳过整行
                        if (y < ext.MinPoint.Y || y > ext.MaxPoint.Y) continue;

                        for (int c = 0; c < cols; c++)
                        {
                            double x = startX + c * colSpacing;
                            // 蜂窝偏移
                            double finalY = y;
                            if (yOffset)
                            {
                                // Y方向偏移：奇数列向Y方向偏移半行距
                                finalY += (c % 2 == 1) ? halfRowSpacing : 0.0;
                            }
                            else
                            {
                                // X方向偏移：奇数行向X方向偏移半列距（原行为）
                                x += (r % 2 == 1) ? halfColSpacing : 0.0;
                            }
                            // bbox 列级预筛选：x 超出包围盒直接跳过
                            if (x < ext.MinPoint.X || x > ext.MaxPoint.X) continue;

                            var pt2d = new Point2d(x, finalY);

                            if (containment.Contains(pt2d))
                            {
                                AddGridPoint(candidate, new Point3d(x, finalY, 0));
                            }
                        }
                    }

                    if (candidate.Count == 0) continue;
                    if (candidate.Count < bestCount) continue;

                    // 计算候选方案的居中度（各点到中心的平均距离，越近越好）
                    double sumDist = 0;
                    foreach (var pt in candidate)
                    {
                        double dxCenter = pt.X - centerX;
                        double dyCenter = pt.Y - centerY;
                        sumDist += Math.Sqrt(dxCenter * dxCenter + dyCenter * dyCenter);
                    }
                    double avgDist = sumDist / candidate.Count;

                    if (candidate.Count > bestCount ||
                        (candidate.Count == bestCount && avgDist < bestCenteredDist))
                    {
                        bestCount = candidate.Count;
                        var reusable = bestResult;
                        bestResult = candidate;
                        candidate = reusable ?? new List<Point3d>(Math.Min(rows * cols / 2, 65536));
                        bestCenteredDist = avgDist;
                    }
                }
            }

            return bestResult ?? new List<Point3d>();
        }


        // HEX - 导出圆间距
        // ================================================================
        // 功能：统计所有有编号叶子区域内圆的平均最近邻间距，导出到 CSV
        // CSV 格式：编号, 间距(mm), X坐标, Y坐标
        // ================================================================

        [CommandMethod("HEX")]
        public void HEX()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            db = doc.Database;
            ed = doc.Editor;

            try
            {
                // 收集框选区域内符合条件的闭合多段线
                var filter = new SelectionFilter(new TypedValue[] { new TypedValue(0, REGION_FILTER) });
                var selRes = ed.GetSelection(filter);
                if (selRes.Status != PromptStatus.OK) return;

                // 获取所有有编号叶子区域
                var regionData = new List<Tuple<ObjectId, Extents3d, Point3d, string>>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    EnsureRegAppsRegistered(tr, new[] { GGE_NUM_APP, GGE_ISLEAF_APP, HCLN_CLEARED_APP });

                    foreach (ObjectId id in selRes.Value.GetObjectIds())
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        // 过滤掉圆判定范围内的圆
                        if (IsCircleInDeleteRange(ent)) continue;

                        if (!(ent is Curve curve) || !IsCurveClosed(curve)) continue;

                        var center = GetRegionCenter(ent);
                        var ext = ent.GeometricExtents;
                        var num = GetEntityNumber(ent);
                        var isLeaf = GetXDataString(ent, GGE_ISLEAF_APP);

                        // 只导出叶子节点
                        if (!IsActiveNumberedLeaf(ent)) continue;

                        regionData.Add(Tuple.Create(id, ext, center, num));
                    }
                    tr.Commit();
                }

                if (regionData.Count == 0)
                {
                    ed.WriteMessage("\n没有可导出的有编号区域。");
                    return;
                }

                // 收集所有符合条件的圆（全图，半径 0.01~0.1mm，不检查编号）
                var allCircles = CollectAllCirclesInRadiusRange();

                // 对每个区域计算平均最近邻间距
                var dataList = new List<Tuple<string, double, double, double, string>>();
                int regionIdx = 0;

                using (var trCalc = db.TransactionManager.StartTransaction())
                {
                    foreach (var region in regionData)
                    {
                        var ext = region.Item2;
                        var ent = trCalc.GetObject(region.Item1, OpenMode.ForRead) as Entity;
                        var curve = ent as Curve;

                        // 边界框预筛选 + 精确多边形判断，收集区域内圆心
                        var centers = CollectCircleCentersInRegion(allCircles, ext, curve);

                        // 计算平均最近邻间距（少于2个圆则记为0）
                        double spacing = centers.Count >= 2
                            ? CalculateAverageNearestNeighborSpacing(centers)
                            : 0.0;

                        // 检测排列方向
                        string direction = "X";
                        if (centers.Count >= 3)
                            direction = DetectHoneycombDirection(centers) ? "Y" : "X";

                        dataList.Add(Tuple.Create(region.Item4, spacing, region.Item3.X, region.Item3.Y, direction));
                        regionIdx++;
                    }
                    trCalc.Commit();
                }

                dataList = dataList.OrderBy(d => d.Item1).ToList();

                // 写入 CSV 文件（保存到CAD文件相同路径下，与 HBR 共用路径逻辑）
                string filename = BuildCsvPath("Spacing");

                using (var file = new StreamWriter(filename, false, new UTF8Encoding(true)))
                {
                    file.WriteLine("编号,间距(mm),X坐标,Y坐标,排列方向");
                    foreach (var item in dataList)
                    {
                        // 数值用 InvariantCulture 输出，保证跨区域/跨机器 CSV 可被 HIX 双文化解析
                        file.WriteLine($"{item.Item1},{item.Item2.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)},{item.Item3.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)},{item.Item4.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)},{item.Item5}");
                    }
                }

                _lastHexExportDirectory = Path.GetDirectoryName(filename);
                SaveLastHexExportDirectory(_lastHexExportDirectory);
                ed.WriteMessage($"\n已导出 {dataList.Count} 条记录到 {filename}");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n错误: {ex.Message}");
            }
        }

        // HCN - 导出区域内圆数量CSV
        // ================================================================
        // 功能：导出每个叶子区域的圆数量（与HEX格式类似，但第二列是圆数量而非间距）
        //       HCN导出的CSV不能被HIX使用，以防用错
        // ================================================================

        [CommandMethod("HCN")]
        public void HCN()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            db = doc.Database;
            ed = doc.Editor;

            try
            {
                // 收集框选区域内符合条件的闭合多段线
                var filter = new SelectionFilter(new TypedValue[] { new TypedValue(0, REGION_FILTER) });
                var selRes = ed.GetSelection(filter);
                if (selRes.Status != PromptStatus.OK) return;

                // 获取所有有编号叶子区域
                var regionData = new List<Tuple<ObjectId, Extents3d, Point3d, string>>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    EnsureRegAppsRegistered(tr, new[] { GGE_NUM_APP, GGE_ISLEAF_APP, HCLN_CLEARED_APP });

                    foreach (ObjectId id in selRes.Value.GetObjectIds())
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        if (IsCircleInDeleteRange(ent)) continue;
                        if (!(ent is Curve curve) || !IsCurveClosed(curve)) continue;

                        var center = GetRegionCenter(ent);
                        var ext = ent.GeometricExtents;
                        var num = GetEntityNumber(ent);
                        var isLeaf = GetXDataString(ent, GGE_ISLEAF_APP);

                        if (!IsActiveNumberedLeaf(ent)) continue;

                        regionData.Add(Tuple.Create(id, ext, center, num));
                    }
                    tr.Commit();
                }

                if (regionData.Count == 0)
                {
                    ed.WriteMessage("\n没有可导出的有编号区域。");
                    return;
                }

                // 收集所有符合条件的圆
                var allCircles = CollectAllCirclesInRadiusRange();

                // 对每个区域计算圆数量
                var dataList = new List<Tuple<string, int, double, double, string>>();
                int regionIdx = 0;

                using (var trCalc = db.TransactionManager.StartTransaction())
                {
                    foreach (var region in regionData)
                    {
                        var ext = region.Item2;
                        var ent = trCalc.GetObject(region.Item1, OpenMode.ForRead) as Entity;
                        var curve = ent as Curve;

                        var centers = CollectCircleCentersInRegion(allCircles, ext, curve);
                        int count = centers.Count;

                        // 检测排列方向
                        string direction = "X";
                        if (centers.Count >= 3)
                            direction = DetectHoneycombDirection(centers) ? "Y" : "X";

                        dataList.Add(Tuple.Create(region.Item4, count, region.Item3.X, region.Item3.Y, direction));
                        regionIdx++;
                    }
                    trCalc.Commit();
                }

                dataList = dataList.OrderBy(d => d.Item1).ToList();

                // 保存到CAD文件相同路径下（与 HBR 共用路径逻辑）
                string filename = BuildCsvPath("Count");

                using (var file = new StreamWriter(filename, false, new UTF8Encoding(true)))
                {
                    file.WriteLine("编号,圆数量,X坐标,Y坐标,排列方向");
                    foreach (var item in dataList)
                    {
                        // 数值用 InvariantCulture 输出，保证跨区域/跨机器 CSV 可被解析
                        file.WriteLine($"{item.Item1},{item.Item2},{item.Item3.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)},{item.Item4.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)},{item.Item5}");
                    }
                }
                ed.WriteMessage($"\n已导出 {dataList.Count} 条记录到 {filename}");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n错误: {ex.Message}");
            }
        }


        // HIX - 导入圆间距并重新生成
        // ================================================================
        // 功能：读取 CSV（编号, 间距, X坐标, Y坐标），与区域匹配
        //       若当前间距与 CSV 不一致则重建：删除原圆 -> 按新间距生成六边形网格
        // 间距比较容差：±1% 或 0.001 mm（取大者）
        // ================================================================

        [CommandMethod("HIX")]
        public void HIX()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            db = doc.Database;
            ed = doc.Editor;

            try
            {
                // 与 HEX 导出路径联动：优先打开本次会话最近一次 HEX 导出目录，
                // AutoCAD 重启后从注册表恢复；目录失效时回退当前 CAD 文档目录或桌面。
                string initialDirectory = _lastHexExportDirectory;
                if (string.IsNullOrEmpty(initialDirectory))
                    initialDirectory = GetLastHexExportDirectory();
                if (string.IsNullOrEmpty(initialDirectory) || !Directory.Exists(initialDirectory))
                {
                    initialDirectory = GetCurrentCadDirectory();
                }

                string filename;
                bool filediaRestore = false;
                if (Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("FILEDIA")) == 0)
                {
                    // FILEDIA=0 会禁用文件对话框; HIX 必须选文件, 临时开启并在结束后恢复。
                    Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("FILEDIA", (short)1);
                    filediaRestore = true;
                }
                try
                {
                    filename = SelectImportCsv("选择间距数据文件（HEX导出的CSV）", initialDirectory, "HexFillHIX");
                    if (filename == null)
                    {
                        ed.WriteMessage("\n未选择文件，操作取消。");
                        return;
                    }
                }
                finally
                {
                    if (filediaRestore)
                    {
                        // FILEDIA 是用户硬性要求：命令结束后仍保持 1，避免后续 NETLOAD/文件对话框失效。
                        Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("FILEDIA", (short)1);
                    }
                }

                // 校验文件名前缀，防止混用（HIX只能使用HEX导出的Spacing_前缀CSV）
                string filePrefix = Path.GetFileNameWithoutExtension(filename);
                if (filePrefix.IndexOf("Spacing_", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    ed.WriteMessage("\n错误：此CSV不是间距数据文件（HEX导出），文件名需包含 Spacing_。");
                    return;
                }

                // 读取 CSV（跳过表头）
                var lines = ReadCsvLinesWithExcelEncoding(filename).Skip(1).ToList();
                var dataList = new List<Tuple<string, double, double, double, bool>>();
                var importedKeys = new HashSet<string>(StringComparer.Ordinal);
                int invalidRows = 0;
                int duplicateRows = 0;
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (HasUnclosedCsvQuote(line)) { invalidRows++; continue; }
                    var parts = ParseCSVLine(line);
                    int fieldCount = parts.Length;
                    while (fieldCount > 5 && string.IsNullOrEmpty(parts[fieldCount - 1])) fieldCount--;
                    if (fieldCount < 4 || fieldCount > 5) { invalidRows++; continue; }
                    string number = NormalizeImportedRootNumber(parts[0]);
                    double spacing, x, y;
                    if (string.IsNullOrEmpty(number) ||
                        !TryParseDoubleAnyCulture(parts[1], out spacing) || spacing < 0 ||
                        !TryParseDoubleAnyCulture(parts[2], out x) ||
                        !TryParseDoubleAnyCulture(parts[3], out y))
                    {
                        invalidRows++;
                        continue;
                    }
                    string direction = fieldCount == 5 ? parts[4].Trim().ToUpperInvariant() : "";
                    if (direction != "" && direction != "X" && direction != "Y")
                    { invalidRows++; continue; }
                    string key = number + "\0" + x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        + "\0" + y.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    if (!importedKeys.Add(key)) { duplicateRows++; continue; }
                    dataList.Add(Tuple.Create(number, spacing, x, y, direction == "Y"));
                }

                if (invalidRows > 0 || duplicateRows > 0)
                    ed.WriteMessage($"\nHIX：跳过 {invalidRows} 行无效数据、{duplicateRows} 行重复记录；重复记录保留首条有效数据。");

                if (dataList.Count == 0)
                {
                    ed.WriteMessage("\nCSV文件没有有效数据。");
                    return;
                }
                var dataByNumber = dataList
                    .GroupBy(item => item.Item1)
                    .ToDictionary(group => group.Key, group => group.ToList());

                // 框选区域
                var filter = new SelectionFilter(new TypedValue[] { new TypedValue(0, REGION_FILTER) });
                var selRes = ed.GetSelection(filter);
                if (selRes.Status != PromptStatus.OK) return;

                int skippedCircles;
                ObjectId[] regionIds = CollectSelectedClosedRegionIds(
                    selRes.Value.GetObjectIds(), out skippedCircles).ToArray();
                if (regionIds.Length == 0)
                {
                    ed.WriteMessage("\n未找到可导入的闭合区域。");
                    return;
                }
                // 收集所有符合条件的圆（全图，半径 0.01~0.1mm，不检查编号）
                var allCircles = CollectAllCirclesInRadiusRange();

                // 预获取区域边界
                var regionExtents = new Dictionary<ObjectId, Extents3d>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in regionIds)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent != null) regionExtents[id] = ent.GeometricExtents;
                    }
                    tr.Commit();
                }

                // 注册应用名
                using (var trInit = db.TransactionManager.StartTransaction())
                {
                    EnsureRegAppsRegistered(trInit, new[] { GGE_NUM_APP, GGE_TEXT_HANDLE_APP, GGE_ISLEAF_APP });
                    trInit.Commit();
                }


                var regionsToProcess = new List<Tuple<ObjectId, double, bool, double>>();
                int skipCount = 0;
                int unmatchedRegions = 0;

                for (int i = 0; i < regionIds.Length; i++)
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var ent = tr.GetObject(regionIds[i], OpenMode.ForRead) as Entity;
                        if (ent == null || !(ent is Curve curve) || !IsCurveClosed(curve))
                        {
                            tr.Commit(); continue;
                        }

                        var center = GetRegionCenter(ent);
                        var num = GetEntityNumber(ent);
                        if (string.IsNullOrEmpty(num)) { unmatchedRegions++; tr.Commit(); continue; }

                        // 在 CSV 中查找匹配的记录（编号 + 坐标双重匹配）
                        List<Tuple<string, double, double, double, bool>> numberMatches;
                        if (!dataByNumber.TryGetValue(num, out numberMatches)) { unmatchedRegions++; tr.Commit(); continue; }
                        var match = numberMatches.FirstOrDefault(d =>
                            Math.Abs(d.Item3 - center.X) < 0.01 &&
                            Math.Abs(d.Item4 - center.Y) < 0.01);
                        if (match == null) { unmatchedRegions++; tr.Commit(); continue; }

                        double targetSpacing = match.Item2;
                        var ext = regionExtents[regionIds[i]];
                        if (targetSpacing < 0)
                        {
                            ed.WriteMessage($"\n区域 {num} 的目标间距为负数，已跳过。");
                            tr.Commit(); continue;
                        }

                        if (targetSpacing == 0)
                        {
                            regionsToProcess.Add(Tuple.Create(regionIds[i], 0.0, match.Item5, CurrentRadius));
                            tr.Commit();
                            continue;
                        }

                        // 计算该区域当前圆的平均最近邻间距
                        var currentCircles = CollectCircleRecordsInRegion(allCircles, ext, curve);
                        var centers = currentCircles.Select(record => Point3dToPoint2d(record.Center)).ToList();

                        double currentSpacing = centers.Count >= 2
                            ? CalculateAverageNearestNeighborSpacing(centers)
                            : -1.0;

                        // 判断是否需要处理：间距容差取 1% 或 0.001mm 中的大值
                        double tol = Math.Max(targetSpacing * 0.01, 0.001);
                        bool needsUpdate = currentSpacing < 0 ||
                                           Math.Abs(currentSpacing - targetSpacing) > tol;

                        if (needsUpdate)
                        {
                            double genRadius = DetectCircleRadius(currentCircles);
                            regionsToProcess.Add(Tuple.Create(regionIds[i], targetSpacing, match.Item5, genRadius));
                        }
                        else
                        {
                            skipCount++;
                        }

                        tr.Commit();
                    }
                }

                if (regionsToProcess.Count == 0)
                {
                    ed.WriteMessage($"\nHIX：没有需要更新的匹配区域，间距一致 {skipCount} 个，无匹配 {unmatchedRegions} 个。");
                    return;
                }

                int totalDeleted = 0;
                int processedRegions = 0;
                int failedRegions = 0;
                int totalCircles = 0;

                for (int i = 0; i < regionsToProcess.Count; i++)
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            var ent = tr.GetObject(regionsToProcess[i].Item1, OpenMode.ForRead) as Entity;
                            if (ent == null) { tr.Commit(); continue; }
                            if (!(ent is Curve curve) || !IsCurveClosed(curve)) { tr.Commit(); continue; }

                            double targetSpacing = regionsToProcess[i].Item2;
                            bool yDirection = regionsToProcess[i].Item3;
                            double genRadius = regionsToProcess[i].Item4;

                            if (targetSpacing == 0)
                            {
                                List<CircleRecord> clearedRecords = CollectCircleRecordsInRegion(
                                    allCircles, ent.GeometricExtents, curve);
                                int cleared = EraseCircleRecords(clearedRecords, tr);
                                tr.Commit();
                                totalDeleted += cleared;
                                processedRegions++;
                                allCircles.Remove(clearedRecords);
                                if ((i + 1) % 20 == 0)
                                    ed.UpdateScreen();
                                continue;
                            }

                            // 物理下限：间距不得小于会话级最小间距 (HDIA 控制)
                            double minSpacing = CurrentMinSpacing;
                            if (targetSpacing < minSpacing) targetSpacing = minSpacing;

                            int deletedCount;
                            int newCount;
                            List<CircleRecord> removedCircles;
                            List<CircleRecord> createdCircles;
                            if (!TryReplaceCirclesWithGrid(
                                ent, tr, targetSpacing, yDirection, genRadius,
                                allCircles, null,
                                out deletedCount, out newCount,
                                out removedCircles, out createdCircles))
                            {
                                ed.WriteMessage($"\n[{i + 1}/{regionsToProcess.Count}] 无法生成新圆，已保留旧圆并标记异常颜色。");
                                tr.Commit();
                                failedRegions++;
                                continue;
                            }

                            tr.Commit();
                            totalDeleted += deletedCount;
                            totalCircles += newCount;
                            processedRegions++;
                            allCircles.ApplyReplacement(removedCircles, createdCircles);
                            if ((i + 1) % 20 == 0)
                                ed.UpdateScreen();
                        }
                        catch (OperationCanceledException ex)
                        {
                            tr.Abort();
                            ed.WriteMessage("\nHIX：" + ex.Message);
                            return;
                        }
                        catch (Exception ex)
                        {
                            failedRegions++;
                            ed.WriteMessage($"\n[{i + 1}/{regionsToProcess.Count}] 更新异常，原圆已回滚保留: {ex.Message}");
                            try { tr.Abort(); } catch { }
                        }
                    }
                }

                ed.WriteMessage($"\nHIX：处理 {processedRegions} 个区域，跳过 {skipCount} 个，无匹配 {unmatchedRegions} 个，失败 {failedRegions} 个，删除 {totalDeleted} 个旧圆，生成 {totalCircles} 个圆。");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n错误: {ex.Message}");
            }
        }

        // HOF - 偏移命令（多段线/圆的偏移 + 面积计算）
        // ================================================================
        // 功能：
        // 1. 第一步：指定偏移距离
        // 2. 第二步：框选偏移对象（支持多选）
        // 3. 第三步：选择目标图层（支持新建图层）
        // 4. 偏移失败的单位标为红色
        // 5. 修复偏移方向问题：自动检测并纠正偏移方向
        // ================================================================

        [CommandMethod("HOF")]
        public void HOF()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            db = doc.Database;
            ed = doc.Editor;

            try
            {
                // 显示偏移设置对话框
                var dialog = new OffsetDialog();

                // 获取当前图层面列表
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    dialog.CurrentLayerName = ((LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead)).Name;
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    foreach (ObjectId id in lt)
                    {
                        var layer = tr.GetObject(id, OpenMode.ForRead) as LayerTableRecord;
                        if (layer != null && !layer.Name.StartsWith("*"))
                        {
                            dialog.LayerNames.Add(layer.Name);
                        }
                    }
                    tr.Commit();
                }

                // 刷新图层列表
                dialog.RefreshLayers();

                // 显示对话框
                DialogResult result = Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(dialog);

                if (result != DialogResult.OK)
                {
                    return;
                }

                double offsetDist = dialog.OffsetDistance;
                bool isExpand = dialog.IsExpand;  // true = 外扩, false = 内缩
                string targetLayer = dialog.SelectedLayerName;

                // 如果选择新建图层
                if (!string.IsNullOrEmpty(targetLayer) && targetLayer != "0")
                {
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
                        if (!lt.Has(targetLayer))
                        {
                            var layer = new LayerTableRecord();
                            layer.Name = targetLayer;
                            layer.Color = Autodesk.AutoCAD.Colors.Color.FromRgb(255, 255, 255);
                            lt.Add(layer);
                            tr.AddNewlyCreatedDBObject(layer, true);
                        }
                        tr.Commit();
                    }
                }

                // ========== 框选偏移对象 ==========
                var filter = new SelectionFilter(new TypedValue[] {
                    new TypedValue(0, "CIRCLE,LWPOLYLINE,POLYLINE")
                });

                var selRes = ed.GetSelection(filter);
                if (selRes.Status != PromptStatus.OK) return;

                ObjectIdCollection allIds = new ObjectIdCollection();
                foreach (SelectedObject so in selRes.Value)
                {
                    allIds.Add(so.ObjectId);
                }

                if (allIds.Count == 0)
                {
                    ed.WriteMessage("\n未选择任何对象");
                    return;
                }

                int processed = 0;
                int failed = 0;
                int total = allIds.Count;


                // ========== 逐个处理 ==========
                for (int i = 0; i < allIds.Count; i++)
                {
                    ObjectId objId = allIds[i];

                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            var ent = tr.GetObject(objId, OpenMode.ForRead) as Entity;
                            if (ent == null)
                            {
                                tr.Commit();
                                continue;
                            }

                            // 按目标面积方向选择偏移结果，保留窄颈内缩后的全部轮廓。
                            List<Entity> offsetEntities = GetOffsetEntities(tr, ent, offsetDist, isExpand);

                            if (offsetEntities.Count == 0)
                            {
                                // 偏移失败，标记原对象为红色
                                var failEnt = tr.GetObject(objId, OpenMode.ForWrite) as Entity;
                                if (failEnt != null)
                                {
                                    failEnt.Color = Autodesk.AutoCAD.Colors.Color.FromRgb(255, 0, 0);
                                }
                                failed++;
                                tr.Commit();
                                continue;
                            }

                            try
                            {
                                AppendHofOffsetEntities(tr, ent, offsetEntities, targetLayer);
                                tr.Commit();
                                processed++;
                                if (processed % 100 == 0) ed.UpdateScreen();
                            }
                            finally
                            {
                                foreach (Entity offsetEnt in offsetEntities) offsetEnt.Dispose();
                            }
                        }
                        catch (Exception)
                        {
                            failed++;
                            try { tr.Abort(); } catch { }
                        }
                    }
                }

                ed.WriteMessage($"\nHOF：成功偏移 {processed} 个对象，失败 {failed} 个。");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n错误: {ex.Message}");
            }
        }

        // Closed offsets may split into several disconnected contours. Keep every
        // contour and select the sign by their combined area, for either winding.
        private void AppendHofOffsetEntities(Transaction tr, Entity source, List<Entity> offsets, string targetLayer)
        {
            ObjectId layerId = GetLayerId(tr, targetLayer);
            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
            foreach (Entity offset in offsets)
            {
                offset.LayerId = layerId;
                offset.Color = source.Color;
                offset.LineWeight = source.LineWeight;
                offset.LinetypeId = source.LinetypeId;
                ms.AppendEntity(offset);
                tr.AddNewlyCreatedDBObject(offset, true);
            }
        }

        private List<Entity> GetOffsetEntities(Transaction tr, Entity ent, double offsetDist, bool isExpand)
        {
            if (!IsFinite(offsetDist) || offsetDist <= 0) return new List<Entity>();
            if (ent is Circle)
                return TryOffsetEntitiesByValue(tr, ent, isExpand ? offsetDist : -offsetDist);

            Curve curve = ent as Curve;
            if (curve == null) return new List<Entity>();
            if (!IsCurveClosed(curve))
                return TryOffsetEntitiesByValue(tr, ent, isExpand ? offsetDist : -offsetDist);

            double oldArea;
            try { oldArea = Math.Abs(curve.Area); }
            catch { return new List<Entity>(); }
            if (!IsFinite(oldArea) || oldArea <= 0) return new List<Entity>();
            foreach (double value in new[] { offsetDist, -offsetDist })
            {
                List<Entity> candidates = TryOffsetEntitiesByValue(tr, ent, value);
                double area = 0;
                bool valid = candidates.Count > 0;
                foreach (Entity candidate in candidates)
                {
                    Curve result = candidate as Curve;
                    try
                    {
                        double partArea = result == null ? double.NaN : Math.Abs(result.Area);
                        if (result == null || !IsCurveClosed(result) || !IsFinite(partArea) || partArea <= 0)
                            valid = false;
                        else area += partArea;
                    }
                    catch { valid = false; }
                }
                if (valid && (isExpand ? area > oldArea : area < oldArea)) return candidates;
                foreach (Entity candidate in candidates) candidate.Dispose();
            }
            return new List<Entity>();
        }

        private List<Entity> TryOffsetEntitiesByValue(Transaction tr, Entity ent, double offsetValue)
        {
            List<Entity> result = new List<Entity>();
            DBObjectCollection offsets = null;
            Polyline converted = null;
            bool accepted = false;
            try
            {
                Circle circle = ent as Circle;
                if (circle != null)
                {
                    double radius = circle.Radius + offsetValue;
                    if (!IsFinite(radius) || radius <= 0.001) return result;
                    result.Add(new Circle(circle.Center, circle.Normal, radius));
                    return result;
                }
                Curve curve = ent as Curve;
                if (curve == null) return result;
                if (curve is Polyline2d || curve is Polyline3d)
                {
                    converted = ConvertToLwPolyline(tr, curve);
                    if (converted == null) return result;
                    curve = converted;
                }
                offsets = curve.GetOffsetCurves(offsetValue);
                if (offsets == null) return result;
                foreach (DBObject item in offsets)
                {
                    Entity candidate = item as Entity;
                    if (candidate == null) throw new InvalidOperationException("偏移返回了无效对象。");
                    Extents3d ext = candidate.GeometricExtents;
                    foreach (double coordinate in new[] { ext.MinPoint.X, ext.MinPoint.Y, ext.MinPoint.Z,
                        ext.MaxPoint.X, ext.MaxPoint.Y, ext.MaxPoint.Z })
                        if (!IsFinite(coordinate)) throw new InvalidOperationException("偏移坐标不是有限值。");
                    result.Add(candidate);
                }
                accepted = true;
            }
            catch
            {
                result.Clear();
            }
            finally
            {
                if (converted != null) converted.Dispose();
                if (!accepted && offsets != null)
                    foreach (DBObject item in offsets) item.Dispose();
            }
            return result;
        }

        // 将旧格式多段线(Polyline2d/Polyline3d)转换为LWPOLYLINE
        private Polyline ConvertToLwPolyline(Transaction tr, Curve curve)
        {
            Polyline lwPl = new Polyline();
            try
            {
                if (curve is Polyline2d pl2d)
                {
                    foreach (ObjectId vid in pl2d)
                    {
                        Vertex2d v = tr.GetObject(vid, OpenMode.ForRead) as Vertex2d;
                        if (v != null)
                        {
                            lwPl.AddVertexAt(lwPl.NumberOfVertices,
                                new Point2d(v.Position.X, v.Position.Y),
                                v.Bulge, v.StartWidth, v.EndWidth);
                        }
                    }
                    lwPl.Closed = pl2d.Closed;
                    return lwPl;
                }
                else if (curve is Polyline3d pl3d)
                {
                    foreach (ObjectId vid in pl3d)
                    {
                        PolylineVertex3d v = tr.GetObject(vid, OpenMode.ForRead) as PolylineVertex3d;
                        if (v != null)
                        {
                            lwPl.AddVertexAt(lwPl.NumberOfVertices,
                                new Point2d(v.Position.X, v.Position.Y), 0, 0, 0);
                        }
                    }
                    lwPl.Closed = pl3d.Closed;
                    return lwPl;
                }
            }
            catch
            {
                lwPl.Dispose();
                return null;
            }
            lwPl.Dispose();
            return null;
        }

        // 获取图层ID（不存在则创建）
        private ObjectId GetLayerId(Transaction tr, string layerName)
        {
            if (string.IsNullOrEmpty(layerName))
            {
                return db.Clayer; // 使用当前图层
            }

            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(layerName))
            {
                return lt[layerName];
            }

            // 图层不存在，升级为写模式后创建
            lt.UpgradeOpen();
            var newLayer = new LayerTableRecord
            {
                Name = layerName,
                Color = Autodesk.AutoCAD.Colors.Color.FromRgb(255, 255, 255)
            };
            lt.Add(newLayer);
            tr.AddNewlyCreatedDBObject(newLayer, true);

            return newLayer.ObjectId;
        }

        /// <summary>
        /// 生成 CSV 导出路径：优先保存到当前 CAD 文件所在目录，
        /// 无活动文件时保存到桌面。前缀用于区分导出类型（Spacing/Count/Brightness 等）。
        /// </summary>
        private string BuildCsvPath(string prefix)
        {
            string cadPath = GetCurrentCadDocumentPath();
            string cadDirectory = GetCurrentCadDirectory();
            string cadName = string.IsNullOrEmpty(cadPath)
                ? ""
                : Path.GetFileNameWithoutExtension(cadPath);
            string cadPrefix = string.IsNullOrEmpty(cadName) ? "" : cadName + "_";
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return Path.Combine(cadDirectory, $"{cadPrefix}{prefix}_{timestamp}.csv");
        }

        private string GetCurrentCadDocumentPath()
        {
            try
            {
                Document document = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                string documentName = document?.Name;
                if (!string.IsNullOrWhiteSpace(documentName) && Path.IsPathRooted(documentName))
                    return documentName;
            }
            catch
            {
            }

            try
            {
                string databaseFilename = db?.Filename;
                if (!string.IsNullOrWhiteSpace(databaseFilename) && Path.IsPathRooted(databaseFilename))
                    return databaseFilename;
            }
            catch
            {
            }

            return null;
        }

        private string GetCurrentCadDirectory()
        {
            string cadPath = GetCurrentCadDocumentPath();
            if (!string.IsNullOrEmpty(cadPath))
            {
                string directory = Path.GetDirectoryName(cadPath);
                if (!string.IsNullOrEmpty(directory))
                    return directory;
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }


        // HCLN - 清除编号命令
        // ================================================================
        // 功能：选中闭合区域，递归清除其所有编号信息（文字 + XData）
        //       同时清除关联的父级编号，确保HNM重新运行时不会恢复旧编号
        // ================================================================
        [CommandMethod("HCLN")]
        public void HCLN()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            db = doc.Database;
            ed = doc.Editor;

            var oldCmdecho = Autodesk.AutoCAD.ApplicationServices.Application.GetSystemVariable("CMDECHO");
            Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("CMDECHO", 0);

            try
            {
                // 选择闭合区域
                PromptSelectionOptions selOpts = new PromptSelectionOptions();
                selOpts.MessageForAdding = "\n选择要清除编号的闭合区域: ";
                SelectionFilter filter = new SelectionFilter(new[] {
                    new TypedValue(0, "LWPOLYLINE,POLYLINE,CIRCLE,ELLIPSE,SPLINE")
                });
                PromptSelectionResult selRes = ed.GetSelection(selOpts, filter);

                if (selRes.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n未选择任何区域。");
                    return;
                }

                ObjectId[] selectedIds = selRes.Value.GetObjectIds();
                var toClear = new HashSet<ObjectId>();
                int clearedCount = 0;
                int skippedClearCount = 0;
                string firstClearError = null;

                // ===== 第一步：获取全图闭合曲线ID（直接遍历模型空间数据库，视口无关；
                // 不再使用 ed.SelectAll，避免视口外的实体不参与全选导致包含关系分析不完整） =====
                var allClosedIds = new List<ObjectId>();
                using (var trScan = db.TransactionManager.StartTransaction())
                {
                    BlockTableRecord scanSpace = (BlockTableRecord)trScan.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                    foreach (ObjectId scanId in scanSpace)
                    {
                        Entity scanEntity = trScan.GetObject(scanId, OpenMode.ForRead, false) as Entity;
                        if (scanEntity == null || scanEntity.IsErased) continue;
                        if (IsCircleInDeleteRange(scanEntity)) continue;
                        if (scanEntity is Curve scanCurve && IsCurveClosed(scanCurve))
                            allClosedIds.Add(scanId);
                    }
                    trScan.Commit();
                }

                // ===== 第二步：分析包含关系（只读事务） =====
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var curveCache = new Dictionary<ObjectId, Curve>();
                    foreach (ObjectId id in allClosedIds)
                    {
                        var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (entity == null || IsCircleInDeleteRange(entity)) continue;
                        if (entity is Curve curve && IsCurveClosed(curve))
                            curveCache[id] = curve;
                    }

                    var selectedCurves = selectedIds
                        .Where(id => curveCache.ContainsKey(id))
                        .ToList();
                    if (selectedCurves.Count == 0)
                    {
                        ed.WriteMessage("\n没有有效的区域可清除。");
                        tr.Commit();
                        return;
                    }

                    toClear.UnionWith(selectedCurves);

                    // 同时收集选中区域的全部后代和全部祖先。
                    // 复用边界采样/包含判定器缓存，同一曲线只采样一次。
                    var samplesCache = new Dictionary<ObjectId, List<Point2d>>();
                    var containmentCache = new Dictionary<ObjectId, CurveContainment>();
                    foreach (var candidate in curveCache)
                    {
                        if (toClear.Contains(candidate.Key)) continue;
                        foreach (ObjectId selectedId in selectedCurves)
                        {
                            Curve selectedCurve = curveCache[selectedId];
                            if (IsFullyContained(candidate.Value, selectedCurve, samplesCache, containmentCache)
                                || IsFullyContained(selectedCurve, candidate.Value, samplesCache, containmentCache))
                            {
                                toClear.Add(candidate.Key);
                                break;
                            }
                        }
                    }

                    tr.Commit();
                }

                // Each region and its number text either commit together or roll back together.
                foreach (ObjectId id in toClear)
                {
                    try
                    {
                        using (var tr = db.TransactionManager.StartTransaction())
                        {
                            Entity ent = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                            if (ent == null || ent.IsErased)
                            {
                                skippedClearCount++;
                                if (firstClearError == null)
                                    firstClearError = "实体已失效或已删除";
                                continue;
                            }

                            MarkEntityClearedByHcln(ent, tr);
                            tr.Commit();
                        }
                        clearedCount++;
                    }
                    catch (Exception ex)
                    {
                        skippedClearCount++;
                        if (firstClearError == null)
                            firstClearError = string.IsNullOrWhiteSpace(ex.Message)
                                ? ex.GetType().Name
                                : ex.Message;
                    }
                }

                int orphanTextCount = 0;
                string cleanupMessage = string.Empty;
                if (skippedClearCount > 0)
                {
                    // Broad orphan cleanup could otherwise remove text belonging to a failed region.
                    cleanupMessage = "；存在未清除区域，暂未清理残留编号文本";
                }
                else
                {
                    try
                    {
                        using (var tr = db.TransactionManager.StartTransaction())
                        {
                            int removedCount = CleanupOrphanNumberTexts(tr, selectedIds, true);
                            tr.Commit();
                            orphanTextCount = removedCount;
                        }
                    }
                    catch (Exception ex)
                    {
                        cleanupMessage = $"；残留编号文本清理失败：{ex.Message}";
                    }
                }

                string skippedMessage = skippedClearCount > 0
                    ? $"；跳过 {skippedClearCount} 个区域（首个原因：{firstClearError}）"
                    : string.Empty;
                ed.WriteMessage($"\nHCLN：清理 {clearedCount} 个区域和 {orphanTextCount} 个残留编号文本{skippedMessage}{cleanupMessage}。");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n错误: {ex.Message}");
            }
            finally
            {
                Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("CMDECHO", oldCmdecho);
            }
        }

    }
}
