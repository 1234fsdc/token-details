# Token Details ZCode Overlay Spec

## 目标

在不修改 `C:\APP\ZCode` 安装文件、不启动第二个 ZCode 进程的前提下，让现有 Token Details 程序同时提供三种面板：

1. **简略面板**：`Token Details`，保留原有全局 ZCode + Codex 多模型摘要。
2. **详情面板**：`Token Details 详情`，保留原有总览/速度/总量三页。
3. **ZCode 会话面板**：`Token Details ZCode`，透明跟随当前 ZCode 主窗口，只显示当前会话当前模型的速度。

## 三种面板

- 默认启动创建简略面板和 ZCode 会话 overlay；详情面板按托盘“详情”打开。
- `--standalone` 只启用原有自由悬浮简略面板，用于无 ZCode 目标或把手拖动回归测试。
- ZCode overlay 不写入 ZCode 配置、数据库或安装目录；ZCode 自动更新不会覆盖 Token Details 程序。

## 当前会话与模型绑定

- 每秒通过 Windows UI Automation 读取 ZCode Header 当前任务标题、当前模型按钮和“选择打开方式”按钮 bounds。
- overlay 锚定在“选择打开方式”按钮左侧 40 CSS px，随该按钮的真实屏幕坐标移动，不再使用固定右侧边距猜测。
- overlay 宿主为原生层叠窗口（`TokenDetailsOverlay` 类，专用线程创建并自泵消息）：PIL 把速度文字渲染成每像素 alpha 位图经 `UpdateLayeredWindow` 合成，窗口内除文字外零像素，无任何底色/边框/阴影。
- 不用浏览器窗口渲染 overlay：WebView2 的 CSS 透明在本机只能透到 WinForms 宿主的 #F0F0F0 底色，无法做到“无底色只有文字”。
- UIA 每秒探测一次；本轮健康（anchor 非空）才更新锚点/标题/模型，超时或异常时整体沿用上次健康值，避免锚点被清空导致 overlay 闪没。
- UIA 持续失败时，同步循环按上次“按钮相对客户区右缘/顶缘”的偏移与当前客户区推出位置；两者都缺失时才隐藏 overlay。
- UIA 探测进程使用 `CREATE_NO_WINDOW` 创建，屏幕上不出现 PowerShell 控制台闪现。
- ZCode overlay 查询必须带这个 `session_id`，不使用全局最近模型数据。
- 同一会话内只保留 Header 当前模型的已完成请求；模型切换后立即重新聚合。
- 切换会话后下一次刷新使用新 session；旧 session 的速度不保留到新会话。
- 新模型没有已完成请求时显示 `0 t/s`（用户 2026-10-01 改定：显示零而不是隐藏），不沿用旧模型数值。
- 每个当前会话/模型最多取最近 8 条有效请求，继续沿用 Token Details 当前速度口径。

## 定位与窗口状态

- 通过可见顶层窗口标题 `ZCode` 找到目标窗口；优先前台 ZCode，否则按可见面积选择主窗口，避免辅助窗口误锚定。
- 使用 `GetClientRect` + `ClientToScreen` 获取 ZCode 客户区屏幕坐标，不使用固定屏幕坐标。
- 以 Workspace Header 的 `h-12`（48 CSS px）为垂直锚点。
- 右侧保留固定逻辑像素间距，宽度、高度和偏移按 `GetDpiForWindow` 转换为物理像素。
- ZCode 移动、调整大小、最大化、还原、跨显示器 DPI 变化时重新定位。
- ZCode 不是前台窗口、最小化、关闭或客户区不可用时隐藏 ZCode overlay；恢复前台后显示。
- overlay 使用 `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`，不抢焦点、不显示任务栏按钮、不拦截 ZCode 鼠标操作。

## 显示内容

- 简略面板显示全局多模型摘要。
- 详情面板显示完整统计页。
- ZCode overlay 只显示当前会话当前模型的速度（数值 + ` t/s`）。
- 无有效速度时显示 `0 t/s`（用户 2026-10-01 改定：显示零而不是隐藏）。
- 配色：数值用 ZCode Header 原生墨水色 `#11151a`，单位/空态灰 `#6b7280`；速度不分档变色。
- 速度数据继续使用当前 Token Details 统计口径；本次不新增实时 token 计数。

## 验收场景

1. 默认启动后同时存在 `Token Details` 和 `Token Details ZCode`；托盘详情打开 `Token Details 详情`。
2. ZCode 当前会话有完成请求时，ZCode overlay 显示该会话当前模型速度。
3. 切换到另一个已有会话，overlay 下一次刷新切换到新会话；旧会话速度不残留。
4. 当前会话切换模型后，overlay 只显示新模型；新模型无历史时显示 `0 t/s`。
5. 移动 ZCode，overlay 位置跟随。
6. 调整 ZCode 窗口大小或最大化/还原，overlay 仍在 Header 内正确锚定。
7. ZCode 最小化或切换到其他应用，overlay 隐藏；恢复 ZCode 前台后重新显示。
8. 将 ZCode 移到不同 DPI 显示器，overlay 尺寸和位置重新换算。
9. overlay 点击不会阻止 ZCode 操作；`--standalone` 模式仍可用原把手拖动测试。
10. 退出 Token Details 不影响 ZCode；退出/更新 ZCode 不修改 Token Details 文件。
