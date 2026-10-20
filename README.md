<div align="center">

<img src="assets/icon-blue.png" width="120" alt="PowerPlanTray"/>

# PowerPlanTray

**托盘电源计划快速切换工具** —— 右键打开快速菜单，图标颜色实时反映电脑状态

![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?logo=windows11&logoColor=white)
![Runtime](https://img.shields.io/badge/%E8%BF%90%E8%A1%8C%E5%BA%93-Windows%20%E8%87%AA%E5%B8%A6-5C2D91)
![Memory](https://img.shields.io/badge/%E5%86%85%E5%AD%98-%E6%97%A0%E5%BC%BA%E5%88%B6%E6%B8%85%E7%90%86%E5%BE%AA%E7%8E%AF-lightgrey)
![License](https://img.shields.io/badge/License-MIT-yellow)

🟢 **绿 · 电影 / 影音** &nbsp;&nbsp;|&nbsp;&nbsp; 🔵 **蓝 · 日常 & AI** &nbsp;&nbsp;|&nbsp;&nbsp; 🔴 **红 · 高性能 & 游戏**

<img src="assets/icon-green.png" width="72"/> <img src="assets/icon-blue.png" width="72"/> <img src="assets/icon-red.png" width="72"/>

</div>

---

## ✨ 特性

- **悬浮快速菜单**：右键托盘图标弹出卡片式菜单，顶部显示当前档位；按系统实际方案名逐行显示、一键切换，当前档显示蓝色对勾；再次右键或点击菜单外收起
- **内联自启动开关**：菜单内直接提供「开机自动启动」开关，无需进入二级页面；切换后回读注册表实际状态，失败会提示
- **双击直达**：双击托盘图标打开 Windows 电源选项
- **克制的视觉层级**：浅灰画布、白色圆角卡片、清晰字体、细描边与柔和阴影；绿/蓝/红分别表示轻度、均衡与性能档
- **颜色状态化**：图标随激活计划自动变色，不用点开就知道电脑在什么模式
- **智能排序**：菜单按"性能从弱到强"排列，关键词规则可自定义
- **双向同步**：系统里改名、切换、增删计划，几秒内自动跟上
- **轻量常驻**：单进程、无第三方 UI 框架、无后台服务；菜单按需打开，关闭后继续驻留托盘；不含专门的内存清理循环，占用由 .NET Framework / WinForms / Windows 内存管理共同决定
- **绿色便携**：单文件 exe，仅依赖 Windows 自带运行库，Win10 / Win11 开箱即用
- **贴细节**：原生圆角+投影（Win11）、资源管理器重启图标自动恢复、防重复启动、菜单度量与托盘图标都按真实 DPI 出图（16~20px 走简化画法，小尺寸不糊）、欢迎气泡仅首次运行弹一次

## 🚀 快速开始

到 [**Releases**](../../releases) 下载发布包，解压后双击**带版本号的 exe**（如 `PowerPlanTray_1.8.7.0.exe`）即可。配置文件固定为同目录的 `PowerPlanTray.ini`，不随 exe 版本号变化。

> 程序启动后常驻托盘。**右键**点击图标打开快速菜单，双击打开 Windows 电源选项。Win11 默认可能将图标收在任务栏右下角的 **^ 溢出区**。

自己编译：克隆仓库后双击 **`build.cmd`**（生成开发用的 `PowerPlanTray.exe`）。使用 Windows 自带的 .NET Framework 编译器，**不需要安装任何 SDK**。

打发布包：双击 **`package.cmd`**。它会先编译，再把发布文件打包到 `dist\PowerPlanTray_<版本>.zip`；ZIP 内含**带版本号的唯一 exe**，工程根目录不会留下重复副本。

## ⚡ 一键复刻作者同款三档电源计划（可选）

**双击 `setup_plans.bat`** 即可（或运行下面的命令）：

```powershell
powershell -ExecutionPolicy Bypass -File setup_plans.ps1
```

| 模式 | 处理器状态(AC) | USB/PCIe 省电 | 睡眠(AC) | 适用场景 |
| --- | :---: | :---: | :---: | --- |
| 🟢 电影模式 | 5% ~ 99%（限制睿频） | 开 | 从不 | 看片：风扇安静、播放不中断 |
| 🔵 AI模式 | 5% ~ 99%（限制睿频） | 关 | 从不 | AI 开发：训练/推理长任务不中断 |
| 🔴 游戏模式 | 5% ~ 100%（睿频全开） | 关 | 从不 | CS2 等竞技游戏：峰值性能 |

- **只配置交流电(AC)**：脚本只写 AC 参数，不主动覆盖直流电(DC)。新复制的计划 DC 沿用“平衡”母本，已有计划保留原 DC 值；每项写完回读校验并核对 DC 未被改动
- **三档参数是首轮评估基线，不是普适最优值**：来自特定笔记本环境，处理器代际、厂商 BIOS、电源管理模式、散热设计与 Windows 版本都会影响结果，需实机对比任务耗时、频率/温度趋势、噪声或帧率后再决定是否调整，切勿仅凭参数名称推断性能
- **核心停驻(Park)默认保留系统/“平衡”行为**：不再对三档强制设为 100；仅当高性能档实测确有收益时才考虑显式设值
- **新增 AC 显示器关闭超时**（默认 15 分钟，可在脚本顶部 `$DisplayOffMinAC` 调整）：与系统睡眠分开管理，无人操作时关屏，后台编译/长任务继续运行。**关闭显示器不等于系统睡眠**；合盖动作由 Windows/厂商设置另行控制，本工具不声称能保证合盖后持续运行
- 脚本**幂等**：已存在同名计划只刷新参数，绝不重复创建；不切换你当前正在使用的计划（仅当活动方案本身被更新时重新应用一次）
- **每项写完回读校验**：本机不存在的设置打印“本机不存在”，不当成成功也不中断脚本；任一失败/警告在最后统一汇总
- **无需管理员权限**（电源计划按用户存储）

## 🔧 配置文件

exe 同目录的 `PowerPlanTray.ini`（可选）：

```ini
弱档关键词=电影,办公,节能,省电,低热,低耗,低功耗
中档关键词=AI,平衡
强档关键词=游戏,卓越,高性能,极致
刷新间隔毫秒=3000
```

计划名称命中哪组关键词，就归入哪一档（决定菜单顺序与图标颜色）；删除 ini 即恢复默认。

运行 `PowerPlanTray.exe /export-icons` 可重新导出三档 256px 图标到 `assets/`（README 配图）。

## 🧹 关于内存占用

纯 WinForms + 系统电源 API（无第三方依赖），进程本身很轻。程序**不包含专门的内存清理循环**（不做定时 GC、`EmptyWorkingSet` 或 `SetProcessWorkingSetSize` 强制修剪）：实际占用由 .NET Framework、WinForms 和 Windows 内存管理共同决定，任务管理器里的工作集数值会随系统调度波动，属正常现象。这里不把单次工作集数值当作优化目标，关注点是长时间空闲下无持续增长、无明显唤醒开销。

## ❓ 常见问题

<details>
<summary><b>托盘里找不到图标？</b></summary>
Win11 默认把新图标收进任务栏右下角的 ^ 溢出区。想常驻显示：任务栏设置 → 其他系统托盘图标 → PowerPlanTray → 开。
</details>

<details>
<summary><b>切换了计划但感觉没效果？</b></summary>
工具只负责"激活"计划，计划参数本身需要配置过才有意义——运行 <code>setup_plans.ps1</code> 一键配置，或在控制面板手动调整。
</details>

<details>
<summary><b>笔记本玩游戏帧数不稳？</b></summary>
插电 + 厂商控制中心风扇拉满 + Windows 设置 → 屏幕 → 显示卡 里把游戏指定为"高性能 GPU"（双显卡机型默认可能走核显）。
</details>

## 📦 目录结构

```
PowerPlanTray/
├── PowerPlanTray.cs          全部源码（单文件）
├── build.cmd                 一键编译（开发用，输出 PowerPlanTray.exe）
├── package.cmd               编译并打发布包到 dist/
├── package.ps1               打包实现（读取版本号，ZIP 内 exe 带版本名）
├── make_icon.ps1             图标与素材生成
├── app.ico                   程序图标（多尺寸）
├── assets/                   README 用三色图标
├── PowerPlanTray.ini         可选：排序/颜色/刷新间隔
├── setup_plans.ps1           可选：一键创建三档电源计划
├── dist/                     发布包输出（含 PowerPlanTray_<版本>.zip，不提交）
├── LICENSE                   MIT
└── .gitignore
```

## 卸载

右键托盘图标 → 快速菜单「退出」；勾选过"开机自动启动"先关闭；删除文件夹即可。

## License

[MIT](LICENSE)
