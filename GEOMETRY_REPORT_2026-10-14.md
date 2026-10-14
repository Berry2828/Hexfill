# HexFill 复杂多边形验证与修复说明

交付版本：**2.2.0.4 / AutoCAD 2018 / AMD64**。用户要求借鉴 Clipper2 的复杂多边形测试，增强 HPLIT、HJOIN 和 HOF 验证；本轮已经补齐独立对照、复现缺陷、修复代码并验证桌面新 DLL。

## 用户能直接感受到的变化

| 命令 | 旧版的问题 | 新版行为 |
|---|---|---|
| HPLIT 分割 | 水平切穿 U 形两臂，应得到 3 块却只得到 2 块；梳齿应得到 4 块却只得到 2 块，部分分割带被错误保留 | 完整提取布尔结果中的全部独立区域，U 形得到 3 块、测试梳齿得到 4 块 |
| HPLIT 大坐标 | 图形移到约 ±1 亿坐标后，面积/质心误差使部分结果错误 | 在局部原点下计算面积与质心，同一图形平移后的结果通过独立几何对照 |
| HJOIN 聚合 | 重叠或内含轮廓在同侧也有重合边，旧算法可能误把这些边当作相邻区域内部边并替换原对象 | 只有共享边两侧分别属于不同区域时才建立聚合连接；重复轮廓、同侧重叠不据此聚合 |
| HOF 偏移方向 | 部分顺时针轮廓选择外扩却得到内缩；顺时针轮廓内缩到完全消失时可能反而外扩 | 不依赖顶点绕行顺序，按所有结果的总面积判断外扩/内缩方向；完全内缩消失按失败处理 |
| HOF 窄颈内缩 | 内缩后断成两块，只保留 AutoCAD 返回的第一块，另一块被释放 | 保留并在目标图层写入全部偏移轮廓，原边界仍保留 |

HOF 完成数量仍表示成功处理的原对象数。例如一条窄颈边界偏移成两条轮廓，计为成功处理 1 个对象、图中新增 2 条轮廓。锁定/写入失败仍由事务回滚，不计为成功。

## 为什么会出错，以及具体修复

- **分割漏块**：AutoCAD Region 布尔运算返回的结果可能在 `Explode` 后继续包含 Region。旧代码只读取 Curve，忽略这些子区域，随后退回简化分割。现在递归提取子 Region；拼接碎片必须真正闭合后才能生成轮廓。
- **坐标精度**：原面积、质心计算直接相乘很大的绝对坐标，再相减，产生数值消减。现在先减去同一局部原点，再计算；HJOIN 也复用该面积算法。
- **误聚合**：原判据只有平行、距离和重合长度。现在记录轮廓内部在边的哪一侧，并要求两条共享边的内部方向相反，兼容顺逆时针混合。
- **偏移方向/丢轮廓**：原代码决定需要负方向后，可能再次使用错误的正方向；同时只返回偏移集合第一个实体。现在返回全部实体，比较合计面积选择正负方向，统一设置属性并一起提交；成功数在提交后增加。

不改动 19 个命令名称、HTC 固定参数、CSV 协议和既有网点生成规则。

## 借鉴来源和对照方法

Clipper2 固定于提交 `f9c5eb6e14a59f6f5d65fbfb3564519a561cf4fd`，保留原 BSL-1.0 许可、源文件头及 SHA256 清单。它只编译为测试参考 DLL，**新版 HexFill.dll 没有 Clipper2 运行依赖**。

- [TestOffsets.cpp](https://github.com/AngusJohnson/Clipper2/blob/f9c5eb6e14a59f6f5d65fbfb3564519a561cf4fd/CPP/Tests/TestOffsets.cpp)：借鉴 TestOffsets9 的方向反转测试和 TestOffsets11 的内缩消失测试，并补入窄颈断开案例。
- [TestPolytreeHoles.cpp](https://github.com/AngusJohnson/Clipper2/blob/f9c5eb6e14a59f6f5d65fbfb3564519a561cf4fd/CPP/Tests/TestPolytreeHoles.cpp)：将 TestPolytreeHoles10（#973）的五矩形双孔案例按 0.001 缩放，放入真实 AutoCAD 数据库验证 HJOIN。
- [TestPolygons.cs](https://github.com/AngusJohnson/Clipper2/blob/f9c5eb6e14a59f6f5d65fbfb3564519a561cf4fd/CSharp/Tests/Tests1/Tests/TestPolygons.cs)：借鉴面积与轮廓数量判据，并增加实际输出与参考结果的 XOR 对称差面积，避免“数量一样但切错位置”。

测试参考结果由 Clipper2 独立执行 Difference、Union、InflatePaths 得到；生产分割/聚合不改成调用 Clipper2。直线偏移对照使用高斜接限制的 Miter；它与 CAD 的圆弧连接不能笼统视为等价。

## 已完成的验证

| 验证 | 结果 | 说明 |
|---|---|---|
| 上版 2.2.0.3 基线 | 657 项检查中 50 项失败 | 用于证明旧缺陷确实存在，失败明细保存在 baseline 日志 |
| 新复杂多边形矩阵 | **772 项通过** | 23 个 HJOIN、27 个 HOF、36 个 HPLIT 案例，另含事务回滚、锁定、属性、无关对象保护与撤销检查 |
| 直接加载桌面交付 DLL | **772 项通过** | `desktop-results.json`，同一固定参考源码 |
| 原分割/聚合几何案例 | **8 个 HPLIT + 1 个 HJOIN 混合案例通过** | 包含弧形凹边界、多区域一次切割、局部路径不延长；核对面积、顶点、闭合、数量和图层 |
| 常规 Core 命令回归 | **90 项通过** | 编号、网点、文字、数据导出/导入、锁定保护等 |
| Python 回归/统一 runner | **66 passed；runner PASS** | 76 条既有 pytest 警告保留；这套模拟测试不能代替上面的真实 CAD 几何验证 |
| 构建 guard / diff | **通过** | x64、版本与 hash 核验，生产 DLL 不引用 Clipper2 |
| Excel 说明书 | **19 命令、3 页签** | 更新版本、DLL 加载路径和 HPLIT/HJOIN/HOF 行为，渲染检查几何说明行 |

复杂矩阵覆盖凹 L、U 形、梳齿、多段折线多次穿越、顶点相交、相切、沿边、内部短路径、重复点、共享部分边、窄颈、单孔/双孔、点接触、方向反转，以及移到 ±1 亿坐标后的相同形状。

精确共享边对称差容差为 0.002mm²，分割 0.005mm²，直线轮廓偏移 0.004mm²。小缺口聚合沿用原规则，单独允许连接带新增面积。

**验收范围**：HPLIT 实际执行命令；HJOIN 调用生产规划/写入方法和真实事务；HOF 调用生产几何与写入方法。HOF 模态界面、完整宿主 GUI、其他 CAD 版本及任意损坏/自交输入没有在本轮完成全面验收。原曲线分割/聚合仍按既有采样精度近似，本轮不宣称与解析圆弧完全一致。

所有 Core 脚本串行执行，打开临时图纸副本且不保存，不连接用户桌面图纸；常规导出测试结束恢复导出目录偏好。

## 文件与复跑

- 桌面插件：`C:\Users\allen\Desktop\HexFill_20261014_Full.dll`
- 最新说明书：`C:\Users\allen\Desktop\HexFill_2.2.0.4_命令使用说明书_20261014.xlsx`
- 版本 2.2.0.4；AMD64；147456 字节。
- SHA256：`503FB032149748899A1A7F42773B09FE1F5436844AFC5473B532D67FECED8948`
- 新回归入口：`verify/polygon_core_regression.py`；原几何案例：`verify/polygon_existing_cases.py`。
- 来源/运行说明：`verify/POLYGON_REGRESSION.md` 和 `verify/third_party/clipper2/README.md`。
- 几何日志：`obj/polygon-regression/`；常规命令日志：`obj/commands-core-smoke/`。

加载时关闭已加载旧 DLL 的 CAD 实例，重新启动 AutoCAD 后通过 `NETLOAD` 选择上述新文件。旧版 DLL 保留便于回退。
