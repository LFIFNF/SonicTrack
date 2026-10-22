# SonicTrack 音乐可视化（整合版）

浅色组件库主界面，在独立窗口中打开可视化组件；当前内置 **3D 频谱瀑布（V6.1）**、**桌面声波（V10）** 与 **复古功放表（V0.0.1）**，可同时运行。声音来自 Windows 当前默认播放设备（WASAPI loopback）。

## 环境与构建

- Windows，[.NET 8](https://dotnet.microsoft.com/)（`net8.0-windows`）
- 构建：`dotnet build -c Release`
- 启动：`bin/Release/net8.0-windows/SonicTrack.App.exe`

可选诊断参数：`--profile-window-move=optimized` / `--profile-window-move=verify`（说明见架构文档）。

## 文档

| 入口 | 说明 |
|------|------|
| [docs/README.md](docs/README.md) | 文档索引与阅读顺序 |
| [docs/功能与业务规则.md](docs/功能与业务规则.md) | 产品功能与业务规则 |
| [docs/系统架构与设计.md](docs/系统架构与设计.md) | 目录地图、数据流、改哪里、怎么验证 |

主窗口拖动性能的历史测量见 [docs/window-move/](docs/window-move/)。

## 当前状态

版本 **1.0.4**（见 `SonicTrack.App.csproj`）。
