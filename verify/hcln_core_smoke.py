"""Exercise the built HCLN command in an isolated AutoCAD 2018 Core Console.

Run explicitly: python -B verify/hcln_core_smoke.py
Only a temporary copy of tests/load_quit.dwg is opened; it is never saved.
Logs are retained in obj/hcln-smoke. This does not attach to desktop AutoCAD.
"""

import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile
from cad_runtime_guard import require_cad_runtime


ROOT = Path(__file__).resolve().parent.parent
CASES = r'''
(setvar "FILEDIA" 1)
(setvar "CMDDIA" 1)
(setvar "CMDECHO" 0)
(command "_.-LAYER" "_Unlock" "*" "")
(command "_.ERASE" "_ALL" "")
(setq hf-result (open "@RESULT@" "w"))
(defun hf-check (name ok) (write-line (strcat (if ok "PASS " "FAIL ") name) hf-result))
(defun hf-val (e app) (cdr (assoc 1000 (cdr (cadr (assoc -3 (entget e (list app))))))))
(defun hf-tag (e app value) (regapp app) (entmod (append (entget e) (list (list -3 (list app (cons 1000 value)))))))
(defun hf-layer (name) (entmake (list '(0 . "LAYER") '(100 . "AcDbSymbolTableRecord") '(100 . "AcDbLayerTableRecord") (cons 2 name) '(70 . 0) '(62 . 7) '(6 . "Continuous"))))
(defun hf-rect (x layer number / e)
  (entmake (list '(0 . "LWPOLYLINE") '(100 . "AcDbEntity") (cons 8 layer) '(100 . "AcDbPolyline") '(90 . 4) '(70 . 1) (cons 10 (list x 0.0)) (cons 10 (list (+ x 10.0) 0.0)) (cons 10 (list (+ x 10.0) 10.0)) (cons 10 (list x 10.0))))
  (setq e (entlast)) (hf-tag e "H_Num" number) (hf-tag e "H_IsLeaf" "1") e)
(defun hf-text (region x layer / e)
  (entmake (list '(0 . "TEXT") (cons 8 layer) (cons 10 (list (+ x 5.0) 5.0 0.0)) '(40 . 1.0) '(1 . "101")))
  (setq e (entlast)) (hf-tag region "H_TextHandle" (cdr (assoc 5 (entget e)))) e)
(defun hf-cleared (e) (= "1" (hf-val e "H_Clr")))
(hf-layer "H_NUM")
(hf-layer "HF_LOCK_TEXT")
(hf-layer "HF_LOCK_REGION")
(setq hf-a (hf-rect 0.0 "0" "101") hf-at (hf-text hf-a 0.0 "H_NUM"))
(setq hf-ath (cdr (assoc 5 (entget hf-at))))
(setq hf-b (hf-rect 20.0 "0" "102") hf-bt (hf-text hf-b 20.0 "HF_LOCK_TEXT"))
(setq hf-c (hf-rect 40.0 "HF_LOCK_REGION" "103") hf-ct (hf-text hf-c 40.0 "H_NUM"))
(setq hf-d (hf-rect 60.0 "0" "104"))
(hf-tag hf-d "H_TextHandle" "7FFFFFFFFFFFFFFF")
(setq hf-e (hf-rect 80.0 "0" "105"))
(hf-tag hf-e "H_TextHandle" "not-a-handle")
(setq hf-f (hf-rect 100.0 "0" "106") hf-ft (hf-text hf-f 100.0 "H_NUM"))
(entdel hf-ft)
(setq hf-out (hf-rect 1000.0 "0" "999") hf-outt (hf-text hf-out 1000.0 "H_NUM"))
(hf-tag hf-out "H_TextHandle" "stale-unselected")
(setq hf-out-before (entget hf-out '("*")))
(command "_.-LAYER" "_Lock" "HF_LOCK_TEXT,HF_LOCK_REGION" "")
(setq hf-b-before (entget hf-b '("*")) hf-c-before (entget hf-c '("*")))
(setq hf-select (ssadd))
(foreach e (list hf-a hf-b hf-c hf-d hf-e hf-f) (ssadd e hf-select))
(command "_.UNDO" "_Mark")
(command "HCLN" hf-select "")
(hf-check "healthy-region-cleared" (hf-cleared hf-a))
(hf-check "healthy-text-erased" (not (entget hf-at)))
(hf-check "locked-text-region-unchanged" (equal hf-b-before (entget hf-b '("*"))))
(hf-check "locked-text-preserved" (entget hf-bt))
(hf-check "locked-region-unchanged" (equal hf-c-before (entget hf-c '("*"))))
(hf-check "locked-region-text-preserved" (entget hf-ct))
(hf-check "missing-handle-cleared" (hf-cleared hf-d))
(hf-check "malformed-handle-cleared" (hf-cleared hf-e))
(hf-check "erased-text-cleared" (hf-cleared hf-f))
(hf-check "unselected-region-unchanged" (equal hf-out-before (entget hf-out '("*"))))
(hf-check "unselected-text-preserved" (entget hf-outt))
(command "_.UNDO" "_Back")
(hf-check "undo-restores-region" (and (= "101" (hf-val hf-a "H_Num")) (not (hf-cleared hf-a))))
(hf-check "undo-restores-text" (entget (handent hf-ath)))
(command "_.-LAYER" "_Unlock" "HF_LOCK_TEXT,HF_LOCK_REGION" "")
(command "HCLN" hf-select "")
(hf-check "retry-clears-all-regions" (and (hf-cleared hf-a) (hf-cleared hf-b) (hf-cleared hf-c) (hf-cleared hf-d) (hf-cleared hf-e) (hf-cleared hf-f)))
(hf-check "retry-erases-previously-locked-texts" (and (not (entget hf-bt)) (not (entget hf-ct))))
(hf-check "retry-preserves-unselected-xdata" (equal hf-out-before (entget hf-out '("*"))))
(hf-check "number-layers-still-unlocked" (= 0 (logand 4 (cdr (assoc 70 (tblsearch "LAYER" "HF_LOCK_TEXT"))))))
(setq hf-g (hf-rect 200.0 "0" "107"))
(entmake '((0 . "TEXT") (8 . "H_NUM") (10 205.0 5.0 0.0) (40 . 1.0) (1 . "orphan")))
(setq hf-orphan (entlast))
(command "_.-LAYER" "_Lock" "H_NUM" "")
(command "HCLN" (ssadd hf-g) "")
(hf-check "orphan-failure-keeps-region-success" (hf-cleared hf-g))
(hf-check "locked-orphan-preserved" (entget hf-orphan))
(hf-check "orphan-layer-remains-locked" (= 4 (logand 4 (cdr (assoc 70 (tblsearch "LAYER" "H_NUM"))))))
(command "_.-LAYER" "_Unlock" "H_NUM" "")
(command "HCLN" (ssadd hf-g) "")
(hf-check "orphan-retry-erases-text" (not (entget hf-orphan)))
(hf-check "orphan-retry-preserves-outside-text" (entget hf-outt))
(hf-check "file-dialogs-enabled" (= 1 (getvar "FILEDIA")))
(close hf-result)
_.QUIT
_Yes
'''


def main():
    require_cad_runtime()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--console", type=Path,
                        default=Path(r"D:\Program Files\Autodesk\AutoCAD 2018\accoreconsole.exe"))
    parser.add_argument("--dll", type=Path, default=ROOT / "bin/Release/HexFill.dll")
    args = parser.parse_args()
    for path in (args.console, args.dll, ROOT / "tests/load_quit.dwg"):
        if not path.is_file():
            parser.error("Required file missing: {}".format(path))
    logs = ROOT / "obj/hcln-smoke"
    logs.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="hexfill-hcln-") as directory:
        scratch = Path(directory)
        drawing = scratch / "test.dwg"
        shutil.copy2(ROOT / "tests/load_quit.dwg", drawing)
        result_file = scratch / "assertions.txt"
        script = scratch / "test.scr"
        script.write_text(
            '_.NETLOAD\n"{}"\n'.format(args.dll.resolve().as_posix())
            + CASES.replace("@RESULT@", result_file.as_posix()).lstrip(),
            encoding="ascii")
        with (logs / "console.log").open("wb") as output:
            run = subprocess.run(
                [str(args.console), "/i", str(drawing), "/s", str(script),
                 "/l", "en-US", "/isolate", "HexFillHclnSmoke", str(scratch / "profile")],
                cwd=str(scratch), stdout=output, stderr=subprocess.STDOUT,
                timeout=120, creationflags=subprocess.CREATE_NO_WINDOW)
        if not result_file.exists():
            raise RuntimeError("No assertions produced. See {}".format(logs / "console.log"))
        results = result_file.read_text(encoding="ascii").splitlines()
        (logs / "assertions.txt").write_text("\n".join(results) + "\n", encoding="ascii")
        print("\n".join(results))
        if run.returncode != 0 or len(results) != 23 or any(not row.startswith("PASS ") for row in results):
            raise RuntimeError("HCLN smoke failed; console exit={}, assertions={}".format(run.returncode, len(results)))
        print("HCLN Core Console: 23 checks passed; logs: {}".format(logs))


if __name__ == "__main__":
    main()
