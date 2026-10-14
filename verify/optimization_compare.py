"""Compare old/new point coordinates and measure managed grid allocations."""
from pathlib import Path
import json
import shutil
import statistics
import tempfile
import commands_core_smoke as core
from acad_verify import HTC_CASES

ROOT = Path(__file__).resolve().parent.parent
core.LOGS = ROOT / 'obj/optimized-20261013/comparison'

def main():
    core.LOGS.mkdir(parents=True, exist_ok=True)
    helper = ROOT / 'obj/OptimizationChecks.dll'
    geometry = {}
    fonts = {}
    performance = {}
    with tempfile.TemporaryDirectory(prefix='hexfill-compare-') as directory:
        scratch = Path(directory)
        shutil.copy2(ROOT / 'tests/load_quit.dwg', scratch / 'test.dwg')
        for label, dll in [('old', Path(r'C:\Users\allen\Desktop\HexFill_20260920_Full.dll')),
                           ('new', ROOT / 'bin/Release/HexFill.dll')]:
            core.DLL = dll
            data = scratch / (label+'.txt')
            body = ['_.NETLOAD', '"{}"'.format(helper.as_posix()), 'HOPTGEOM', '"{}"'.format(data.as_posix())]
            for name, script in HTC_CASES:
                body.extend(script())
                body.append('(hf-dump "{}")'.format((scratch / (label+'-'+name+'.csv')).as_posix()))
            core.run_core(scratch, label+'-geometry', body)
            rows = data.read_text(encoding='utf-8').splitlines()
            assert sum(row.startswith('CASE ') for row in rows) == 8
            geometry[label] = [row for row in rows if not row.startswith('PERF,')]
            perf = [row.split(',')[1:] for row in rows if row.startswith('PERF,')]
            assert len(perf) == 3
            performance[label] = {'points': int(perf[0][0]),
                'median_ms': statistics.median(float(row[1]) for row in perf),
                'median_allocated_bytes': statistics.median(int(row[2]) for row in perf)}
            (core.LOGS / (label+'-points.txt')).write_text('\n'.join(rows), encoding='utf-8')
            fonts[label] = {}
            for name, _ in HTC_CASES:
                text = (scratch / (label+'-'+name+'.csv')).read_text(encoding='ascii')
                fonts[label][name] = text
                (core.LOGS / (label+'-'+name+'.csv')).write_text(text, encoding='ascii')
        assert geometry['old'] == geometry['new'], 'Synthetic geometry coordinates changed'
        assert fonts['old'] == fonts['new'], 'Actual text dot coordinates changed'
        assert performance['old']['points'] == performance['new']['points']
        result = {'synthetic_cases_identical':8, 'actual_text_cases_identical':8, 'performance':performance}
        (core.LOGS / 'results.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
        print(json.dumps(result, indent=2), flush=True)

        core.DLL = ROOT / 'bin/Release/HexFill.dll'
        data = scratch / 'balloons.txt'
        body = ['(command "_.ERASE" "_ALL" "")',
            'HBAL', '_non', '0,0', '_non', '10,0', '_non', '20,0', 'Done',
            '_.NETLOAD', '"{}"'.format(helper.as_posix()),
            'HOPTBAL', '"{}"'.format(data.as_posix())]
        core.run_core(scratch, 'balloon-retry', body)
        rows = data.read_text(encoding='utf-8').splitlines()
        assert len(rows) == 10 and all(row.startswith('PASS ') for row in rows), rows
        (core.LOGS / 'balloon-results.txt').write_text('\n'.join(rows), encoding='utf-8')
        print('\n'.join(rows), flush=True)

if __name__ == '__main__':
    main()
