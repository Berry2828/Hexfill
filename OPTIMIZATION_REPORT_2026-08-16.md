# HexFill 优化报告（2026-08-16）

基线：`C:\Users\allen\Documents\Hexfill 2`（当前活跃工作副本，含 8/16 HGAP 闭合线顶点清理改动）。
本轮范围（用户确认四选全部）：行为不变的性能/质量优化 + 修复已知缺陷 + 静态审查报告 + 结构拆分。
**注意：本会话无法执行编译与 AutoCAD 回归（bash 不可用），所有改动均为静态实施，必须按第四节在本地验证。**

> **后续决策（同日）**：本报告完成后用户确定「只交付一份完整版 `HexFill.dll`，不再做 Lite/精简版」。
> 报告中对 `HexFill.Lite.csproj` 的登记与 Lite 白名单相关表述即日起作废，详见 `CHANGELOG.md` 的交付策略条目。

## 一、已实施改动总览

### 1. 修复的已知缺陷（对应 2026-08-13 报告第四节风险清单）

| # | 缺陷 | 改动文件 | 说明 |
|---|------|----------|------|
| 1 | HGAP 兜底闭合必然抛异常 | `Commands/HgapCommand.cs` | `TryHgapCleanClosePolyline` 改为先 `Closed = true` 再 `SetBulgeAt(N-1, 0)`；开放多段线在闭合前对 N-1 段索引调用必然越界 |
| 2 | HPLIT 量纲混用 | `Commands/HplitCommand.cs` | `HplitPointOnSegment` 用投影比例 + 按段长换算的容差替代 dot 与长度容差直接比较 |
| 3 | HAD/HIX CSV 文化区解析 | `Commands/HadCommand.cs`、`HexFill.cs` | 新增 `TryParseDoubleAnyCulture`（先 InvariantCulture 再当前文化）；HEX/HCN 导出改为 InvariantCulture 格式 |
| 4 | HAD 均匀度口径 | `Commands/HadCommand.cs` | 只在 CAD 中存在该根区域（且有叶子）时才计入均匀度统计 |
| 5 | 间距下限与会话级 HDIA 不一致 | `HexFill.cs`、`Commands/HadCommand.cs` | 新增 `GetMinSpacingForRadius`：0.06mm 直径→0.14mm、0.03mm→0.1mm；HGR/HRS/HIX/HAD 按区域内实际圆径钳制 |
| 6 | HCHK 自交端点漏报 | `Commands/HcheckCommand.cs` | ta/tb 容差按段长换算为绝对距离（≥0.001mm 才算内部交点） |
| 7 | HCHK LINE 缺编号引用校验 | `Commands/HcheckCommand.cs` | LINE 分支补调 `CheckHcheckEntityNumberHandle` |
| 9 | HJOIN 重复选中 | `Commands/HjoinCommand.cs` | 选择列表先按 ObjectId 去重再建计划 |
| 11 | HCLN 视口外实体漏检 | `HexFill.cs` | `ed.SelectAll` 改为遍历模型空间数据库（视口无关，与 7/19「视口无关」原则一致） |
| 14 | HEX/HCN/HBR 测试过期 | `verify/test_hexport.py` | 校验器更新为 5 列（编号,间距/数量,坐标,排列方向）/4 列（HBR）格式 |
| 15 | HOF 重复偏移计算 | `HexFill.cs` | `GetOffsetValue` 增加 `out Entity`：方向测试结果为正方向时直接复用，`GetOffsetCurves` 两次→一次 |
| 17 | HPLIT 越界交点/失败开放 | `Commands/HplitCommand.cs` | `HplitSegmentIntersection` 端点容差改为长度相关（绝对距离 ≈1e-6mm）；`HplitPathMayOverlap` 异常时 fail-closed |

### 2. 结构拆分（行为不变）

- `Commands/HbalCommand.cs`（新增，304 行）：HBAL 命令 + 球标常量/字段 + 删除事件重编号逻辑（从 HexFill.cs 原样搬出）。
- `Ui/OffsetDialog.cs`（新增，201 行）：HOF 设置对话框（从 HexFill.cs 原样搬出）。
- `HexFill.cs`：4170 → 3685 行；仅保留核心命令与共享辅助方法。
- `HexFill.csproj` / `HexFill.Lite.csproj` / `build_hexfill.rsp` 已登记两个新源文件；Lite 白名单校验脚本按 csproj 的 Compile 节点扫描，HBAL/HOF 均在 Lite 白名单内，不受影响。

### 3. 静态审查未改动项（建议在可构建环境逐项评估）

- HCHK 模型空间/当前空间混用（#8）：仅影响布局空间场景，改动的包含关系复杂，未动。
- 静默吞错（#10）：多个 catch 块无诊断，加日志会改变输出噪声，未动。
- HBR 逐项级联失败（#12）：需要设计逐项容错与统计，未动。
- 锁定标识图层时清标识失败（#13）：需要锁定层检测与跳过策略，未动。
- HPLIT zigzag 无条件替换（#16）：需要面积/质量校验设计，未动。
- HPLIT 擦边相交钳制（#17 后半）：已用长度相关容差缓解，顶点真实相交的兼容性未验证，保持现状。

## 二、改动文件清单

| 文件 | 状态 |
|------|------|
| `HexFill.cs` | 修改（HIX/HGR/HRS 钳制、HCLN 遍历、HOF 合并、HEX/HCN 导出、删除球标+对话框块） |
| `Commands/HplitCommand.cs` | 修改（量纲、端点容差、fail-closed） |
| `Commands/HgapCommand.cs` | 修改（兜底闭合顺序） |
| `Commands/HcheckCommand.cs` | 修改（自交容差、LINE 校验） |
| `Commands/HjoinCommand.cs` | 修改（去重） |
| `Commands/HadCommand.cs` | 修改（解析、口径、间距钳制） |
| `Commands/HbalCommand.cs` | 新增（球标 partial 文件） |
| `Ui/OffsetDialog.cs` | 新增（HOF 对话框） |
| `HexFill.csproj` / `HexFill.Lite.csproj` / `build_hexfill.rsp` | 修改（登记新源文件） |
| `verify/test_hexport.py` | 修改（5 列格式同步） |
| `CHANGELOG.md` | 修改（本轮条目） |

## 三、静态自检结果

- 11 个 C# 源文件花括号平衡全部通过（含字符串/注释跳过）。
- 球标/对话框代码块 1:1 搬移，无逻辑改动（HbalCommand 仅含 HBAL 一个 CommandMethod）。
- 性能 guard 脚本要求的必需模式（CircleSpatialIndex / BuildNearestPointTree / BuildCurveContainment / CollectCircleRecordsInRegion / CollectSelectedClosedRegionIds / NearestNeighborBruteForceThreshold / ApplyReplacement）仍全部存在于 HexFill.cs；禁止模式未引入。
- 未在本机编译（bash 不可用），需本地构建确认零警告。

## 四、本地验证步骤（必做）

1. 编译（只构建完整版一份 DLL）：
```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe HexFill.csproj /p:Configuration=Release
```
或直接使用项目现有 build_hexfill.rsp / VS 构建；构建期 guard（性能、CAD 状态）应全过。
注：2026-08-16 起只交付完整版 HexFill.dll，不再构建 Lite（HexFill.Lite.csproj 已废弃）。
2. 纯 Python 套件：
```
cd "C:\Users\allen\Documents\Hexfill 2" && python verify/runner.py
```
（HEXPORT 已同步为 5 列格式）
3. 真实 AutoCAD 回归（重点覆盖本轮改动命令）：
- HPLIT 8 例（尤其竖切/对角/L 形：验证端点容差改动未破坏分割）
- HGAP 4 例 + 首末段带弧/平行的开放线兜底闭合（此前必失败的路径，本轮应通过）
- HCHK 自交/圆弧用例（端点容差改动后长段端点附近自交应能报出）
- HAD/HIX 闭环（CSV 用小数点与逗号小数两种写法各测一次）
- HGR/HRS/HRD 在 0.03mm 与 0.06mm 直径图纸上各测一次（间距下限按实际圆径）
- HOF 外扩/内缩（确认偏移结果与旧版一致、无重复计算）
- HJOIN 品字形（重复选中同一线应被忽略）
- HCLN 外框+子区域（视口外对象参与清除）
- HBAL 删除重编号（新文件编译后事件仍生效）

## 五、结论

本轮在不改变命令行为的前提下完成了：11 项已知缺陷修复（#1-#7、#9、#11、#14、#15、#17 部分）、HOF 双偏移合并等性能优化、球标与对话框的结构拆分，以及测试同步。
剩余 6 项风险（#8、#10、#12、#13、#16、#17 后半）保持原样并给出后续方向。
由于本会话无编译/回归能力，以上改动须经本地构建与真实 AutoCAD 回归确认后再作为交付版本使用。
