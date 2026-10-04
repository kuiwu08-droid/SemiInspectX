# SemiInspectX — 半导体检测与计量模拟平台

一个面向设备软件、视觉计量和自动化测试岗位的作品集项目：**C#/.NET 10 + WPF + C++17/OpenCV 4.14 + SQLite**。

![工作站截图](docs/images/workstation.png)

完整流程：加载配方与晶圆 → 虚拟平台移动 → 虚拟相机采集 → C++ 对齐、缺陷检测和线宽计量 → 图像与结果保存 → 更新晶圆图 → 故障报警 → 清除故障、Reset、重新运行。

所有设备和图像均为模拟。微米单位使用模拟标定；评测结果只说明约定合成数据范围内的表现，不能当作真实晶圆精度或产线良率。

## 启动

```powershell
./scripts/bootstrap.ps1 -InstallCpp
./scripts/build.ps1
./scripts/run.ps1
```

首次下载较大：本地 .NET SDK、OpenCV、NuGet 包均在项目 D 盘目录的 `.tools` 中。C++ 编译组件沿用现有 Visual Studio 安装位置。

界面自动加载默认配方和 100 个有效 Die。直接按 Start 可以开始。Fault Injection 在运行前配置，Die index 为确定性触发位置；故障后选择 None，再按 Reset 和 Start。每次 Start 都创建新 RunId，历史记录保留。

非法配方在开始运行前被拒绝；不会把一次参数错误当作设备断开。Pause 等待已采集的帧全部保存后进入 Paused；Stop 保留已采集帧的结果，将运行记录为 Aborted；设备或存储故障记录为 Failed。

历史窗口支持 Lot、Wafer、Run 精确匹配查询和结果类型筛选，双击一条运行记录查看对应结果，再导出 CSV。配方 JSON 可调整 ROI、阈值、线宽容差、模拟像素比例、worker 数和队列容量；加载时进行校验。

## 验证与性能

```powershell
./scripts/build.ps1
./scripts/evaluate.ps1
./.tools/dotnet/dotnet.exe src/SemiInspectX.Runner/bin/Release/net10.0/SemiInspectX.Runner.dll benchmark --dataset datasets/generated/evaluation --output artifacts/benchmark --count 1000 --rounds 5
```

项目包括 4 个原生测试和托管回归测试，覆盖 100 Die 扫描、配方快照、跨语言输入校验、故障恢复、暂停排空、满队列停止、重复命令、串行/流水线结果一致以及 1,000 次启停循环。实际结果见 [验证说明](docs/verification.md) 和 `docs/reports`。

默认演示数据 seed=42；独立带噪评测 seed=314159；独立无噪计量评测 seed=271828。真值来自生成器绘制参数，检测器不读取标注。缺陷召回按对齐坐标中的 bounding-box IoU ≥0.5 匹配。

基准固定输入、OpenCV 内部线程数和模拟延迟，预热后运行五轮，每轮 1,000 帧；记录吞吐、P50/P95 延迟和进程内存。流水线性能与机器、定时器精度、存储开销有关，结果只使用真实测量。

本机默认设备延迟下：串行 31.82、流水线 31.92 dies/s，基本没有提速。关闭模拟延迟的 `recipes/throughput.json` 下：串行 557.58、流水线 912.94 dies/s，吞吐比 1.637×；同时流水线每轮 P95 延迟约 30–41 ms，高于串行的 1.7–2.3 ms。结果展示吞吐与排队延迟的权衡，不能写成“吞吐和延迟都提升”。完整逐轮数据见 `docs/reports/benchmark-throughput.json`。

## 交付与学习

```powershell
./scripts/publish.ps1
./scripts/run.ps1 -Snapshot
./scripts/run.ps1 -Demo
```

发布包位于 `artifacts/releases`，解压到可写目录后运行 `SemiInspectX.Desktop.exe`，无需安装 .NET SDK。演示模式只捕获本程序的 WPF 画面，按正常扫描、暂停恢复、相机故障、Reset、Stop、存储故障顺序展示。

- [架构与接口](docs/architecture.md)
- [验证与边界](docs/verification.md)
- [真实工程问题记录](docs/engineering-notes.md)
- [10–15 分钟讲解与修改练习](docs/interview-guide.md)
- [第三方依赖与许可证](docs/third-party.md)

GitHub Actions 配置已写入仓库；上传 GitHub 后才能确认云端执行状态。首版不包含真实设备、PLC/TCP、REST/Vue、深度学习或 AI Agent。
