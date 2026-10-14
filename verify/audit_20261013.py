"""Release audit: desktop DLL, temporary drawings, isolated profile.

Some PASS assertions confirm existing defects; these are reproduction checks,
not acceptance tests asserting that the release is free of defects.
"""
from pathlib import Path
import math
import shutil
import tempfile

import commands_core_smoke as core

ROOT = Path(__file__).resolve().parent.parent
core.DLL = Path(r"C:\Users\allen\Desktop\HexFill_20260920_Full.dll")
core.LOGS = ROOT / "obj/audit-20261013/edge-cases"


def main():
    core.LOGS.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="hexfill-audit-") as directory:
        scratch = Path(directory)
        shutil.copy2(ROOT / "tests/load_quit.dwg", scratch / "test.dwg")
        body = [
            '(command "_.ERASE" "_ALL" "")',
            '(command "_.-LAYER" "_Make" "HF_AUDIT_LOCK" "")',
            '(entmake \'((0 . "CIRCLE") (8 . "HF_AUDIT_LOCK") (10 5.0 5.0 0.0) (40 . 0.03)))',
            '(setq audit-old (entlast))',
            '(command "_.-LAYER" "_Set" "0" "_Lock" "HF_AUDIT_LOCK" "")',
            '(command "_.RECTANG" "0,0" "10,10")',
            '(setq audit-region (entlast))',
            '(command "HGR" (ssadd audit-region) "" "2")',
            core.check("locked-old-circle-still-exists", '(entget audit-old)'),
            core.check("new-circles-added-despite-erase-failure", '(> (hf-count \'((0 . "CIRCLE") (8 . "0"))) 0)'),
            core.check("lock-remains-enabled", '(= 4 (logand 4 (cdr (assoc 70 (tblsearch "LAYER" "HF_AUDIT_LOCK")))))'),
        ]
        core.run_core(scratch, "locked-circle-replacement", body)

        # A valid spacing CSV exported through Excel's Unicode Text option.
        # Verify BOM detection in the .NET reader, rather than assuming UTF8
        # in File.ReadAllLines disables recognition of UTF16.
        data = scratch / "Spacing_unicode.csv"
        data.write_text("Number\tSpacing\tX\tY\tDirection\n101\t1.4\t5\t5\tX\n", encoding="utf-16")
        body = [
            '(command "_.ERASE" "_ALL" "")',
            '(regapp "H_Num")',
            '(regapp "H_IsLeaf")',
            '(command "_.RECTANG" "0,0" "10,10")',
            '(setq audit-region (entlast))',
            '(defun audit-tag (e app value) (regapp app) (entmod (append (entget e) (list (list -3 (list app (cons 1000 value)))))))',
            '(audit-tag audit-region "H_Num" "101")',
            '(audit-tag audit-region "H_IsLeaf" "1")',
            core.check("numbering-fixture-is-valid", '(assoc -3 (entget audit-region \'("H_Num")))'),
            '(command "HGR" (ssadd audit-region) "" "2")',
            '(setq audit-count (hf-count \'((0 . "CIRCLE"))))',
            '(setvar "FILEDIA" 0)',
            'HIX',
            '"{}"'.format(data.as_posix()),
            '_ALL',
            '',
            core.check("utf16-input-updates-circles", '(> (hf-count \'((0 . "CIRCLE"))) audit-count)'),
        ]
        core.run_core(scratch, "hix-utf16", body)

        data = scratch / "Spacing_nonfinite.csv"
        data.write_text("Number,Spacing,X,Y,Direction\n101,Infinity,5,5,X\n", encoding="utf-8-sig")
        body = [
            '(command "_.ERASE" "_ALL" "")',
            '(regapp "H_Num")',
            '(command "_.RECTANG" "0,0" "10,10")',
            '(setq audit-region (entlast))',
            '(defun audit-tag (e app value) (regapp app) (entmod (append (entget e) (list (list -3 (list app (cons 1000 value)))))))',
            '(audit-tag audit-region "H_Num" "101")',
            core.check("numbering-fixture-is-valid", '(assoc -3 (entget audit-region \'("H_Num")))'),
            '(entmake \'((0 . "CIRCLE") (8 . "0") (10 5.0 5.0 0.0) (40 . 0.015)))',
            '(setq audit-old (entlast))',
            '(setvar "FILEDIA" 0)',
            'HIX',
            '"{}"'.format(data.as_posix()),
            '_ALL',
            '',
            core.check("infinite-spacing-reached-generation-and-marked-old-circle", '(= 1 (cdr (assoc 62 (entget audit-old))))'),
        ]
        core.run_core(scratch, "hix-infinite-spacing", body)

        body = [
            '(command "_.ERASE" "_ALL" "")',
            '(command "_.RECTANG" "0,0" "5000,5000")',
            '(setq audit-region (entlast))',
            '(command "HGR" (ssadd audit-region) "" "0.1")',
            core.check("oversized-grid-created-no-circles", '(= 0 (hf-count \'((0 . "CIRCLE"))))'),
        ]
        core.run_core(scratch, "grid-int32-overflow", body)
        output = (core.LOGS / "grid-int32-overflow.log").read_bytes().decode("utf-16-le", errors="replace")
        assert "Parameter name: capacity" in output, "Expected the specific grid-capacity error"

    for size, spacing, radius in [(100, 0.06, 0.015), (5000, 0.1, 0.015)]:
        rows = math.floor((size - 2 * radius) / (spacing * math.sqrt(3) / 2)) + 1
        cols = math.floor((size - 2 * radius) / spacing) + 1
        product = rows * cols
        print("Grid estimate: size={} spacing={} rows={} cols={} candidates={} nine_scans={} int32_overflow={}".format(
            size, spacing, rows, cols, product, 9 * product, product > 2**31 - 1))


if __name__ == "__main__":
    main()
