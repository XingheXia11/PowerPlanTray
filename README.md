<div align="center">

<img src="assets/icon-blue.png" width="120" alt="PowerPlanTray"/>

# PowerPlanTray

**托盘电源计划快速切换工具** —— 左键一键切换，图标颜色实时反映电脑状态

![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6?logo=windows11&logoColor=white)
![Runtime](https://img.shields.io/badge/%E8%BF%90%E8%A1%8C%E5%BA%93-Windows%20%E8%87%AA%E5%B8%A6-5C2D91)
![RAM](https://img.shields.io/badge/%E5%B8%B8%E5%AD%98%E5%86%85%E5%AD%98-%3E%200.3%20MB-brightgreen)
![License](https://img.shields.io/badge/License-MIT-yellow)

🟢 **绿 · 电影 / 影音** &nbsp;&nbsp;|&nbsp;&nbsp; 🔵 **蓝 · 日常 & AI** &nbsp;&nbsp;|&nbsp;&nbsp; 🔴 **红 · 高性能 & 游戏**

<img src="assets/icon-green.png" width="72"/> <img src="assets/icon-blue.png" width="72"/> <img src="assets/icon-red.png" width="72"/>

</div>

---

## ✨ 特性

- **一键切换**：常驻托盘，左键弹出菜单，点哪个切哪个，当前计划带 ✓
- **颜色状态化**：图标随激活计划自动变色，不用点开就知道电脑在什么模式
- **智能排序**：菜单按"性能从弱到强"排列，关键词规则可自定义
- **双向同步**：控制面板里改名、切换、增删计划，几秒内自动跟上
- **近乎零占用**：单进程常驻内存 **< 1 MB**，无窗口、无后台服务、无开机广告
- **绿色便携**：单文件 exe，仅依赖 Windows 自带运行库，Win10 / Win11 开箱即用
- **贴细节**：资源管理器重启图标自动恢复、防重复启动、高 DPI 适配

## 🚀 快速开始

到 [**Releases**](../../releases) 下载 `PowerPlanTray.zip`，解压后双击 `PowerPlanTray.exe` 即可。

> 没有窗口就是正常状态——去任务栏右下角找电池图标（Win11 默认收在 **^ 溢出区**）。

自己编译：克隆仓库后双击 **`build.cmd`**。使用 Windows 自带的 .NET Framework 编译器，**不需要安装任何 SDK**。

## ⚡ 一键复刻作者同款三档电源计划（可选）

**双击 `setup_plans.bat`** 即可（或运行下面的命令）：

```powershell
powershell -ExecutionPolicy Bypass -File setup_plans.ps1
```

| 模式 | 处理器状态 | USB/PCIe 省电 | 睡眠 | 适用场景 |
| --- | :---: | :---: | :---: | --- |
| 🟢 电影模式 | 5% ~ 99%（关睿频） | 开 | 从不 | 看片：风扇安静、播放不中断 |
| 🔵 AI模式 | 20% ~ 99%（关睿频） | 关 | 从不 | AI 开发：训练/推理长任务不中断 |
| 🔴 游戏模式 | 5% ~ 100%（睿频全开） | 关 | 从不 | CS2 等竞技游戏：峰值性能、防热节流掉帧 |

- 脚本**幂等**：已存在同名计划只刷新参数，绝不重复创建
- **无需管理员权限**，不切换你当前正在使用的计划
- 在笔记本实测调优：游戏模式刻意不锁最小频率 100%，交给 Speed Shift 动态调频——锁满频只会让机身发热、GPU 被动降频

## 🔧 配置文件

exe 同目录的 `PowerPlanTray.ini`（可选）：

```ini
弱档关键词=电影,办公,节能,省电,低热,低耗,低功耗
中档关键词=AI,平衡
强档关键词=游戏,卓越,高性能,极致
刷新间隔毫秒=3000
```

计划名称命中哪组关键词，就归入哪一档（决定菜单顺序与图标颜色）；删除 ini 即恢复默认。

## 🧹 为什么内存这么小

纯 WinForms + 系统电源 API（无第三方依赖），启动后周期性把冷内存页归还系统（`SetProcessWorkingSetSize`），任务管理器里的常驻占用稳定在 **0.3 ~ 1 MB**。

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
├── build.cmd                 一键编译
├── make_icon.ps1             图标与素材生成
├── app.ico                   程序图标（多尺寸）
├── assets/                   README 用三色图标
├── PowerPlanTray.ini         可选：排序/颜色/刷新间隔
├── setup_plans.ps1           可选：一键创建三档电源计划
├── LICENSE                   MIT
└── .gitignore
```

## 卸载

托盘菜单 → 退出；勾选过"开机自动启动"先取消勾选；删除文件夹即可。

## License

[MIT](LICENSE)
