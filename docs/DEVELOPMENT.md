# 开发与验证

普通使用者从 Releases 下载 DLL 即可，无需执行本页命令。

## 当前构建

- `HexFill.sln` / `HexFill.csproj`：完整版，Release / x64，.NET Framework 4.6.1。
- 需要本机合法安装的 AutoCAD 2018 托管引用 `acdbmgd.dll`、`acmgd.dll`、`accoremgd.dll`；按实际位置设置 csproj 的 `AutoCADDir`。
- `build_hexfill.rsp` 和 `build_release.ps1` 是现行交付入口，依赖本机 Roslyn 编译器 `packages/Microsoft.Net.Compilers/tools/csc.exe`、.NET 4.6.1 Reference Assemblies 和本机 AutoCAD 路径；在其他机器上需配置引用路径。
- `build_release.ps1` 在交付前生成并核验同版本 Excel 说明书。现有生成器依赖 Codex bundled Node / `@oai/artifact-tool`；这些运行时不随仓库分发。若无此文档运行时，可使用 Visual Studio 构建源码，但对外交付 DLL 时仍须附匹配的说明书。
- 不再维护 Lite 项目。产品不依赖 `verify/third_party/clipper2/`，该目录仅用于测试参考。

## 允许直接运行的检查

安装 Python 与 pytest 后，在仓库根目录执行：

```powershell
python -B -m pytest verify -q -p no:cacheprovider
python -B verify/runner.py
powershell -NoProfile -ExecutionPolicy Bypass -File verify/verify_performance_guards.ps1 -ProjectDirectory .
```

以上入口是模拟/算法/源码检查，不代表 CAD 宿主实测。旧测试存在 pytest 返回值/收集警告，应以退出码和失败统计判断结果。

## CAD 验证保护

根目录 `CAD_RUNTIME_VALIDATION_DISABLED` 与 `AGENTS.md` 保留。当前禁止启动 AutoCAD、Core Console、COM 或桌面自动操作；不要移除标记或尝试用隐藏进程绕过限制。

`commands_core_smoke.py`、`hcln_core_smoke.py`、`polygon_core_regression.py`、`polygon_existing_cases.py`、`revision_core_regression.py`、`optimization_core_smoke.py` 及其 C# 辅助文件保留作维护参考，但当前不运行。几何测试内容见 `verify/POLYGON_REGRESSION.md`。唯一保留的 DWG 夹具为 `tests/load_quit.dwg`，供这些脚本使用，不能保存到用户图纸。

另保留 `tests/Brightness_had_smoke.csv` 和 `tests/hbr_export_Brightness_20260710_230924.csv` 两个 CSV 输入样本，它们由现行 `test_csv_roundtrip.py` 引用，不能因文件日期早而删除。

## 第三方参考完整性

Clipper2 固定提交、许可和原文件哈希位于 `verify/third_party/clipper2/`。其原始测试参考文件由 manifest 完整性校验引用，保留这些文件以免破坏当前几何验证工具；它们不进入产品 DLL 和使用包。
