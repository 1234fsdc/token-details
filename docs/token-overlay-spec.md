# Token Details ZCode Overlay Spec

## 目标

在不修改 `C:\APP\ZCode` 安装文件、不启动第二个 ZCode 进程的前提下，让现有 Token Details 程序在当前 ZCode 窗口 Header 右侧显示 Token 速度。

## 模式

- 默认模式：创建透明、无边框、置顶、鼠标穿透的 overlay，跟随前台 ZCode 主窗口。
- `--standalone`：保留原有自由悬浮面板，用于兼容没有 ZCode 窗口或需要手动拖动的场景。
- overlay 不写入 ZCode 配置、数据库或安装目录；ZCode 自动更新不会覆盖 overlay 程序。

## 定位与窗口状态

- 通过可见顶层窗口标题 `ZCode` 找到目标窗口；优先使用当前前台 ZCode 窗口。
- 使用 `GetClientRect` + `ClientToScreen` 获取 ZCode 客户区屏幕坐标，不使用固定屏幕坐标。
- 以 Workspace Header 的 `h-12`（48 CSS px）为垂直锚点。
- 右侧保留固定逻辑像素间距，宽度、高度和偏移按 `GetDpiForWindow` 转换为物理像素。
- ZCode 移动、调整大小、最大化、还原、跨显示器 DPI 变化时重新定位。
- ZCode 不是前台窗口、最小化、关闭或客户区不可用时隐藏 overlay；恢复前台后显示。
- overlay 使用 `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`，不抢焦点、不显示任务栏按钮、不拦截 ZCode 鼠标操作。

## 显示内容

- overlay 只显示 ZCode 数据源的第一条速度行，避免把 Codex 行显示在 ZCode 窗口上。
- 无有效速度时显示 `— t/s`。
- 速度数据继续使用当前 Token Details 统计口径；本次不新增实时 token 计数。

## 验收场景

1. 启动 ZCode 后启动 Token Details，overlay 出现在 ZCode Header 右侧。
2. 移动 ZCode，overlay 位置跟随。
3. 调整 ZCode 窗口大小或最大化/还原，overlay 仍在 Header 内正确锚定。
4. ZCode 最小化或切换到其他应用，overlay 隐藏；恢复 ZCode 前台后重新显示。
5. 将 ZCode 移到不同 DPI 显示器，overlay 尺寸和位置重新换算。
6. overlay 点击不会阻止 ZCode 操作；`--standalone` 模式仍可用原把手拖动测试。
7. 退出 Token Details 不影响 ZCode；退出/更新 ZCode 不修改 Token Details 文件。
