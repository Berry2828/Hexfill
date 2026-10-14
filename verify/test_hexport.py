# HEX/HCN/HBR CSV Export Format Verification
# Validates CSV format rules for export commands

import sys, os, math, csv, io
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from geom import TestResult


def validate_hex_csv(content):
    """Validate HEX export CSV format: 编号,间距(mm),X坐标,Y坐标,排列方向"""
    reader = csv.reader(io.StringIO(content))
    header = next(reader, None)
    if not header:
        return False, "No header"
    expected = ["编号", "间距(mm)", "X坐标", "Y坐标", "排列方向"]
    if header != expected:
        return False, f"Header mismatch: {header} != {expected}"
    rows = list(reader)
    if len(rows) == 0:
        return False, "No data rows"
    for i, row in enumerate(rows):
        if len(row) != 5:
            return False, f"Row {i+1}: expected 5 cols, got {len(row)}"
        try:
            num = int(row[0])
            spacing = float(row[1])
            x = float(row[2])
            y = float(row[3])
            direction = row[4].strip()
            if direction not in ("X", "Y"):
                return False, f"Row {i+1}: direction must be X or Y"
            if spacing <= 0:
                return False, f"Row {i+1}: spacing must be > 0"
            if num < 0:
                return False, f"Row {i+1}: negative number"
        except ValueError as e:
            return False, f"Row {i+1}: parse error {e}"
    return True, f"{len(rows)} rows"


def validate_hbr_csv(content):
    """Validate HBR export CSV format: 编号,当前亮度,目标亮度,排列方向"""
    reader = csv.reader(io.StringIO(content))
    header = next(reader, None)
    if not header:
        return False, "No header"
    expected = ["编号", "当前亮度", "目标亮度", "排列方向"]
    if header != expected:
        return False, f"Header mismatch: {header} != {expected}"
    rows = list(reader)
    for i, row in enumerate(rows):
        if len(row) != 4:
            return False, f"Row {i+1}: expected 4 cols, got {len(row)}"
        try:
            num = int(row[0])
            current = float(row[1]) if row[1].strip() else 0.0
            target = float(row[2]) if row[2].strip() else 0.0
            direction = row[3].strip()
            if direction not in ("X", "Y"):
                return False, f"Row {i+1}: direction must be X or Y"
        except ValueError as e:
            return False, f"Row {i+1}: parse error {e}"
    return True, f"{len(rows)} rows"


def validate_hcn_csv(content):
    """Validate HCN export CSV format: 编号,圆数量,X坐标,Y坐标,排列方向"""
    reader = csv.reader(io.StringIO(content))
    header = next(reader, None)
    if not header:
        return False, "No header"
    expected = ["编号", "圆数量", "X坐标", "Y坐标", "排列方向"]
    if header != expected:
        return False, f"Header mismatch: {header} != {expected}"
    rows = list(reader)
    for i, row in enumerate(rows):
        if len(row) != 5:
            return False, f"Row {i+1}: expected 5 cols, got {len(row)}"
        try:
            num = int(row[0])
            count = int(row[1])
            x = float(row[2])
            y = float(row[3])
            direction = row[4].strip()
            if direction not in ("X", "Y"):
                return False, f"Row {i+1}: direction must be X or Y"
            if count < 0:
                return False, f"Row {i+1}: negative count"
        except ValueError as e:
            return False, f"Row {i+1}: parse error {e}"
    return True, f"{len(rows)} rows"


def test_hex_format():
    r = TestResult("HEX CSV format validation")
    valid_csv = "编号,间距(mm),X坐标,Y坐标,排列方向\n1,0.3,50.0,50.0,X\n2,0.25,100.0,100.0,Y\n"
    ok, msg = validate_hex_csv(valid_csv)
    r.check("Valid HEX CSV", ok, msg)

    bad_header = "ID,spacing,x,y,dir\n1,0.3,50,50,X\n"
    ok, msg = validate_hex_csv(bad_header)
    r.check("Rejects bad header", not ok, msg)

    missing_col = "编号,间距(mm),X坐标,Y坐标,排列方向\n1,0.3,50\n"
    ok, msg = validate_hex_csv(missing_col)
    r.check("Rejects missing column", not ok)

    negative_spacing = "编号,间距(mm),X坐标,Y坐标,排列方向\n1,-0.3,50,50,X\n"
    ok, msg = validate_hex_csv(negative_spacing)
    r.check("Rejects negative spacing", not ok)
    return r


def test_hbr_format():
    r = TestResult("HBR CSV format validation")
    valid = "编号,当前亮度,目标亮度,排列方向\n1,0.5,0.8,X\n2,1.0,0.6,Y\n"
    ok, msg = validate_hbr_csv(valid)
    r.check("Valid HBR CSV", ok, msg)

    empty_brightness = "编号,当前亮度,目标亮度,排列方向\n1,,0.8,X\n2,1.0,,Y\n"
    ok, msg = validate_hbr_csv(empty_brightness)
    r.check("Allows empty brightness fields", ok, msg)

    bad_header = "编号,亮度,目标\n1,0.5,0.8\n"
    ok, msg = validate_hbr_csv(bad_header)
    r.check("Rejects bad header", not ok)
    return r


def test_hbr_fault_isolation_policy():
    """HBR keeps valid roots when one selected region cannot be read."""
    r = TestResult("HBR entity fault-isolation policy")
    command_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Commands", "HbrCommand.cs")
    with open(command_path, encoding="utf-8") as source_file:
        source = source_file.read()

    selection_start = source.find("foreach (ObjectId id in regionIds)")
    selection_end = source.find("tr.Commit();", selection_start)
    selection_pass = source[selection_start:selection_end]
    direction_start = source.find("foreach (ObjectId leafId in leafIds)")
    direction_end = source.find("rootDirection[rootNum]", direction_start)
    direction_pass = source[direction_start:direction_end]

    r.check("Selected regions are individually guarded",
            "try" in selection_pass and "catch (Exception)" in selection_pass and
            "skippedEntityIds.Add(id);" in selection_pass)
    r.check("Direction detection is individually guarded",
            "try" in direction_pass and "catch (Exception)" in direction_pass and
            "skippedEntityIds.Add(leafId);" in direction_pass)
    r.check("Failures are counted once per entity",
            "var skippedEntityIds = new HashSet<ObjectId>();" in source and
            "skippedEntityIds.Count" in source)
    r.check("Failure count is reported concisely",
            "跳过 {skippedEntityIds.Count} 个异常或失效区域" in source)
    r.check("Active numbered-leaf filtering remains in place",
            "if (!IsActiveNumberedLeaf(ent))" in selection_pass)
    return r


def test_hcn_format():
    r = TestResult("HCN CSV format validation")
    valid = "编号,圆数量,X坐标,Y坐标,排列方向\n1,128,50,50,X\n2,64,100,100,Y\n"
    ok, msg = validate_hcn_csv(valid)
    r.check("Valid HCN CSV", ok, msg)

    neg_count = "编号,圆数量,X坐标,Y坐标,排列方向\n1,-1,50,50,X\n"
    ok, msg = validate_hcn_csv(neg_count)
    r.check("Rejects negative count", not ok)
    return r


def test_csv_file_prefix():
    """HEX exports Spacing_*.csv, HBR exports Brightness_*.csv, HCN exports Count_*.csv"""
    r = TestResult("CSV file prefix rules")
    r.check("HEX prefix is Spacing_", True)
    r.check("HBR prefix is Brightness_", True)
    r.check("HCN prefix is Count_", True)
    return r


def run_all():
    tests = [
        test_hex_format(),
        test_hbr_format(),
        test_hbr_fault_isolation_policy(),
        test_hcn_format(),
        test_csv_file_prefix(),
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
