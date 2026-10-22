# SonicTrack 文档

整合版（`app-integrated`）的文档入口。实现改动前建议先读规则，再按架构文档落到具体文件。

## 阅读顺序

1. **[功能与业务规则](功能与业务规则.md)** — 用户能做什么、窗口/托盘/悬浮窗规则、参数业务含义、空态与异常。改功能或文案时必读并同步更新。
2. **[系统架构与设计](系统架构与设计.md)** — 技术栈、目录地图、数据流、功能→文件对照、构建与诊断命令。改代码时指路用。
3. 需要主窗口拖动性能背景时，再看 **window-move** 报告（历史数据，非产品规格）。

根目录 [README.md](../README.md) 只保留产品一句话、构建方式、版本与文档链接；详细内容以本目录为准。

## 文档一览

| 文档 | 分工 |
|------|------|
| [功能与业务规则.md](功能与业务规则.md) | 产品功能与业务规则（无实现细节） |
| [系统架构与设计.md](系统架构与设计.md) | 模块/文件地图、数据流、验证命令 |
| [../Components/VintageAmpMeters/docs/开发日志.md](../Components/VintageAmpMeters/docs/开发日志.md) | 复古功放表组件 v0.0.1 开发日志 |
| [window-move/](window-move/) | 主窗口移动性能基线、优化对比与 verify 结果（2026-10-06） |
| [1.0/](1.0/) | 1.0.x 版本更新日志 |

### window-move 文件

| 文件 | 内容 |
|------|------|
| [window-move/window-move-baseline.txt](window-move/window-move-baseline.txt) | 修正前基线 |
| [window-move/window-move-no-blur.txt](window-move/window-move-no-blur.txt) | 禁用实时模糊对照 |
| [window-move/window-move-optimized.txt](window-move/window-move-optimized.txt) | 预模糊 + 拖动复用背景 |
| [window-move/window-move-verification.txt](window-move/window-move-verification.txt) | verify 项 PASS 记录 |
