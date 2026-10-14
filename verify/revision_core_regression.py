"""Serial real CAD checks for HTC geometry and HAD minimum-spacing color."""
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import commands_core_smoke as core
from cad_runtime_guard import require_cad_runtime

ROOT = Path(__file__).resolve().parent.parent
LOGS = ROOT / 'obj/fixes-20261014/core'

def main():
    require_cad_runtime()
    LOGS.mkdir(parents=True, exist_ok=True)
    core.LOGS = LOGS
    helper = LOGS / 'RevisionTextChecks.dll'
    refs = [core.CONSOLE.parent / n for n in ('acdbmgd.dll', 'acmgd.dll', 'accoremgd.dll')]
    subprocess.run([str(ROOT / 'packages/Microsoft.Net.Compilers/tools/csc.exe'), '/nologo', '/target:library', '/platform:x64',
                    '/out:'+str(helper), *['/r:'+str(p) for p in refs], str(ROOT/'verify/RevisionTextChecks.cs')], cwd=ROOT, check=True)
    results = []
    with tempfile.TemporaryDirectory(prefix='hexfill-revision-', ignore_cleanup_errors=True) as directory, core.preserve_export_paths():
        scratch = Path(directory)
        shutil.copy2(ROOT/'tests/load_quit.dwg', scratch/'test.dwg')
        measurement = LOGS/'measurements-detail.txt'
        core.run_core(scratch, 'text-measurements', ['_.NETLOAD', '"'+helper.as_posix()+'"', 'HREVTEST', '"'+measurement.as_posix()+'"'])
        rows = measurement.read_text(encoding='utf-8').splitlines()
        (LOGS/'text-measurements.txt').write_text('\n'.join(rows), encoding='utf-8')
        assert rows[-1].startswith('SUMMARY ') and 'failures=0' in rows[-1], rows
        results += [r for r in rows if r.startswith('PASS ')]
        actual=scratch/'actual-htc.txt'
        core.run_core(scratch,'actual-htc',['_.ERASE','_ALL','','_.NETLOAD','"'+helper.as_posix()+'"',
            'HREVSETHTC','HTC','(ssget "_X" \'((0 . "TEXT")))','','HREVASSERTHTC','"'+actual.as_posix()+'"'])
        actual_rows=actual.read_text(encoding='utf-8').splitlines()
        assert len(actual_rows)==6 and all(r.startswith('PASS ') for r in actual_rows), actual_rows
        results+=actual_rows
        # HDIA 0.03/0.06 diameters have existing minimum spacings 0.10/0.14.
        cases = [('unchanged-min010', .10, 1, False, True, True),
                 ('clamped-unchanged-min010', .10, 4, False, True, True),
                 ('new-min010', .3, 100, False, True, False),
                 ('near-min010', .1005, 4, False, True, False),
                 ('unchanged-above-min', .2, 1, False, False, True),
                 ('unchanged-min014', .14, 1, True, True, True),
                 ('new-min014', .3, 100, True, True, False)]
        for name, old, target, toggle, red, unchanged in cases:
            csv = scratch/('Brightness_'+name+'.csv')
            csv.write_text('编号,当前亮度,目标亮度,排列方向\n1,1,'+str(target)+',X\n', encoding='utf-8-sig')
            body = ['_.ERASE', '_ALL', '', '_.PLINE','-.2,-.2','.8,-.2','.8,.8','-.2,.8','_C',
                    '_.PLINE','0,0','.6,0','.6,.6','0,.6','_C',
                    '(setq hf-leaf (entlast))',
                    'HNM','1','_ALL','']
            if toggle: body += ['HDIA']
            body += ['HGR','(ssadd hf-leaf)','',''+str(old),
                     '(setq hf-old (ssget "_X" \'((0 . "CIRCLE"))))',
                     '(setq hf-handles nil hf-i 0)',
                     '(repeat (sslength hf-old) (setq hf-handles (cons (cdr (assoc 5 (entget (ssname hf-old hf-i)))) hf-handles) hf-i (1+ hf-i)))',
                     'HAD','"'+csv.as_posix()+'"','_ALL','',
                     core.check(name+'-circles', '(> (hf-count \'((0 . "CIRCLE"))) 1)'),
                     core.check(name+'-red', '(= (hf-count \'((0 . "CIRCLE") (62 . 1))) '+('(hf-count \'((0 . "CIRCLE")))' if red else '0')+')')]
            if unchanged:
                body += [core.check(name+'-same-entities', '(and (= (length hf-handles) (hf-count \'((0 . "CIRCLE")))) (vl-every \'handent hf-handles))')]
            body += ['(hf-dump "'+(scratch/(name+'.csv')).as_posix()+'")']
            if red and unchanged:
                body += ['HAD','"'+csv.as_posix()+'"','_ALL','',
                         core.check(name+'-repeat-stays-red', '(= (hf-count \'((0 . "CIRCLE") (62 . 1))) (hf-count \'((0 . "CIRCLE"))))'),
                         core.check(name+'-repeat-same-entities', '(and (= (length hf-handles) (hf-count \'((0 . "CIRCLE")))) (vl-every \'handent hf-handles))')]
            results += core.run_core(scratch,name,body)
            if red:
                from acad_verify import average_nearest_spacing
                spacing = average_nearest_spacing(core.circles(scratch/(name+'.csv')))
                assert abs(spacing-(.14 if toggle else .10)) < 1e-6, (name, spacing)
                results += ['PASS '+name+'-actual-spacing']
    (LOGS/'results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
    print('Revision checks: '+str(len(results))+' passed', flush=True)

if __name__ == '__main__': main()
