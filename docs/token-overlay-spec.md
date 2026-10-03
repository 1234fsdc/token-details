# Token Details ZCode Overlay Spec

## 目标

在不修改 `C:\APP\ZCode` 安装文件、不启动第二个 ZCode 进程的前提下，让现有 Token Details 程序同时提供三种面板：

1. **简略面板**：`Token Details`，保留原有全局 ZCode + Codex 多模型摘要。
2. **详情面板**：`Token Details 详情`，保留原有总览/速度/总量三页。
3. **ZCode 会话面板**：`Token Details ZCode`，透明跟随当前 ZCode 主窗口，只显示当前会话当前模型的速度。

## 三种面板

- 默认启动只显示 ZCode 会话 overlay（另有隐藏的 1×1 宿主窗口保住 pywebview 生命周期）；简略面板与详情面板都经托盘菜单按需打开——“简略面板”为勾选开关（关闭/× 只隐藏），默认不显示。
- `--standalone` 只启用原有自由悬浮简略面板，用于无 ZCode 目标或把手拖动回归测试。
- ZCode overlay 不写入 ZCode 配置、数据库或安装目录；ZCode 自动更新不会覆盖 Token Details 程序。

## 当前会话与模型绑定

- 每秒通过 Windows UI Automation 读取 ZCode Header 当前任务标题、当前模型按钮和“选择打开方式”按钮 bounds。
- 标题绑定：精确匹配优先；Header 会按宽度省略号截断（UIA 读到显示串），去掉省略号后按前缀匹配最近未归档会话（前缀 ≥8 字符，LIKE 转义）。仍无结果时**仅冷启动**（从未锚定过 UIA 数据）兜底绑定 10 分钟内最近活跃的未归档会话（`fallback_session`）——锚定过之后标题读不到不再用这个兜底：最近活跃几乎总是刚离开的旧会话，会把它的速度跟到新会话上（用户 2026-10-02 反馈）。
- 空标题消歧（`resolve_title`，用户 2026-10-02 改定）：短暂读不到（UIA 毛刺/走树间隙）沿用上次健康标题，持续读不到超过 4s 就接受空 → 显示 0——宁可 0 也不把旧会话的速度跟过去。标题过滤**只认"锚点同行"精确带**（与"选择打开方式"按钮同排、右缘不越按钮、宽 ≥30px、长 ≥2 字符，短标题也可读）。曾有绝对 Top≤120 的 blanket 兜底带，2026-10-02 删除：header 正下方的聊天首行会落进该带且常比真标题长，被当标题后 DB 无匹配 → 运行中会话显示 0（bind-zero 日志实锤：聊天行 Top=101/len=31 劫持真标题 Top=39/len=27）。
- overlay 锚定在“选择打开方式”按钮左侧 40 CSS px，随该按钮的真实屏幕坐标移动，不再使用固定右侧边距猜测。
- overlay 宿主为原生层叠窗口（`TokenDetailsOverlay` 类，专用线程创建并自泵消息）：PIL 把速度文字渲染成每像素 alpha 位图经 `UpdateLayeredWindow` 合成，窗口内除文字外零像素，无任何底色/边框/阴影。
- 不用浏览器窗口渲染 overlay：WebView2 的 CSS 透明在本机只能透到 WinForms 宿主的 #F0F0F0 底色，无法做到“无底色只有文字”。
- UIA 采集由**常驻观察进程**供给（2026-10-02 改定，替代每秒新起 PowerShell——进程启动 ~0.5s 且整树遍历忙时可达 12s+，切换会话最坏 2-3s 才换数）：优先用 C# 原生 `token_watcher.exe`（.NET Framework + Windows 自带 csc 现编译，41MB/5.6% CPU；缺 .cs/缺 csc/编译失败回退 PowerShell 版，编译失败但旧 exe 存在时用旧 exe），协议与 PS 版逐字一致。首次全量走树后缓存标题/模型/锚点三个元素引用，每 150ms 快路径读这 3 个元素的实时属性（~10ms，会话切换/窗口拖动即时可见）；兜底全量重走间隔自适应（上次走树 >3s 拉长 3s→15s，忙时不连续空走）；元素引用失效立即重走。切换会话 ~0.5s 内换数。模型按钮识别取**最靠底部的** provider/model 格式按钮（2026-10-03 改定）：真按钮固定在底栏输入区，模型弹层的选项条目同名同格式且在它上方，首匹配会选中弹层条目、弹层关闭后元素脱离界面持续吐旧值（0 显示的第三根因，bind-zero 实锤）。
- Python 侧 `_watcher_loop` 守护（无输出 25s 重启，喂狗时间戳为本次 spawn 私有，防跨重启误杀）+ `overlay_bind_loop` 绑定线程（0.3s 一拍，db 连接线程私有）；watcher 每行输出经 `parse_watcher_line` 归一。控件过滤脚本与 `zcode_context` 保持一致，两处同步改。
- watcher 内存治理：脚本内每 ~30s 强制 GC（UIA COM 包装靠惰性终结释放，长跑实测 35min 涨到 1.2GB）；每行 JSON 带 `ws`（自身私有内存 MB），Python 侧 **ws>500MB 熔断重建**，内存有硬上限。
- 绑定粘滞：本轮健康（anchor 非空）才更新锚点/标题/模型，超时或异常时整体沿用上次健康值，避免锚点被清空导致 overlay 闪没。健康轮内的部分失败（anchor 在但 Text 查询空转，标题/模型为空）同样沿用上次值，不把会话绑定清成 0。
- UIA 持续失败时，同步循环按上次“按钮相对客户区右缘/顶缘”的偏移与当前客户区推出位置；两者都缺失时才隐藏 overlay。
- UIA 探测进程使用 `CREATE_NO_WINDOW` 创建，屏幕上不出现 PowerShell 控制台闪现。
- ZCode overlay 查询必须带这个 `session_id`，不使用全局最近模型数据。
- 同一会话内只保留 Header 当前模型的已完成请求；模型切换后立即重新聚合。模型读数先经会话记录校验（用户 2026-10-03 改定"确保读到正确的模型"）：读到的模型在该会话无任何完成记录 → 判定读数不可信（模型弹层条目误缓存后吐旧值/子代理模型/刚切到从没用过的模型），回退按会话最近实际模型显示（hint 置空走 message 最新 assistant 行自检），bind-zero 日志记 `model hint ... 回退最近模型`；**整个会话无任何完成记录才显示 0**（修订 2026-10-01 的"新模型无记录显示 0"）。
- 切换会话后下一次刷新使用新 session；旧 session 的速度不保留到新会话。
- 新模型没有已完成请求时显示 `0 t/s`（用户 2026-10-01 改定：显示零而不是隐藏），不沿用旧模型数值。2026-10-03 修订：该规则收紧到"整个会话无任何完成记录才显示 0"——会话有记录但当前模型读数查不到时，先按"模型读数校验"回退会话最近实际模型（读数多半是弹层串味的假值），见上条。
- overlay 空闲归零（用户 2026-10-02 改定，仅 overlay，简略/详情面板 90s 口径不变）：绑定会话 90s 无活动即显示 0。判活用 `session_last_act`——取会话最新 200 条 **part** 行的 `MAX(time_updated)`：长回合生成中 message 行只在工具调用边界刷新（实测停 90s+），part 行随流式/工具持续写入，只看 message 会把运行中会话误归零。最近完成请求 <90s 时跳过判活查询（必然活跃）；用户消息 part 也算活动。走树失败（Text 空转）保留旧元素引用不清（清了会让快路径全空、TTL 到期误归零），仅整树一无所获或元素读抛异常时清。binder 脏标记 key 含 10s 时间桶：纯空闲（无任何会话写库）时 MAX(rowid)/标题全冻结，桶过期强制重算，否则 90s 归零门控不再被触发、速度冻在屏上。
- 归零诊断（2026-10-02 加）：binder 在"有速度→0 的瞬间"限频 60s 记一条 `[bind-zero]` 原因到 weberr.log（90s 空闲归零带 sid 与两个时长、标题空超 TTL、标题无匹配会话、会话无有效请求），复现"运行中显示 0"时按时间对账即可定位路径。
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
- 配色（D 全灰等宽方案）：数字 Segoe UI Semibold 13px、`#5f6368`（与 Header 图标同灰阶，tabular 等宽右对齐锁位），单位 10px `#9aa1ab`，空态 `#b9bfc7`；速度不分档变色。候选方向（B+/E/H/F）及实景对比见 `docs/preview/overlay-style-preview.html`。
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
