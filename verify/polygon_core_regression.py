"""Serial AutoCAD 2018 geometry regression against a pinned, test-only Clipper2 oracle.

Never opens or saves a user's drawing. --dll can audit a previous release.
HPLIT uses the real command; HJOIN uses its real planning/write transactions;
HOF uses its real geometry helper (the modal WinForms UI is not covered).
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
from cad_runtime_guard import require_cad_runtime

ROOT = Path(__file__).resolve().parent.parent
LOGS = ROOT / "obj/polygon-regression"
CONSOLE = Path(r"D:\Program Files\Autodesk\AutoCAD 2018\accoreconsole.exe")
CSC = ROOT / "packages/Microsoft.Net.Compilers/tools/csc.exe"

def compile_helpers():
    LOGS.mkdir(parents=True, exist_ok=True)
    vendor = ROOT / "verify/third_party/clipper2/src"
    manifest = json.loads((vendor.parent/"manifest.json").read_text(encoding="utf-8-sig"))
    for name, digest in manifest["files"].items():
        assert hashlib.sha256((vendor.parent/name).read_bytes()).hexdigest().upper() == digest, "Changed oracle file: " + name
    subprocess.run([str(CSC), "/nologo", "/target:library", "/langversion:8.0", "/nullable:enable",
                    "/deterministic+", "/out:"+str(LOGS/"Clipper2.TestOracle.dll"),
                    *map(str, sorted(vendor.glob("*.cs")))], check=True, cwd=ROOT)
    refs = [CONSOLE.parent/name for name in ("acdbmgd.dll", "acmgd.dll", "accoremgd.dll")]
    subprocess.run([str(CSC), "/nologo", "/target:library", "/platform:x64", "/deterministic+",
                    "/out:"+str(LOGS/"PolygonRegressionChecks.dll"),
                    "/r:"+str(LOGS/"Clipper2.TestOracle.dll"), *["/r:"+str(p) for p in refs],
                    str(ROOT/"verify/PolygonRegressionChecks.cs")], check=True, cwd=ROOT)

def split_case(n):
    points = [(-5,10),(25,10)]
    if n == 1: points = [(4,-5),(4,25)]
    if n == 4: points = [(0,70),(100,70),(0,30),(100,30)]
    if n == 5: points = [(-10,70),(110,70),(-10,30),(110,30)]
    if n == 6: points = [(-5,-5),(25,25)]
    if n == 7: points = [(-5,5),(5,-5)]
    if n == 8: points = [(-5,0),(25,0)]
    if n == 9: points = [(5,5),(15,15)]
    if n == 11: points = [(-5,10),(10,10),(10,10),(25,10)]
    return points

def main():
    require_cad_runtime()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dll", type=Path, default=ROOT/"bin/Release/HexFill.dll")
    parser.add_argument("--label", default="current")
    args = parser.parse_args()
    LOGS.mkdir(parents=True, exist_ok=True)
    compile_helpers()
    with tempfile.TemporaryDirectory(prefix="hexfill-polygons-") as directory:
        scratch = Path(directory)
        shutil.copy2(ROOT/"tests/load_quit.dwg", scratch/"test.dwg")
        result = scratch/"results.txt"
        body = ["_.NETLOAD", '"'+args.dll.resolve().as_posix()+'"',
                "_.NETLOAD", '"'+(LOGS/"PolygonRegressionChecks.dll").as_posix()+'"',
                '(setvar "FILEDIA" 1)', '(setvar "CMDDIA" 1)', '(setvar "OSMODE" 0)',
                '(setvar "SNAPMODE" 0)', '(setvar "CMDECHO" 0)',
                "HGEOINIT", '"'+result.as_posix()+'"', "HGEOUNIT"]
        for v in range(3):
            for n in range(12):
                body += ["HGEOSET", str(v*100+n)]
                if v == 0 and n == 2: body += ["_.UNDO", "_Mark"]
                body += ["HPLIT", "_ALL", ""]
                pts = split_case(n)
                if v == 1: pts = list(reversed(pts))
                if v == 2: pts = [(x+100000000,y-100000000) for x,y in pts]
                for x,y in pts: body += ["_non", "{},{}".format(x,y)]
                body += ["", "HGEOASSERT"]
                if v == 0 and n == 2: body += ["_.UNDO", "_Back", "HGEOUNDO"]
        body += ["HGEODONE", "_.QUIT", "_Yes"]
        script = scratch/"regression.scr"
        script.write_text("\n".join(body)+"\n", encoding="ascii")
        with (LOGS/(args.label+"-console.log")).open("wb") as output:
            run = subprocess.run([str(CONSOLE), "/i", str(scratch/"test.dwg"), "/s", str(script),
                                  "/l", "en-US", "/isolate", "HexFillHclnSmoke", str(scratch/"profile")],
                                 cwd=scratch, stdout=output, stderr=subprocess.STDOUT, timeout=180,
                                 creationflags=subprocess.CREATE_NO_WINDOW | subprocess.BELOW_NORMAL_PRIORITY_CLASS)
        rows = result.read_text(encoding="utf-8").splitlines() if result.exists() else []
        (LOGS/(args.label+"-results.txt")).write_text("\n".join(rows), encoding="utf-8")
        failed = [r for r in rows if r.startswith("FAIL ")]
        summary = {"dll":str(args.dll.resolve()), "exit":run.returncode,
                   "checks":sum(r.startswith(("PASS ","FAIL ")) for r in rows),
                   "failures":failed, "summary":rows[-1] if rows else "no results"}
        (LOGS/(args.label+"-results.json")).write_text(json.dumps(summary,indent=2), encoding="utf-8")
        print(json.dumps(summary,indent=2), flush=True)
        assert run.returncode == 0 and rows and rows[-1].startswith("SUMMARY "), "Incomplete host run"
        assert not failed, "Geometry regressions: see " + str(LOGS)

if __name__ == "__main__": main()
