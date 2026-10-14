# HOF Offset Verification
# Tests offset calculation logic - AutoCAD offset = parallel edge displacement

import sys, os, math
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from geom import Point2, Polyline, Vec2, TestResult


def segment_offset_intersection(a1, a2, b1, b2, dist):
    """
    Offset two adjacent edges (a1->a2, b1->b2) outward by dist.
    Returns the intersection of the offset lines.
    This simulates AutoCAD''s offset behavior.
    """
    # Edge normals (perpendicular, pointing outward for CCW polygon)
    def edge_normal(p, q, ccw=True):
        d = Vec2(q.x - p.x, q.y - p.y)
        # For CCW polygon, outward = right normal = CW rotation = (dy, -dx)
        if ccw:
            n = Vec2(d.y, -d.x)
        else:
            n = Vec2(-d.y, d.x)
        length = n.length()
        if length < 1e-12:
            return Vec2(0, 0)
        return Vec2(n.x / length, n.y / length)

    n1 = edge_normal(a1, a2, ccw=True)
    n2 = edge_normal(b1, b2, ccw=True)

    # Offset lines
    l1_p1 = Point2(a1.x + n1.x * dist, a1.y + n1.y * dist)
    l1_p2 = Point2(a2.x + n1.x * dist, a2.y + n1.y * dist)
    l2_p1 = Point2(b1.x + n2.x * dist, b1.y + n2.y * dist)
    l2_p2 = Point2(b2.x + n2.x * dist, b2.y + n2.y * dist)

    # Line-line intersection
    d = (l1_p1.x - l1_p2.x) * (l2_p1.y - l2_p2.y) - (l1_p1.y - l1_p2.y) * (l2_p1.x - l2_p2.x)
    if abs(d) < 1e-12:
        # Parallel, use midpoint
        return Point2((l1_p2.x + l2_p1.x) / 2, (l1_p2.y + l2_p1.y) / 2)
    t = ((l1_p1.x - l2_p1.x) * (l2_p1.y - l2_p2.y) - (l1_p1.y - l2_p1.y) * (l2_p1.x - l2_p2.x)) / d
    return Point2(l1_p1.x + t * (l1_p2.x - l1_p1.x), l1_p1.y + t * (l1_p2.y - l1_p1.y))


def offset_polyline(poly, dist):
    """Offset polyline edges outward by dist (positive=outward)."""
    n = poly.n
    new_verts = []
    for i in range(n):
        prev = poly.verts[(i - 1) % n]
        curr = poly.verts[i]
        next_v = poly.verts[(i + 1) % n]
        new_v = segment_offset_intersection(prev, curr, curr, next_v, dist)
        new_verts.append(new_v)
    return Polyline(new_verts)


def polygon_area(verts):
    n = len(verts)
    area = 0.0
    for i in range(n):
        j = (i + 1) % n
        area += verts[i].x * verts[j].y
        area -= verts[j].x * verts[i].y
    return abs(area) / 2.0


def test_hof_rectangle_outward():
    r = TestResult("HOF Rectangle outward offset")
    poly = Polyline([Point2(0, 0), Point2(100, 0), Point2(100, 100), Point2(0, 100)])
    offset = offset_polyline(poly, 5.0)

    r.check("Offset larger", offset.total_length() > poly.total_length())
    area = polygon_area(offset.verts)
    # 110 * 110 = 12100
    r.check("Area ~12100", abs(area - 12100) < 100, f"area={area:.1f}")
    return r


def test_hof_rectangle_inward():
    r = TestResult("HOF Rectangle inward offset")
    poly = Polyline([Point2(0, 0), Point2(100, 0), Point2(100, 100), Point2(0, 100)])
    offset = offset_polyline(poly, -5.0)

    r.check("Offset smaller", offset.total_length() < poly.total_length())
    area = polygon_area(offset.verts)
    # 90 * 90 = 8100
    r.check("Area ~8100", abs(area - 8100) < 100, f"area={area:.1f}")
    return r


def test_hof_distance_validation():
    r = TestResult("HOF Distance validation")
    poly = Polyline([Point2(0, 0), Point2(10, 0), Point2(10, 10), Point2(0, 10)])
    offset_small = offset_polyline(poly, -3.0)
    r.check("Handles large inward offset", offset_small.n >= 3)
    # Zero offset = identity
    offset_zero = offset_polyline(poly, 0.0)
    r.check("Zero offset is identity", abs(polygon_area(offset_zero.verts) - 100) < 1)
    return r


def test_hof_non_rectangular():
    r = TestResult("HOF Non-rectangular offset")
    # Triangle
    poly = Polyline([Point2(0, 0), Point2(50, 0), Point2(25, 43.3)])
    offset = offset_polyline(poly, 3.0)
    r.check("Triangle offsets correctly", offset.n == 3)
    area_orig = polygon_area(poly.verts)
    area_offset = polygon_area(offset.verts)
    r.check("Area increases with outward offset", area_offset > area_orig)
    return r


def run_all():
    tests = [
        test_hof_rectangle_outward(),
        test_hof_rectangle_inward(),
        test_hof_distance_validation(),
        test_hof_non_rectangular(),
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
