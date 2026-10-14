# HGAP Vertex Cleanup & Duplicate-Line Verification
# Simulates the new vertex-problem handling added to Commands/HgapCommand.cs:
#   1. zero-length edges / duplicate vertices
#   2. consecutive collinear redundant vertices
# plus a regression check that duplicate standalone (non-polyline-aggregate)
# open lines are still merged into one clean line.

import sys, os, math
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from geom import Point2, TestResult

TOL = 0.001  # matches 0.001 used in HgapCommand.cs


# ---------------------------------------------------------------------------
# Python mirrors of the new C# logic
# ---------------------------------------------------------------------------

def is_redundant_collinear(a, b, c, tol):
    """Mirror HgapIsRedundantCollinearVertex:
    b is redundant on a->c iff |ac|>tol, perpendicular distance <= tol,
    and b's projection lies inside [a, c]."""
    acx, acy = c.x - a.x, c.y - a.y
    ac_len = math.hypot(acx, acy)
    if ac_len <= tol:
        return False
    cross = abs((b.x - a.x) * acy - (b.y - a.y) * acx)
    if cross > tol * ac_len:
        return False
    proj = ((b.x - a.x) * acx + (b.y - a.y) * acy) / (ac_len * ac_len)
    return -1e-9 <= proj <= 1.0 + 1e-9


def clean_collinear_chain(pts, closed, tol):
    """Mirror CleanHgapCollinearChainPoints (mutates pts in place)."""
    changed = True
    while changed:
        changed = False
        count = len(pts)
        if count < 3:
            return
        for i in range(count):
            if not closed and (i == 0 or i == count - 1):
                continue
            prev = pts[(i - 1 + count) % count]
            nxt = pts[(i + 1) % count]
            if is_redundant_collinear(prev, pts[i], nxt, tol):
                del pts[i]
                changed = True
                break


def clean_polyline_vertices(pts, tol):
    """Mirror CleanHgapPolylineVertices on an OPEN polyline.
    Returns (cleaned_pts, changed)."""
    changed = False
    # 1) remove zero-length edges / duplicate adjacent vertices (backwards)
    i = len(pts) - 1
    while i >= 1:
        if pts[i].distance(pts[i - 1]) <= tol:
            del pts[i]
            changed = True
        i -= 1
    # 2) remove consecutive collinear redundant vertices
    while True:
        progress = False
        n = len(pts)
        for i in range(1, n - 1):
            if is_redundant_collinear(pts[i - 1], pts[i], pts[i + 1], tol):
                del pts[i]
                progress = True
                changed = True
                break
        if not progress:
            break
    return pts, changed


def clean_closed_polyline_vertices(pts, tol):
    """Mirror CleanHgapClosedPolylineVertices on a CLOSED polyline vertex list.
    Returns (cleaned_pts, changed)."""
    pts = list(pts)
    changed = False
    # 1) zero-length edges / duplicate adjacent vertices, wrap-around included
    i = len(pts) - 1
    while i >= 0:
        if len(pts) < 3:
            break
        nxt = (i + 1) % len(pts)
        if pts[i].distance(pts[nxt]) <= tol:
            del pts[i]
            changed = True
        i -= 1
    # 2) collinear redundant vertices on a closed ring (wrap-around included)
    before = len(pts)
    clean_collinear_chain(pts, True, tol)
    if len(pts) < before:
        changed = True
    return pts, changed


def clean_chain_points(points, tol):
    """Mirror CleanHgapChainPoints (dedupe adjacent + open collinear cleanup)."""
    clean = []
    for p in points:
        if not clean or clean[-1].distance(p) > tol:
            clean.append(p)
    clean_collinear_chain(clean, False, tol)
    return clean


def finalize_chain(points, tol):
    """Mirror the chain-finalization in CleanupHgapGeometry:
    dedupe, detect closed, drop closing dup point, closed collinear cleanup."""
    pts = clean_chain_points(points, tol)
    closed = len(pts) > 2 and pts[0].distance(pts[-1]) <= tol
    if closed:
        pts = pts[:-1]
        clean_collinear_chain(pts, True, tol)
    return pts, closed


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------

def test_zero_length_edges():
    r = TestResult("HGAP zero-length edges / duplicate vertices")
    # Open line with duplicate vertex in the middle
    pts = [Point2(0, 0), Point2(10, 0), Point2(10, 0), Point2(10, 10)]
    out, changed = clean_polyline_vertices(list(pts), TOL)
    r.check("Deduplicates adjacent identical vertex",
            changed and len(out) == 3 and out[1].distance(Point2(10, 0)) < 1e-9,
            f"out={out}")
    # Line that collapses entirely (all points identical) -> invalid
    pts2 = [Point2(5, 5), Point2(5, 5), Point2(5, 5)]
    out2, changed2 = clean_polyline_vertices(list(pts2), TOL)
    r.check("Fully-degenerate line drops below 2 vertices",
            changed2 and len(out2) == 1,
            f"out={out2}")
    # Duplicate vertex at the very start (backward removal must catch it)
    pts3 = [Point2(0, 0), Point2(0, 0), Point2(1, 0), Point2(1, 1)]
    out3, _ = clean_polyline_vertices(list(pts3), TOL)
    r.check("Removes duplicate at polyline start",
            len(out3) == 3 and out3[0].distance(Point2(0, 0)) < 1e-9,
            f"out={out3}")
    return r


def test_collinear_redundant_vertices():
    r = TestResult("HGAP consecutive collinear redundant vertices")
    # A->B->C->D straight line with 2 redundant middle points
    pts = [Point2(0, 0), Point2(3, 0), Point2(7, 0), Point2(10, 0)]
    out, changed = clean_polyline_vertices(list(pts), TOL)
    r.check("Removes all collinear redundant vertices",
            changed and len(out) == 2,
            f"out={out}")
    r.check("Endpoints preserved",
            out[0].distance(Point2(0, 0)) < 1e-9 and out[-1].distance(Point2(10, 0)) < 1e-9,
            f"out={out}")
    # Two collinear consecutive points (3 collinear, 1 not)
    pts2 = [Point2(0, 0), Point2(5, 0), Point2(10, 0), Point2(10, 5)]
    out2, _ = clean_polyline_vertices(list(pts2), TOL)
    r.check("Keeps turn point, removes only middle collinear one",
            len(out2) == 3 and out2[1].distance(Point2(10, 0)) < 1e-9,
            f"out={out2}")
    # Slight off-line point must NOT be removed (perp distance > tol)
    pts3 = [Point2(0, 0), Point2(5, 0.01), Point2(10, 0)]
    out3, changed3 = clean_polyline_vertices(list(pts3), TOL)
    r.check("Off-line vertex (0.01mm) preserved",
            not changed3 and len(out3) == 3,
            f"out={out3}")
    return r


def test_turnback_point_not_removed():
    r = TestResult("HGAP turn-back point protection")
    # A->B->A: b must NOT be treated as redundant (a and c coincide)
    a, b, c = Point2(0, 0), Point2(5, 0), Point2(0, 0)
    r.check("Turn-back point preserved (a==c guard)",
            not is_redundant_collinear(a, b, c, TOL),
            "is_redundant_collinear returned True")
    # B outside [A,C] range: A(0,0) B(-1,0) C(10,0) -> not redundant
    r.check("Point before segment start preserved",
            not is_redundant_collinear(Point2(0, 0), Point2(-1, 0), Point2(10, 0), TOL),
            "projection guard failed")
    # B beyond C: A(0,0) B(11,0) C(10,0) -> not redundant
    r.check("Point after segment end preserved",
            not is_redundant_collinear(Point2(0, 0), Point2(11, 0), Point2(10, 0), TOL),
            "projection guard failed")
    return r


def test_closed_chain_collinear_cleanup():
    r = TestResult("HGAP closed chain collinear cleanup (wrap-around)")
    # Triangle A(0,0)->B(10,0)->C(10,10) whose chain starts mid-edge at
    # F(5,0) (on AB). F is a redundant collinear vertex at the chain head;
    # the open-chain pass skips head/tail points, only the closed wrap-around
    # pass can remove it.
    pts, closed = finalize_chain(
        [Point2(5, 0), Point2(10, 0), Point2(10, 10), Point2(0, 0), Point2(5, 0)], TOL)
    r.check("Detected as closed", closed, "")
    r.check("Wrap-around collinear head vertex removed",
            len(pts) == 3,
            f"pts={pts}")
    r.check("Triangle corners preserved",
            all(any(p.distance(c) < 1e-9 for p in pts)
                for c in [Point2(0, 0), Point2(10, 0), Point2(10, 10)]),
            f"pts={pts}")
    return r


def test_closed_polyline_vertices_cleanup():
    r = TestResult("HGAP closed polyline vertex cleanup (aggregate lines)")
    # Closed rectangle with a duplicate vertex in the middle
    pts, changed = clean_closed_polyline_vertices(
        [Point2(0, 0), Point2(5, 0), Point2(5, 0), Point2(5, 5), Point2(0, 5)], TOL)
    r.check("Deduplicates duplicate vertex on closed polyline",
            changed and len(pts) == 4 and
            pts[1].distance(Point2(5, 0)) < 1e-9 and
            pts[2].distance(Point2(5, 5)) < 1e-9,
            f"pts={pts}")
    # Closed rectangle with a collinear redundant vertex on an edge
    pts2, changed2 = clean_closed_polyline_vertices(
        [Point2(0, 0), Point2(2.5, 0), Point2(5, 0), Point2(5, 5), Point2(0, 5)], TOL)
    r.check("Removes collinear redundant vertex on closed polyline",
            changed2 and len(pts2) == 4 and
            all(any(p.distance(c) < 1e-9 for p in pts2)
                for c in [Point2(0, 0), Point2(5, 0), Point2(5, 5), Point2(0, 5)]),
            f"pts={pts2}")
    # Wrap-around collinear vertex at the chain head (only closed pass removes it)
    pts3, changed3 = clean_closed_polyline_vertices(
        [Point2(2.5, 0), Point2(5, 0), Point2(5, 5), Point2(0, 5), Point2(0, 0)], TOL)
    r.check("Wrap-around collinear vertex removed on closed polyline",
            changed3 and len(pts3) == 4 and
            pts3[0].distance(Point2(5, 0)) < 1e-9,
            f"pts={pts3}")
    # Fully-degenerate closed ring collapses below 3 vertices -> invalid
    pts4, changed4 = clean_closed_polyline_vertices(
        [Point2(5, 5), Point2(5, 5), Point2(5, 5)], TOL)
    r.check("Degenerate closed ring drops below 3 vertices",
            changed4 and len(pts4) < 3,
            f"pts={pts4}")
    return r


def test_duplicate_standalone_lines_regression():
    r = TestResult("HGAP duplicate standalone lines still merge (regression)")
    # Two fully-overlapping open lines; one carries redundant collinear
    # vertices. Geometry identical after cleanup, duplicate must collapse
    # into one clean line (MergeHgapSegments + finalize chain).
    segs = []
    for start, end in [(Point2(0, 0), Point2(10, 0)), (Point2(10, 0), Point2(10, 10))]:
        segs.append((start, end))
    for start, end in [(Point2(0, 0), Point2(3, 0)), (Point2(3, 0), Point2(10, 0)), (Point2(10, 0), Point2(10, 10))]:
        segs.append((start, end))

    def merge(seg_list):
        # mirror MergeHgapSegments: union of collinear overlapping segments
        merged = []
        for s in seg_list:
            cur = s
            changed = True
            while changed:
                changed = False
                for j, m in enumerate(merged):
                    u = merge_two(cur, m)
                    if u is not None:
                        cur = u
                        del merged[j]
                        changed = True
                        break
            merged.append(cur)
        return merged

    def merge_two(s1, s2):
        a, b = s1
        c, d = s2
        length = a.distance(b)
        if length <= TOL:
            return None
        for p in (c, d):
            if not on_line(p, a, b):
                return None
        q0, q1 = sorted([proj(c, a, b), proj(d, a, b)])
        if min(length, q1) - max(0.0, q0) <= TOL:
            return None
        mn, mx = min(0.0, q0), max(length, q1)
        vx, vy = (b.x - a.x) / length, (b.y - a.y) / length
        return (Point2(a.x + vx * mn, a.y + vy * mn),
                Point2(a.x + vx * mx, a.y + vy * mx))

    def on_line(p, a, b):
        return abs((p.x - a.x) * (b.y - a.y) - (p.y - a.y) * (b.x - a.x)) <= TOL * max(a.distance(b), 1e-9)

    def proj(p, a, b):
        vx, vy = b.x - a.x, b.y - a.y
        return ((p.x - a.x) * vx + (p.y - a.y) * vy) / max(a.distance(b), 1e-9)

    merged = merge(segs)
    # Build chain: start at first seg, append collinear/connected ones
    chain = [merged[0][0], merged[0][1]]
    rest = merged[1:]
    while rest:
        progressed = False
        for k, sg in enumerate(rest):
            if chain[-1].distance(sg[0]) <= TOL:
                chain += [sg[0], sg[1]]
                del rest[k]
                progressed = True
                break
            if chain[-1].distance(sg[1]) <= TOL:
                chain += [sg[1], sg[0]]
                del rest[k]
                progressed = True
                break
        if not progressed:
            break
    pts, closed = finalize_chain(chain, TOL)
    r.check("Duplicate lines collapse to a single chain", len(merged) == 2, f"merged={merged}")
    r.check("Final polyline open with clean corners",
            not closed and len(pts) == 3,
            f"pts={pts} closed={closed}")
    r.check("Geometry equals the intended L-shape",
            pts[0].distance(Point2(0, 0)) < 1e-9 and
            pts[1].distance(Point2(10, 0)) < 1e-9 and
            pts[2].distance(Point2(10, 10)) < 1e-9,
            f"pts={pts}")
    return r


def run_all():
    tests = [
        test_zero_length_edges(),
        test_collinear_redundant_vertices(),
        test_turnback_point_not_removed(),
        test_closed_chain_collinear_cleanup(),
        test_closed_polyline_vertices_cleanup(),
        test_duplicate_standalone_lines_regression(),
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
