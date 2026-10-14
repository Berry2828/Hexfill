# HRS Respace Formula Verification
# Tests the spacing adjustment formula independently

import sys, os, math
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from geom import TestResult


def hrs_formula(original_spacing, percent):
    """
    HRS formula from HexFill.cs:
      brightness_ratio = 100 / (100 + P)
      new_spacing = original_spacing * sqrt(brightness_ratio)
      circle_count_change = 1 + P/100

    P > 0 → brightness_ratio < 1 → tighter → more circles
    P < 0 → brightness_ratio > 1 → looser → fewer circles
    P <= -100 → clear all
    """
    if percent <= -100:
        return 0.0  # clear all
    brightness_ratio = 100.0 / (100.0 + percent)
    new_spacing = original_spacing * math.sqrt(brightness_ratio)
    return max(new_spacing, 0.06)  # min spacing = max(diameter, minSpacing)


def expected_circle_ratio(percent):
    """Circle count change ratio: (1 + P/100)"""
    if percent <= -100:
        return 0.0
    return 1.0 + percent / 100.0


def test_hrs_increase():
    r = TestResult("HRS +50% (denser)")
    spacing = hrs_formula(0.3, 50)
    r.check("Spacing decreased", spacing < 0.3, f"new={spacing:.4f}")
    r.check("Formula correct: 0.3 * sqrt(100/150) = 0.245",
            abs(spacing - 0.2449) < 0.001, f"got {spacing:.4f}")
    ratio = expected_circle_ratio(50)
    r.check("Circle ratio 1.5x", abs(ratio - 1.5) < 0.001, f"ratio={ratio:.2f}")
    return r


def test_hrs_decrease():
    r = TestResult("HRS -50% (sparser)")
    spacing = hrs_formula(0.3, -50)
    r.check("Spacing increased", spacing > 0.3, f"new={spacing:.4f}")
    r.check("Formula correct: 0.3 * sqrt(100/50) = 0.424",
            abs(spacing - 0.4243) < 0.001, f"got {spacing:.4f}")
    ratio = expected_circle_ratio(-50)
    r.check("Circle ratio 0.5x", abs(ratio - 0.5) < 0.001, f"ratio={ratio:.2f}")
    return r


def test_hrs_clear():
    r = TestResult("HRS -100% (clear all)")
    spacing = hrs_formula(0.3, -100)
    r.check("Spacing = 0 (clear)", spacing == 0.0, f"spacing={spacing}")
    ratio = expected_circle_ratio(-100)
    r.check("Circle ratio = 0", ratio == 0.0)
    return r


def test_hrs_nochange():
    r = TestResult("HRS 0% (no change)")
    spacing = hrs_formula(0.3, 0)
    r.check("Spacing unchanged", abs(spacing - 0.3) < 0.001, f"spacing={spacing:.4f}")
    ratio = expected_circle_ratio(0)
    r.check("Circle ratio 1.0x", abs(ratio - 1.0) < 0.001)
    return r


def test_hrs_edge_cases():
    r = TestResult("HRS Edge cases")
    s1 = hrs_formula(0.1, 100)
    r.check("+100% halves spacing", abs(s1 - 0.0707) < 0.001, f"got {s1:.4f}")
    s2 = hrs_formula(0.1, -80)
    r.check("-80% spacing", abs(s2 - 0.2236) < 0.005, f"got {s2:.4f}")
    return r


def run_all():
    tests = [
        test_hrs_increase(),
        test_hrs_decrease(),
        test_hrs_clear(),
        test_hrs_nochange(),
        test_hrs_edge_cases(),
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
