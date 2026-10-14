# HexFill - AutoCAD 2018 插件

> **交付策略（2026-08-16 起）**：只交付一份完整版 `HexFill.dll`，不再构建/维护 Lite 精简版。
> 所有命令（HDIA/HNM/HCLN/HGR/HRS/HRD/HEX/HCN/HIX/HBR/HAD/HOF/HBAL/HSET/HCHK/HPLIT/HJOIN/HGAP/HTC）都在同一份 DLL 中。

当前完整版为 **2.2.0.5（2026-10-14）**。修复 HTC 位置/宽高对齐、HBR/HAD 和 HEX/HIX 导入窗口目录、HAD 最小间距标红，以及 HJOIN 孔洞和窄缝拼接，见 [本轮修复报告](FIX_REPORT_2026-10-14.md)。此前复杂多边形修复见 [几何优化报告](GEOMETRY_REPORT_2026-10-14.md)。

**验证限制**：用户已禁止启动 CAD、CAD Core Console、COM 会话和桌面自动操作。保留 `CAD_RUNTIME_VALIDATION_DISABLED`；仅运行编译、静态检查和纯离线测试。详见 `AGENTS.md`。后台隐藏进程崩溃也会弹错误报告，因此不能用隐藏进程规避此限制。

**DLL 配套说明书**：每次生成/交付 DLL，必须附同版本的 Excel 说明书，放在同一目录。“本版改动”页明确列出修改命令、功能变化和修复问题，其余页更新当前命令用法。维护 `tools/manual/release-notes.json` 和 `command-manual.json`；构建脚本先生成并核验说明书，再发布 DLL。

## 项目概述

这是一个 AutoCAD 2018 的 .NET 插件，用于在闭合区域内按固定间距生成六边形网格分布的圆。所有命令以 **H** 开头，形成统一的 HexFill 命令族。

| 命令 | 含义 | 功能描述 |
|------|------|----------|
| **HGR** | Hex Generate Raster | 在闭合区域内按指定间距生成六边形网格圆 |
| **HRS** | Hex Respace | 按间距百分比调整区域内圆的密度 |
| **HEX** | Hex Export | 将区域编号和圆间距导出为 CSV |
| **HIX** | Hex Import | 从 CSV 读取间距数据并重新生成圆 |
| **HNM** | Hex Number | 对嵌套区域进行层级编号 |
| **HOF** | Hex Offset | 支持批量偏移并指定目标图层 |

## 技术规格

- **目标框架**: .NET Framework 4.6.1
- **目标平台**: AutoCAD 2018
- **开发语言**: C# 6.0
- **核心算法**: 严格六边形网格填充 (HexGrid Strict)

## 编译说明

推荐运行 `build_to_desktop.bat`，或执行 `powershell -NoProfile -ExecutionPolicy Bypass -File build_release.ps1`。构建统一使用 x64、.NET Framework 4.6.1 引用；先运行 guard、编译暂存文件并核验版本/架构/hash，再输出当天的 `HexFill_yyyyMMdd_Full.dll`。交付前可加 `-NoPublish` 先完成构建与实机验证。

复杂几何回归运行 `python -B verify/polygon_core_regression.py` 和 `python -B verify/polygon_existing_cases.py`，详见 [测试说明](verify/POLYGON_REGRESSION.md)。所有 AutoCAD Core 脚本串行运行；Clipper2 仅作为测试参考，不加入产品 DLL。

### 方法一：使用 Visual Studio

1. 打开 `HexFill.csproj`
2. 添加 AutoCAD 2018 的 DLL 引用：
   - `accoremgd.dll`
   - `acdbmgd.dll`
   - `acmgd.dll`
   
   这些 DLL 通常位于：
   ```
   C:\Program Files\Autodesk\AutoCAD 2018\
   ```

3. 设置引用属性：`Copy Local = False`
4. 编译项目（Release 模式）

### 方法二：使用命令行

1. 修改 `HexFill.csproj`，取消注释 AutoCAD 引用部分，并修改 DLL 路径为您的 AutoCAD 安装路径

2. 运行编译命令：
   ```
   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe HexFill.csproj /p:Configuration=Release
   ```

## 加载插件到 AutoCAD

### 方法一：手动加载

1. 打开 AutoCAD 2018
2. 输入命令 `NETLOAD`
3. 选择编译生成的 `HexFill.dll`
4. 插件加载后即可使用所有命令

### 方法二：自动加载

1. 将 `HexFill.dll` 复制到固定位置
2. 运行以下注册表脚本（保存为 `.reg` 文件）：

```reg
Windows Registry Editor Version 5.00

[HKEY_CURRENT_USER\Software\Autodesk\AutoCAD\R22.0\ACAD-1001:804\Applications\HexFill]
"DESCRIPTION"="HexFill - Hexagonal Grid Fill Plugin"
"LOADCTRLS"=dword:00000002
"LOADER"="C:\\Path\\To\\Your\\HexFill.dll"
"MANAGED"=dword:00000001
```

注意：`R22.0` 和 `ACAD-1001:804` 需要根据您的 AutoCAD 版本调整。

## 命令详细说明

### HGR - 按间距生成圆

1. 输入命令 `HGR`
2. 选择一个或多个闭合区域（多段线、圆、椭圆）
3. 输入圆间距（mm），默认最小间距为 `max(圆直径, 0.1mm)`
4. 程序会在每个区域内生成严格六边形网格分布的圆

### HRS - 按间距百分比调整

1. 输入命令 `HRS`
2. 选择一个或多个区域
3. 输入调整百分比（可正可负）
4. 程序会按百分比调整区域内圆的间距

### HEX - 导出间距数据

1. 输入命令 `HEX`
2. 选择要导出的区域
3. 指定 CSV 文件保存位置
4. 数据将导出为：编号,间距,X坐标,Y坐标

### HIX - 导入间距数据

1. 输入命令 `HIX`
2. 选择 CSV 文件
3. 选择目标区域
4. 程序会自动匹配并按 CSV 中的间距重新生成圆

### HNM - 自动编号

1. 输入命令 `HNM`
2. 输入起始编号（默认为1）
3. 选择所有要编号的闭合区域（包括外框和内框）
4. 程序会自动分析包含关系并进行层级编号

### HOF - 偏移

1. 输入命令 `HOF`
2. 在对话框中设置：
   - 偏移方向（外扩/内缩）
   - 偏移距离
   - 目标图层
3. 框选要偏移的对象

## 注意事项

1. 本插件仅在 AutoCAD 2018 上测试通过
2. 如需在其他版本使用，需要修改引用的 AutoCAD DLL 版本
3. 编译时确保 AutoCAD DLL 的 `Copy Local` 设置为 `False`

## 文件列表

```
HexFill/
├── HexFill.csproj          # 完整版项目文件
├── HexFill.Lite.csproj     # 【已废弃】Lite 精简版项目文件（2026-08-16 起不再交付）
├── HexFill.sln             # 解决方案文件
├── HexFill.cs              # 主源代码（核心命令与共享辅助方法，partial class）
├── Commands/               # 分命令 partial 文件（HBR/HAD/HGAP/HCHK/HJOIN/HPLIT/HSET/HTC/HBAL）
├── Ui/
│   └── OffsetDialog.cs     # HOF 偏移设置对话框
├── Properties/
│   └── AssemblyInfo.cs     # 程序集信息
├── verify/                 # 纯 Python 验证套件与构建期 guard 脚本
├── tests/                  # 真实 AutoCAD 回归用例（scr/dwg）
├── OPTIMIZATION_NOTES.md   # 优化记录
└── README.md               # 本说明文件
```

## 作者信息

- 创建日期：2026年3月13日
- 版本：2.2.0.3（2026-10-13 稳定性与网格内存优化）
