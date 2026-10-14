# HexFill 功能检查与优化报告（2026-08-13）

目标：全命令功能检查 + 不影响行为的前提下尽可能优化。本轮**只做行为不变的优化**，
审查中发现的真实功能缺陷记录在文末风险清单，未在本轮改动（避免改变已验证行为）。

## 一、检查方式

1. 通读 `HexFill.cs`（4096 行）与 7 个命令文件（HPLIT/HJOIN/HGAP/HCHK/HBR/HAD/HSET），
   并对跨文件共享辅助方法做只读核查。
2. 基线编译（Full + Lite，构建期 guard 全过），随后运行真实 AutoCAD 2018 回归
   （`verify/acad_verify.py ALL`，同一最小化 CAD 会话）。
3. 逐项实施优化后重新编译（零警告），复跑纯 Python 套件（`runner.py` 全通过）
   与真实 CAD 回归（各套件独立会话，见第三节）。

## 二、已实施的优化（全部行为不变）

| 位置 | 改动 | 收益 |
|------|------|------|
| HexFill.cs `GetXDataString` | `GetXDataForApplication` 返回的 `ResultBuffer` 用后即 `Dispose`（try/finally） | 消除编号/亮度/球标等热路径的原生内存泄漏 |
| HexFill.cs `SetXDataString` | 读回的 `ent.XData` 复制后释放；`ent.XData =` 赋值后释放新缓冲 | 同上 |
| HexFill.cs `IsFullyContained` | 新增惰性缓存重载：同一曲线只做一次边界采样/包含判定器构建；原签名保留为包装 | HNM/HCLN 大选择集父子分析从每对采样降为每曲线一次 |
| HexFill.cs HNM | 旧编号清理循环先只读打开、有编号才升级写；父区域循环共享采样缓存 | 减少写升级与重复采样 |
| HexFill.cs HCLN | 后代/祖先收集循环共享同一采样缓存 | 同上 |
| HexFill.cs `GenerateHexGridStrict` | 半行距/半列距提升循环外；先判定后构造 Point3d；居中距离内联计算（同一 sqrt 公式） | 网格热路径减少结构体与方法开销 |
| HexFill.cs 三处遍历 | `RXClass.GetClass(...)` 提到循环外（圆收集、球标重编号、球标最大序号） | 省去每实体一次原生查找 |
| HplitCommand.cs | 直线段采样直接 `GetPoint2dAt`（跳过参数映射）；`AddHplitPoint` 平方距离比较；删除 3 个无调用方法（两个 `GetHplitExtensionLength` 重载、`ExtendHplitPath`） | 采样热路径提速、减少死代码 |
| HjoinCommand.cs | 候选表 minX/minY/maxX/maxY 预计算；删除无效 `candidates[i].Sort()`；无重叠且无超短段时跳过整表重建；多环共享模板按 ObjectId 缓存；删除死字段 `HjoinSegment.SourceIndex`；内联判断与 `HjoinCanUsePolyline` 去重 | 候选构建与重建阶段提速 |
| HcheckCommand.cs | `HcheckSegment` 由 class 改 struct；候选表包围盒预计算；删除死字段 `SourceIndex` 与无用 `sourceIndex` 参数 | 消除逐段堆分配、排序 lambda 不再重复计算 |
| HgapCommand.cs | `FindHgapConnectedSourceIds` 全量 O(n²) 改为 minX 扫描线预筛选（padding = max(连接容差, 0.001)，不漏任何候选对，结果集合与顺序无关）；删除冗余 `createdBefore` | 大量线段时连接分析提速 |
| HadCommand.cs | 删除重复的最小间距钳制（死代码）；缺失/有效亮度统计合并为单循环；旧间距均值改显式循环；圆心列表改显式 for | 减少 LINQ 分配与重复扫描 |
| HbrCommand.cs | 移除未使用 `using System.Linq`；根分组用 `TryGetValue` | 微优化 |

说明：HRS 于 2026-08-07 加入的 `skipParentRegions` 行为未动；所有几何判定、容差、
事务边界、消息文本与输出格式均保持原样。

## 三、验证结果

### 编译
- Full（`HexFill.csproj`）与 Lite（`HexFill.Lite.csproj`）均零警告、零错误；
  Lite 11 命令白名单、性能回归 guard、CAD 状态 guard 全部通过。
- 独立用 csc 7.3 对两套源文件语法校验同样零警告。

### 纯 Python 套件（`verify/runner.py`）
ACAD_STATE / HDIA / HEXPORT / HGR / HIMPORT / HNM / HOF / HPLIT / HPLIT_MULTI /
HRS / HUTIL 全部 PASS。

### 真实 AutoCAD 回归（优化后构建）
| 套件 | 结果 |
|------|------|
| GRID（生成失败保留旧圆+异常着色） | PASS |
| HEX→HIX 闭环（间距 2.0→1.4 实测 1.4） | PASS |
| HBR→HAD 闭环（270 圆→68 圆，间距 2→4） | PASS |
| HOF 模态对话框（面积 10000→12100） | PASS |
| HBAL 双文档重编号 | PASS |
| 锁定图层批量隔离（HPLIT/HJOIN） | PASS |
| HPLIT 8 例（垂直/L/Z/圆弧凹形/多矩形/不延长末段） | 全部 PASS |
| HSET 3 例（分割间距/缺口阈值/聚合距离） | 全部 PASS |
| HGAP 4 例（缺口标识/重叠线清理/重叠多段线/近端点连接） | 全部 PASS |
| HJOIN 混合 5 组（含 bulge 74 顶点曲线轮廓） | PASS |
| HCHK 普通/圆弧无误报 2 例 | PASS |
| HNM 4 例（分割重编号、无确认、HCLN 清孤立、单根跳过） | 全部 PASS |
| Lite 白名单回归（GRID/HIX/HOF/HBAL/HNM） | PASS |

### 已知环境障碍（与本次改动无关，已做 A/B 排除）
`HCHK problem_markers` 用例在本机当前环境下会卡死：用例脚本中的
`_.TEXT 400,0 2 0 99_99 ""` 一行（AutoCAD 内置 TEXT 命令，未调用插件任何命令）
使 CAD 进入模态状态，COM `SendCommand` 永久阻塞（会话内出现"文字格式"就地编辑器
窗口残留）。证据：① 用 08-07 优化前源码编译的旧 DLL 复跑同一用例同样卡死；
② 全新会话只发送 TEXT 命令（不含任何插件命令）即复现；③ 同一会话内
ERASE/-LAYER/PLINE/LINE 等命令全部正常。7 月审计时该用例通过，推测为
CAD 2018 与当前系统环境（输入法/动态输入）交互变化所致，与插件代码无关。
其余全部用例在优化后构建上通过。

## 四、功能检查发现（本轮未改动，建议确认后另行处理）

以下为三份代码审查中发现的功能性问题，按"不影响功能"原则本轮未修改，
仅记录供决策：

1. **HGAP 兜底路径必然抛异常**（HgapCommand.cs L532-535）：开放多段线
   `SetBulgeAt(NumberOfVertices-1)` 越界且顺序错误；首/末段带弧或平行时兜底
   闭合路径触发即整命令失败。正确写法应为先 `Closed = true` 再设置末段 bulge。
2. **HPLIT `HplitPointOnSegment` 量纲混用**（L1196-1197）：`dot`（长度²量纲）
   与 `tolerance`（长度量纲）直接比较，短边容差被放大、长边被压缩。
3. **HAD 亮度解析未用 InvariantCulture**（HadCommand.cs L76-77）：小数逗号区域
   下跨区 CSV 可能解析失败被当"未填写"。
4. **HAD 统计口径**：有效根计数与均匀度统计在检查 CAD 是否存在该根之前累计，
   会纳入 CSV 有、图纸无的根。
5. **HAD 最小间距取会话级 `CurrentMinSpacing`**：与图中实际圆半径（0.06mm 直径
   图纸）不一致时可能生成过密圆。
6. **HCHK 自交判定参数空间容差**（HcheckCommand.cs L285-286）：`ta/tb` 无量纲
   参数与 0.001 比较，长段上端点附近自交不报。
7. **HCHK 编号引用校验只覆盖多段线**：LINE 分支跳过 `CheckHcheckEntityNumberHandle`。
8. **HCHK 模型空间/当前空间混用**：引用扫描用 ModelSpace，标识写 CurrentSpace，
   布局空间运行不一致。
9. **HJOIN 选择列表未去重**：同一多段线重复选中可能产出退化环；擦除集合依赖
   重叠判定对称性，缺少断言保护。
10. **多个 `catch { }` / catch→true 静默吞错**：HPLIT L97-100 把一切异常计为
    "跳过"且无诊断；`HplitLayerIsLocked`/`HjoinLayerIsLocked`/`HgapLayerIsLocked`
    把读取失败当"已锁定"；`EraseCircleRecords`/`MarkCircleRecordsForGenerationFailure`
    失败静默不计数。
11. **HCLN 依赖 `ed.SelectAll`**：若视口外实体不参与全选，包含关系分析不完整
    （与审计"视口无关"原则不一致）。
12. **HBR 整命令级联失败**：循环内无逐项容错，单个实体打开失败即整命令中止。
13. **锁定标识图层时 HGAP/HCHK 清标识阶段整体失败**（`ClearHgapMarkers`/
    `ClearHcheckMarkers` 擦除抛 `eLockedLayer`）。
14. **HEX/HCN CSV 5 列表头与 `test_hexport.py` 4 列断言不一致**（验证脚本
    已过期，非插件缺陷，建议同步测试）。
15. **`HOF` 偏移测试偏移**：`GetOffsetValue` 先试偏移后再次正式偏移，同一
    `GetOffsetCurves` 计算执行两次（纯性能项，可后续合并）。
16. **HPLIT zigzag 结果无条件替换 Region 结果**（HplitCommand.cs L64）：只要
    zigzag 产出块数更多就覆盖 Region 布尔结果，缺少质量/面积校验，可能用
    过度切分的结果替换正确分割。
17. **HPLIT 擦边相交钳制**（`HplitSegmentIntersection` L1131-1138）：t/u 在
    [-1e-7, 1+1e-7] 内的越界交点被接受并钳制到端点，仅擦过顶点的路径可能
    被判为"贯穿"；`HplitPathMayOverlap` 异常时返回 true（fail-open），与
    LayerIsLocked 系列的 fail-closed 策略不一致。

## 五、结论

- 优化全部为行为不变类：同一真实 CAD 回归在优化前后结果一致（可对比项全部 PASS）。
- XData 缓冲释放、HNM/HCLN 采样缓存、HGAP 扫描线预筛选是收益最大的三项；
  其余为热路径微优化与死代码清理。
- 未修复项（第四节）均为真实功能缺陷或稳健性问题，建议按清单逐项评估后再改，
  每项修复都应配套真实 CAD 用例。
