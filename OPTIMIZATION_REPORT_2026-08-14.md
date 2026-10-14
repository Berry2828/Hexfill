# HexFill 2026-08-14 合并与优化报告

目标：全盘定位最新版 HexFill 项目 → 检查 → 优化一版 → 交付最终完整版 DLL 到桌面。

## 一、全盘定位结果

| 位置 | 状态 |
|------|------|
| `C:\Users\allen\AppData\Local\Programs\Reasonix\HexFill` | **最新版**（HexFill.cs 2026-08-13 17:29，含 HTC 文字转网点命令与 8/13 修复；Reasonix 工作区副本，git 仓库） |
| `C:\Users\allen\Documents\Hexfill 2` | Codex 同步副本（8/7 HRS 优化后；8/13-14 做过一轮行为不变优化，但**缺 HTC**） |
| `C:\Users\allen\Documents\Codex\...\HexFill_Ultimate_20260710_214835` 等 | 2026-06/07 旧版 |
| `D:\Claude text\...`、`D:\Users\allen\.kun\default_workspace\HexFill*`、`E:\`（无） | 2026-06/07 旧版 |
| 微信收到的 `HexFill_HRA_20260811.dll` / `HexFill_HTC修复_20260813.dll` | 交付产物；HRA 未进入主线（当前源码无 HRA 命令，仅保留其算法模拟测试 `verify/test_hra.py`） |

结论：以 **Reasonix 工作区副本**为最新基线，把 Documents 版已完成的优化合并进去。

## 二、本次检查发现（最新版特有）

1. **Lite 构建已损坏**（真实缺陷）：HTC 写在共享 `HexFill.cs` 内，被 Lite 编译进 DLL，
   Lite 白名单校验直接构建失败（`Lite contains commands outside the whitelist: HTC`），
   与项目规则"新增命令只进完整版"冲突。
2. HTC 代码小瑕疵（不影响行为）：`BuildTextOutlines` 的 `tr`/`ent` 参数未使用；
   构造了一个从未使用的 `System.Drawing.Font` 对象；`baselineOffset` 死变量；
   注释仍描述已弃用的 Explode 方案。
3. 其余命令行为与既有审计一致（前一轮 17 项风险清单仍适用，未改动）。

## 三、本次改动

### 修复
- **HTC 移入 `Commands\HtcCommand.cs`**（仅完整版编译）→ Lite 恢复 11 命令白名单，
  构建零警告通过。HTC 命令本身行为不变。

### 合并优化（全部行为不变，从 Documents 版移植）
- XData `ResultBuffer` 显式释放（Get/SetXDataString，消除原生内存泄漏）
- HNM/HCLN 包含分析边界采样缓存（大选择集父子判定不再每对重复采样）
- HNM 旧编号清理只读预检；`RXClass.GetClass` 提升循环外（3 处）
- `GenerateHexGridStrict` 热路径内联（半行/列距、居中距离、Point3d 延后构造）
- HPLIT 直线段顶点直取、平方距离比较、3 个死方法删除
- HJOIN 候选表预计算、无效排序删除、无变化时跳过整表重建、模板缓存、死字段删除
- HCHK `HcheckSegment` 改 struct、候选表预计算、死字段删除
- HGAP `FindHgapConnectedSourceIds` O(n²) → minX 扫描线预筛选
- HAD/HBR 死代码与 LINQ 清理
- HTC 死参数/死变量/无用 Font 对象清理，注释同步为实际 GDI+ 实现

## 四、验证结果

- **编译**：Full（含 HTC）与 Lite 均零警告零错误；性能 guard、CAD 状态 guard、
  Lite 白名单 guard 全部通过。
- **纯 Python**：`runner.py` 全套（ACAD_STATE/HDIA/HEXPORT/HGR/HIMPORT/HNM/HOF/
  HPLIT/HPLIT_MULTI/HRA/HRS/HUTIL）VERIFICATION PASSED；pytest 44 项通过。
- **真实 AutoCAD 2018 回归**（合并后 Full DLL）：
  - HTC 真机：entmakex "中"(H=10) → 6327 圆，x[0.046,8.360] y[0.000,9.950]
    全部落在字形笔画范围内，无残留轮廓聚合线，原文字保留 → PASS
  - HNM 4 例、HGAP 4 例、HPLIT 8 例、HJOIN 混合 5 组、HCHK 普通/圆弧 2 例 → 全部 PASS
  - Lite 白名单回归（GRID/HIX/HOF/HBAL/HNM）→ PASS
- 环境限制沿用前轮结论：`HCHK problem_markers` 用例脚本中的 `_.TEXT` 命令在当前
  环境使 CAD 进入模态卡死（旧 DLL 与纯 TEXT 命令同样复现，与插件代码无关），
  该用例未纳入本次回归。

## 五、交付物

- 桌面：`HexFill_Full_20260814.dll`（最终完整版，唯一一份）
- 归档：`collab/out/005/`（同 DLL + 本报告）
- 同步：`C:\Users\allen\Documents\Hexfill 2`（Codex 原目录）已同步最新源码与文档
