# HexFill 命令优化 · 连续优化交接页 (HANDOFF)

## 2026-10-14 四项修复与验证禁用，2.2.0.5

- 后续交付新要求：DLL 必须带同版本说明书，包含本版修改命令、功能变化、具体修复问题。已补桌面 `HexFill_2.2.0.5_命令使用说明书_20261014.xlsx`，4 页签/19 命令，“本版改动”置于首位。生成入口 `tools/manual/build_manual.mjs`，版本变更资料 `release-notes.json`，完整用法 `command-manual.json`；`build_release.ps1` 先生成核验手册，再发布 DLL。保持 CAD 验证禁用。

- 用户确认 HTC 仍保留微软雅黑，要求位置和整体宽高一致。使用原文字真实局部可见范围拟合；MTEXT 采用已解析片段布局，空格片段无几何范围，须跳过。
- HAD/HIX 桌面选择窗口使用强制默认目录标志，与 HBR/HEX 最近导出目录联动。桌面窗口方法单独 NoInlining，避免 Core 路径触及桌面 GUI 类型。
- HAD 原有最小间距为 0.10/0.14mm（HDIA 直径 0.03/0.06mm）。已达最小值且间距不变时只改红色，原实体、数量和位置保持不变；生成后触及最小值也标红，重复执行不变成品红。
- HJOIN 截图样例按坐标重建：旧版三种变体均丢孔洞；修复共享边端点配对、同源开口窄缝、零长度残段及小闭环。矩阵 859 项、旧 8 分割+1混合聚合通过。
- 文字/标红专项既有结果 205 项、常规命令 90 项通过；最新纯离线 pytest 69 项、runner、构建 guard 通过。生产 SHA256 为 `8362516E78B3086CF23BA8B342F3F5E818C3137E7A70697EA633333428DDE43B`。
- **当前用户硬性要求：不再触发 CAD，包括隐藏 Core、COM、桌面实操。** 隐藏 Core 崩溃会拉起 Autodesk 错误报告。保留根目录 `CAD_RUNTIME_VALIDATION_DISABLED` 和 `AGENTS.md`；只编译、静态分析、纯算法/模拟验证，已有实测结果可读取。禁止为了验证自行移除禁用保护。
- CAD 已持久断网，33 程序/132 WFP 过滤器/66 防火墙规则；真实 Core 网络探测 IPv4/IPv6 直连及 127.0.0.1/::1 全部拒绝，其他应用本地监听正向控制通过。配置与说明位于 `tools/offline/`；未经新授权不要撤销断网。
- 本轮错误报告与测试进程已停止；不修改全系统错误报告设置，也不关闭用户自行打开的程序。
- 报告 `FIX_REPORT_2026-10-14.md`；最终只交付完整版 DLL，后台复制到桌面，勿再次启动 CAD 验收。

## 2026-10-14 复杂多边形验证与完整版 2.2.0.4

- 用户要求借鉴 Clipper2 的复杂多边形测试，增强分割、聚合与偏移验证，沿用直接修复授权。
- 新增 test-only Clipper2 固定提交 `f9c5eb6e14a59f6f5d65fbfb3564519a561cf4fd`，原始许可/头注释/源码哈希保留；不加入生产 csproj/rsp 或交付 DLL。
- 2.2.0.3 基线 657 项中复现 50 项断言失败：HOF 顺时针外扩错方向、窄颈内缩丢第二块、内缩消失错外扩；HPLIT 凹形多穿越漏块、大坐标误判；HJOIN 同侧共享边被误处理。
- 修复嵌套 Region 递归提取、开放碎片闭合判定；用局部坐标求面积/质心；HJOIN 共享边须有相反内部方向；HOF 返回/写入全部结果、按合计面积选正负偏移，成功统计放提交后。
- `verify/polygon_core_regression.py` 在真实 Core 完成 772 项；矩阵含 23 HJOIN、27 HOF、36 HPLIT 和事务/撤销/属性/范围检查。最终桌面 DLL 再跑 772 全通过。`verify/polygon_existing_cases.py` 原 8 HPLIT（含弧）+ 1 HJOIN 混合几何案例通过；常规 Core 90、pytest 66、runner、guard 通过。
- HPLIT 实际命令，HJOIN 实际规划/写入事务；HOF 实际几何及生产写入函数。HOF 模态 GUI、完整跨文档 UI 不在本轮验收范围。测试必须串行，不保存用户图纸或模板。
- 发布桌面 `HexFill_20261014_Full.dll`，2.2.0.4、AMD64、147456 字节；SHA256 `503FB032149748899A1A7F42773B09FE1F5436844AFC5473B532D67FECED8948`。
- 桌面最新说明书 `HexFill_2.2.0.4_命令使用说明书_20261014.xlsx`，19 命令、3 页签。报告 `GEOMETRY_REPORT_2026-10-14.md`，测试入口说明 `verify/POLYGON_REGRESSION.md`。

## 2026-10-13 完整版 2.2.0.3 优化交付

- 用户授权直接优化并要求最终解释。修复共享旧圆删除吞异常、区域事务/索引一致性；HGR 独立区域提交，HRS/HRD/HIX/HAD 统计仅计成功提交。HRS -100% 正确清除单圆。
- 网格增加有限数值与规模保护（每对象 1000 万候选点、100 万生成圆），两个候选缓冲区复用；HTC 按行筛选轮廓并逐文字失败隔离，增加宿主取消检查。固定微软雅黑/0.03/0.06 及既有边缘规则保留。
- HIX 支持原 Excel 编码读取器、拒绝非法数值/方向/坏行、首条有效重复记录、无匹配统计。HBAL 记录删除来源空间；三次有限重试后保留任务，图层解锁重新触发，Idle 结束恢复原命令 db/ed。关闭文档清理注册缓存。
- 统一 x64/v4.6.1 默认构建，增加 `build_release.ps1` 与确定性构建，发布前核验版本/架构/hash；脚本不再使用固定 20260816 文件名。
- 验证：pytest 66、runner、Core 常规 90、HCLN 23、新边界 55+无匹配 3、HBAL 10 通过；8 类合成几何 + 8 类真实文字逐点一致。144500 点样例计算内存分配 46819376→22024504 字节（降低 52.96%），耗时基本不变。GUI 验收边界与串行测试说明见优化报告。
- 发布目标：桌面 `HexFill_20261013_Full.dll`，2.2.0.3、AMD64、146432 字节；SHA256 `4192EEAB731C159486FB070AA7A5F266AFBF9FF8AAE15EB5F2918DCAD6CB1C84`。详见 `OPTIMIZATION_REPORT_2026-10-13.md`。

## 2026-10-13 成品审查（未修改生产代码或 DLL）

- 用户要求审查桌面最终成品的优化空间。DLL 2.2.0.2、AMD64、141824 字节；与 bin/Release 产物 SHA-256 一致。
- 现有 pytest 66 passed（76 条既有警告）、统一 runner、性能/HNM/XData/状态 guard 通过。直接加载桌面 DLL 的隔离 AutoCAD 2018 Core Console 90 项检查通过，日志 `obj/audit-20261013/core/`。
- 新增 `verify/audit_20261013.py` 复现：HGR 遇锁定旧圆仍生成新圆（旧圆保留、新增 30）；5000×5000 区域/0.1 间距发生 List capacity int 溢出；HIX 接受 Infinity 后生成失败、旧圆标红且仍计为“处理”。有效 UTF-16 BOM+Tab 间距表导入正常。
- 待优化：先修删除失败/事务/索引一致性，再做网格规模与有限数值保护；HTC 单事务批量失败、HBAL Idle 清空任务后吞异常仅确认为源码风险，未 GUI 实测。
- 详见 `AUDIT_REPORT_2026-10-13.md`。本轮没有发布新 DLL，也没有更改用户图纸或模板；新增脚本中部分 PASS 表示成功复现缺陷，不表示缺陷已修复。

## 2026-09-20 全命令检查与完整版 2.2.0.2

- 本轮由 Codex 直接完成。保留 HTC 微软雅黑、固定 0.03mm/0.06mm，HEX/HIX、HBR/HAD 导出目录持久化；只交付完整版 DLL。
- 修复 HOF：把数据库实际当前图层传入对话框；“当前图层”不再错误回退到 0；可编辑下拉框按输入文字读取目标图层；两条确认路径都拒绝 NaN/Infinity 等非有限距离。
- 修正 `acad_verify.py` 中 HTC 过期的 0.1mm 实测判据，增加实际半径与抽样最近邻间距校验。
- 新增 `verify/commands_core_smoke.py`：隔离 Core Console、临时图纸副本、90 项真实命令检查；测试结束恢复 HexFill 导出目录注册表值。复用已有可工作的隔离标识 `HexFillHclnSmoke`，不保存用户模板。
- 最新 DLL 验证：pytest 66 passed（76 条既有警告），统一 runner 通过；Core 命令检查 90 项、HCLN 23 项通过；真实 WinForms 控件逻辑检查 `verify/OffsetDialogChecks.cs` 16 项通过；性能/HNM/XData/状态 guard 及 diff 检查通过。
- COM 图形会话重试仍报 `0x80080005`，未进入命令逻辑。HOF 完整宿主界面、HBAL 跨文档 Idle 重编号仍未完成本轮实测；禁止把 Core/独立控件检查描述为完整 GUI 验收。
- 发布：`C:\Users\allen\Desktop\HexFill_20260920_Full.dll`，2.2.0.2、AMD64、141824 字节。详见 `RELEASE_CHECK_20260920.md`。

## 2026-09-17 HCLN 逐实体容错清除

- `HCLN` 写入阶段每个区域使用独立事务；编号文字和区域标记一起提交或回滚。锁定区域、锁定编号文字写入失败时保留原状态，其他区域继续清除。
- 已失效、已删除或格式错误的文字句柄允许清理；现存文字删除异常必须传出，不能被旧辅助方法吞掉。
- 完成消息报告提交成功数、跳过数及首个原因。部分失败时暂缓残留文字清理；清理阶段失败单独提示，保留区域成功结果。HCLN 修复陈旧句柄限定在所选范围，HNM 行为不变。
- 新增 `verify/hcln_core_smoke.py`：在独立 Core Console 进程、隔离配置及临时图纸副本中实际运行 DLL，23 项检查覆盖正常/锁定区域、锁定文字、失效句柄、撤销、重试、范围保护、清理异常和 FILEDIA。
- 原有 Python 套件为模拟/源码检查；新增实测使用真实 AutoCAD 数据库断言，替换本轮早期的字符串匹配测试。
- 最终验证：Roslyn x64 编译成功；Python 62 项通过（保留 76 条既有收集/返回值警告），统一 runner 全通过；Core Console 23 项通过；性能、HNM/XData、对话框状态 guard 和 diff 检查通过。
- 构建使用仓库 `packages/Microsoft.Net.Compilers/tools/csc.exe @build_hexfill.rsp`；系统自带老 csc 不支持项目已有语法。产物为 `bin/Release/HexFill.dll`。
- 限制：尚未覆盖损坏实体在扫描/包含关系分析阶段抛异常的场景，也未进行完整 AutoCAD 界面及大型生产图纸验证。

## 2026-08-29 FILEDIA 持续启用防回归

- 审查发现 HIX/HAD 在异常 `FILEDIA=0` 时为弹出文件窗口临时设 1，但 finally 硬编码写回 0 → 后续 NETLOAD/文件对话框又不弹，违背用户硬性要求。
- 已改为命令结束后保持 `FILEDIA=1`，新增 `verify/test_htc.py` 源码 guard 禁止重新写回 0。
- 验证：编译通过；`pytest` 62 passed；性能/HNM/XData/AutoCAD 状态 guard 全部 PASS。

## 2026-08-29 对话框不弹出修复 (环境 + 代码兜底) 【用户硬性要求: 以后不能再犯】

- **根因**：中断的 acad_verify 实机验证把 `FILEDIA=0` 写进了用户默认模板 `acad.dwt` → 新图纸 NETLOAD/文件对话框全不弹出。
- **环境修复**：`acad.dwt`/`acadiso.dwt` 的 FILEDIA/CMDDIA 恢复 1；per-user `acaddoc.lsp` 加启动 setvar 兜底（FILEDIA/CMDDIA/CMDECHO=1, OSMODE=4133）。
- **代码兜底**：HAD/HIX 文件选择改为「FILEDIA=0 时临时开启强制弹本地对话框」，删除 WinForms 分支与死代码 OwnerWindow。
- **教训（已入库）**：实机验证脚本中断可能污染 AutoCAD 模板/系统变量；任何 agent 跑 acad_verify 前/后须确认 FILEDIA=1，中断时主动恢复模板。
- 验证：编译通过；`pytest` 61 passed；runner 全 PASS。交付 `HexFill_HTC_DialogFix_20260829.dll`。

## 2026-08-29 HTC 圆范围修复 (em 缩放)

- 修复"生成圆范围与文字实际显示范围不符"：`scaleY` 改回 **em 缩放 `textHeight/1000`**（回归源 commit `319acb8` 误用 `textHeight/gdH`，圆放大 ~1000/gdH ≈1.3~1.5 倍）。
- guard：`verify/test_htc.py` 新增 em 缩放检查；`pytest` 61 passed。
- 交付：`HexFill_HTC_emScaleFix_20260829.dll` 已放桌面，待 AutoCAD 实测验收（圆范围应与原文字显示一致）。

## 2026-08-29 latest optimization (follow-up)

- 修复 `HBR` 编译错误 `CS0136`（方法级/块级同名局部变量 `skippedMessage` 冲突，内层改名 `skipNote`）。
- `HAD` 空亮度字段改判为"未填写"缺失（恢复"请补充亮度后重试"引导），不再误报"非法数值"；仅非空解析失败才计非法数值。Python 模拟器 `simulate_had_import` 同步更新并新增空亮度用例。
- `HAD` 非法方向提示补充"（按实测方向）"。
- 新增 `verify/test_csv_roundtrip.py`：忠实映射 HBR/HAD CSV 全链路（编码识别/分隔符/引号/行策略），覆盖 Excel 改写变体与真实样本。
- 真机回归说明：`acad_verify.py ALL` 部分运行确认 GRID/HEX→HIX/HBR→HAD/HOF/LOCKED/HPLIT 真机 PASS；HBAL 多文档 `<unknown>.Count` 失败定位为测试框架多文档 COM 陈旧引用问题（非插件回归，HBAL 重编号逻辑未改）。因实机套件价值有限，改用非 CAD CSV 往返验证。
- 验证：编译通过；`pytest` 58 passed；`python verify/runner.py` 全部 PASS；性能/HNM 撤销/XData/AutoCAD 状态 guard 通过。
- **两版合并评审完成**（2026-08-29）：Codex 版为本仓库权威；Reasonix 独有项多为用户 08-19 否决项或过时项，不反向合并；按"两版以最新信息为准"仅将 HTC 参数更正为 0.03/0.06（见下）。eBadDxf 修复确认 Reasonix 缺失，如 Reasonix 仍使用需同步。
- **HTC 参数更正**：用户确认要求为 0.03mm/0.06mm（08-16）；08-20 误记的 0.1/0.1 已改回并记录更正。HtcCommand.cs 直径/间距已改，字高校验（0.03mm）自洽。

## 2026-08-29 latest optimization

- HAD CSV 导入现在会统计并提示坏行、非法数值、非法方向和重复根编号；有效记录继续处理，重复编号按首条有效记录处理。
- 发布响应文件 `build_hexfill.rsp` 已固定 `/platform:x64`，与 AutoCAD AMD64 引用一致。
- 本轮验证：x64 MSBuild 成功；Python `pytest` 51 passed；`python verify/runner.py` 全部 PASS；性能、HNM 撤销/XData、AutoCAD 状态 guard 全部通过。
- 未完成：AutoCAD 真机回归仍需在目标安装环境验证。

## 2026-08-20 latest delivery

- HCLN export linkage: `HEX`, `HCN`, and `HBR` must ignore regions with `H_Clr=1`; HNM clears that state only after a successful new numbering run.
- HTC fixed parameters: dot diameter `0.1mm`, center spacing `0.1mm`. ⚠️ 本行 08-20 记录有误，用户确认应为 **0.03mm / 0.06mm**（2026-08-29 更正）。
- HTC X alignment: translate all converted outlines by the difference between the AutoCAD text-extents center and the generated-outline extents center. This corrects a slight right shift without changing scale, Y mapping, rotation, font, or dot generation.
- HTC full-position alignment: align both X and Y centers of the converted outline extents to the AutoCAD text `GeometricExtents`; keep the accepted outline proportions and fixed dot parameters unchanged.
- Command-line messages: remove debug/version/progress/intermediate-stage output; preserve input validation, warnings/errors, and concise single-line completion summaries without changing command behavior.

> 本文件是 Codex / Reasonix / Harness (DSH) 之间**接续优化的唯一权威锚点**。
> 规则: 每个 agent 在优化会话**结束时必须更新**本文件; **开始前必须读完**本文件 + 查统一知识库。
> **用户硬性要求优先级最高**: 与任何经验/习惯冲突时, 一律以用户要求为准。

## 版本说明 (重要)

- **Codex 版**: `C:\Users\allen\Documents\Hexfill 2` (git, 本仓库)
- **Reasonix 版**: `C:\Users\allen\AppData\Local\Programs\reasonix\hexfill` (git, 独立仓库)
- 两版同源于 2026-07-10 初始提交, 之后各自优化、尚未合并。
- 同一需求可能只在其中一版实现: 动手前先 `git log`/diff 对比两版差异, 或查知识库来源标签 (from:codex / from:project-reasonix)。

## 用户硬性要求 (最高优先级, 从统一知识库同步)

1. HTC 命令: 以后按照**微软雅黑**的字体转换轮廓 (2026-08-14 用户要求) —— 不得改回其他字体
2. HTC 命令: 圆直径固定 **0.03mm**、圆心间距固定 **0.06mm**, 不受任何设置影响 (2026-08-16 用户要求; 2026-08-29 合并评审中用户再次确认, 08-20 误记 0.1/0.1 已更正)
3. HEX/HIX 与 HBR/HAD 均须路径联动：导出到当前 CAD 文档目录，导入窗口打开对应命令最近一次导出目录，并跨 AutoCAD 重启保留 (2026-08-20 用户要求)
4. (新要求请各 agent 在此追加, 并以 kb_push 上报, 提炼器会自动归类)

## 当前状态快照

- 更新时间: 2026-08-19 (Harness 根据知识库 + 两仓库 git 状态生成)
- Codex 版最近成果: HNM 撤销后重编号 eBadDxfSequence 修复 (2026-08-19); HBR 亮度表跨 Excel 版本问题已定位 (待改造)
- Reasonix 版最近成果: HRS 命令卡顿优化 (根因: 框选父区域重复处理)
- git: 两仓库均只有初始提交, 当前工作区即最新状态 (本次已提交 checkpoint, 见 git log)

## 待办 (下一步优化方向)

1. [完成] HBR/HAD 协议改造: 跨 Excel 版本稳定的导出 + 容错导入 (2026-08-29 已完成 + CSV 往返验证)
2. [完成] 两版合并评审: 结论 Codex 为权威, Reasonix 独有项多为用户否决/过时项不反向合并 (2026-08-29); HTC 参数更正 0.03/0.06
3. [待定] HNM 修复两版同步: 已确认 Reasonix 缺 eBadDxfSequence 修复; 若 Reasonix 版仍在部署使用, 需同步该修复 (否则无需动作)

## 已知坑清单 (来自统一知识库经验卡, 开工前必读)

- HBR 亮度表被 Excel 改写 => HAD 加载失败 (编码/分隔符/格式) —— 知识库 #104
- HNM 撤销后重编号产生 eBadDxfSequence —— 知识库 #105
- HRS 卡顿根因: 框选到父区域导致重复处理 —— 知识库 #169
- MTEXT 控制代码被 GDI 当字面字符渲染 —— 知识库 (HTC 修复说明 0816)
- hexfill-validator MCP: 验证失败先调 diagnose, 命令变化可自修复 —— 知识库 #111

## 接续操作手册

### 任一 agent 开工 (Codex / Reasonix / Harness)
```
# 1. 查用户要求 + 经验 (分级检索, 要求优先)
curl "http://127.0.0.1:8777/lessons/match?q=hexfill"   # 或 MCP 工具 kb_lessons
# 2. 读本文件
# 3. 查两版工作区差异
git -C "C:\Users\allen\Documents\Hexfill 2" status --short
git -C "C:\Users\allen\AppData\Local\Programs\reasonix\hexfill" status --short
```

### 任一 agent 收工
1. kb_push 上报本轮结论 (根因/改动/验证) —— 提炼器自动归类 (教训/经验/用户要求)
2. 更新本文件: 用户硬性要求 / 状态快照 / 待办 / 已知坑
3. 生成当日报表 (OPTIMIZATION_REPORT_2026-08-XX.md / HRS_优化_修改说明.md 惯例)
4. 提交 git checkpoint (信息含日期与主题)
## 合并状态 (2026-08-19, 分支 v11-merge)

- 已产出: `D:\AutoCAD插件\HexFill_v12_merged_20260819.dll` (v2.2.0.0, 编译通过; v11 原件未动)
- 已合并: HNM撤销bug修复 / 雅黑统一转轮廓 / HTC固定0.03+0.06 / HBR-HAD Excel跨版本兼容 / MTEXT代码剥离 / HCLN视口外实体 / 全部A类bug修复
- 已回退(保持v11原样): C4间距按会话级HDIA (不再按实际圆径钳制) / C5 HAD均匀度按CSV全部根统计 / C3改回强制雅黑(TrueType也统一)
- 待真机验证: 在 AutoCAD 中 netload v12 DLL 后逐项验收 (HNM撤销重编号 / HTC雅黑网点 / HTC固定值 / MTEXT无代码符号 / HCLN视口外 / HGR-HRS-HIX-HAD间距与HAD均匀度与v11一致)
- 回退方法: git checkout 5a046aa (回到合并前) ; 分支: git branch v11-merge
