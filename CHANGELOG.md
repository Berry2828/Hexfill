# HexFill 改动记录

## 2026-10-14 完整版 2.2.0.5

- HTC 保留微软雅黑和固定 0.03/0.06 参数，按真实未旋转文字片段的可见范围校准位置、宽高，再恢复旋转。MTEXT 使用 CAD 已解析的片段布局，跳过无墨迹空格片段；输出跟随原文字所属空间和标高。
- HAD/HIX 复用文件选择入口，桌面窗口使用 `ForceDefaultFolder | DefaultIsFolder`，分别采用最近 HBR/HEX 导出目录；桌面窗口方法与 Core 命令行方法隔离。
- HAD 触及 HDIA 最小间距的原网点和新网点一律标红；间距不变时仅改颜色，不删除或重建原圆；重复执行保持红色。原 HDIA 两档最小间距 0.10/0.14mm 保留。
- HJOIN 按实际共享边配对连接端点，处理参与聚合对象内的开口窄缝，保留孔洞和独立对象。三路窄缝连接后退化的残段不再当作开放边追踪；微小有效闭环保留。
- 现有后台日志：几何 859 项、文字/最小间距 205 项、常规命令 90 项、原 8 分割及 1 混合聚合案例通过。最终编译与 69 项离线测试通过。
- 用户指出隐藏 Core 崩溃仍触发错误报告后，停止所有 CAD 验证，加入运行时禁用标记及 Core/COM 入口保护；不再重启 CAD 或显示对话框验收。
- CAD 安装目录内 33 个程序配置持久断网：132 个 WFP 应用过滤器、66 条防火墙规则；既有网络实测验证 IPv4/IPv6 直连及本地代理连接返回 10013。

## 2026-10-14 完整版 2.2.0.4

- 借鉴 Clipper2 的轮廓方向、内缩消失、孔洞及面积/数量测试，加入固定源码版本的独立几何对照；测试源码校验 SHA256，不增加生产运行依赖。
- HPLIT 递归提取布尔结果中的嵌套 Region，保留 U 形/梳齿区域所有分割块；未闭合曲线碎片不再强行当作闭环。面积与质心使用局部坐标，修复大坐标下的数值消减。
- HJOIN 按轮廓内部方向识别共享边，避免对同侧重叠/重复边进行误删除；面积计算复用稳定算法。
- HOF 按全部轮廓总面积选择外扩/内缩方向，修复顺时针外扩与内缩完全消失；内缩断开时写入全部结果，提交后计成功，保留原对象。
- 新复杂回归 772 项通过；原 8 个 HPLIT 及 1 个多区域 HJOIN 几何案例、常规 Core 90 项、pytest 66 项、统一 runner 和 guard 通过。桌面 DLL 已直接加载验证。
- 发布 `HexFill_20261014_Full.dll`，同步更新 Excel 命令说明书为 2.2.0.4。详细边界与来源见 `GEOMETRY_REPORT_2026-10-14.md`。

## 2026-10-13 完整版 2.2.0.3

- 修复旧圆删除失败仍生成新圆；以区域为单位回滚、提交后更新空间索引，锁定旧圆不会叠加新网点。
- 九种偏移复用候选缓冲区；HTC 按行筛选轮廓，逐文字事务隔离失败；合成几何和实际文字逐点输出与旧版一致。
- 增加有限数值、1000 万候选点/100 万生成圆的单对象限制及宿主取消检查，避免容量溢出和巨量分配。
- HIX 拒绝非有限间距/坐标，统计坏行、重复和无匹配；HGR/HRS/HRD/HIX/HAD 完成统计只计成功提交，修复 HRS -100% 对单圆的清除。
- HBAL 保留失败任务，有限重试与解锁重试，按删除来源空间重编号并恢复原命令上下文；关闭文档清理缓存。
- 统一构建与发布入口、x64/v4.6.1、确定性编译和日期/版本/hash 核验。详见 `OPTIMIZATION_REPORT_2026-10-13.md`。

## 2026-09-20 完整版 2.2.0.2

- 修复 HOF “当前图层”错误指向 0 图层，以及手动输入目标图层名称被忽略的问题。
- HOF 两条确认路径均拒绝 NaN、Infinity、零和负数偏移距离。
- 保留 HCLN 逐区域容错、撤销恢复和清除范围保护。
- HTC 实测判据同步为固定直径 0.03mm、间距 0.06mm，补充真实圆径与间距校验。
- 增加隔离 AutoCAD Core Console 全命令补测和 HOF 控件逻辑检查；测试使用临时图纸，恢复导出目录偏好。
- 通过 66 项 Python 测试、113 项 Core Console 检查、16 项 HOF 控件逻辑检查。GUI COM 初始化故障下的剩余验收边界见发布检查报告。

## 2026-09-17 HCLN 清除失败隔离

- HCLN 逐区域提交清除操作；锁定区域或编号文字失败时保留该区域原状态，并继续清除其他区域。
- 不再吞掉现存编号文字删除错误；完成提示报告成功数、跳过数和失败原因。
- 部分失败时暂缓残留文字清理；清理阶段错误不会掩盖已经提交的成功结果，陈旧句柄修复限定在所选范围。
- 增加 AutoCAD Core Console 实测脚本 `verify/hcln_core_smoke.py`，23 项断言覆盖锁定、失效句柄、撤销、重试及范围保护。

## 2026-08-29 FILEDIA 持续启用防回归

- 修复 HIX/HAD：当环境异常为 `FILEDIA=0` 时，命令为弹出选文件窗口而临时设为 1，原代码在 finally 又写回 0，导致下一条 NETLOAD/文件命令仍不弹窗口；现改为命令结束后保持 `FILEDIA=1`。
- 新增源码 guard：`verify/test_htc.py` 同时检查 HIX/HAD 不得把 FILEDIA 写回 0。
- 验证：编译通过；`pytest` 62 passed；性能/HNM/XData/AutoCAD 状态 guard 全部通过。

## 2026-08-29 对话框不弹出修复（环境 + 代码兜底）

- **根因**：中断的自动化实机验证把 `FILEDIA=0` 写进了用户默认模板 `acad.dwt`，导致新图纸 NETLOAD/文件对话框全部不弹出。
- **环境修复**：把 `acad.dwt`/`acadiso.dwt` 的 FILEDIA/CMDDIA 恢复为 1（含 CMDECHO/OSMODE/ORTHOMODE/SNAPMODE）；并在 per-user `acaddoc.lsp` 增加启动 setvar 兜底，保证每次开图变量正常（以后不再犯）。
- **代码兜底**：HAD/HIX 文件选择改为「FILEDIA=0 时临时开启并强制弹本地对话框，结束后恢复」；删除未在真机验证的 WinForms 分支与死代码 `OwnerWindow`。
- 验证：编译通过；`pytest` 61 passed；统一验证 runner 全部通过。
- 交付：`HexFill_HTC_DialogFix_20260829.dll` 已放桌面。

## 2026-08-29 HTC 圆范围修复（em 缩放）

- 修复 HTC 生成圆范围与文字实际显示范围不符：`scaleY` 从 `textHeight/gdH`（墨迹高=字高，圆放大 ~1000/gdH ≈1.3~1.5 倍）改回 **em 缩放 `textHeight/1000`**（GDI emSize=1000 对应字高，墨迹高=gdH×字高/1000，与 AutoCAD TrueType 实际渲染一致）。
- 回归源：commit `319acb8`（"方案B-高度=字高"）曾把正确的 em 缩放误改为字高/墨迹高。
- DBText 宽度仍与高度等比（em，保比例）；MTEXT 宽度仍用 ActualWidth 精确对齐。
- 新增 guard：`verify/test_htc.py` 锁住 em 缩放公式（防回归）。
- 验证：编译通过；`pytest` 61 passed；统一验证 runner 全部通过。
- 交付：`HexFill_HTC_emScaleFix_20260829.dll` 已放桌面。

## 2026-08-29 合并评审结论 + HTC 参数更正（用户确认）

- **两版合并评审**（Codex 版 vs Reasonix 版）结论：Codex 版为本仓库权威实现，Reasonix 独有项多为用户 08-19 否决项（HTC 自身字体/1:1 映射、按圆径钳制间距、详细分步消息）或过时项，**不反向合并**；按"两版以最新信息为准"仅更正 HTC 参数。
- **HTC 圆直径/间距改回 0.03mm / 0.06mm**（用户 2026-08-29 再次确认 08-16 要求）。修正 08-20 误记的 0.1/0.1；HTC 内部字高校验（直径 0.03mm）随之自洽。
- 验证：编译通过；`pytest` 58 passed；统一验证 runner 全部通过。

## 2026-08-29 HAD/HBR CSV 往返验证（非 CAD）

- 新增 `verify/test_csv_roundtrip.py`：忠实映射 `ReadCsvLinesWithExcelEncoding` / `ParseCSVLine` / `TryParseDoubleAnyCulture` / HAD 行策略（坏行/非法数值/空亮度缺失/重复/非法方向），覆盖 Excel 改写变体（UTF-16/UTF-32、分号/Tab 分隔、逗号小数）与仓库真实样本（`tests/hbr_export_Brightness_*.csv` 空亮度、`Brightness_had_smoke.csv` 有效行）。
- 验证结果：`pytest` 58 passed（新增 6 项 CSV 往返）；统一验证 runner 全部通过；性能/HNM/XData/AutoCAD 状态 guard 通过。
- 真机回归说明：本机 `acad_verify.py ALL` 部分运行确认 GRID/HEX→HIX/HBR→HAD/HOF/LOCKED/HPLIT 在真机 PASS；HBAL 多文档用例失败 `<unknown>.Count` 定位为测试框架多文档 COM 陈旧引用问题，非插件回归；因价值有限，未继续跑完整实机套件。

## 2026-08-29 HAD 空亮度引导恢复与编译修复

- 修复 `HBR` 编译错误 `CS0136`：方法级与 if 块内同名局部变量 `skippedMessage` 作用域冲突，内层改名 `skipNote`。
- `HAD` 空亮度字段改判为"未填写"（按缺失处理并给出"请补充亮度后重试"引导），不再误报为"非法数值"；仅非空但解析失败的字段计入非法数值。恢复 HBR 导出后未填亮度即运行 HAD 的旧有引导体验。
- `HAD` 非法方向提示补充说明"（按实测方向）"，与回退行为一致。
- 验证：编译通过；`pytest` 52 passed（含新增空亮度缺失判定用例）；统一验证 runner 全部通过。

## 2026-08-29 HBR/HAD 导入健壮性与 x64 发布

- `HAD` 对坏行、非法亮度数值、非法排列方向和重复根编号进行统计提示；有效记录继续处理，重复编号按 CSV 首条有效记录稳定处理。
- 保留 Excel UTF-8/UTF-16/ANSI、逗号/分号/Tab 分隔和逗号小数兼容。
- `build_hexfill.rsp` 默认使用 x64，避免 AutoCAD AMD64 引用的架构不匹配警告；仍只生成完整版 `HexFill.dll`。
- 验证：x64 MSBuild 成功；Python `pytest` 51 passed；统一验证 runner 全部通过；性能与 AutoCAD 状态 guard 通过。

## 2026-08-20 HCLN export linkage and HTC dot parameters

- `HEX`, `HCN`, and `HBR` now share the active-leaf filter. Regions marked `H_Clr=1` by `HCLN` are excluded from all exports until `HNM` assigns a new number.
- `HTC` uses fixed dot diameter `0.1mm` and center spacing `0.1mm`. ⚠️ 本行 08-20 记录有误，用户确认应为 **0.03mm / 0.06mm**（2026-08-29 已更正）。
- Build and structural verification passed before delivery.

## 2026-08-20 HEX/HIX 导出路径联动

- `HEX` 与 `HBR` 均继续将 CSV 导出到当前 CAD 文档所在目录；未保存的新图纸因没有文档目录，回退桌面。
- 当前 CAD 路径优先从活动文档 `Document.Name` 获取完整路径，再回退 `Database.Filename`，修复部分 `SaveAs`/COM 场景下 `Database.Filename` 为空而错误导出到桌面的问题。
- `HEX` 导出后记录导出文件夹，随后运行 `HIX` 时文件窗口自动打开该文件夹。
- `HEX/HIX` 使用独立的 `HKCU\Software\HexFill\LastHexExportDirectory` 记忆，不会与 `HBR/HAD` 的亮度表目录串用。
- AutoCAD 重启后仍能恢复最近一次 `HEX` 导出目录；目录失效时优先回退当前 CAD 文档目录。

## 2026-08-20 HBR/HAD 导出路径联动恢复

- 恢复 `2026-08-07 V11` 的操作体验：`HBR` 导出后记录导出文件夹，随后运行 `HAD` 时文件窗口直接打开该文件夹。
- 导出目录同时保存到 `HKCU\Software\HexFill\LastHbrExportDirectory`，AutoCAD 重启后仍可恢复；目录失效时回退桌面。
- 保留现有 Excel 跨版本编码、分隔符和小数格式兼容逻辑。

## 2026-08-19 HNM 撤销后重编号修复

- 修复 `HNM` 建立编号后执行 `Ctrl+Z`，再次对同一区域编号时可能出现 `eBadDxfSequence`。
- XData 注册应用名不再信任无法随 `Undo/PURGE` 回滚的进程缓存，每次写入前均以当前数据库 `RegAppTable` 为准。
- 删除实体最后一组 XData 时不再写入非法的空 `ResultBuffer`，并忽略不属于合法 `1001` 应用段的孤立 XData 值。
- DLL 文件版本更新为 `2.1.0.1`。

## 2026-08-16 HTC：MTEXT 格式代码剥离 + 固定圆直径/间距

- **MTEXT 格式代码剥离**：MTEXT 内容中的控制代码（{\f字体|b0|i0|c134|p2; \H \C \W \Q \A
  \T \L/\l \O/\o \K/\k \S堆叠 \U+XXXX 等）此前被 GDI 当字面字符渲染进网点，导致
  "生成的文字前面出现代码和符号"。新增 `StripMTextControlCodes` 解析器，只保留原始
  文字中真正可见的字符（\P/\n 转换行、\~ 转空格、\{ \} \\ 为字面量、\S 保留可见
  字符、分组括号不渲染）；验证：带 {\fSimSun|b0|i0|c134|p2;AB} 的 MTEXT 生成网点数
  与纯 "AB" 完全一致（2964），代码零残留。
- **固定圆直径/间距**：HTC 不再交互询问、不受 HDIA 等任何设置影响，固定
  **圆直径 0.03mm（半径 0.015mm）、圆心间距 0.06mm**。
- **验证**：verify/acad_verify.py HTC 套件扩为 7 用例（新增 mtext_codes），
  半径全部 0.015、代码剥离后网点数与纯文字一致，全部 PASS。

## 2026-08-16 HTC 文字转网点 1:1 精确映射修复（含 MTEXT）

- **大小 1:1**：修复网点被放大/缩小的问题。根因：AutoCAD 的 GeometricExtents 是"满 em 盒"
  （高=字高，含空白），原实现把 GDI 字形墨迹按包围盒映射填满整个显示范围，导致
  网点尺寸随字形与 em 盒比例偏差 ~1.3 倍（"AB" 放大、"中" 缩小）。
  现改为 **scale = 字高/1000**（em 盒=字高 → 墨迹真实尺寸），与可见字形严格 1:1。
- **位置 1:1（TEXT）**：锚点改为文字**插入点 Position**（默认对齐即基线起点），不再用
  GeometricExtents 左下角（它会随字形下伸变化，如"中"下伸 ~1.8mm，导致整体下移）。
  旋转绕插入点进行，任意旋转角精确；Center/Right/Bottom/Mid/Top 等对齐模式按
  AlignmentPoint + 字体度量（cellAscent/cellDescent/前进宽度）计算锚点偏移。
- **位置 1:1（MTEXT）**：实测 MTEXT 显示范围即文字框（单行盒高=字高、盒顶=墨迹顶、
  行距 1.6667×字高×行距因子），锚点 = 首行基线起点（X=盒左缘，Y=盒顶 − 墨迹顶距基线×scale），
  多行由 GDI 逐行堆叠自动就位。不再把墨迹拉伸填满整个文字框。
- **不改字体**：TrueType 文字（宋体/黑体/雅黑/Arial 等）不再强制改为微软雅黑，
  直接渲染文字**自身真实字体**（GDI PrivateFontCollection 加载样式字体文件），
  网点与原文字字形/位置一一对应；仅 SHX/大字体（GDI 无法渲染的描边字体）保留
  转雅黑兜底（新建独立样式，不动共享样式）。
- **SHX 判定修复**：AutoCAD 的 SHX 样式文件名无扩展名（如 "txt"），旧判定
  `EndsWith(".shx")` 漏判导致 SHX 文字不切换、网点错位；改为按扩展名有无 +
  TypeFace + 大字体三重判定，并发现 GDI 存在名为 "Txt"/"RomanS" 的字体
  （FontFamily 模糊匹配陷阱），不能依赖"字体解析失败"反推 SHX。
- **验证**：verify/acad_verify.py HTC 套件扩为 6 用例（latin/chinese/zero_hole/rotated/
  mtext/mtext_multiline），网点与 GDI 墨迹预测误差 ≤0.1mm（MTEXT 多行行距与
  AutoCAD 1.6667×字高精确一致），全部 PASS。

## 2026-08-16 验证体系补全：HCHK 测试修复 + HTC 文字转网点真机验证

- **HCHK 验证脚本修复**：`verify/acad_verify.py` 的 `hchk_problem_script` 原先用交互式
  `_.TEXT` 命令创建孤立编号文字，经 COM SendCommand 发送时卡在文字输入提示、文字未创建，
  导致 HCHK 检测不到问题（0 标记）而误报失败。改为与其他测试一致的 `entmakex` 确定性创建
  （显式指定图层 H_NUM、插入点、字高、内容），HCHK 检测本身无缺陷——现在 problem_markers
  用例正确检出 4 类问题（未闭合缺口/聚合线自交/重叠线段/孤立编号文字，共 8 个标记实体）。
- **新增 HTC 文字转网点真机验证**：`acad_verify.py` 增加 HTC 套件（命令 `HTC`，并入 ALL），
  用 `entmakex` 创建 TEXT 后运行 HTC，校验网点圆数量（≥20）、半径（=直径/2 精确值）、
  全部圆心落在文字 GeometricExtents 范围内、原文字保留。覆盖拉丁文字（"AB"）与中文（"中"）
  两个用例；此前 HTC 仅有临时 Python 逻辑模拟（`tests/_htc_logic_test.py`，不在仓库中），
  从未有集成真机验证——这是"文字转网点检测功能不完善"的补全。

## 2026-08-16 交付策略变更：只交付一份完整版 DLL

- **自即日起不再构建/交付 Lite 精简版**（`HexFill_Lite.dll`），只产出一份完整版 `HexFill.dll`，全部命令都在同一份 DLL 中。
- `HexFill.Lite.csproj`、`verify/verify_lite_commands.ps1`、`HexFill_精简版命令清单.md` 均标注【已废弃】保留作历史留存，不再维护。
- `acad_verify.py` 的 LITE 回归套件标注废弃（仅保留用于验证历史 Lite DLL）。
- 以后新增命令只进完整版，无需再考虑 Lite 白名单；命令清单只维护完整版一份。

## 2026-08-16 全命令优化 + 已知缺陷修复 + 结构拆分

- **HGAP 兜底闭合异常修复**：`TryHgapCleanClosePolyline` 先 `Closed = true` 再 `SetBulgeAt`，
  修复开放多段线对 N-1 段索引调用越界导致整命令失败的缺陷（8/13 风险清单 #1）。
- **HPLIT 量纲修复**：`HplitPointOnSegment` 沿线段方向改用投影比例判定，
  不再把长度²量纲的 dot 与长度量纲容差直接比较（#2）。
- **CSV 双文化解析**：HAD/HIX 数值解析先 InvariantCulture 再当前区域文化，
  修复小数逗号区域跨区 CSV 解析失败（#3）；HEX/HCN 导出统一 InvariantCulture 格式。
- **HAD 均匀度口径**：CSV 有、图纸无的根不再计入均匀度统计（#4）。
- **间距下限按实际圆径**：HGR/HRS/HIX/HAD 新增 `GetMinSpacingForRadius`，
  按区域内实际圆直径钳制（0.06mm 直径→0.14mm、0.03mm→0.1mm），
  不再依赖会话级 HDIA 设置（#5）。
- **HCHK 自交判定**：端点容差由无量纲参数改为按段长换算的绝对距离（#6）；
  LINE 分支补做编号引用校验（#7）。
- **HJOIN 选择去重**：同一聚合线重复选中只处理一次，避免退化环（#9）。
- **HCLN 视口无关**：`ed.SelectAll` 改为直接遍历模型空间数据库，
  视口外实体也参与包含关系分析（#11）。
- **HOF 偏移去重**：方向测试结果在正向时直接复用，`GetOffsetCurves` 由两次降为一次（#15）。
- **HPLIT 保守判定**：`HplitPathMayOverlap` 异常时 fail-closed；
  `HplitSegmentIntersection` 端点容差改为长度相关（#17 部分）。
- **结构拆分**：球标命令（HBAL + 删除自动重编号）拆到 `Commands/HbalCommand.cs`，
  HOF 对话框拆到 `Ui/OffsetDialog.cs`，`HexFill.cs` 由 4170 行降至 3685 行；行为不变。
- **测试同步**：`test_hexport.py` 更新为当前 5 列 CSV 格式（#14）。
- 未改动项（需在可构建环境评估）：HCHK 模型/当前空间混用（#8）、静默吞错（#10）、
  HBR 级联失败（#12）、锁定标识图层清标识失败（#13）、HPLIT zigzag 质量校验（#16）。

## 2026-07-19 全命令一致性与数据安全审计
# 2026-08-19 HTC 文字轮廓贴合优化

- 修正倾斜 `DBText`/`MTEXT` 的斜切轴：每行围绕自身基线变换，避免网点整体横移和多行逐行错位。
- 网点由“圆心在字形内”升级为“整个圆在字形内”，外轮廓和字孔边缘不再出现半径级外溢。
- 新增 AutoCAD 2018 斜体文字专项验证用例。
# 2026-08-19 HBR/HAD Excel 跨版本兼容

- `HBR` 导出的亮度表保持 UTF-8 BOM 与标准 CSV 引号格式，避免中文表头在旧版 Excel 中乱码。
- `HAD` 新增 Excel 兼容读取：自动识别 UTF-8、UTF-16、UTF-32、ANSI 编码，兼容逗号、分号和制表符分列。
- 数值解析支持小数点/逗号、千分位混合格式，避免把 `1,25` 错读为 `125`。
- 新增 AutoCAD 2018 回归样本：以 UTF-16、分号分列、逗号小数的 Excel 保存格式完成 `HBR → HAD` 导入。
# 2026-08-20 HTC horizontal-outline alignment

- Corrected the slight rightward offset of HTC-generated text outlines by aligning their overall X-axis center to the selected AutoCAD text entity's geometric extents.
- The correction is translation-only; font selection, glyph scale, Y mapping, rotation, and the fixed `0.1mm` diameter / `0.1mm` center spacing remain unchanged.
# 2026-08-20 HTC position and command-line message cleanup

- Corrected HTC's remaining overall position offset by aligning generated outline centers to the selected text's actual display extents on both X and Y axes, without changing proportions or dot parameters.
- Removed diagnostic, version, progress, and redundant intermediate messages; retained required prompts, warnings, errors, and concise command result summaries.
