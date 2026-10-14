# HexFill 2.2.0.2 发布检查

日期：2026-09-20。交付完整版，基于 HCLN 修复提交 `491f532`，追加本轮 HOF 修复与回归检查。

## 发布文件

- 桌面文件：`C:\Users\allen\Desktop\HexFill_20260920_Full.dll`
- 构建产物：`bin/Release/HexFill.dll`
- 版本：2.2.0.2；PE 架构：AMD64（0x8664）；大小：141824 字节。
- 构建时间：2026-09-20 19:37:11（Asia/Shanghai）。
- SHA-256：`AD319DEFE8B6C37A67B283B71BA91352FA6FEF784FD601E7F03BDE6F9BB26C1E`
- 构建命令：`packages\Microsoft.Net.Compilers\tools\csc.exe @build_hexfill.rsp`。
- 实测宿主：AutoCAD 2018 Core Console。未验证其他 AutoCAD 或中望 CAD 版本，尤其不把 .NET Framework DLL 视为新 .NET 宿主的兼容性证明。

## 修复

HOF 现在使用数据库实际当前图层；手动输入的目标图层名称会保留，并按原命令规则创建或使用该图层。偏移距离必须是有限正数。HCLN 已有的逐区域事务、锁定对象保护、失效句柄清理和撤销恢复全部保留。

HTC 仍使用微软雅黑、0.03mm 圆直径和 0.06mm 圆心间距。此次纠正的是旧验证脚本的 0.1mm 判据，没有改变用户确认的网点规格。

## 19 个命令检查范围

| 命令 | 本轮检查与证据 |
| --- | --- |
| HDIA | 切换源码及模拟回归；每个 HTC 实测前切换 HDIA，确认 HTC 固定参数不受影响 |
| HNM | 5 类真实场景：撤销重编号、切割后重编号、清除残留、单根跳过；15 项数据断言 |
| HGR | 导出链路真实生成 120 个圆；微小区域生成失败保留旧圆 |
| HRS | 真实生成失败保护；密度调整算法模拟回归 |
| HRD | 真实生成失败保护；方向调整相关源码及模拟检查 |
| HEX | 实际导出间距 CSV，检查数据行和 2mm 初始间距 |
| HCN | 实际导出数量 CSV，检查记录和非零圆数量 |
| HIX | 新 Core 进程加载 DLL 后导入修改后的间距表，实际间距为 1.4mm |
| HBR | 实际导出根区域亮度表，校验记录，持久保存导出目录 |
| HAD | 导入 UTF-16、分号分列、逗号小数的亮度表；实际间距 2.8mm，圆数量 247→68 |
| HOF | 修复当前/输入图层和非有限数值；真实 WinForms 控件逻辑 16 项；几何算法模拟回归；宿主完整界面未验收 |
| HCLN | 23 项真实检查：逐实体失败隔离、锁定文字/区域、坏句柄、撤销、重试、范围保护 |
| HBAL | 真实创建 3 个球标块；跨文档删除后的自动重编号未完成本轮实测 |
| HGAP | 7 类真实场景，检查对象数量、闭合状态、缺口标记；沿用之前更细的顶点实测记录 |
| HCHK | 正常、弧线、异常图形 3 类实测，检查原对象数量和标记 |
| HJOIN | 真实合并两个可写区域，锁定区域保持原始数据；此前混合曲线批量实测记录保留 |
| HPLIT | 编号链路和锁定区域实测；此前 8 类折线/凹形/多区域实测记录保留 |
| HSET | 实测修改切割间距、缺口阈值、聚合距离后的结果，并恢复默认值 |
| HTC | 8 类真实文字：英文、中文、空心 0、旋转、斜体、MTEXT、多行、格式码；检查原文字保留、所有圆径、抽样最近邻间距、0 字孔洞 |

## 验证记录

- `python -B -m pytest verify -q --disable-warnings`：66 passed，76 条既有收集/返回值警告。部分旧测试是模拟/源码检查，不能代替宿主实测。
- `python -B verify/runner.py`：通过。该 runner 还会核验旧测试返回的 TestResult，避免仅依赖 pytest 对返回值的忽略。
- `python -B verify/commands_core_smoke.py`：90 项通过，日志 `obj/commands-core-smoke/`。
- `python -B verify/hcln_core_smoke.py`：23 项通过，日志 `obj/hcln-smoke/`。
- HOF 控件逻辑：编译 `Ui/OffsetDialog.cs` 与 `verify/OffsetDialogChecks.cs` 后运行，16 项通过。不显示窗口，也不模拟鼠标操作。
- `verify/verify_performance_guards.ps1`：性能、HNM/XData、AutoCAD 状态保护均通过。
- `git diff --check`：通过。

Core 测试使用临时图纸副本、隔离配置，不保存用户图纸或模板；结束后恢复导出路径偏好。导出目录在进程退出后仍存在于注册表的检查通过，但文件选择窗口默认目录的完整 GUI 交互仍未验收。

AutoCAD 图形界面 COM 再次启动失败（0x80080005），尚未执行到 HOF/HBAL 界面流程。因此不宣称所有命令均完成端到端 GUI 验收。未覆盖大型生产图纸、异常损坏实体扫描阶段和所有撤销/取消组合。

## 加载

关闭已加载旧 HexFill 的 AutoCAD 实例，重新打开 AutoCAD，使用 `NETLOAD` 加载桌面完整版 DLL。同一会话不要同时加载不同版本的 HexFill。
