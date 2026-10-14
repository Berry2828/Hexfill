# HexFill Verification Framework
# Simulates geometric operations to verify command logic before AutoCAD deployment

import math
from dataclasses import dataclass
from typing import List, Tuple, Optional

@dataclass
class Point2:
    x: float
    y: float
    
    def __hash__(self): return hash((round(self.x, 6), round(self.y, 6)))
    def __eq__(self, other): return abs(self.x - other.x) < 1e-9 and abs(self.y - other.y) < 1e-9
    def distance(self, other): return math.hypot(self.x - other.x, self.y - other.y)
    def __repr__(self): return f"({self.x:.4f}, {self.y:.4f})"
    def __sub__(self, other): return Vec2(self.x - other.x, self.y - other.y)

@dataclass
class Vec2:
    x: float
    y: float
    def length(self): return math.hypot(self.x, self.y)
    def normalize(self):
        l = self.length()
        return Vec2(self.x/l, self.y/l) if l > 1e-12 else Vec2(0, 0)
    def dot(self, other): return self.x * other.x + self.y * other.y
    def perp(self): return Vec2(-self.y, self.x)
    def __mul__(self, s): return Vec2(self.x * s, self.y * s)
    def __rmul__(self, s): return Vec2(self.x * s, self.y * s)
    def __repr__(self): return f"Vec({self.x:.4f}, {self.y:.4f})"

class Polyline:
    """Simple closed polyline (no arcs)"""
    def __init__(self, vertices: List[Point2]):
        self.verts = vertices
        self.n = len(vertices)
    
    def edge(self, i: int) -> Tuple[Point2, Point2]:
        return self.verts[i], self.verts[(i + 1) % self.n]
    
    def total_length(self) -> float:
        return sum(self.verts[i].distance(self.verts[(i + 1) % self.n]) for i in range(self.n))
    
    def parameter_at_point(self, pt: Point2) -> float:
        """Distance from start along the polyline to the point on the curve"""
        dist = 0.0
        for i in range(self.n):
            a, b = self.edge(i)
            if point_on_segment(pt, a, b, 0.001):
                dist += a.distance(pt)
                return dist
            dist += a.distance(b)
        # Point not on polyline, find closest
        return -1.0

    def point_at_parameter(self, param: float) -> Point2:
        """Get point at given distance from start"""
        dist = 0.0
        for i in range(self.n):
            a, b = self.edge(i)
            seg_len = a.distance(b)
            if dist + seg_len >= param - 1e-9:
                t = (param - dist) / seg_len
                t = max(0, min(1, t))
                return Point2(a.x + t * (b.x - a.x), a.y + t * (b.y - a.y))
            dist += seg_len
        return self.verts[0]


def point_on_segment(pt: Point2, a: Point2, b: Point2, tol: float = 0.001) -> bool:
    """Check if point is on line segment ab within tolerance"""
    ab = b - a
    l2 = ab.dot(ab)
    if l2 < 1e-12:
        return pt.distance(a) < tol
    t = max(0, min(1, (pt - a).dot(ab) / l2))
    closest = Point2(a.x + t * ab.x, a.y + t * ab.y)
    return pt.distance(closest) < tol


def point_on_infinite_line(pt: Point2, a: Point2, b: Point2, tol: float = 0.01) -> bool:
    """Check if point is on the infinite line through a and b"""
    ab = b - a
    l2 = ab.dot(ab)
    if l2 < 1e-12:
        return pt.distance(a) < tol
    t = (pt - a).dot(ab) / l2
    closest = Point2(a.x + t * ab.x, a.y + t * ab.y)
    return pt.distance(closest) < tol


def segment_intersection(a1: Point2, a2: Point2, b1: Point2, b2: Point2) -> Optional[Point2]:
    """Line segment intersection (with extension)"""
    d = (a1.x - a2.x) * (b1.y - b2.y) - (a1.y - a2.y) * (b1.x - b2.x)
    if abs(d) < 1e-12:
        return None
    t = ((a1.x - b1.x) * (b1.y - b2.y) - (a1.y - b1.y) * (b1.x - b2.x)) / d
    return Point2(a1.x + t * (a2.x - a1.x), a1.y + t * (a2.y - a1.y))


class TestResult:
    def __init__(self, name: str):
        self.name = name
        self.passed = True
        self.checks = []
    
    def check(self, desc: str, condition: bool, detail: str = ""):
        self.checks.append((desc, condition, detail))
        if not condition:
            self.passed = False
    
    def report(self) -> str:
        status = "PASS" if self.passed else "FAIL"
        lines = [f"\n{'='*50}", f"  {self.name}: {status}", f"{'='*50}"]
        for desc, ok, detail in self.checks:
            mark = "+" if ok else "X"
            lines.append(f"  [{mark}] {desc}")
            if detail and not ok:
                lines.append(f"      {detail}")
        return "\n".join(lines)
