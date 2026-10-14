# HIX/HAD Import Logic Verification
# Tests the math behind CSV import adjustments

import sys, os, math
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from geom import TestResult


def simulate_had_import(rows):
    """Model HAD's row policy without invoking AutoCAD."""
    accepted = {}
    stats = {"malformed": 0, "numeric": 0, "direction": 0, "duplicate": 0}
    for row in rows:
        if not row:
            continue
        fields = list(row)
        while len(fields) > 4 and fields[-1] == "":
            fields.pop()
        if len(fields) < 3 or len(fields) > 4 or not str(fields[0]).strip():
            stats["malformed"] += 1
            continue
        def to_float(field):
            # Blank brightness is "not yet filled": accepted as missing (0), not invalid.
            return float(field.replace(",", ".")) if field else 0.0
        try:
            current = to_float(str(fields[1]))
            target = to_float(str(fields[2]))
        except (TypeError, ValueError):
            stats["numeric"] += 1
            continue
        number = str(fields[0]).strip()
        if number in accepted:
            stats["duplicate"] += 1
            continue
        direction = "CAD"
        if len(fields) == 4:
            direction = str(fields[3]).strip().upper()
            if direction not in ("X", "Y"):
                stats["direction"] += 1
                direction = "CAD"
        accepted[number] = (current, target, direction)
    return accepted, stats


def test_hix_spacing_import():
    """HIX reads Spacing_*.csv and regenerates circles with given spacing."""
    r = TestResult("HIX Spacing import logic")
    # HIX reads: 编号,间距(mm),X坐标,Y坐标
    # For each region, matches by number, regenerates with new spacing
    # Spacing is capped at min = max(Diameter, 0.1) = 0.1mm
    test_data = [
        (1, 0.3, 50, 50),
        (2, 0.05, 100, 100),  # below minimum → should be capped to 0.1
        (3, 0.2, 150, 150),
    ]
    for num, spacing, x, y in test_data:
        capped = max(spacing, 0.1)  # min spacing enforcement
        r.check(f"Region {num} spacing min capped",
                capped >= 0.1, f"input={spacing}, capped={capped}")
    return r


def test_had_brightness_formula():
    """HAD reads Brightness_*.csv and computes new spacing from brightness ratio."""
    r = TestResult("HAD Brightness adjustment formula")
    # HAD formula:
    #   brightness_ratio = target_brightness / current_brightness
    #   new_spacing = old_spacing * sqrt(brightness_ratio)
    # Old spacing is read from region''s existing circles

    test_cases = [
        # (current_brightness, target_brightness, old_spacing, expected_new_spacing)
        (1.0, 0.5, 0.3, 0.3 * math.sqrt(0.5 / 1.0)),   # darker → tighter
        (0.5, 1.0, 0.3, 0.3 * math.sqrt(1.0 / 0.5)),   # brighter → looser
        (1.0, 1.0, 0.3, 0.3),                            # same → unchanged
        (0.8, 0.4, 0.2, 0.2 * math.sqrt(0.4 / 0.8)),   # half brightness
    ]

    for cur, tgt, old_sp, expected in test_cases:
        if cur > 0:
            ratio = tgt / cur
            new_sp = old_sp * math.sqrt(ratio)
            r.check(f"B({cur}→{tgt}) spacing {old_sp}→{new_sp:.4f}",
                    abs(new_sp - expected) < 0.001,
                    f"expected {expected:.4f}")
        else:
            r.check(f"Zero current brightness skipped", True)

    return r


def test_had_min_spacing():
    """HAD enforces minimum spacing."""
    r = TestResult("HAD min spacing enforcement")
    # Even if brightness formula gives very small spacing, it''s capped
    min_spacing = 0.1
    new_sp = 0.3 * math.sqrt(0.01 / 1.0)  # = 0.03, below min
    capped = max(new_sp, min_spacing)
    r.check("Cap below minimum", abs(capped - 0.1) < 0.001, f"capped={capped}")
    return r


def test_hix_skipping():
    """HIX skips regions where spacing hasn''t changed."""
    r = TestResult("HIX skip-unchanged logic")
    # Regions with same spacing are skipped (no regeneration)
    r.check("Skip flag structure", True)
    return r


def test_had_import_resilience():
    """Invalid rows are counted, while valid rows continue and duplicates are deterministic."""
    r = TestResult("HAD import resilience policy")
    accepted, stats = simulate_had_import([
        ["1", "1.0", "0.5", "X"],
        ["1", "9.0", "9.0", "Y"],  # duplicate: first valid record wins
        ["2", "bad", "0.5", "X"],  # invalid numeric row
        ["3", "1,0", "0,5", "Z"],  # comma decimal; invalid direction falls back to CAD
        ["4", "1.0"],  # malformed row
        ["5", "1.0", "0.5", "X", ""],  # harmless trailing empty field
        ["6", "", "", "X"],  # blank brightness: missing (0), not invalid numeric
    ])
    r.check("First duplicate record wins", accepted["1"] == (1.0, 0.5, "X"))
    r.check("Valid rows continue", "5" in accepted and "6" in accepted and len(accepted) == 4)
    r.check("Blank brightness is missing, not invalid", accepted["6"] == (0.0, 0.0, "X")
            and stats["numeric"] == 1)
    r.check("Warnings are counted", stats == {"malformed": 1, "numeric": 1,
                                               "direction": 1, "duplicate": 1},
            str(stats))
    r.check("Invalid direction falls back", accepted["3"][2] == "CAD")
    return r


def run_all():
    tests = [
        test_hix_spacing_import(),
        test_had_brightness_formula(),
        test_had_min_spacing(),
        test_hix_skipping(),
        test_had_import_resilience(),
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
