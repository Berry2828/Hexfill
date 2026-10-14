# 复杂多边形回归

当前存在 `CAD_RUNTIME_VALIDATION_DISABLED`：以下 CAD/Core 命令当前禁止运行，仅保留方法说明。须用户明确重新授权后才可安排宿主验证。

使用真实 AutoCAD 2018 Core Console、临时图纸副本和隔离配置。脚本不保存图纸，
不连接桌面 AutoCAD，不改变用户模板。所有 Core 测试必须串行执行。

```powershell
.\build_release.ps1 -NoPublish
python -B verify\polygon_core_regression.py
python -B verify\polygon_existing_cases.py
```

新几何矩阵：23 个 HJOIN 案例、27 个 HOF 案例、36 个 HPLIT 案例，以及事务、
属性、无关对象保护和撤销检查。每个结果检查有限/简单轮廓、轮廓数量、区域面积
及与独立 Clipper2 参考结果的对称差面积。方向、孔洞与大坐标另有变体。

- HPLIT：实际调用命令，覆盖矩形、凹 L、U 形、梳齿、多次穿越折线、顶点、
  相切、沿边、内部短路径、重复点和大坐标，通常面积/对称差容差为 0.005mm²。
- HJOIN：调用生产规划与写入方法，使用实际数据库事务，覆盖共享边、T 形、
  缺口、重复点、混合方向、单孔、双孔、点接触、重叠同侧边和锁定回滚。
  精确共享边对称差容差为 0.002mm²；0.1mm 缺口按已有聚合规则允许新增连接面积。
- HOF：调用生产几何方法，对直线多边形使用 Clipper2 Miter（高斜接限制）参考，
  对称差容差为 0.004mm²；覆盖双向偏移、方向反转、窄颈断开和内缩消失。
  另调用生产写入方法检查全部轮廓、目标图层与原对象保留。模态界面不在此脚本范围。
- `polygon_existing_cases.py` 复用 `acad_verify.py` 的原 8 个 HPLIT 和 1 个 HJOIN
  混合案例，核对真实面积、顶点位置、闭合性、数量和图层，包括弧形边界。

固定的测试参考源码及来源说明见 `third_party/clipper2/README.md`。
日志和实际几何快照在 `obj/polygon-regression/`。`--dll` 可指定旧版审查；
`--label` 为日志命名。基线失败表示旧版缺陷被复现，不能当作新版通过结果。
