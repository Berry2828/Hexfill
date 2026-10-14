# -*- coding: utf-8 -*-
"""
HTC 修复逻辑验证（无 AutoCAD 环境）：
用 GDI 导出的 "0" 字形轮廓点模拟新旧两版坐标转换 + 奇偶规则填充。
验证：
  A. 新锚定（em缩放+基线对齐）与 CAD TrueType 显示一致
  B. 奇偶规则：外圈与内圈之间(环带)填充、中心(孔洞)空心
"""
import math

GLYPH0_DATA = '201.75,463,0\n203.125,437.75,1\n205,413.875,1\n207.75,391.375,1\n211,370.25,1\n215,350.375,1\n219.625,331.875,1\n225,314.75,1\n231,298.875,1\n245.125,270,1\n261.75,243.75,1\n281.125,220.125,1\n303.25,199.25,1\n315.25,190.125,1\n328,182.25,1\n341.5,175.5,1\n355.5,170,1\n370.25,165.75,1\n385.75,162.75,1\n401.75,160.875,1\n418.5,160.25,1\n435.375,161.125,1\n451.625,163.75,1\n467.25,168.25,1\n482.5,174.375,1\n497.125,182.375,1\n511.125,192.125,1\n524.625,203.625,1\n537.625,216.875,1\n549.75,231.5,1\n560.875,247,1\n570.75,263.5,1\n579.625,280.875,1\n587.375,299.125,1\n594.125,318.25,1\n599.625,338.375,1\n604.125,359.375,1\n607.625,381.25,1\n610.625,404,1\n612.75,427.5,1\n614.375,451.75,1\n615.25,476.75,1\n615.5,502.625,1\n615,529.25,1\n613.875,556.75,1\n612,583.875,1\n609.125,609.75,1\n605.625,634.375,1\n601.125,657.75,1\n595.75,679.875,1\n589.625,700.625,1\n582.625,720.125,1\n574.75,738.375,1\n565.875,755.25,1\n556.125,770.875,1\n545.125,785,1\n533.25,797.875,1\n520.25,809.5,1\n506.25,819.625,1\n491.25,828.5,1\n475.125,836,1\n458.5,841.875,1\n442,845.875,1\n425.375,848,1\n408.75,848.25,1\n392.125,846.5,1\n375.625,843,1\n359,837.5,1\n342.375,830.125,1\n326.375,821.5,1\n311.625,812.25,1\n298.125,802.25,1\n285.75,791.625,1\n274.625,780.25,1\n264.75,768.25,1\n256.125,755.625,1\n248.625,742.25,1\n235.875,714.5,1\n225.125,686.125,1\n216.375,656.875,1\n209.5,627,1\n206.875,611.25,1\n204.625,594,1\n203,575.5,1\n201.75,555.75,1\n201,534.625,1\n200.75,512,1\n201,488.25,1\n201.75,463,129\n283.75,439.5,0\n281.25,466,1\n279.875,491.75,1\n279.375,516.75,1\n279.875,541,1\n281.25,564.625,1\n283.75,587.375,1\n287.25,609.5,1\n291.625,630.875,1\n296.875,651.125,1\n303,669.875,1\n309.875,687,1\n317.5,702.625,1\n325.875,716.75,1\n335.125,729.375,1\n345.25,740.5,1\n356,750,1\n367.25,757.875,1\n378.625,764.125,1\n390,768.375,1\n401.375,771,1\n412.875,771.875,1\n424.5,770.875,1\n436.125,768.25,1\n447.875,763.75,1\n459.25,757.75,1\n469.75,750.25,1\n479.5,741.5,1\n488.375,731.5,1\n496.5,720.125,1\n503.875,707.25,1\n510.375,693.25,1\n516.125,677.75,1\n525.5,644.875,1\n532.25,610.5,1\n536.25,574.5,1\n537.625,537.125,1\n537.25,499.875,1\n536.25,464.375,1\n534.375,430.625,1\n531.75,398.5,1\n527.375,368,1\n520,339,1\n509.75,311.375,1\n496.625,285.25,1\n489,273.25,1\n480.75,262.875,1\n472,254,1\n462.5,246.625,1\n452.375,240.75,1\n441.75,236.5,1\n430.375,233.75,1\n418.5,232.5,1\n406.5,232.875,1\n394.875,234.75,1\n383.625,238.375,1\n372.625,243.75,1\n362.125,250.625,1\n351.875,259.25,1\n342.125,269.375,1\n332.625,281.25,1\n323.75,294.875,1\n315.625,310.25,1\n308.375,327.375,1\n301.875,346.25,1\n296.125,366.875,1\n291.125,389.375,1\n287,413.5,1\n283.75,439.5,129'



# ---------- 字形数据 ----------
subpaths = []
cur = []
for line in GLYPH0_DATA.splitlines():
    x, y, t = line.strip().split(',')
    cur.append((float(x), float(y), int(t)))
    if int(t) & 0x80:
        subpaths.append(cur)
        cur = []
print(f"子路径数: {len(subpaths)}，各点数: {[len(s) for s in subpaths]}")

# ---------- 字体 metrics（SimHei，实验测得） ----------
EM_UNITS = 256.0
CELL_ASCENT = 220.0
CELL_DESCENT = 36.0
baseline_gdi = CELL_ASCENT / EM_UNITS * 1000.0   # 859.375

H = 10.0        # CAD 文字高度 mm
WF = 1.0        # WidthFactor
pos = (0.0, 0.0)  # CAD 插入点（基线左端点）

def to_cad_new(pt):
    """新版：em 缩放 + 基线锚定"""
    scale = H / 1000.0
    sx = scale * WF
    dx = pt[0] * sx
    dy = (baseline_gdi - pt[1]) * scale   # 基线以上为正
    return (pos[0] + dx, pos[1] + dy)

def to_cad_old(pt):
    """旧版：字形包围盒归一化 + 顶部锚定（bug 版）"""
    minx = min(p[0] for s in subpaths for p in s)
    maxx = max(p[0] for s in subpaths for p in s)
    miny = min(p[1] for s in subpaths for p in s)
    maxy = max(p[1] for s in subpaths for p in s)
    glyph_h = maxy - miny
    scale = H / glyph_h
    sx = scale * WF
    dx = (pt[0] - minx) * sx
    dy = -(pt[1] - miny) * scale
    top_y = pos[1] + H
    return (pos[0] + dx, top_y + dy)

def bbox(polys):
    xs = [p[0] for poly in polys for p in poly]
    ys = [p[1] for poly in polys for p in poly]
    return min(xs), max(xs), min(ys), max(ys)

new_polys = [[to_cad_new(p) for p in s] for s in subpaths]
old_polys = [[to_cad_old(p) for p in s] for s in subpaths]

nx0, nx1, ny0, ny1 = bbox(new_polys)
ox0, ox1, oy0, oy1 = bbox(old_polys)
print(f"\n[新锚定] 圆轮廓范围: x[{nx0:.3f},{nx1:.3f}] y[{ny0:.3f},{ny1:.3f}]  (高 {ny1-ny0:.2f} 宽 {nx1-nx0:.2f})")
print(f"[旧锚定] 圆轮廓范围: x[{ox0:.3f},{ox1:.3f}] y[{oy0:.3f},{oy1:.3f}]  (高 {oy1-oy0:.2f} 宽 {ox1-ox0:.2f})")
print(f"[CAD实际显示预期] '0' H=10 黑体: y[{baseline_gdi*H/1000-8.48:.2f}, {baseline_gdi*H/1000-1.60:.2f}] 高 6.88, "
      f"x[{2.01:.2f},{6.16:.2f}] 宽 4.15")

# ---------- 奇偶规则点内测试 ----------
def point_in(p, poly):
    inside = False
    n = len(poly)
    j = n - 1
    for i in range(n):
        xi, yi = poly[i][0], poly[i][1]
        xj, yj = poly[j][0], poly[j][1]
        if ((yi > p[1]) != (yj > p[1])) and \
           (p[0] < (xj - xi) * (p[1] - yi) / (yj - yi) + xi):
            inside = not inside
        j = i
    return inside

def crossings(p, polys):
    return sum(1 for poly in polys if point_in(p, poly))

# 直接验证字形中心点
cx, cy = (nx0+nx1)/2, (ny0+ny1)/2
c_count = crossings((cx, cy), new_polys)
print(f"\n[奇偶规则] 字形中心点({cx:.2f},{cy:.2f}) 被 {c_count} 条轮廓包围 → "
      f"{'填充(错误)' if c_count % 2 == 1 else '空心(正确)'}")
ring_count = crossings((cx, ny1-0.4), new_polys)  # 环带顶部（外圈顶下0.4，高于内孔顶）
print(f"[奇偶规则] 环带测试点(顶下0.4) 被 {ring_count} 条包围 → {'填充(正确)' if ring_count % 2 == 1 else '空心(错误)'}")

# 模拟 HTC 网格（yOffset=true 蜂窝，spacing=0.3 细化观察环带填充）
spacing = 0.3
radius = 0.03
ext = bbox(new_polys)
col_sp = spacing * math.sqrt(3) / 2
row_sp = spacing
rows = int((ext[3]-ext[2]-2*radius)//row_sp) + 1
cols = int((ext[1]-ext[0]-2*radius)//col_sp) + 1
start_x = ext[0] + radius
start_y = ext[2] + radius

filled_new, filled_old = 0, 0
for r in range(rows):
    for c in range(cols):
        x = start_x + c * col_sp
        y = start_y + r * row_sp + (0.5 * row_sp if c % 2 else 0)
        p = (x, y)
        # 新：奇偶规则
        if crossings(p, new_polys) % 2 == 1:
            filled_new += 1
        # 旧：每个子路径独立填充（任意轮廓内即填）
        if crossings(p, new_polys) >= 1:
            filled_old += 1
print(f"\n[网格模拟] 新(奇偶): {filled_new} 点 / 旧(独立填充): {filled_old} 点 → 旧版中心多填 {filled_old-filled_new} 点")

# 断言
assert c_count % 2 == 0, "中心应空心"
assert ring_count % 2 == 1, "环带应填充"
assert filled_new < filled_old, "新版点数应少于旧版（中心被排除）"
# 新锚定范围应与 CAD 显示一致（容差 0.3mm）
exp_y0 = baseline_gdi*H/1000 - 8.48
exp_y1 = baseline_gdi*H/1000 - 1.60
assert abs(ny0 - exp_y0) < 0.3 and abs(ny1 - exp_y1) < 0.3, "Y 范围应匹配 CAD 显示"
print("\n=== 全部断言通过 ===")
