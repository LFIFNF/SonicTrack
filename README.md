# SonicTrack

SonicTrack is a Windows desktop music visualizer built with WPF on .NET 8. It captures whatever the system is playing (WASAPI loopback) and renders it in real time as a 3D spectrum waterfall or as a GPU-drawn waveform, both of which can float on the desktop as transparent overlay windows.

SonicTrack 是一个 Windows 桌面音乐可视化程序（WPF / .NET 8）。它通过 WASAPI loopback 采集 Windows 当前默认播放设备的声音，不需要麦克风或虚拟声卡，提供「3D 频谱瀑布」和「桌面声波」两个可视化组件，都可以作为悬浮窗显示在桌面上。

## 功能

- **组件库主界面**：浅色主界面，以卡片列出组件，支持搜索。点击卡片在独立窗口中打开组件，两个组件可以同时运行。
- **3D 频谱瀑布（V6.1）**：实时频谱柱体整体向左平移，高度和颜色保持记录值；可调柱高压缩、增益、对比度、历史长度和内部黑柱不透明度；支持视角调节（水平、俯仰、距离、左右、上下），在预览中按住左键可平移画面，「重置视角」恢复上次保存的视角。
- **桌面声波（V10）**：OpenGL 绘制的实时声波，目标更新频率可选 60 / 120 / 144 / 240 Hz；可选主声波颜色、底层声波、保护底板（自动选择对比色）和抗锯齿；信号来源可切换为本机播放音频或内置演示波形。
- **桌面悬浮窗**：两个组件都有透明桌面悬浮窗，可拖动位置、右键关闭，支持置顶和鼠标穿透。
- **托盘常驻**：关闭主界面时，如果还有桌面悬浮窗开着，程序不退出，主界面隐藏到通知区域。
- **参数保存**：参数和窗口状态保存在程序目录，下次启动自动恢复。

## 系统要求

- Windows 10 / 11
- 构建：[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)；只运行已构建的程序：.NET 8 Desktop Runtime
- 支持 OpenGL 硬件加速的显卡和驱动（两个组件的画面都通过 OpenGL 绘制）
- 一个可用的音频播放设备（采集的是当前默认播放设备的声音）

## 构建与运行

```powershell
git clone https://github.com/LFIFNF/SonicTrack.git
cd SonicTrack
dotnet build -c Release
dotnet run -c Release
```

也可以用 Visual Studio 2022 打开 `SonicTrack.App.csproj` 直接运行。

## 使用说明

### 窗口行为

- 关闭「桌面声波」的设置窗口时，如果桌面悬浮窗开着，悬浮窗继续运行，设置窗口只是隐藏。卡片上会显示「桌面悬浮窗运行中」，可以从卡片重新打开设置或关闭悬浮窗。悬浮窗关闭后，隐藏的设置窗口也随之关闭。
- 关闭主界面时，如果还有悬浮窗开着，其他组件窗口照常关闭，主界面隐藏到任务栏右下角的通知区域。单击或双击托盘图标回到主界面；右键菜单有「显示主界面」「关闭悬浮窗」「退出」。主界面隐藏期间最后一个悬浮窗也关闭时，程序自动退出。
- 没有悬浮窗时，关闭主界面即完全退出。
- 3D 频谱瀑布的桌面悬浮窗随它的组件窗口一起关闭。
- 启用鼠标穿透后，鼠标操作会落到后方窗口，请在组件窗口里关闭悬浮窗或取消穿透。

### 参数文件

程序在可执行文件所在目录保存以下文件，删除即可恢复默认：

| 文件 | 内容 |
|---|---|
| `v61-parameters.json` | 3D 频谱瀑布的滑条参数和悬浮窗选项 |
| `v61-window.json` | 3D 频谱瀑布窗口大小和预览位置 |
| `v10-parameters.json` | 桌面声波参数和悬浮窗位置 |
| `app-settings.json` | 开发者选项 |
| `crash.log` | 未处理异常记录（仅在出错时生成） |

### 开发者选项

在「关于」页连续点击大标题「关于」5 次（每次间隔 1.5 秒内），侧栏「更多」下方会出现「开发者选项」。可以调整滑轨宽度（8–36 px，默认 8）、珊瑚红圆点大小（24–64 px，默认 24）和滑轨阴影（0–200%，默认 40%），对两个组件窗口中的所有滑条实时生效。设置保存在 `app-settings.json`，删除该文件即恢复为未开启和默认尺寸。

## 项目结构

- `Shell/`：主窗口（组件库）、组件窗口标题栏、卡片缩略图、托盘图标、壁纸背景、开发者选项。
- `Theme/Theme.xaml`：珊瑚红强调色及按钮、复选框、下拉框、滚动条等浅色样式。
- `Components/V61/`：3D 频谱瀑布（音频分析、频谱历史、OpenGL 渲染、桌面悬浮窗）。
- `Components/V10/`：桌面声波（音频捕获、波形网格、OpenGL 渲染、桌面悬浮窗、银灰珊瑚滑条）。
- `docs/window-move/`：主窗口移动性能测试报告。

## 外观说明

Windows 10 不给这类窗口提供系统级背景模糊，主界面的「毛玻璃」用桌面壁纸的模糊副本加白色半透明层模拟。壁纸载入时在解码分辨率上做一次高斯模糊并冻结，不在整个窗口上运行实时 BlurEffect；拖动期间复用原有背景，松开后再按屏幕位置对齐。读取不到壁纸时退回纯浅灰。组件窗口内嵌 OpenGL 原生画面，不能使用透明窗口，所以是直角窗口加系统阴影。

## 性能排查：主窗口移动卡顿

早期主界面是 WPF 分层透明窗口，每次 LocationChanged 都修改壁纸 ImageBrush 的 Viewport，并在大面积背景上运行半径 70 的实时 Gaussian BlurEffect，导致拖动时 CPU 占用高、回调不稳定。现在改为背景预先模糊、交互移动期间不修改背景画刷、移动结束时再对齐；3D 频谱瀑布的显示刷新率查询也按显示器缓存。

1040×660 DIP 窗口、相同壁纸和移动轨迹、目标 240 次/秒的受控 SetWindowPos 测试：

| 路径 | 实际移动回调 Hz | 移动调用 P95 | 回调间隔 P95 | 进程 CPU / 单核心 |
|---|---:|---:|---:|---:|
| 修正前 | 191.2 | 12.752 ms | 16.025 ms | 18.4% |
| 仅禁用实时模糊 | 212.7 | 7.603 ms | 8.821 ms | 7.6% |
| 预先模糊 + 拖动复用背景 | 240.2 | 0.529 ms | 4.999 ms | 5.2% |

该测试衡量的是移动调用和回调，不是鼠标输入延迟或屏幕呈现 FPS。可以用以下参数复现：

- `--profile-window-move=optimized`：在独立诊断窗口运行移动测试后自动退出，报告保存在程序目录。
- `--profile-window-move=verify`：检查冻结壁纸、拖动期间复用/结束对齐、程序定位、最大化还原以及刷新率缓存。

例如 `dotnet run -c Release -- --profile-window-move=verify`。历史报告见 `docs/window-move/`。

## 第三方依赖

- [NAudio](https://github.com/naudio/NAudio) 2.2.1（MIT License）：WASAPI loopback 音频采集。

## 许可证

本项目以 [MIT License](LICENSE) 发布。