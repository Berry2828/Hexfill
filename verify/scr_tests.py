#!/usr/bin/env python3
"""
HexFill SCR Test Generator
Generates AutoCAD SCR test scripts that can be run via SCRIPT command.
Each SCR draws test geometry, runs the command, and validates output.

Usage: python verify/scr_tests.py              # generate all SCR tests
       python verify/scr_tests.py HPLIT        # generate HPLIT test only
"""

import sys, os, math
from pathlib import Path
from datetime import datetime

VERIFY_DIR = Path(__file__).parent.absolute() if "__file__" in dir() else Path.cwd()
PROJECT_DIR = VERIFY_DIR.parent
SCR_DIR = PROJECT_DIR / "tests" / "scr"


def write_scr(name, content):
    """Write SCR file with proper line endings."""
    SCR_DIR.mkdir(parents=True, exist_ok=True)
    path = SCR_DIR / f"{name}.scr"
    with open(path, "w", encoding="ascii") as f:
        f.write(content)
    print(f"  Generated: {path}")
    return path


def generate_hplit_tests():
    """Generate HPLIT test SCRs."""
    tests = []

    # Test 1: Rectangle vertical cut
    scr = """_.ERASE _ALL
 
_.PLINE 0,0 100,0 100,100 0,100 _C
HPLIT
_W
-10,-10
110,110

50,-10
50,110
(setq ss (ssget "X" '((0 . "LWPOLYLINE"))))
(if ss
  (progn
    (setq n (sslength ss))
    (princ (strcat "\\nHPLIT-RECT: Polylines=" (itoa n)))
    (if (= n 2)
      (princ "\\n>>> TEST_PASSED: 2 polylines created")
      (princ (strcat "\\n>>> TEST_FAILED: expected 2, got " (itoa n)))
    )
  )
  (princ "\\n>>> TEST_FAILED: no polylines found")
)
"""
    path = write_scr("hplit_rect_vertical", scr)
    tests.append(("HPLIT Rectangle 100x100 vertical cut", path))

    # Test 2: Rectangle diagonal cut
    scr = """_.ERASE _ALL
 
_.PLINE 0,0 100,0 100,100 0,100 _C
HPLIT
_W
-10,-10
110,110

-10,-10
110,110
(setq ss (ssget "X" '((0 . "LWPOLYLINE"))))
(if ss
  (progn
    (setq n (sslength ss))
    (princ (strcat "\\nHPLIT-DIAG: Polylines=" (itoa n)))
    (if (= n 2)
      (princ "\\n>>> TEST_PASSED: 2 polylines created")
      (princ (strcat "\\n>>> TEST_FAILED: expected 2, got " (itoa n)))
    )
  )
  (princ "\\n>>> TEST_FAILED: no polylines found")
)
"""
    path = write_scr("hplit_rect_diagonal", scr)
    tests.append(("HPLIT Rectangle diagonal cut", path))

    # Test 3: L-shape cut
    scr = """_.ERASE _ALL
 
_.PLINE 0,0 100,0 100,40 40,40 40,100 0,100 _C
HPLIT
_W
-10,-10
110,110

70,-10
70,110
(setq ss (ssget "X" '((0 . "LWPOLYLINE"))))
(if ss
  (progn
    (setq n (sslength ss))
    (princ (strcat "\\nHPLIT-LSHAPE: Polylines=" (itoa n)))
    (if (= n 2)
      (princ "\\n>>> TEST_PASSED: 2 polylines created")
      (princ (strcat "\\n>>> TEST_FAILED: expected 2, got " (itoa n)))
    )
  )
  (princ "\\n>>> TEST_FAILED: no polylines found")
)
"""
    path = write_scr("hplit_lshape", scr)
    tests.append(("HPLIT L-shape cut", path))

    # Test 4: Narrow polyline (stress test)
    scr = """_.ERASE _ALL
 
_.PLINE 0,0 10,0 10,3 0,3 _C
HPLIT
_W
-2,-2
12,5

5,-2
5,5
(setq ss (ssget "X" '((0 . "LWPOLYLINE"))))
(if ss
  (progn
    (setq n (sslength ss))
    (princ (strcat "\\nHPLIT-NARROW: Polylines=" (itoa n)))
    (if (= n 2)
      (princ "\\n>>> TEST_PASSED: 2 polylines created")
      (princ (strcat "\\n>>> TEST_FAILED: expected 2, got " (itoa n)))
    )
  )
  (princ "\\n>>> TEST_FAILED: no polylines found")
)
"""
    path = write_scr("hplit_narrow", scr)
    tests.append(("HPLIT Narrow polyline", path))

    return tests


def generate_hgr_tests():
    """Generate HGR test SCRs."""
    tests = []

    scr = """_.ERASE _ALL
 
_.PLINE 0,0 50,0 50,50 0,50 _C
HGR
_W
-5,-5
55,55

0.3
(setq ss (ssget "X" '((0 . "CIRCLE"))))
(if ss
  (progn
    (setq n (sslength ss))
    (princ (strcat "\\nHGR-RECT: Circles=" (itoa n)))
    (if (> n 0)
      (princ "\\n>>> TEST_PASSED: circles generated")
      (princ "\\n>>> TEST_FAILED: no circles")
    )
  )
  (princ "\\n>>> TEST_FAILED: no circles found")
)
"""
    path = write_scr("hgr_basic", scr)
    tests.append(("HGR Basic rectangle 50x50", path))
    return tests


def generate_hof_tests():
    """Generate HOF test SCRs."""
    tests = []

    # Note: HOF uses a dialog, so SCR testing is limited
    # This tests if HOF command loads without error
    scr = """_.ERASE _ALL
 
_.PLINE 0,0 100,0 100,100 0,100 _C
HOF
"""
    path = write_scr("hof_load", scr)
    tests.append(("HOF command load test", path))
    return tests


def generate_load_test():
    """Generate NETLOAD test to verify DLL loads without error."""
    scr = """_.ERASE _ALL
 
(princ "\\nDLL load test - check for load errors above")
(princ "\\n>>> TEST_INFO: If no errors, DLL loaded successfully")
"""
    path = write_scr("load_test", scr)
    return [("DLL load test", path)]


def generate_all():
    """Generate all test SCRs."""
    all_tests = []

    print("\n=== Generating HPLIT test SCRs ===")
    all_tests.extend(generate_hplit_tests())

    print("\n=== Generating HGR test SCRs ===")
    all_tests.extend(generate_hgr_tests())

    print("\n=== Generating HOF test SCRs ===")
    all_tests.extend(generate_hof_tests())

    print("\n=== Generating load test SCR ===")
    all_tests.extend(generate_load_test())

    # Generate batch runner
    batch = '; HexFill Test Batch\n; Run in AutoCAD: _.SCRIPT "path/to/this.scr"\n; Make sure to NETLOAD HexFill.dll first!\n'
    batch += f'; Generated: {datetime.now().isoformat()}\n'
    batch += '(princ "\\n========================================")\n'
    batch += '(princ "\\n  HexFill Automated Test Suite")\n'
    batch += '(princ "\\n========================================\\n")\n'

    # Add NETLOAD hint
    dlls = sorted(Path.home().glob("Desktop/HexFill_*.dll"), key=lambda p: p.stat().st_mtime, reverse=True)
    if dlls:
        latest = str(dlls[0]).replace("\\", "/")
        batch += f'(if (not (member "HPLIT" (atoms-family 0))) (command "_.NETLOAD" "{latest}"))\n'
        batch += f'(princ "\\nUsing DLL: {Path(latest).name}\\n")\n'

    for name, path in all_tests:
        batch += f'\n(princ "\\n--- {name} ---\\n")\n'
        batch += f'(command "_.SCRIPT" "{str(path).replace(chr(92), "/")}")\n'

    batch += '\n(princ "\\n========================================\\n")\n'
    batch += '(princ "  Test Suite Complete\\n")\n'
    batch += '(princ "========================================\\n")\n'

    batch_path = write_scr("_run_all", batch)
    print(f"\n=== Batch runner: {batch_path} ===")

    return all_tests


if __name__ == "__main__":
    tests = generate_all()
    print(f"\nGenerated {len(tests)} test SCRs in {SCR_DIR}")
    print(f"\nTo run in AutoCAD:")
    print(f"  1. NETLOAD HexFill_*.dll")
    print(f'  2. _.SCRIPT "{SCR_DIR / "_run_all.scr"}"')
