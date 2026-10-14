# HNM Containment Analysis Verification
# Tests hierarchical region containment logic

import sys, os, math
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from geom import Point2, Polyline, TestResult


def point_in_polygon(pt, poly):
    """Ray casting."""
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


def polygon_contains(outer, inner):
    """Check if outer polygon contains inner polygon (all inner vertices inside outer)."""
    return all(point_in_polygon(v, outer) for v in inner.verts)


def analyze_hierarchy(polys):
    """
    Analyze containment hierarchy.
    Returns: {parent_id: [child_ids]}, leaf_ids, root_ids
    """
    n = len(polys)
    # For each pair, check containment
    children = {i: [] for i in range(n)}
    parents = {i: [] for i in range(n)}

    for i in range(n):
        for j in range(n):
            if i == j:
                continue
            if polygon_contains(polys[i], polys[j]):
                children[i].append(j)
                parents[j].append(i)

    # Direct children only: if A contains B and B contains C, C is child of B, not A
    direct_children = {i: [] for i in range(n)}
    for i in range(n):
        for j in children[i]:
            # j is directly contained by i if no intermediate k exists
            is_direct = True
            for k in children[i]:
                if k != j and polygon_contains(polys[k], polys[j]):
                    is_direct = False
                    break
            if is_direct:
                direct_children[i].append(j)

    # Leaf nodes: have no children
    leaves = [i for i in range(n) if len(direct_children[i]) == 0]
    # Root nodes: have no parents
    roots = [i for i in range(n) if len(parents[i]) == 0]

    return direct_children, leaves, roots


def test_hnm_simple_nesting():
    r = TestResult("HNM Simple nesting (outer + inner)")
    outer = Polyline([Point2(0, 0), Point2(100, 0), Point2(100, 100), Point2(0, 100)])
    inner = Polyline([Point2(20, 20), Point2(80, 20), Point2(80, 80), Point2(20, 80)])
    polys = [outer, inner]

    children, leaves, roots = analyze_hierarchy(polys)
    r.check("Outer contains inner", 1 in children[0], f"children={children}")
    r.check("Inner is leaf", 1 in leaves, f"leaves={leaves}")
    r.check("Outer is root", 0 in roots, f"roots={roots}")
    return r


def test_hnm_double_nesting():
    r = TestResult("HNM Double nesting (3 levels)")
    outer = Polyline([Point2(0, 0), Point2(100, 0), Point2(100, 100), Point2(0, 100)])
    mid = Polyline([Point2(10, 10), Point2(90, 10), Point2(90, 90), Point2(10, 90)])
    inner = Polyline([Point2(30, 30), Point2(70, 30), Point2(70, 70), Point2(30, 70)])
    polys = [outer, mid, inner]

    children, leaves, roots = analyze_hierarchy(polys)
    r.check("Only outer is root", roots == [0], f"roots={roots}")
    r.check("Only inner is leaf", leaves == [2], f"leaves={leaves}")
    # Direct children: outer->mid, mid->inner (not outer->inner)
    r.check("Direct parent: mid contains inner", 2 in children[1],
            f"children[1]={children[1]}")
    return r


def test_hnm_siblings():
    r = TestResult("HNM Sibling regions (no nesting)")
    poly1 = Polyline([Point2(0, 0), Point2(50, 0), Point2(50, 50), Point2(0, 50)])
    poly2 = Polyline([Point2(60, 0), Point2(110, 0), Point2(110, 50), Point2(60, 50)])
    polys = [poly1, poly2]

    children, leaves, roots = analyze_hierarchy(polys)
    r.check("Both are roots", len(roots) == 2, f"roots={roots}")
    r.check("Both are leaves", len(leaves) == 2, f"leaves={leaves}")
    r.check("No containment", all(len(v) == 0 for v in children.values()))
    return r


def run_all():
    tests = [
        test_hnm_simple_nesting(),
        test_hnm_double_nesting(),
        test_hnm_siblings(),
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
