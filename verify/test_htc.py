# -*- coding: utf-8 -*-
"""
HTC 用户硬性要求回归 guard（源码检查，非 CAD）。
锁住两条最高优先级用户要求：
  #1 按微软雅黑转换轮廓（2026-08-14，不得改回其他字体）
  #2 圆直径固定 0.03mm、圆心间距固定 0.06mm（2026-08-16，08-29 用户再次确认）
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from geom import TestResult

HTC_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Commands", "HtcCommand.cs")


def _htc_source():
    with open(HTC_PATH, encoding="utf-8-sig") as f:
        return f.read()


def test_htc_fixed_dot_parameters():
    """HTC 圆直径 0.03mm / 圆心间距 0.06mm 固定值，不受设置影响。"""
    r = TestResult("HTC 固定圆直径/间距 0.03/0.06")
    source = _htc_source()
    r.check("圆直径固定 0.03mm", "double diameter = 0.03;" in source,
            "期望 diameter=0.03（曾被误改为 0.1，2026-08-29 更正）")
    r.check("圆心间距固定 0.06mm", "double spacing = 0.06;" in source,
            "期望 spacing=0.06")
    r.check("无 0.1 固定值残留", "double diameter = 0.1;" not in source
            and "double spacing = 0.1;" not in source)
    return r


def test_htc_force_yahei():
    """HTC 统一按微软雅黑转换轮廓（用户要求，不得改回其他字体）。"""
    r = TestResult("HTC 强制微软雅黑")
    source = _htc_source()
    r.check("强制 msyh.ttc", 'fontFileName = "msyh.ttc";' in source)
    r.check("强制 Microsoft YaHei", 'typeFaceName = "Microsoft YaHei";' in source)
    r.check("文件头注释与实际一致(不描述已否决的自身字体方案)",
            "不再强制改为微软雅黑" not in source)
    return r


def test_htc_em_scale_mapping():
    """先保留 em 度量，再在未旋转平面中拟合真实可见宽高。
    实际位置/尺寸另由 RevisionTextChecks 的真实 CAD 数据验证。"""
    r = TestResult("HTC 轮廓 em 缩放映射")
    source = _htc_source()
    r.check("scaleY 用 em 缩放(字高/1000)",
            "scaleY = textHeight / 1000.0;" in source,
            "期望 em 缩放: 墨迹高=gdH×字高/1000, 而非 =字高")
    r.check("无 字高/墨迹高 的放大公式残留",
            "scaleY = textHeight / gdH" not in source)
    r.check("按真实可见范围校准宽高", "double fitX = extW /" in source and "double fitY = extH /" in source)
    r.check("先消除旋转再恢复旋转", "Matrix3d.Rotation(-rotation" in source and "toLocal.Inverse()" in source)
    return r


def test_dialog_file_dia_remains_enabled():
    """HIX/HAD 临时开启 FILEDIA 后，命令结束仍保持 1，防止后续 NET 命令不弹窗。"""
    r = TestResult("CAD 对话框 FILEDIA 持续启用")
    root = os.path.dirname(os.path.abspath(__file__))
    files = [
        os.path.join(root, "..", "HexFill.cs"),
        os.path.join(root, "..", "Commands", "HadCommand.cs"),
    ]
    for path in files:
        with open(path, encoding="utf-8-sig") as f:
            source = f.read()
        name = os.path.basename(path)
        r.check(f"{name}: FILEDIA 恢复为 1", 'SetSystemVariable("FILEDIA", (short)1)' in source)
        r.check(f"{name}: 不得在 finally 恢复为 0",
                'SetSystemVariable("FILEDIA", (short)0)' not in source)
    return r


def run_all():
    tests = [
        test_htc_fixed_dot_parameters(),
        test_htc_force_yahei(),
        test_htc_em_scale_mapping(),
        test_dialog_file_dia_remains_enabled(),
    ]
    all_pass = True
    for t in tests:
        print(t.report())
        if not t.passed:
            all_pass = False
    return all_pass


if __name__ == "__main__":
    sys.exit(0 if run_all() else 1)
