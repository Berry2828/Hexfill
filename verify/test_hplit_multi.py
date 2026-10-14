import sys, os, math
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from geom import Point2, Vec2, Polyline, TestResult, segment_intersection, point_on_segment


def closest_point_on_polyline(pt, pline):
    """Find closest point on a polyline (list of Point2). Returns (point, seg_index, t)."""
    best_dist = float('inf')
    best_pt = None
    best_seg = 0
    best_t = 0.0
    for i in range(len(pline) - 1):
        a, b = pline[i], pline[i + 1]
        ab = Vec2(b.x - a.x, b.y - a.y)
        l2 = ab.dot(ab)
        if l2 < 1e-12:
            d = pt.distance(a)
            if d < best_dist:
                best_dist, best_pt, best_seg, best_t = d, a, i, 0.0
        else:
            t = max(0, min(1, (Vec2(pt.x - a.x, pt.y - a.y).dot(ab)) / l2))
            cp = Point2(a.x + t * ab.x, a.y + t * ab.y)
            d = pt.distance(cp)
            if d < best_dist:
                best_dist, best_pt, best_seg, best_t = d, cp, i, t
    return best_pt, best_seg, best_t


def tangent_at_polyline_point(seg_idx, t, pline):
    """Get tangent direction at a point on polyline."""
    a, b = pline[seg_idx], pline[min(seg_idx + 1, len(pline) - 1)]
    tangent = Vec2(b.x - a.x, b.y - a.y)
    tl = tangent.length()
    if tl < 1e-12:
        return Vec2(1, 0)
    return Vec2(tangent.x / tl, tangent.y / tl)


def hplit_simulate_multi(poly, cut_pts, gap_mm=1.0):
    """Simulate HPLIT with multi-segment cut.
    Uses per-segment offset lines (extend first backward, last forward).
    Classifies split SEGMENTS by midpoint (not endpoints).
    Skips gap segments (same edge, distance < gap)."""
    if len(cut_pts) < 2:
        return None
    half_gap = gap_mm / 2.0
    xs = [v.x for v in poly.verts]
    ys = [v.y for v in poly.verts]
    diag = math.hypot(max(xs) - min(xs), max(ys) - min(ys)) * 2.0

    # Collect intersection hits from per-segment offset lines
    hits = []
    for si in range(len(cut_pts) - 1):
        s, e = cut_pts[si], cut_pts[si + 1]
        seg_dir = Vec2(e.x - s.x, e.y - s.y)
        if seg_dir.length() < 0.01:
            continue
        seg_dir = seg_dir.normalize()
        seg_perp = Vec2(-seg_dir.y, seg_dir.x)

        # Only extend first backward, last forward (matching C#)
        ext_s = Point2(s.x - seg_dir.x * diag, s.y - seg_dir.y * diag) if si == 0 else s
        ext_e = Point2(e.x + seg_dir.x * diag, e.y + seg_dir.y * diag) if si == len(cut_pts) - 2 else e

        for sign in [1, -1]:
            od = Vec2(seg_perp.x * sign, seg_perp.y * sign)
            l_p1 = Point2(ext_s.x + od.x * half_gap, ext_s.y + od.y * half_gap)
            l_p2 = Point2(ext_e.x + od.x * half_gap, ext_e.y + od.y * half_gap)
            for i in range(poly.n):
                a, b = poly.edge(i)
                if a.distance(b) < 1e-9:
                    continue
                p = segment_intersection(a, b, l_p1, l_p2)
                if p and point_on_segment(p, a, b, 0.001):
                    hits.append((p, i))

    # Dedup
    unique = []
    for p, ei in hits:
        if not any(p.distance(up) < 0.001 for up, _ in unique):
            unique.append((p, ei))
    if len(unique) < 4:
        return None

    unique.sort(key=lambda x: (x[1], poly.verts[x[1]].distance(x[0])))

    # Build split segments along polyline perimeter
    segments = []
    n = len(unique)
    for k in range(n):
        p_start, ei_start = unique[k]
        p_end, ei_end = unique[(k + 1) % n]

        seg_verts = [p_start]
        curr = ei_start
        first = True
        while True:
            vi, vj = poly.verts[curr], poly.verts[(curr + 1) % poly.n]
            if curr == ei_start and first:
                first = False
                if seg_verts[-1].distance(vj) > 0.001:
                    seg_verts.append(vj)
                if curr == ei_end:
                    break
            elif curr == ei_end:
                if seg_verts[-1].distance(vi) > 0.001:
                    seg_verts.append(vi)
                if seg_verts[-1].distance(p_end) > 0.001:
                    seg_verts.append(p_end)
                break
            else:
                if seg_verts[-1].distance(vi) > 0.001:
                    seg_verts.append(vi)
                if seg_verts[-1].distance(vj) > 0.001:
                    seg_verts.append(vj)
            curr = (curr + 1) % poly.n
            if curr == ei_start:
                break
        if seg_verts[-1].distance(p_end) > 0.001:
            seg_verts.append(p_end)
        segments.append(seg_verts)

    # Classify each SEGMENT by its midpoint (not endpoints)
    l1_all, l2_all = [], []
    for seg_verts in segments:
        if len(seg_verts) < 2:
            continue

        s, e = seg_verts[0], seg_verts[-1]
        # Detect gap segment: both ends on same edge, short distance
        seg_len = e.distance(s)
        s_on_edge = point_on_segment(s, poly.verts[unique[0][1]], poly.verts[(unique[0][1] + 1) % poly.n], 0.001)

        if seg_len < gap_mm * 1.5:
            continue  # skip gap segments

        # Classify by midpoint
        mid_pt = Point2((s.x + e.x) / 2, (s.y + e.y) / 2)
        cp, seg_idx, t = closest_point_on_polyline(mid_pt, cut_pts)
        tangent = tangent_at_polyline_point(seg_idx, t, cut_pts)
        normal = Vec2(-tangent.y, tangent.x)
        to_pt = Vec2(mid_pt.x - cp.x, mid_pt.y - cp.y)
        side = to_pt.x * normal.x + to_pt.y * normal.y

        if side >= 0:
            for v in seg_verts:
                if not l1_all or l1_all[-1].distance(v) > 0.001:
                    l1_all.append(v)
        else:
            for v in seg_verts:
                if not l2_all or l2_all[-1].distance(v) > 0.001:
                    l2_all.append(v)

    if len(l1_all) < 2 or len(l2_all) < 2:
        return None
    return (l1_all, l2_all)


def polygon_area(verts):
    n = len(verts); area = 0.0
    for i in range(n):
        j = (i + 1) % n
        area += verts[i].x * verts[j].y - verts[j].x * verts[i].y
    return abs(area) / 2.0


# --- Tests ---

def test_multi_collinear():
    r = TestResult("Multi 3pt collinear (vertical)")
    poly = Polyline([Point2(0, 0), Point2(100, 0), Point2(100, 100), Point2(0, 100)])
    result = hplit_simulate_multi(poly, [Point2(50, -10), Point2(50, 50), Point2(50, 110)])
    r.check("Split succeeds", result is not None)
    if result:
        l1, l2 = result
        r.check("2 polylines", len(l1)>=3 and len(l2)>=3, f"L:{len(l1)} R:{len(l2)}")
        r.check("Area OK", abs(polygon_area(l1)+polygon_area(l2)-9900)<200)
    return r


def test_multi_diagonal():
    r = TestResult("Multi 3pt diagonal")
    poly = Polyline([Point2(0, 0), Point2(100, 0), Point2(100, 100), Point2(0, 100)])
    result = hplit_simulate_multi(poly, [Point2(-10, -10), Point2(50, 50), Point2(110, 110)])
    r.check("Split succeeds", result is not None)
    if result:
        l1, l2 = result
        r.check("2 polylines", len(l1)>=3 and len(l2)>=3)
    return r


def test_multi_l_shape():
    r = TestResult("Multi L-shape cut (down then right)")
    poly = Polyline([Point2(0, 0), Point2(100, 0), Point2(100, 100), Point2(0, 100)])
    result = hplit_simulate_multi(poly, [Point2(50, -10), Point2(50, 30), Point2(110, 30)])
    r.check("Split succeeds", result is not None)
    if result:
        l1, l2 = result
        r.check("2 polylines", len(l1)>=3 and len(l2)>=3, f"L:{len(l1)}v R:{len(l2)}v")
        area1 = polygon_area(l1)
        area2 = polygon_area(l2)
        r.check("Combined area OK", abs(area1 + area2 - 10000) < 400,
                f"total={area1 + area2:.1f}")
    return r


def test_multi_z_shape():
    r = TestResult("Multi Z-shape cut (down+right+down)")
    poly = Polyline([Point2(0, 0), Point2(100, 0), Point2(100, 100), Point2(0, 100)])
    result = hplit_simulate_multi(poly, [Point2(50, -10), Point2(50, 30), Point2(80, 30), Point2(80, 110)])
    r.check("Split succeeds", result is not None)
    if result:
        l1, l2 = result
        r.check("2 polylines", len(l1)>=3 and len(l2)>=3, f"L:{len(l1)}v R:{len(l2)}v")
        area1 = polygon_area(l1)
        area2 = polygon_area(l2)
        r.check("Combined area OK", abs(area1 + area2 - 10000) < 600,
                f"total={area1 + area2:.1f}")
    return r


def test_multi_l_shape_edges():
    r = TestResult("Multi L-shape crossing bottom+right")
    poly = Polyline([Point2(0, 0), Point2(100, 0), Point2(100, 100), Point2(0, 100)])
    result = hplit_simulate_multi(poly, [Point2(30, -10), Point2(30, 50), Point2(110, 50)])
    r.check("Split succeeds", result is not None)
    if result:
        l1, l2 = result
        r.check("2 polylines", len(l1)>=3 and len(l2)>=3, f"L:{len(l1)}v R:{len(l2)}v")
        area1 = polygon_area(l1)
        area2 = polygon_area(l2)
        r.check("Combined area OK", abs(area1 + area2 - 10000) < 500,
                f"total={area1 + area2:.1f}")
    return r


from test_hplit import (
    test_rectangle_vertical, test_rectangle_diagonal, test_l_shape,
    test_convex_irregular, test_narrow_polyline
)

def run_all():
    tests = [
        test_rectangle_vertical(), test_rectangle_diagonal(),
        test_l_shape(), test_convex_irregular(), test_narrow_polyline(),
        test_multi_collinear(), test_multi_diagonal(),
        test_multi_l_shape(), test_multi_z_shape(), test_multi_l_shape_edges(),
    ]
    all_pass = True
    for t in tests:
        print(t.report())
        if not t.passed: all_pass = False
    return all_pass

if __name__ == "__main__":
    ok = run_all()
    print(f"\n{'='*50}\n  OVERALL: {'ALL PASS' if ok else 'SOME FAILED'}\n{'='*50}")
    import sys; sys.exit(0 if ok else 1)
