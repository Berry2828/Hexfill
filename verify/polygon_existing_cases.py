"""Run the existing HPLIT/HJOIN geometry expectations in an isolated Core host.

Includes the original curved-outline and mixed-region cases, using their real
area, vertex and layer assertions from acad_verify instead of count-only checks.
"""
import json
from pathlib import Path
import shutil
import tempfile
import commands_core_smoke as core
import acad_verify as acad
from polygon_core_regression import compile_helpers

ROOT = Path(__file__).resolve().parent.parent

def read(path):
    result = []
    for row in path.read_text(encoding="utf-8").splitlines():
        closed, area, layer, points = row.split("|")
        result.append({"closed":closed == "1", "area":float(area), "layer":layer,
                       "vertices":[tuple(map(float,p.split(","))) for p in points.split(";")]})
    return result

def main():
    compile_helpers()
    core.LOGS = ROOT/"obj/polygon-regression/existing-cases"
    core.LOGS.mkdir(parents=True, exist_ok=True)
    results = []
    with tempfile.TemporaryDirectory(prefix="hexfill-legacy-polygons-") as directory:
        scratch = Path(directory)
        shutil.copy2(ROOT/"tests/load_quit.dwg",scratch/"test.dwg")
        body = ["_.NETLOAD", '"'+(ROOT/"obj/polygon-regression/PolygonRegressionChecks.dll").as_posix()+'"']
        for command, cases in (("HPLIT",acad.HPLIT_CASES),("HJOIN",acad.HJOIN_CASES)):
            for case in cases:
                body += acad.hplit_script(case) if command == "HPLIT" else case["script"]()
                body += ["HGEOEXPORT", '"'+(scratch/(command+"-"+case["name"]+".txt")).as_posix()+'"']
        core.run_core(scratch,"legacy-polygons",body)
        for command, cases in (("HPLIT",acad.HPLIT_CASES),("HJOIN",acad.HJOIN_CASES)):
            for case in cases:
                data = read(scratch/(command+"-"+case["name"]+".txt"))
                (core.LOGS/(command+"-"+case["name"]+".json")).write_text(json.dumps(data,indent=2),encoding="utf-8")
                if command == "HPLIT":
                    ok, errors = acad.assert_hplit_result(data,case)
                    assert ok, (case["name"],errors)
                else:
                    class Snapshot:
                        def run_script(self,*args,**kwargs): pass
                        def get_polylines(self): return data
                    assert acad.run_hjoin_case(case,Snapshot(),120),case["name"]
                results.append("PASS "+command+"-"+case["name"])
    (core.LOGS/"results.json").write_text(json.dumps(results,indent=2),encoding="utf-8")
    print("\n".join(results))

if __name__ == "__main__": main()
