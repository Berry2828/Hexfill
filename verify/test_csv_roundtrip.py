# -*- coding: utf-8 -*-
"""
HBR/HAD CSV 往返验证（非 CAD）—— 忠实映射 HexFill.cs + Commands/HadCommand.cs 的解析逻辑。
覆盖 codex 2026-08-29 改动：Excel 编码识别、分隔符、引号、HAD 行策略
（坏行 / 非法数值 / 空亮度缺失 / 重复编号 / 非法方向）。
用仓库真实样本 + Excel 改写变体（UTF-16 / 分号 / 逗号小数 / Tab）验证。
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from geom import TestResult

VERIFY_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_DIR = os.path.dirname(VERIFY_DIR)


# ---------- 忠实映射 C# ReadCsvLinesWithExcelEncoding + SplitCsvLines ----------
def read_csv_bytes(data):
    if data[:4] == b"\x00\x00\xfe\xff":
        content = data[4:].decode("utf-32-be")
    elif data[:4] == b"\xff\xfe\x00\x00":
        content = data[4:].decode("utf-32-le")
    elif data[:3] == b"\xef\xbb\xbf":
        content = data[3:].decode("utf-8-sig")
    elif data[:2] == b"\xff\xfe":
        content = data[2:].decode("utf-16-le")
    elif data[:2] == b"\xfe\xff":
        content = data[2:].decode("utf-16-be")
    else:
        # 无 BOM: 先严格 UTF-8, 失败回退系统 ANSI (中文系统 = GBK)
        try:
            content = data.decode("utf-8")
        except UnicodeDecodeError:
            content = data.decode("gbk", errors="replace")
    lines = [ln for ln in content.replace("\r\n", "\n").replace("\r", "\n").split("\n") if ln]
    return lines


# ---------- 忠实映射 C# ParseCSVLine / DetectCsvDelimiter / NormalizeCsvField ----------
def _count_delimiter(line, d):
    n = 0
    quoted = False
    i = 0
    while i < len(line):
        ch = line[i]
        if ch == '"':
            if quoted and i + 1 < len(line) and line[i + 1] == '"':
                i += 1
            else:
                quoted = not quoted
        elif ch == d and not quoted:
            n += 1
        i += 1
    return n


def detect_delimiter(line):
    comma = _count_delimiter(line, ",")
    semi = _count_delimiter(line, ";")
    tab = _count_delimiter(line, "\t")
    if tab >= comma and tab >= semi and tab > 0:
        return "\t"
    if semi > comma:
        return ";"
    return ","


def normalize_field(value):
    return value.strip().strip("﻿").strip("​").strip()


def parse_csv_line(line):
    delim = detect_delimiter(line)
    fields = []
    field = []
    quoted = False
    i = 0
    while i < len(line):
        ch = line[i]
        if ch == '"':
            if quoted and i + 1 < len(line) and line[i + 1] == '"':
                field.append('"')
                i += 1
            else:
                quoted = not quoted
        elif ch == delim and not quoted:
            fields.append(normalize_field("".join(field)))
            field = []
        else:
            field.append(ch)
        i += 1
    fields.append(normalize_field("".join(field)))
    return fields


def has_unclosed_quote(line):
    quoted = False
    i = 0
    while i < len(line):
        if line[i] != '"':
            i += 1
            continue
        if quoted and i + 1 < len(line) and line[i + 1] == '"':
            i += 2
            continue
        quoted = not quoted
        i += 1
    return quoted


# ---------- 忠实映射 C# TryParseDoubleAnyCulture ----------
def parse_double_any_culture(text):
    normalized = normalize_field(text).replace(" ", "").replace(" ", "")
    if normalized.startswith("'"):
        normalized = normalized[1:]
    if normalized.startswith('="') and normalized.endswith('"') and len(normalized) >= 4:
        normalized = normalized[2:-1]
    if not normalized:
        return None
    last_dot = normalized.rfind(".")
    last_comma = normalized.rfind(",")
    if last_dot >= 0 and last_comma >= 0:
        if last_comma > last_dot:
            normalized = normalized.replace(".", "").replace(",", ".")
        else:
            normalized = normalized.replace(",", "")
    elif last_comma >= 0:
        normalized = normalized.replace(",", ".")
    try:
        return float(normalized)
    except ValueError:
        return None


# ---------- 忠实映射 Commands/HadCommand.cs 的 HAD 行策略 ----------
def had_import_policy(all_lines):
    bright_lines = all_lines[1:]  # Skip(1) 表头
    root_numbers = []
    root_curr = {}
    root_tgt = {}
    root_dir = {}
    stats = {"malformed": 0, "numeric": 0, "direction": 0, "duplicate": 0}
    for line in bright_lines:
        if not line.strip():
            continue
        if has_unclosed_quote(line):
            stats["malformed"] += 1
            continue
        parts = parse_csv_line(line)
        field_count = len(parts)
        while field_count > 4 and parts[field_count - 1] == "":
            field_count -= 1
        if field_count < 3 or field_count > 4:
            stats["malformed"] += 1
            continue
        num = normalize_field(parts[0])
        if num.startswith("'"):
            num = num[1:].strip()
        if num.startswith('="') and num.endswith('"') and len(num) >= 4:
            num = num[2:-1]
        num = num.strip()
        if not num:
            stats["malformed"] += 1
            continue
        cur_field = normalize_field(parts[1])
        tgt_field = normalize_field(parts[2])
        cur_blank = cur_field == ""
        tgt_blank = tgt_field == ""
        curr = parse_double_any_culture(cur_field) if not cur_blank else 0.0
        tgt = parse_double_any_culture(tgt_field) if not tgt_blank else 0.0
        cur_parsed = curr is not None
        tgt_parsed = tgt is not None
        curr = curr if curr is not None else 0.0
        tgt = tgt if tgt is not None else 0.0
        numeric_invalid = (
            (not cur_parsed and not cur_blank) or (not tgt_parsed and not tgt_blank)
            or curr != curr or abs(curr) == float("inf")
            or tgt != tgt or abs(tgt) == float("inf"))
        if numeric_invalid:
            stats["numeric"] += 1
            continue
        if num in root_curr:
            stats["duplicate"] += 1
            continue
        root_numbers.append(num)
        root_curr[num] = curr
        root_tgt[num] = tgt
        if field_count == 4:
            direction = normalize_field(parts[3]).upper()
            if direction in ("X", "Y"):
                root_dir[num] = direction
            else:
                stats["direction"] += 1
    return root_numbers, root_curr, root_tgt, root_dir, stats


# ---------- 测试 ----------
def test_real_hbr_export_blank_brightness():
    """真实 HBR 导出(UTF-8 BOM, 空亮度): 表头跳过, 空亮度走缺失(0,0)而非非法数值。"""
    r = TestResult("真实 HBR 导出空亮度往返")
    path = os.path.join(PROJECT_DIR, "tests", "hbr_export_Brightness_20260710_230924.csv")
    data = open(path, "rb").read()
    r.check("真实样本是 UTF-8 BOM", data[:3] == b"\xef\xbb\xbf")
    lines = read_csv_bytes(data)
    nums, curr, tgt, d, stats = had_import_policy(lines)
    r.check("解析到根 10", nums == ["10"], str(nums))
    r.check("空亮度=缺失(0,0), 非非法数值", curr["10"] == 0.0 and tgt["10"] == 0.0
            and stats["numeric"] == 0, str(stats))
    r.check("方向保留 X", d.get("10") == "X")
    r.check("无坏行/重复", stats["malformed"] == 0 and stats["duplicate"] == 0)
    return r


def test_real_had_smoke_valid():
    """真实 had_smoke(无 BOM, 有效数据 10,100,80,X): 正确解析 + 无警告。"""
    r = TestResult("真实 HAD smoke 有效行")
    path = os.path.join(PROJECT_DIR, "tests", "Brightness_had_smoke.csv")
    data = open(path, "rb").read()
    r.check("无 BOM 样本", data[:3] != b"\xef\xbb\xbf")
    lines = read_csv_bytes(data)
    nums, curr, tgt, d, stats = had_import_policy(lines)
    r.check("解析到根 10", nums == ["10"])
    r.check("亮度 100->80 正确", curr["10"] == 100.0 and tgt["10"] == 80.0)
    r.check("方向 X", d.get("10") == "X")
    r.check("无任何警告", stats == {"malformed": 0, "numeric": 0, "direction": 0, "duplicate": 0},
            str(stats))
    return r


def test_utf16le_semicolon_comma_decimal():
    """Excel 另存变体: UTF-16LE BOM + 分号分隔 + 逗号小数。"""
    r = TestResult("Excel 改写变体 UTF-16/分号/逗号小数")
    text = "编号;当前亮度;目标亮度;排列方向\n1;0,5;1,0;Y\n2;1,25;2,5;X\n"
    data = text.encode("utf-16-le")
    data = b"\xff\xfe" + data
    lines = read_csv_bytes(data)
    nums, curr, tgt, d, stats = had_import_policy(lines)
    r.check("UTF-16 解码 + 分号解析", nums == ["1", "2"], str(nums))
    r.check("逗号小数正确(0,5->0.5)", abs(curr["1"] - 0.5) < 1e-9 and abs(tgt["1"] - 1.0) < 1e-9)
    r.check("1,25->1.25(单逗号=小数点)", abs(curr["2"] - 1.25) < 1e-9)
    r.check("方向正确", d.get("1") == "Y" and d.get("2") == "X")
    r.check("无警告", stats["malformed"] == 0 and stats["numeric"] == 0 and stats["duplicate"] == 0)
    return r


def test_tab_delimiter():
    r = TestResult("Tab 分隔变体")
    text = "编号\t当前亮度\t目标亮度\t排列方向\n5\t2.0\t1.0\tX\n"
    data = text.encode("utf-8-sig")
    lines = read_csv_bytes(data)
    nums, curr, tgt, d, stats = had_import_policy(lines)
    r.check("Tab 解析", nums == ["5"] and abs(curr["5"] - 2.0) < 1e-9, str(nums))
    r.check("无警告", stats["malformed"] == 0)
    return r


def test_roundtrip_generated():
    """往返: 生成 HBR 导出格式 -> HAD 解析, 覆盖各类行。"""
    r = TestResult("HBR 导出格式往返 + 行策略")
    header = "编号,当前亮度,目标亮度,排列方向\n"
    rows = ['"1",,,"X"', '"2",100,80,Y', '"3",bad,0.5,X', '"3",1.0,1.0,X',
            '"4",,,"X",', '"5"', '"6",,,Z', '"7",,,X', '"8",2.0,1.0,X', '"8",9.0,9.0,Y']
    data = (header + "\n".join(rows) + "\n").encode("utf-8-sig")
    lines = read_csv_bytes(data)
    nums, curr, tgt, d, stats = had_import_policy(lines)
    r.check("空亮度根1缺失(0,0)", curr["1"] == 0.0 and tgt["1"] == 0.0)
    r.check("有效根2", curr["2"] == 100.0 and tgt["2"] == 80.0 and d["2"] == "Y")
    r.check("坏数值首条被丢弃, 第二条成为有效记录", curr["3"] == 1.0 and tgt["3"] == 1.0
            and stats["numeric"] == 1, str(stats))
    r.check("尾随空字段正常(根4)", "4" in curr and d["4"] == "X")
    r.check("字段不足根5计坏行", "5" not in curr and stats["malformed"] == 1)
    r.check("非法方向根6按实测回退", "6" in curr and "6" not in d and stats["direction"] == 1)
    r.check("合法根7方向 X", d["7"] == "X")
    r.check("重复编号首个有效记录优先", curr["8"] == 2.0 and tgt["8"] == 1.0
            and d["8"] == "X" and stats["duplicate"] == 1, str(stats))
    r.check("统计总数", stats == {"malformed": 1, "numeric": 1, "direction": 1, "duplicate": 1},
            str(stats))
    return r


def test_edge_cases():
    r = TestResult("边界: 未闭合引号 / 仅表头")
    text = "编号,当前亮度,目标亮度,排列方向\n"
    data = text.encode("utf-8")
    lines = read_csv_bytes(data)
    nums, _, _, _, stats = had_import_policy(lines)
    r.check("仅表头 -> 无数据行", nums == [])
    # 未闭合引号行
    data2 = ("编号,当前亮度,目标亮度,排列方向\n\"9,,,X\n").encode("utf-8-sig")
    lines2 = read_csv_bytes(data2)
    nums2, _, _, _, stats2 = had_import_policy(lines2)
    r.check("未闭合引号计坏行", nums2 == [] and stats2["malformed"] == 1, str(stats2))
    return r


def run_all():
    tests = [
        test_real_hbr_export_blank_brightness(),
        test_real_had_smoke_valid(),
        test_utf16le_semicolon_comma_decimal(),
        test_tab_delimiter(),
        test_roundtrip_generated(),
        test_edge_cases(),
    ]
    all_pass = True
    for t in tests:
        print(t.report())
        if not t.passed:
            all_pass = False
    return all_pass


if __name__ == "__main__":
    sys.exit(0 if run_all() else 1)
