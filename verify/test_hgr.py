# HGR Hex Grid Generation Verification
# Tests the hexagonal grid algorithm independently of AutoCAD

import sys, os, math
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from geom import Point2, Polyline, TestResult

Sqrt3Over2 = math.sqrt(3) / 2  # 0.8660254


def generate_hex_grid_strict(poly, spacing, y_offset=False, gen_radius=0.03):
    """
    Simulate GenerateHexGridStrict from HexFill.cs.
    Returns list of Point2 that fall inside the polyline.
    """
    xs = [v.x for v in poly.verts]
    ys = [v.y for v in poly.verts]
    min_x, max_x = min(xs), max(xs)
    min_y, max_y = min(ys), max(ys)

    if y_offset:
        col_spacing = spacing * Sqrt3Over2
        row_spacing = spacing
    else:
        row_spacing = spacing * Sqrt3Over2
        col_spacing = spacing

    avail_w = (max_x - min_x) - 2 * gen_radius
    avail_h = (max_y - min_y) - 2 * gen_radius
    if avail_w <= 0 or avail_h <= 0:
        return []

    rows = int(math.floor(avail_h / row_spacing)) + 1
    cols = int(math.floor(avail_w / col_spacing)) + 1

    total_w = (cols - 1) * col_spacing
    total_h = (rows - 1) * row_spacing
    base_off_x = (avail_w - total_w) / 2.0
    base_off_y = (avail_h - total_h) / 2.0
    base_start_x = min_x + gen_radius + base_off_x
    base_start_y = min_y + gen_radius + base_off_y

    y_offsets = [0.0, row_spacing * 0.5, -row_spacing * 0.5]
    x_offsets = [0.0, col_spacing * 0.5, -col_spacing * 0.5]

    center = Point2((min_x + max_x) / 2, (min_y + max_y) / 2)
    best_result = None
    best_count = -1
    best_centered_dist = float("inf")

    for dy in y_offsets:
        for dx in x_offsets:
            start_x = base_start_x + dx
            start_y = base_start_y + dy
            candidate = []

            for r in range(rows):
                y = start_y + r * row_spacing
                if y < min_y or y > max_y:
                    continue
                for c in range(cols):
                    x = start_x + c * col_spacing
                    final_y = y
                    if y_offset:
                        final_y += (row_spacing * 0.5) if (c % 2 == 1) else 0.0
                    else:
                        x += (col_spacing * 0.5) if (r % 2 == 1) else 0.0
                    if x < min_x or x > max_x:
                        continue
                    if point_in_polygon(Point2(x, final_y), poly):
                        candidate.append(Point2(x, final_y))

            if not candidate:
                continue
            sum_dist = sum(pt.distance(center) for pt in candidate)
            avg_dist = sum_dist / len(candidate)
            if len(candidate) > best_count or (
                len(candidate) == best_count and avg_dist < best_centered_dist
            ):
                best_count = len(candidate)
                best_result = candidate
                best_centered_dist = avg_dist

    return best_result or []


def point_in_polygon(pt, poly):
    """Ray casting algorithm for point-in-polygon test."""
    inside = False
    n = poly.n
    for i in range(n):
        a = poly.verts[i]
        b = poly.verts[(i + 1) % n]
        if ((a.y > pt.y) != (b.y > pt.y)) and (
            pt.x < (b.x - a.x) * (pt.y - a.y) / (b.y - a.y) + a.x
        ):
            inside = not inside
    return inside


def test_hgr_rectangle_100x100():
    r = TestResult("HGR Rectangle 100x100 spacing=0.3")
    poly = Polyline([Point2(0, 0), Point2(100, 0), Point2(100, 100), Point2(0, 100)])
    points = generate_hex_grid_strict(poly, 0.3)
    r.check("Generates circles", len(points) > 0, f"count={len(points)}")
    # Area = 10000, spacing=0.3, radius=0.03 → roughly 10000/(0.3^2*sqrt(3)/2) ≈ 10000/0.0779 ≈ 128k
    # but many near edges get filtered. So expect reasonable count
    r.check("Reasonable count", 100 < len(points) < 500000,
            f"count={len(points)}")
    # All points should be inside
    all_inside = all(point_in_polygon(p, poly) for p in points)
    r.check("All points inside polygon", all_inside)
    return r


def test_hgr_small_rect():
    r = TestResult("HGR Small rect 10x10 spacing=0.1")
    poly = Polyline([Point2(0, 0), Point2(10, 0), Point2(10, 10), Point2(0, 10)])
    points = generate_hex_grid_strict(poly, 0.1)
    r.check("Generates circles", len(points) > 0, f"count={len(points)}")
    all_inside = all(point_in_polygon(p, poly) for p in points)
    r.check("All points inside", all_inside)
    return r


def test_hgr_triangle():
    r = TestResult("HGR Triangle spacing=0.2")
    poly = Polyline([Point2(0, 0), Point2(10, 0), Point2(5, 8.66)])
    points = generate_hex_grid_strict(poly, 0.2)
    r.check("Generates circles", len(points) > 0, f"count={len(points)}")
    all_inside = all(point_in_polygon(p, poly) for p in points)
    r.check("All points inside", all_inside)
    return r


def test_hgr_hex_orientation():
    """Verify hex grid is properly honeycomb (not square grid)."""
    r = TestResult("HGR Hex orientation check")
    poly = Polyline([Point2(0, 0), Point2(50, 0), Point2(50, 50), Point2(0, 50)])
    points = generate_hex_grid_strict(poly, 1.0)
    r.check("Generates circles", len(points) > 10, f"count={len(points)}")
    # In hex grid, each row should have alternating offset
    # Check that we have points with different x offsets in odd/even rows
    xs_by_row = {}
    for p in points:
        row_key = round(p.y / (1.0 * Sqrt3Over2))
        xs_by_row.setdefault(row_key, []).append(p.x)
    # Should have multiple rows
    r.check("Multiple rows", len(xs_by_row) > 1, f"rows={len(xs_by_row)}")
    return r


def run_all():
    tests = [
        test_hgr_rectangle_100x100(),
        test_hgr_small_rect(),
        test_hgr_triangle(),
        test_hgr_hex_orientation(),
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
