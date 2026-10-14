# HexFill 2.2.0.5 修复与交付

## 本轮改动

| 项目 | 修复后的行为 |
|---|---|
| HTC | 保留微软雅黑、0.03mm 圆直径和 0.06mm 圆心间距。按原文字未旋转平面的真实可见宽高、位置拟合，再恢复旋转；MTEXT 自动换行、对齐和内嵌格式采用 CAD 已解析的片段布局。空格片段跳过，原文字不改动。 |
| HBR → HAD、HEX → HIX | 导出目录仍在会话和注册表保存；桌面文件窗口强制采用对应最近导出目录，不再被 CAD 窗口历史覆盖。桌面方法与命令行方法隔离。 |
| HAD | 达到 HDIA 最小间距时原网点、新网点均标红。原本就是最小间距且本次不变时仅写颜色，保留原实体和位置；重复执行保持红色。两档既有下限仍为 0.10/0.14mm。 |
| HJOIN | 依据实际共享边连接端点，清除参与聚合对象内通向孔洞的开口窄缝，保留孔洞和独立对象。处理三路窄缝中的退化残段，避免拼接误入孔洞或放弃整组结果。 |

网点的边缘仍受固定六边形网格离散度及整圆须留在笔画内的规则约束；文字轮廓的可见范围已对齐，网点不会强行移到笔画外。

## 已有验证证据

- 截图坐标重建的旧版基线：3 个方向/坐标变体共 9 项失败，均涉及孔洞轮廓数量和面积。
- 最新复杂几何矩阵 **859 项通过**：截图、选择顺序反转、反向顶点、大坐标、小孔及既有分割/偏移/聚合案例，使用测试专用 Clipper2 作独立几何参考。
- 原 **8 个 HPLIT + 1 个混合 HJOIN** 案例通过，含曲线、弧形和三路分割缝恢复。
- 文字/标红专项既有结果 **205 项通过**：75 种 TEXT 组合、9 种 MTEXT 布局（168 个范围/原文不变断言），HDIA 两档最小间距、新生成标红、不变时保留实体、重复标红及实际间距检查。
- 常规命令 **90 项通过**：8 类文字实际网点半径/间距、CSV 导出及重启后导入、锁定范围保护等。
- 用户禁止 CAD 验证后，最终只执行 **69 项纯离线 pytest、统一 runner、构建 guard、x64 编译和文件哈希核验**，均通过。

目录窗口的强制标志使用 Autodesk 提供的 [OpenFileDialogFlags](https://help.autodesk.com/cloudhelp/2022/ENU/OARX-ManagedRefGuide/files/OARX-ManagedRefGuide-Autodesk_AutoCAD_Windows_OpenFileDialog_OpenFileDialogFlags.html)。按用户要求，最终没有打开该窗口进行桌面验收。已有后台证据不代表完整 GUI 或其他 CAD 版本验收。

## 错误报告弹窗与后续验证限制

隐藏的 CAD Core 进程崩溃也会启动 Autodesk 错误报告。此前认为隐藏进程不会打扰桌面，这个判断不成立。测试及错误报告进程已停止；根目录禁用标记、AGENTS.md 和测试入口检查阻止继续启动 CAD/Core/COM。

**未经用户明确重新授权，不得启动任何 CAD 验证，不得移除禁用保护。** 不修改全系统的错误报告设置，不影响用户自行启动 CAD。后续仅做编译、静态审查、纯算法和模拟测试。

## CAD 持久断网

安装目录内 **33 个程序**已配置 **132 个 WFP 应用过滤器及 66 条防火墙阻止规则**，包含 IPv4/IPv6 和本地代理连接。

既有真实 Core 探测：外部 IPv4/IPv6、127.0.0.1 和 ::1 均返回 `10013 / AccessDenied`，代理监听器收到 CAD 连接数为 0；其他应用连接同一监听器的正向控制通过。规则持久保存；未修改 Clash 配置或其他应用的防火墙规则。WFP 按应用身份过滤的机制见 [Microsoft ALE 文档](https://learn.microsoft.com/en-us/windows/win32/fwp/application-layer-enforcement--ale-)。

配置记录 `tools/offline/result.json`，程序清单 `tools/offline/programs.json`，网络证据 `obj/fixes-20261014/offline/network-results.json`。这些网络探测现在也受 CAD 禁用标记保护，不再执行。

## 交付文件

- `C:\Users\allen\Desktop\HexFill_20261014_Full.dll`
- 版本 **2.2.0.5**，AMD64 / .NET Framework 4.6.1。
- SHA256：`8362516E78B3086CF23BA8B342F3F5E818C3137E7A70697EA633333428DDE43B`。
- 构建源码 `Commands/HtcCommand.cs`、`Commands/HadCommand.cs`、`Commands/HjoinCommand.cs`、`HexFill.cs`；本轮前的 DLL 保存在 `obj/fixes-20261014/HexFill.baseline.dll`。

已后台编译并复制交付文件，不自动打开 CAD 或加载插件。用户自行方便时重启 CAD，通过 NETLOAD 选择新 DLL。
