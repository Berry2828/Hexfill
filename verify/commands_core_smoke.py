"""Run real command regressions without desktop COM or user drawing changes.

The full DLL must already be built. Uses isolated Core Console profiles and a
temporary drawing copy; export-directory preferences are restored on exit.
"""

import csv
from contextlib import contextmanager
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import winreg
from cad_runtime_guard import require_cad_runtime

from acad_verify import (
    HNM_CASES, HTC_CASES, GRID_CASES, HGAP_CASES, HSET_CASES,
    HCHK_CASES, HGAP_MARKER_LAYER, htc_dot_errors,
)


ROOT = Path(__file__).resolve().parent.parent
DLL = ROOT / "bin/Release/HexFill.dll"
CONSOLE = Path(r"D:\Program Files\Autodesk\AutoCAD 2018\accoreconsole.exe")
LOGS = ROOT / "obj/commands-core-smoke"
HELPERS = r'''
(setvar "FILEDIA" 1)
(setvar "CMDDIA" 1)
(setvar "CMDECHO" 0)
(setvar "OSMODE" 0)
(setvar "SNAPMODE" 0)
(command "_.-LAYER" "_Unlock" "*" "")
(command "_.ZOOM" "_C" "50,50" "350")
(defun hf-count (filter / s) (if (setq s (ssget "_X" filter)) (sslength s) 0))
(defun hf-check (name ok) (write-line (strcat (if ok "PASS " "FAIL ") name) hf-result))
(defun hf-dump (path / f s i d p)
  (setq f (open path "w") s (ssget "_X" '((0 . "CIRCLE"))) i 0)
  (if s (repeat (sslength s)
    (setq d (entget (ssname s i)) p (cdr (assoc 10 d)) i (1+ i))
    (write-line (strcat (rtos (car p) 2 12) "," (rtos (cadr p) 2 12) "," (rtos (cdr (assoc 40 d)) 2 12)) f)))
  (close f))
'''


@contextmanager
def preserve_export_paths():
    names = ("LastHexExportDirectory", "LastHbrExportDirectory")
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, r"Software\HexFill") as key:
        saved = {}
        for name in names:
            try:
                saved[name] = winreg.QueryValueEx(key, name)
            except FileNotFoundError:
                saved[name] = None
        try:
            yield key
        finally:
            for name, value in saved.items():
                if value is None:
                    try:
                        winreg.DeleteValue(key, name)
                    except FileNotFoundError:
                        pass
                else:
                    winreg.SetValueEx(key, name, 0, value[1], value[0])


def run_core(scratch, name, body):
    require_cad_runtime()
    result = scratch / (name + ".txt")
    script = scratch / (name + ".scr")
    script.write_text(
        '_.NETLOAD\n"{}"\n'.format(DLL.as_posix()) + HELPERS.lstrip()
        + '(setq hf-result (open "{}" "w"))\n'.format(result.as_posix())
        + "\n".join(body) + '\n(hf-check "file-dialogs-enabled" (= 1 (getvar "FILEDIA")))\n'
        + '(close hf-result)\n_.QUIT\n_Yes\n',
        encoding="gbk")
    with (LOGS / (name + ".log")).open("wb") as output:
        completed = subprocess.run(
            [str(CONSOLE), "/i", str(scratch / "test.dwg"), "/s", str(script),
             "/l", "en-US", "/isolate", "HexFillHclnSmoke", str(scratch / "profile")],
            cwd=scratch, stdout=output, stderr=subprocess.STDOUT,
            timeout=120, creationflags=subprocess.CREATE_NO_WINDOW | subprocess.BELOW_NORMAL_PRIORITY_CLASS)
    rows = result.read_text(encoding="ascii").splitlines() if result.exists() else []
    (LOGS / (name + ".txt")).write_text("\n".join(rows), encoding="ascii")
    expected_checks = sum(line.startswith('(hf-check ') for line in body) + 1
    if completed.returncode != 0 or len(rows) != expected_checks or any(not row.startswith("PASS ") for row in rows):
        raise AssertionError("{} failed: exit={}, results={}; see {}".format(
            name, completed.returncode, rows, LOGS))
    print("\n".join(rows), flush=True)
    return rows


def check(name, expression):
    return '(hf-check "{}" {})'.format(name, expression)


def circles(path):
    with path.open(encoding="ascii") as stream:
        return [{"center": (float(x), float(y)), "radius": float(radius)}
                for x, y, radius in csv.reader(stream)]


def main():
    LOGS.mkdir(parents=True, exist_ok=True)
    results = []
    with tempfile.TemporaryDirectory(prefix="hexfill-commands-") as directory, preserve_export_paths() as key:
        scratch = Path(directory)
        shutil.copy2(ROOT / "tests/load_quit.dwg", scratch / "test.dwg")
        body = []
        for case in HNM_CASES:
            body.extend(case["script"]())
            body.extend([
                check("HNM-" + case["name"] + "-texts",
                      '(= {} (hf-count \'((0 . "TEXT,MTEXT") (8 . "H_NUM"))))'.format(case["expected_texts"])),
                check("HNM-" + case["name"] + "-regions",
                      '(= {} (hf-count \'((0 . "LWPOLYLINE"))))'.format(case["expected_polylines"])),
                check("HNM-" + case["name"] + "-closed",
                      '(= {} (hf-count \'((0 . "LWPOLYLINE") (70 . 1))))'.format(case["expected_closed"])),
            ])
        results.extend(run_core(scratch, "numbering", body))

        body = ['(command "_.ZOOM" "_C" "200,50" "800")']
        for command, cases in (("HGAP", HGAP_CASES), ("HSET", HSET_CASES), ("HCHK", HCHK_CASES)):
            for case in cases:
                body.extend(case["script"]())
                name = command + "-" + case["name"]
                body.append(check(name + "-regions", '(= {} (hf-count \'((0 . "LWPOLYLINE"))))'.format(case["expected_polylines"])))
                if "expected_closed" in case:
                    body.append(check(name + "-closed", '(= {} (hf-count \'((0 . "LWPOLYLINE") (70 . 1))))'.format(case["expected_closed"])))
                marker_layer = "HCHK_\u56fe\u5f62\u5065\u5eb7\u6807\u8bc6" if command == "HCHK" else HGAP_MARKER_LAYER
                for field, operator in (("marker_min", ">="), ("marker_max", "<=")):
                    if field in case:
                        body.append(check(name + "-" + field, '({} (hf-count \'((8 . "{}"))) {})'.format(operator, marker_layer, case[field])))
        results.extend(run_core(scratch, "repair-settings", body))

        body = []
        for name, script in HTC_CASES:
            body.append("HDIA")
            body.extend(script())
            body.append(check("HTC-" + name + "-original-kept", '(= 1 (hf-count \'((0 . "TEXT,MTEXT"))))'))
            body.append('(hf-dump "{}")'.format((scratch / (name + ".csv")).as_posix()))
        results.extend(run_core(scratch, "text-dots", body))
        for name, _ in HTC_CASES:
            generated = circles(scratch / (name + ".csv"))
            errors = htc_dot_errors(generated)
            if name == "zero_hole" and generated:
                xs, ys = zip(*(c["center"] for c in generated))
                cx, cy = (min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2
                if any(abs(x - cx) < 0.4 and abs(y - cy) < 0.4 for x, y in zip(xs, ys)):
                    errors.append("zero glyph hole filled")
            if errors:
                raise AssertionError("HTC {}: {}".format(name, errors))
            row = "PASS HTC-{}-geometry ({} dots; diameter=0.03 spacing=0.06)".format(name, len(generated))
            print(row, flush=True)
            results.append(row)

        body = GRID_CASES[0]["script"]()
        for layer, count in GRID_CASES[0]["expected_layers"].items():
            body.append(check(layer + "-preserves-circles", '(= {} (hf-count \'((0 . "CIRCLE") (8 . "{}"))))'.format(count, layer)))
        body.extend(["_.ERASE", "_ALL", "", "HBAL", "_non", "0,0", "_non", "10,0", "_non", "20,0", "Done",
                     check("HBAL-three-blocks", '(= 3 (hf-count \'((0 . "INSERT") (2 . "H_BALLOON_DEF"))))')])
        results.extend(run_core(scratch, "grid-balloons", body))

        body = ["_.ERASE", "_ALL", "", "_.-LAYER", "_M", "LOCKED_KEEP", "",
                "_.RECTANG", "0,0", "100,100", '(setq hf-locked (entlast) hf-before (entget hf-locked))',
                "_.-LAYER", "_S", "0", "", "_.RECTANG", "150,0", "250,100",
                "_.-LAYER", "_LO", "LOCKED_KEEP", "",
                "HPLIT", "_ALL", "", "_non", "-10,50", "_non", "260,50", "",
                check("HPLIT-locked-source-preserved", '(equal hf-before (entget hf-locked))'),
                check("HPLIT-unlocked-split", '(= 3 (hf-count \'((0 . "LWPOLYLINE"))))'),
                "HJOIN", "_ALL", "",
                check("HJOIN-locked-source-preserved", '(equal hf-before (entget hf-locked))'),
                check("HJOIN-unlocked-merged", '(= 2 (hf-count \'((0 . "LWPOLYLINE"))))')]
        results.extend(run_core(scratch, "locked-layers", body))

        setup = ["_.ERASE", "_ALL", "", "_.PLINE", "0,0", "30,0", "30,30", "0,30", "_C",
                 "_.PLINE", "5,5", "25,5", "25,25", "5,25", "_C",
                 "HNM", "1", "_ALL", "", "HGR", "_W", "4,4", "26,26", "", "2"]
        body = setup + ["HEX", "_ALL", "", "HBR", "_ALL", "", "HCN", "_ALL", "",
                check("exports-have-circles", '(> (hf-count \'((0 . "CIRCLE"))) 20)')]
        results.extend(run_core(scratch, "exports", body))
        spacing = next(scratch.glob("*Spacing_*.csv"))
        brightness = next(scratch.glob("*Brightness_*.csv"))
        with next(scratch.glob("*Count_*.csv")).open(encoding="utf-8-sig", newline="") as stream:
            count_rows = list(csv.reader(stream))
        assert len(count_rows) == 2 and int(count_rows[1][1]) > 20
        results.append("PASS HCN-count-export")
        for preference in ("LastHexExportDirectory", "LastHbrExportDirectory"):
            assert Path(winreg.QueryValueEx(key, preference)[0]) == scratch
            results.append("PASS " + preference + "-persisted")
        with spacing.open(encoding="utf-8-sig", newline="") as stream:
            spacing_rows = list(csv.reader(stream))
        assert len(spacing_rows) == 2 and abs(float(spacing_rows[1][1]) - 2) < 0.01
        spacing_rows[1][1] = "1.4"
        with spacing.open("w", encoding="utf-8-sig", newline="") as stream:
            csv.writer(stream).writerows(spacing_rows)
        with brightness.open(encoding="utf-8-sig", newline="") as stream:
            brightness_rows = list(csv.reader(stream))
        assert len(brightness_rows) == 2
        brightness_rows[1][1:3] = ["1,0000", "0,2500"]
        with brightness.open("w", encoding="utf-16", newline="") as stream:
            csv.writer(stream, delimiter=";").writerows(brightness_rows)
        body = setup + ["HIX", '"{}"'.format(spacing.as_posix()), "_ALL", "",
                '(hf-dump "{}")'.format((scratch / "hix.csv").as_posix()),
                "HAD", '"{}"'.format(brightness.as_posix()), "_ALL", "",
                '(hf-dump "{}")'.format((scratch / "had.csv").as_posix())]
        results.extend(run_core(scratch, "imports-after-restart", body))
        from acad_verify import average_nearest_spacing
        for name, expected in (("hix", 1.4), ("had", 2.8)):
            actual = average_nearest_spacing(circles(scratch / (name + ".csv")))
            assert abs(actual - expected) < 0.02, (name, expected, actual)
            results.append("PASS {}-import-spacing-{}".format(name.upper(), actual))
        assert len(circles(scratch / "had.csv")) < len(circles(scratch / "hix.csv"))
        results.append("PASS HAD-lower-brightness-reduces-count")
    (LOGS / "results.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    print("Core command checks: {} passed; logs: {}".format(len(results), LOGS))


if __name__ == "__main__":
    main()
