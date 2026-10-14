# HDIA Diameter Toggle Verification
# Simple state toggle: 0.03mm <-> 0.06mm

import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from geom import TestResult


def hdia_toggle(current):
    """Toggle between 0.03 and 0.06"""
    if abs(current - 0.03) < 0.001:
        return 0.06
    else:
        return 0.03


def test_hdia():
    r = TestResult("HDIA Diameter toggle")
    d1 = hdia_toggle(0.03)
    r.check("0.03 -> 0.06", abs(d1 - 0.06) < 0.001, f"got {d1}")
    d2 = hdia_toggle(d1)
    r.check("0.06 -> 0.03", abs(d2 - 0.03) < 0.001, f"got {d2}")
    d3 = hdia_toggle(d2)
    r.check("0.03 -> 0.06 again", abs(d3 - 0.06) < 0.001, f"got {d3}")
    return r


def run_all():
    t = test_hdia()
    print(t.report())
    return t.passed


if __name__ == "__main__":
    ok = run_all()
    print(f"\n{'='*50}")
    print(f"  OVERALL: {'ALL PASS' if ok else 'SOME FAILED'}")
    print(f"{'='*50}")
    import sys; sys.exit(0 if ok else 1)
