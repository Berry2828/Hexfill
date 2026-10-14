# 仓库文件说明

此目录保存最新版完整版源码与现行维护工具，面向直接使用的文件集中在 Releases。

| 保留内容 | 原因 |
|---|---|
| `HexFill.cs`、`Commands/`、`Ui/`、`Properties/` | 现行 19 命令完整源代码 |
| csproj、sln、构建入口和 response 文件 | 构建当前完整版所需 |
| README、COMMAND_MANUAL、CHANGELOG | 当前功能、操作与版本变化 |
| `tools/manual/`、`docs/manuals/` | 同版本说明书源资料与成品 |
| `verify/` | 现行回归测试、静态保护和 CAD 禁用保护 |
| `tests/load_quit.dwg` | 保留测试脚本共同引用的唯一 DWG 夹具 |
| `tests/Brightness_had_smoke.csv`、`tests/hbr_export_Brightness_20260710_230924.csv` | 现行 CSV 往返测试使用的两个输入样本；文件日期早但仍有用途 |
| `verify/third_party/clipper2/` | 几何测试参考、许可和哈希完整性资料，不是产品依赖 |
| `AGENTS.md`、`CAD_RUNTIME_VALIDATION_DISABLED` | 本项目验证与交付要求 |

本轮移除 **129 个旧文件**：停用 Lite 工程与 Lite 校验、历史审查/修复说明、
过期命令清单、交接笔记、一次性源码修补脚本、旧版对比脚本、旧 SCR/图纸/CSV，
以及仅对开发机生效的 CAD 断网管理工具。原项目目录保持原样，未删除本地源文件。

清理采用普通 Git 提交，旧内容仍可在历史提交中找回；当前分支和最新版下载包不再携带它们。
