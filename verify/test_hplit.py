# HPLIT Geometric Verification Tests
# Tests the polyline splitting algorithm independently of AutoCAD

import sys, os, math
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from geom import Point2, Vec2, Polyline, TestResult, segment_intersection, point_on_segment, point_on_infinite_line


def hplit_simulate(poly, cut_pt1, cut_pt2, gap_mm=1.0):
    """Simulate HPLIT: split a closed polyline with two parallel lines gap_mm apart."""
    cut_dir = Vec2(cut_pt2.x - cut_pt1.x, cut_pt2.y - cut_pt1.y)
    cut_len = cut_dir.length()
    if cut_len < 0.01:
        return None
    cut_dir = cut_dir.normalize()
    perp = Vec2(-cut_dir.y, cut_dir.x)
    half_gap = gap_mm / 2.0

    xs = [v.x for v in poly.verts]
    ys = [v.y for v in poly.verts]
    diag = math.hypot(max(xs) - min(xs), max(ys) - min(ys)) * 2.0
    mid = cut_pt1

    line1_p1 = Point2(mid.x + perp.x * half_gap - cut_dir.x * diag,
                       mid.y + perp.y * half_gap - cut_dir.y * diag)
    line1_p2 = Point2(mid.x + perp.x * half_gap + cut_dir.x * diag,
                       mid.y + perp.y * half_gap + cut_dir.y * diag)
    line2_p1 = Point2(mid.x - perp.x * half_gap - cut_dir.x * diag,
                       mid.y - perp.y * half_gap - cut_dir.y * diag)
    line2_p2 = Point2(mid.x - perp.x * half_gap + cut_dir.x * diag,
                       mid.y - perp.y * half_gap + cut_dir.y * diag)

    # Find intersections with poly edges, tagged with edge index
    hits = []
    for i in range(poly.n):
        a, b = poly.edge(i)
        if a.distance(b) < 1e-9:
            continue
        for lid, lp1, lp2 in [(1, line1_p1, line1_p2), (2, line2_p1, line2_p2)]:
            p = segment_intersection(a, b, lp1, lp2)
            if p and point_on_segment(p, a, b, 0.001):
                hits.append((p, i, lid))

    # Deduplicate
    unique = []
    for p, ei, lid in hits:
        if not any(p.distance(up) < 0.001 for up, _, _ in unique):
            unique.append((p, ei, lid))

    l1c = sum(1 for _, _, lid in unique if lid == 1)
    l2c = sum(1 for _, _, lid in unique if lid == 2)
    if l1c < 2 or l2c < 2:
        return None

    # Sort hits by (edge_index, distance_from_vertex) to follow polyline order
    def sort_key(item):
        p, ei, lid = item
        v = poly.verts[ei]
        return (ei, v.distance(p))

    unique.sort(key=sort_key)
    split_pts_data = unique  # list of (Point2, edge_idx, line_id)
    n = len(split_pts_data)

    # Build segments: each consecutive pair of split points
    segments = []
    for k in range(n):
        p_start, ei_start, _ = split_pts_data[k]
        p_end, ei_end, _ = split_pts_data[(k + 1) % n]

        seg_verts = [p_start]

        # Walk edges from ei_start to ei_end (possibly wrapping around)
        curr = ei_start
        first = True
        while True:
            vi = poly.verts[curr]
            vj = poly.verts[(curr + 1) % poly.n]

            if curr == ei_start and first:
                # Starting edge: go from p_start to vj
                first = False
                if seg_verts[-1].distance(vj) > 0.001:
                    seg_verts.append(vj)
                if curr == ei_end:
                    # Start and end on same edge → gap segment
                    break
            elif curr == ei_end:
                # Final edge: go from vi to p_end
                if seg_verts[-1].distance(vi) > 0.001:
                    seg_verts.append(vi)
                if seg_verts[-1].distance(p_end) > 0.001:
                    seg_verts.append(p_end)
                break
            else:
                # Intermediate edge: add vi then vj
                if seg_verts[-1].distance(vi) > 0.001:
                    seg_verts.append(vi)
                if seg_verts[-1].distance(vj) > 0.001:
                    seg_verts.append(vj)

            curr = (curr + 1) % poly.n
            # Safety: if we''ve gone full circle, break
            if curr == ei_start:
                break

        # Ensure p_end is included
        if seg_verts[-1].distance(p_end) > 0.001:
            seg_verts.append(p_end)

        segments.append(seg_verts)

    # Classify segments
    l1_verts, l2_verts = [], []
    for seg_verts in segments:
        if len(seg_verts) < 2:
            continue
        s, e = seg_verts[0], seg_verts[-1]
        s_on1 = point_on_infinite_line(s, line1_p1, line1_p2, 0.01)
        e_on1 = point_on_infinite_line(e, line1_p1, line1_p2, 0.01)
        s_on2 = point_on_infinite_line(s, line2_p1, line2_p2, 0.01)
        e_on2 = point_on_infinite_line(e, line2_p1, line2_p2, 0.01)

        if s_on1 and e_on1:
            for v in seg_verts:
                if not l1_verts or l1_verts[-1].distance(v) > 0.001:
                    l1_verts.append(v)
        elif s_on2 and e_on2:
            for v in seg_verts:
                if not l2_verts or l2_verts[-1].distance(v) > 0.001:
                    l2_verts.append(v)

    if len(l1_verts) < 2 or len(l2_verts) < 2:
        return None
    return (l1_verts, l2_verts)


def polygon_area(verts):
    n = len(verts)
    area = 0.0
    for i in range(n):
        j = (i + 1) % n
        area += verts[i].x * verts[j].y
        area -= verts[j].x * verts[i].y
    return abs(area) / 2.0


# --- Test Cases ---

def test_rectangle_vertical():
    r = TestResult("Rectangle vertical cut")
    poly = Polyline([Point2(0,0), Point2(100,0), Point2(100,100), Point2(0,100)])
    result = hplit_simulate(poly, Point2(50,-10), Point2(50,110))
    r.check("Split succeeded", result is not None)
    if result:
        l1, l2 = result
        r.check("l1 has >=3 vertices", len(l1) >= 3, f"got {len(l1)}")
        r.check("l2 has >=3 vertices", len(l2) >= 3, f"got {len(l2)}")
        left_xs = [v.x for v in l1]
        right_xs = [v.x for v in l2]
        r.check("Left poly max X around 49.5", abs(max(left_xs) - 49.5) < 0.1,
                f"max_x={max(left_xs):.2f}")
        r.check("Left poly min X around 0", abs(min(left_xs) - 0) < 0.1,
                f"min_x={min(left_xs):.2f}")
        r.check("Right poly min X around 50.5", abs(min(right_xs) - 50.5) < 0.1,
                f"min_x={min(right_xs):.2f}")
        r.check("Right poly max X around 100", abs(max(right_xs) - 100) < 0.1,
                f"max_x={max(right_xs):.2f}")
        area1 = polygon_area(l1)
        area2 = polygon_area(l2)
        r.check("Left area around 4950", abs(area1 - 4950) < 100, f"area={area1:.1f}")
        r.check("Right area around 4950", abs(area2 - 4950) < 100, f"area={area2:.1f}")
        r.check("Combined area around 9900", abs(area1 + area2 - 9900) < 200,
                f"total={area1 + area2:.1f}")
    return r


def test_rectangle_diagonal():
    r = TestResult("Rectangle diagonal cut")
    poly = Polyline([Point2(0,0), Point2(100,0), Point2(100,100), Point2(0,100)])
    result = hplit_simulate(poly, Point2(-10,-10), Point2(110,110))
    r.check("Split succeeded", result is not None)
    if result:
        l1, l2 = result
        r.check("Two polylines", len(l1) >= 3 and len(l2) >= 3,
                f"L:{len(l1)}v R:{len(l2)}v")
        area1 = polygon_area(l1)
        area2 = polygon_area(l2)
        r.check("Combined area reasonable", 9000 < area1 + area2 < 10000,
                f"total={area1 + area2:.1f}")
    return r


def test_l_shape():
    r = TestResult("L-shape vertical cut")
    poly = Polyline([
        Point2(0,0), Point2(100,0), Point2(100,40),
        Point2(40,40), Point2(40,100), Point2(0,100)
    ])
    result = hplit_simulate(poly, Point2(70,-10), Point2(70,110))
    r.check("Split succeeded", result is not None)
    if result:
        l1, l2 = result
        r.check("Two polylines", len(l1) >= 3 and len(l2) >= 3,
                f"L:{len(l1)}v R:{len(l2)}v")
        area1 = polygon_area(l1)
        area2 = polygon_area(l2)
        r.check("Combined area reasonable", 5000 < area1 + area2 < 7000,
                f"total={area1 + area2:.1f}")
    return r


def test_convex_irregular():
    r = TestResult("Irregular pentagon")
    poly = Polyline([
        Point2(0,0), Point2(100,10), Point2(90,80),
        Point2(50,100), Point2(10,60)
    ])
    result = hplit_simulate(poly, Point2(45,-10), Point2(55,110))
    r.check("Split succeeded", result is not None)
    if result:
        l1, l2 = result
        r.check("Two polylines", len(l1) >= 3 and len(l2) >= 3,
                f"L:{len(l1)}v R:{len(l2)}v")
    return r


def test_narrow_polyline():
    r = TestResult("Narrow polyline")
    poly = Polyline([Point2(0,0), Point2(10,0), Point2(10,2), Point2(0,2)])
    result = hplit_simulate(poly, Point2(5,-1), Point2(5,3))
    r.check("Split succeeds on narrow poly", result is not None,
            "gap < poly width so should work")
    return r


def run_all():
    tests = [
        test_rectangle_vertical(),
        test_rectangle_diagonal(),
        test_l_shape(),
        test_convex_irregular(),
        test_narrow_polyline(),
    ]
    all_pass = True
    for t in tests:
        print(t.report())
        if not t.passed:
            all_pass = False
    return all_pass


if __name__ == "__main__":
    ok = run_all()
    print(f"\n{'='*50}")
    print(f"  OVERALL: {'ALL PASS' if ok else 'SOME FAILED'}")
    print(f"{'='*50}")
    import sys; sys.exit(0 if ok else 1)
