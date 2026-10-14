"""Acceptance checks for 2.2.0.3; isolated real AutoCAD database, no saved drawings."""
from pathlib import Path
import shutil
import tempfile
from acad_verify import average_nearest_spacing
import commands_core_smoke as core

ROOT = Path(__file__).resolve().parent.parent
core.LOGS = ROOT / 'obj/optimized-20261013/edge-cases'

def log(name):
    return (core.LOGS / (name + '.log')).read_bytes().decode('utf-16-le', errors='replace')

TAG = '(defun opt-tag (e app val) (regapp app) (entmod (append (entget e) (list (list -3 (list app (cons 1000 val)))))))'
SETUP = [
    '(command "_.ERASE" "_ALL" "")', TAG,
    '(command "_.-LAYER" "_Make" "OPT_LOCK" "_Set" "0" "")',
    '(command "_.RECTANG" "0,0" "10,10")', '(setq opt-region (entlast))',
    '(opt-tag opt-region "H_Num" "101")', '(opt-tag opt-region "H_IsLeaf" "1")',
    '(entmake \'((0 . "CIRCLE") (8 . "0") (10 3.0 5.0 0.0) (40 . 0.03)))',
    '(setq opt-writable (entlast))',
    '(entmake \'((0 . "CIRCLE") (8 . "OPT_LOCK") (10 5.0 5.0 0.0) (40 . 0.03)))',
    '(setq opt-locked (entlast))',
    '(command "_.-LAYER" "_Lock" "OPT_LOCK" "")',
    '(setq opt-before-a (entget opt-writable) opt-before-b (entget opt-locked))',
]

def main():
    core.LOGS.mkdir(parents=True, exist_ok=True)
    results = []
    with tempfile.TemporaryDirectory(prefix='hexfill-optimized-') as directory:
        scratch = Path(directory)
        shutil.copy2(ROOT / 'tests/load_quit.dwg', scratch / 'test.dwg')
        spacing = scratch / 'Spacing_locked.csv'
        spacing.write_text('Number,Spacing,X,Y,Direction\n101,1.4,5,5,X\n', encoding='utf-8-sig')
        brightness = scratch / 'Brightness_locked.csv'
        brightness.write_text('Number,Current,Target,Direction\n101,100,25,X\n', encoding='utf-8-sig')
        for name, commands in [
            ('HGR', ['(command "HGR" (ssadd opt-region) "" "2")']),
            ('HRS', ['(command "HRS" (ssadd opt-region) "" "50")']),
            ('HRD', ['(command "HRD" (ssadd opt-region) "")']),
            ('HRS_clear', ['(command "HRS" (ssadd opt-region) "" "-100")']),
            ('HIX', ['(setvar "FILEDIA" 0)', 'HIX', '"{}"'.format(spacing.as_posix()), '_ALL', '']),
            ('HAD', ['(setvar "FILEDIA" 0)', 'HAD', '"{}"'.format(brightness.as_posix()), '_ALL', '']),
        ]:
            body = SETUP + commands + [
                core.check(name+'-writable-old-unchanged', '(equal opt-before-a (entget opt-writable))'),
                core.check(name+'-locked-old-unchanged', '(equal opt-before-b (entget opt-locked))'),
                core.check(name+'-no-new-circles', '(= 2 (hf-count \'((0 . "CIRCLE"))))'),
            ]
            results.extend(core.run_core(scratch, name, body))
            assert '失败 1' in log(name), (name, 'failed summary missing')

        body = SETUP + [
            '(command "_.RECTANG" "20,0" "30,10")', '(setq opt-good (entlast))',
            '(setq opt-selection (ssadd opt-region))', '(ssadd opt-good opt-selection)',
            '(command "HGR" opt-selection "" "2")',
            core.check('batch-failed-region-unchanged', '(equal opt-before-a (entget opt-writable))'),
            core.check('batch-healthy-region-generated', '(> (hf-count \'((0 . "CIRCLE"))) 2)'),
            '(command "_.-LAYER" "_Unlock" "OPT_LOCK" "")',
            '(command "_.UNDO" "_Mark")',
            '(command "HGR" (ssadd opt-region) "" "2")',
            core.check('unlocked-retry-deletes-old-circles', '(and (not (entget opt-writable)) (not (entget opt-locked)))'),
            '(command "_.UNDO" "_Back")',
            core.check('undo-restores-old-circles', '(and (entget opt-writable) (entget opt-locked))'),
        ]
        results.extend(core.run_core(scratch, 'batch-retry-undo', body))

        for value in ('Infinity', 'NaN', '-Infinity', '1e999'):
            data = scratch / 'Spacing_invalid.csv'
            data.write_text('Number,Spacing,X,Y,Direction\n101,{},5,5,X\n'.format(value), encoding='utf-8-sig')
            body = SETUP + ['(setvar "FILEDIA" 0)', 'HIX', '"{}"'.format(data.as_posix()),
                core.check('invalid-value-old-unchanged', '(equal opt-before-a (entget opt-writable))'),
                core.check('invalid-value-no-new-circles', '(= 2 (hf-count \'((0 . "CIRCLE"))))')]
            name = 'invalid-'+value.replace('-', 'negative-')
            results.extend(core.run_core(scratch, name, body))
            assert '跳过 1 行无效数据' in log(name)

        body = ['(command "_.ERASE" "_ALL" "")',
            '(command "_.RECTANG" "0,0" "5000,5000")', '(setq opt-region (entlast))',
            '(entmake \'((0 . "CIRCLE") (10 5.0 5.0 0.0) (40 . 0.015)))', '(setq opt-old (entlast) opt-before (entget opt-old))',
            '(command "HGR" (ssadd opt-region) "" "0.1")',
            core.check('oversized-grid-keeps-old-circle', '(equal opt-before (entget opt-old))'),
            core.check('oversized-grid-no-new-circles', '(= 1 (hf-count \'((0 . "CIRCLE"))))')]
        results.extend(core.run_core(scratch, 'grid-limit', body))
        assert '网格规模过大' in log('grid-limit') and 'Parameter name: capacity' not in log('grid-limit')

        body = ['(command "_.ERASE" "_ALL" "")',
            '(command "_.RECTANG" "0,0" "500,500")', '(setq opt-region (entlast))',
            '(entmake \'((0 . "CIRCLE") (10 5.0 5.0 0.0) (40 . 0.015)))', '(setq opt-old (entlast) opt-before (entget opt-old))',
            '(command "HGR" (ssadd opt-region) "" "0.5")',
            core.check('output-limit-keeps-old-circle', '(equal opt-before (entget opt-old))'),
            core.check('output-limit-no-new-circles', '(= 1 (hf-count \'((0 . "CIRCLE"))))')]
        results.extend(core.run_core(scratch, 'output-limit', body))
        assert '单对象生成圆超过 100 万个' in log('output-limit')

        mixed = scratch / 'Spacing_mixed.csv'
        mixed.write_text("Number;Spacing;X;Y;Direction\n101;Infinity;5;5;X\n'101;1,4;5;5;X\n101;1,1;5;5;X\n101;-1;5;5;X\n101;1,4;NaN;5;X\n101;1,4;5;5;Z\n", encoding='utf-16')
        body = SETUP + ['(command "_.-LAYER" "_Unlock" "OPT_LOCK" "")',
            '(setvar "FILEDIA" 0)', 'HIX', '"{}"'.format(mixed.as_posix()), '_ALL', '',
            core.check('mixed-valid-row-replaces-old', '(and (not (entget opt-writable)) (not (entget opt-locked)))'),
            '(hf-dump "{}")'.format((scratch / 'mixed.csv').as_posix())]
        results.extend(core.run_core(scratch, 'hix-mixed-csv', body))
        assert abs(average_nearest_spacing(core.circles(scratch / 'mixed.csv')) - 1.4) < 0.01
        assert '跳过 4 行无效数据、1 行重复记录' in log('hix-mixed-csv')
        results.append('PASS HIX-UTF16-semicolon-comma-decimal-first-valid-duplicate')

        body = ['(command "_.ERASE" "_ALL" "")',
            '(command "_.RECTANG" "0,0" "10,10")', '(setq opt-region (entlast))',
            '(entmake \'((0 . "CIRCLE") (10 5.0 5.0 0.0) (40 . 0.015)))',
            '(command "HRS" (ssadd opt-region) "" "-100")',
            core.check('HRS-clear-single-circle', '(= 0 (hf-count \'((0 . "CIRCLE"))))')]
        results.extend(core.run_core(scratch, 'clear-single-circle', body))

        body = ['(command "_.ERASE" "_ALL" "")',
            '(entmake \'((0 . "TEXT") (10 0.0 0.0 0.0) (40 . 3.0) (1 . "A")))',
            '(entmake \'((0 . "TEXT") (10 10000.0 0.0 0.0) (40 . 5000.0) (1 . "A")))',
            '(entmake \'((0 . "TEXT") (10 20.0 0.0 0.0) (40 . 3.0) (1 . "A")))',
            'HTC', '_ALL', '',
            core.check('HTC-all-source-texts-kept', '(= 3 (hf-count \'((0 . "TEXT"))))'),
            '(hf-dump "{}")'.format((scratch / 'htc-batch.csv').as_posix())]
        results.extend(core.run_core(scratch, 'htc-failure-isolation', body))
        dots = core.circles(scratch / 'htc-batch.csv')
        assert any(c['center'][0] < 10 for c in dots) and any(20 <= c['center'][0] < 30 for c in dots)
        assert not any(c['center'][0] > 100 for c in dots)
        assert '处理 2 个文字' in log('htc-failure-isolation') and '跳过 1 个' in log('htc-failure-isolation')
        results.append('PASS HTC-healthy-texts-survive-oversized-text')
    (core.LOGS / 'results.txt').write_text('\n'.join(results)+'\n', encoding='utf-8')
    print('Optimization acceptance: {} checks passed'.format(len(results)))

def check_unmatched():
    core.LOGS.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='hexfill-unmatched-') as directory:
        scratch = Path(directory)
        shutil.copy2(ROOT / 'tests/load_quit.dwg', scratch / 'test.dwg')
        data = scratch / 'Spacing_unmatched.csv'
        data.write_text('Number,Spacing,X,Y,Direction\n999,1.4,5,5,X\n', encoding='utf-8-sig')
        body = SETUP + ['(setvar "FILEDIA" 0)', 'HIX', '"{}"'.format(data.as_posix()), '_ALL', '',
            core.check('unmatched-old-unchanged', '(equal opt-before-a (entget opt-writable))'),
            core.check('unmatched-no-new-circles', '(= 2 (hf-count \'((0 . "CIRCLE"))))')]
        rows = core.run_core(scratch, 'hix-unmatched', body)
        assert '间距一致 0 个，无匹配 1 个' in log('hix-unmatched')
        (core.LOGS / 'unmatched-results.txt').write_text('\n'.join(rows)+'\n', encoding='utf-8')

if __name__ == '__main__':
    main()
    check_unmatched()
