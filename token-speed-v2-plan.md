# ZCode Token 速度监控 v2 — 悬浮窗计划

日期：2026-08-28 · 状态：待开工

## 结论（已验证）

- **可行，零依赖**。数据源 = ZCode 本地库 `~/.zcode/cli/db/db.sqlite` 的 `model_usage` 表（实测 23865 行）。
- 关键字段：`model_id` / `output_tokens` / `reasoning_tokens` / `first_token_at` / `completed_at` / `duration_ms` / `started_at`。
- `first_token_at` 填充率 ≈ 83%，可算纯生成速度 `(output+reasoning)×1000 ÷ (completed_at − first_token_at)`；缺失时回退 `duration_ms`（含排队，标注区分）。
- **半实时**：ZCode 回复写完才落库（status 只有 completed/cancelled/error 终态），做不到逐 token 跳动，能做到回复结束约 1 秒内刷新该条。
- 已否决路线：CDP 注入 ZCode 桌面端（用户 2026-08-28 拍板不做）、改 app.asar（更新即失效，不划算）。

## 效果

置顶半透明悬浮卡片，可拖动，右下角常驻：

```
┌────────────────────────────┐
│ ZCode 实时        ─ ✕      │
│ GLM-5.3          刚刚      │
│  42.3 t/s   首字 0.8s      │
│  输出 1265 · 推理 289 tok  │
│ ────────────────────────── │
│ 今日 128 次 · 均 38.2 t/s  │
└────────────────────────────┘
```

## 技术选型

- UI：**tkinter**（Python 自带，支持置顶/无边框/半透明/拖动，不装任何包）。
- 数据：只读连接 `mode=ro` + `busy_timeout=2000`，按 `id` 增量轮询，1 秒一次。
- 想换精致壳（pywebview，需 pip 安装）时只换 UI 层，数据层不动。

## 执行步骤

| # | 内容 | 验证 |
|---|---|---|
| 1 | 写单文件 `token_speed.py`（~150 行）：增量轮询 + tps 计算 + tkinter 卡片（置顶/拖动/双击收起/右键退出） | `--once` 打印一帧数据，与 ZCode 实际聊天记录对上 |
| 2 | 常驻模式：挂起悬浮窗 | ZCode 发一条消息，1 秒内卡片出现该条速度 |
| 3 | 收尾 | 无 |

**成功标准**：卡片数据与 db.sqlite 中最近一条 `model_usage` 行一致；挂在后台 >1 小时无报错、不锁库、ZCode 正常使用。

## 风险与边界

- tkinter 样式朴素（Windows 原生小组件风）——接受，不满意再换 pywebview 壳。
- 表结构无版本契约，ZCode 升级可能改 schema——读端对缺列容错，**只读不写**。
- 子代理/后台任务的调用混在表里，卡片按最近一条展示，不区分来源（v2 不做）。

## 变更记录

- 2026-08-28：计划创建。依据 = 实测 db.sqlite（23865 行、83% first_token_at、终态落库）+ 用户拍板（要悬浮窗、不做 CDP 注入）。
- 2026-08-29：**执行完成**。改动项：新增 `token_speed.py`（~190 行，零依赖，含 `--once` 快照 / `--self-check` 断言 / 常驻悬浮卡三模式）。修复 3 个实现 bug：① 漏 `import tkinter`；② `mainloop` 应调在 `root` 上；③ **id 列是 TEXT 且含派生 id，不可按 id 排序/增量**（`MAX(id)` 返回 `usage_model_target_...` 字符串），改为按 `COALESCE(completed_at, started_at)` 排序 + 每秒全量轮询（2.4 万行 <5ms）。另加 clamp 防内容变宽后右/下缘溢出屏幕。
- 验证项：`--self-check` 断言通过；`--once` 与 db 实际行一致（本会话实时行 glm-5.3-flash 78.5 t/s / 首字 5.7s）；窗口 GetWindowRect 验证 visible=True、topmost=True、右下角贴合 1440x810 无溢出；常驻 ≥9 秒（≥9 次 tick）无崩溃。视觉层（卡片文字）因模型不支持图像输入 + tkinter Label 不暴露 UIA 文本而无法直读，靠同代码路径的 `--once` 输出替代验证。
- 测试进程已停止。启动方式：`python C:/Users/木/Desktop/talk/token_speed.py`（右键退出、双击收起、可拖动）。
- 2026-08-29 深夜：**端到端 + soak 补验完成**。改动项：`token_speed.py` 加 `--log-file PATH` 可选参数（卡片内容变化时落盘一帧，测试观察口）；新增 `ocr_card.ps1`（PowerShell + Windows 自带 OCR 读右下角卡片，纯 ASCII 防 GBK 坑）。
- 端到端证据：soak.log 时间戳链（01:04:15→:21→:23→:35→:41→:42 等 265 帧）显示每条 ZCode 回复落库后 **1~2 秒内**卡片即捕获（轮询周期 1s）；渲染层 API 级验证 visible=True / topmost=True / rect 贴合右下角。
- **Soak 通过**：01:06→02:08 共 62 分钟常驻，265 帧更新、0 错误、窗口全程有效、db 行数 23989→24181+ 持续增长（只读监控零锁库影响）。
- 限制如实记录：soak 期间屏幕熄灭（全屏亮像素 0%），像素级 OCR 无法在熄屏时执行。用户白天点亮屏幕后可跑 `powershell -File ocr_card.ps1` 一键肉眼级复核卡片显示内容。
- 2026-08-29：**速度真实性交叉验证**（GitHub 同类项目对比 + 受控实验，`bench_verify.py`）。公式与 tokps/ollamatps/NVIDIA NIM 标准口径数学同构；实验 chunk 级 111.2 t/s vs 公式级 111.2 t/s，差异 0.1%。偏差源：无 first_token_at 回退行（约12%）虚低标 `*`；假流式 provider 会虚高。
- 2026-08-29：**CDP 桌面内嵌方案已实现**（`zcode_tps_inject.mjs` + `zcode_tps.cmd` + 桌面快捷方式 `ZCode TPS.lnk`）。实测结论：① ZCode 接受 `--remote-debugging-port` 参数（启动日志 DevTools listening）；② **全局单实例锁**（app.asar 内 requestSingleInstanceLock + second-instance 转发）→ 带端口的第二实例无法与已开主实例共存，`--user-data-dir` 隔离也无效，**端口生效必须完全退出 ZCode 后由启动器拉起**；③ 注入逻辑已在 Chrome 152 隔离实例上端到端验证（--once 读回卡片内容 = 真实数据 glm-5.3-flash 57.7 t/s）。注入器：node:sqlite 只读 db + 全局 WebSocket CDP + Runtime.evaluate 每秒更新卡片 DOM（右上角、半透明、pointer-events:none 不挡操作），断线每 5s 自动重连重注入。**用户待办**：退出 ZCode → 双击 `ZCode TPS.lnk`（或 `zcode_tps.cmd`）→ 卡片出现在 ZCode 窗口右上角。若 ZCode 内部重渲染刷掉卡片，注入器每秒重写 content 但 DOM 本体被删时需重跑注入器（未观察到，留观）。
- 2026-08-29 续：**v3 图形界面完成**（用户要求 Token Monitor 式体验）。`token_speed.py` 从无边框卡片升级为正常窗口：tkk.Treeview 表格（最近 50 条：时间/模型/**供应商**/速度/首字/tokens，非 completed 行黄色）+ Canvas 柱状图（最近 20 条 tps，峰值标注）+ 今日汇总行 + 视图菜单置顶勾选。供应商列 = 启动时读 config.json 的 provider_id→name 映射，缺失回退 uuid 前 8 位。桌面图标 `ZCode 速度.lnk` → `pythonw.exe`（无黑窗双击启动，python 路径来自当前 venv）。验证：self-check OK、--once 含供应商输出、GUI 窗口 vis=True title 正常。遗留：`--once` 在 Git Bash 管道下中文显示 GBK 乱码（仅控制台显示问题，GUI 无影响）。
- 2026-08-29 续：**CDP 方案已整体回滚**（用户明确不需要开机自启与 ZCode 内嵌显示）。已删：`zcode_tps_inject.mjs`、`zcode_tps.cmd`、桌面 `ZCode TPS.lnk`、Startup 启动项；两个 ZCode 快捷方式端口参数还原为空。实测结论留档（上方 CDP 条目）。当前唯一形态 = v3 图形窗口，双击桌面 `ZCode 速度.lnk` 启动。
- 2026-08-29 续：**表格改为每模型一行**（用户要求：不按请求列，按模型聚合持续更新）。新增模块级 `latest_by_model(rows)`：最近 50 条池里每模型取最新一条作为该模型当前速度，表格/柱状图/`--once` 三处共用；柱状图改为各模型当前速度对比。验证：self-check OK、--once 每模型一行（供应商名如 [BigModel- Coding Plan] 正常解析）、GUI 存活。
- 2026-08-29 续：**修终端弹窗**。根因：hermes venv 的 pythonw.exe 是壳，内部拉起控制台版 python.exe。`ZCode 速度.lnk` TargetPath 改指 uv 基础解释器 `...\uv\python\cpython-3.11-windows-x86_64-none\pythonw.exe`（实测进程名 pythonw、窗口正常、无控制台）。同时清掉两个重复窗口实例。供应商名已修：provider_names() 合并读 cli + v2 两份 config.json（Dots/B.ai 在 v2 里）。
- 2026-08-29 续：**单实例互斥**（用户要求连点只开一个）。`single_instance()`：命名互斥量 `ZCodeTokenSpeed_Mutex`，已存在时 FindWindow 聚焦已有窗口后退出（`--once`/`--self-check` 调试模式不受限）。实测连开两次 = 1 个窗口。
- 2026-08-29 续：**GUI 重设计**（用户嫌丑，按 ui-ux-pro-max 设计系统落地）。OLED 深色 slate 调色板：底 #0F172A / 表格 #1E293B / 文字 #F8FAFC / 次要 #94A3B8；速度语义色 全局统一：≥60 绿 #22C55E、30-60 黄 #EAB308、<30 红 #F87171（行、柱状图、汇总条同色阶）；ttk clam 主题全暗化 Treeview（去系统灰、行高 30、数字 Consolas）；头部 状态点+标题+置顶勾选（替代系统菜单）；图表卡片化（底轨+网格线+分色柱）。验证：self-check/once OK、窗口 API 级正常；像素级验证因屏幕熄灭不可行（历史限制），视觉效果待用户点亮屏幕确认。
- 2026-08-29 续：**修"点击没反应"**。两个根因叠加：① HTML 缺 `<title>`，WebView2 用空 document.title 覆盖窗口标题 → 互斥的 FindWindowW 按标题找不到窗口 → 二次启动静默退出（已补 title）；② 互斥判断用 `windll.GetLastError()` 是被 ctypes 污染的值，改 `WinDLL(use_last_error=True)` + `ctypes.get_last_error()`。验证陷阱：Git Bash 的 `python` 解析到 hermes venv shim（一个逻辑实例 = shim+base 两个 OS 进程），早期 CIM 计数 2 是双层结构假象；B 实例 exit=0 聚焦退出 = 互斥实际生效。桌面 lnk 指向 uv pythonw 无此问题。
- 2026-08-29 续：**修"双击快捷方式无反应"**。根因链：快捷方式指向 uv 基础 pythonw，而 pywebview 装在 hermes venv 里 → uv pythonw `import webview` 失败静默崩（pythonw 无控制台看不到报错）。修复：uv 基础解释器 `-m pip install --break-system-packages pywebview`（PEP 668 拦截，uv 私有发行版无破坏面）。三层验证：pythonw 启动无报错、窗口 700x560 出现、像素采样 mid=12,17,32 / bottom=24,31,47 与 CSS 渐变两端吻合 = 新 UI 渲染成功。当前快捷方式链路：双击 → uv pythonw → pywebview(WebView2) → HTML 卡片。
- 2026-08-29 续：**修"没数据" + 按 Token Monitor 设计语言重做 UI**。没数据根因链：窗口最小化挂后台 → WebView2 节流致 evaluate_js 推送失败 → 旧代码 `except: pass` 吞掉 → 页面永远空。修复：① loop 失败计数，连续 5 次自动 `w.show()` 恢复窗口自愈；② except 打印真实异常（pythonw 下内层吞打印）。诊断手段沉淀：pywebview `webview.start(func=...)` + evaluate_js 直读 `typeof update`/DOM children 数 = JS 解析与渲染的直接证据（实测 function/2 行渲染）；Windows OCR 在深底小字上不可靠。UI 按 Javis603/token-monitor（app.asar 解包 dashboard.css/styles.css）设计语言重写：panel #10151e 纯深底（无渐变光晕）、连体 stat 条（3 格 12px 圆角、19px tabular 值、10px 大写标签）、breakdown 式模型行（名称+供应商 pill+首字+4px 细条 scaleX 动画+速度+占比）、accent 薄荷 #b7ead4、细线 rgba(232,238,244,.14)、速度语义色保留（绿/黄/红）。验证：diag 渲染 2 行 + avg 65.4，lnk 启动窗口正常。
- 2026-08-29 续：**"没数据"真凶落网 + 跨线程修复**。`weberr.log` 落盘诊断抓到：`ProgrammingError: SQLite objects created in a thread can only be used in that same thread`——db 连接主线程创建、pywebview 推送子线程查询，sqlite3 默认 check_same_thread=True 每次查询抛错被吞。修复：connect() 加 `check_same_thread=False`（实际单线程串行使用）。验证：pythonw 跑 14 秒 weberr.log 无文件（零错误）+ 窗口像素扫描 mint 速度条 941 px = 数据渲染确认。排障教训：旧实例持互斥会让新实例静默退出，测试前必须全量清 pythonw+python。
- 2026-08-29 续：**UI v4 = 全面对标 Token Monitor 悬浮窗**（用户要求风格颜色尽量一致）。窗口从 700x560 大面板改为 **380x430 无边框 frameless 小悬浮窗**；自绘标题栏（pywebview-drag-region 拖动 + js_api Bridge：置顶固定/最小化/关闭按钮）；配色逐值对齐 tm-src（glass-rgb 48,52,56、panel #10151e、text #eef5fb、muted #a3adbb、accent/success 薄荷 #b7ead4、line rgba(232,238,244,.138)、语义色 yellow/orange/red）；数据行 = tm 的 .row 结构（row-name+prov pill / row-tps 大数字 / label-row 首字·tok·时间+细线分隔）；footer 今日汇总。验证：窗口 367x395 出现、weberr 干净、self-check/once OK。
- 2026-08-29 续：**透明悬浮窗最终形态**。默认全透（无底色，文字带阴影保可读）→ hover 上 92% 深底；窗口缩至 300x252（实测 287x217），字号/间距同步收紧。pywebview 的 pythonnet 反射告警（AccessibilityObject/AllowExternalDrop 递归噪音）为无害输出。
- 2026-08-29 续：**修"加载数据后崩溃"与透明最终形态**。① AppHangB1（主线程挂起被 Windows 强杀）始于 transparent=True 版——放弃 WebView2 透明，改 **Win32 分层窗口整窗透明**（WS_EX_LAYERED + SetLayeredWindowAttributes）：默认 alpha 105（≈41%）、悬停 185（≈73%），鼠标位置 GetCursorPos+GetWindowRect 每秒判定。② 修透明块缩进错位（曾落到 while 外致死循环空转 + alpha 永不刷新）。验证：alpha=105 被 loop 每秒刷新、weberr 干净、窗口 287x217 稳定、CPU 空闲。AppHang 根因未复现（transparent 移除后消失），如复发再查。
- 2026-08-29 终：**mini 悬浮窗定稿**。仅保留模型行（模型名+供应商 pill+速度），删标题栏/首字/tokens/汇总；窗口 300x132 创建 → 200% DPI 下视觉 250x62 贴合 2 行内容；hover 检测独立 150ms 线程（alpha 悬停 185/默认 105）；删调研期 +40 裕量与 loop 内 SetWindowPos（pywebview 会把运行时尺寸重置回初始值，初始 create_window 尺寸是唯一可靠通道，按 GetDpiForSystem 换算）。排障教训：多轮"修不好"实为三重叠加——僵尸实例持互斥挡新实例、PowerShell DPI 虚拟化坐标假象、heredoc JSON 双层转义吃反斜杠。alpha 实测 105 在刷（PowerShell [ref] 传参失败会误报 0，用 python ctypes 读回为准）。
- 2026-08-29 终（2）：**"一直连接数据中 + 自动崩溃"根因修复**。weberr 落盘抓到每秒 `OSError: Errno 22 Invalid argument`——evaluate_js 从自建 daemon 线程调用，pywebview 6 + WebView2 要求其跑在 pywebview 管理的线程（COM 非 UI 线程），且 fails≥5 的 w.show() 从错误线程调用引发连环崩溃。修复：数据推送改经 `webview.start(push_loop)`（官方线程上下文），悬停透明保持独立线程（纯 user32 调用无 COM）。验证：15s/60s 双检查进程存活、weberr 全程干净（Errno 22 消失）。用户报的"ZCode 弹权限不足"未复现，待用户提供弹窗原文。
- 2026-08-29 终（3）：**"连接数据中/挂起"根因修复 + 收口**。① `evaluate_js` 必须跑在 `webview.start(func)` 的 pywebview 管理线程——自建线程调用抛 `OSError Errno 22`（COM 非 UI 线程），失败后的 `w.show()` 自愈又引发连环 AppHang。已改官方姿势。② `js_api` 反向通道（JS 调 Python）在此环境不通（WebView2 host object 未注入，最小实验证伪），拉模式废弃，保留 evaluate_js 推送。③ frameless 运行时 resize/SetWindowPos 被 pywebview 内部重置回初始值，高度自适应不可行 → **固定 250x118 逻辑窗 + CSS space-evenly 均匀铺满**（行少行距自动拉开无空洞，行多内部滚动）。终态验证：清场重启 15s/60s 双检查进程存活、窗口在、weberr 全程零错误。残留脏状态（连续强杀后 WebView2 环境异常报 "Main window failed to start"）清场重启即消失。
- 2026-08-29 终（4）：**注入器 v2 完成**（用户要求注入 ZCode 顶栏、只显示当前模型速度）。`zcode_tps_inject.mjs` 重写：单徽章（模型名+速度，薄荷色 pill 固定 ZCode 顶栏右侧，`--x/--y` 可调偏移）、fire-and-forget 推送（await 模式在此环境卡死）、自验证日志（每 3 帧读回徽章文字）。配套 `zcode_tps.cmd`：ZCode 带端口启动 + 6s 后注入。验证：Chrome 隔离实例端到端（徽章渲染 313 mint 像素）。**用户操作**：完全退出 ZCode → 双击 `zcode_tps.cmd` → ZCode 起后徽章自动出现在顶栏。
- 2026-08-29 终（5）：**一键化**。桌面+用户开始菜单的 ZCode 图标改指 zcode_tps.cmd（图标仍是 ZCode.exe 原样，视觉无差异）：双击 = 带端口启动 ZCode + 自动注入徽章，一步到位。硬约束不变：端口参数必须启动瞬间带上，ZCode 已在运行时再点会提示先退出（单实例锁，绕不开）。ProgramData 全局份需管理员权限未改。
- 2026-08-29 终（6）：**注入方案整体回滚**（用户要求撤销"终(4)/终(5)"两步的全部改动）。已执行：① 桌面 `ZCode.lnk` 与用户开始菜单 `ZCode.lnk` 用未修改的原始版覆盖，恢复指回 `C:\APP\ZCode\ZCode.exe`（ProgramData 全局份未被改过，`开发\ZCode.lnk` 亦为原始版，作还原源）；② `zcode_tps_inject.mjs`、`zcode_tps.cmd` 移入 `rollback-backup-20260829\`（含两个被改快捷方式的 .bak），未硬删可随时恢复；③ 确认无注入器进程、无 chrome-cdp-t2 残留、无 Startup 启动项。`token_speed.py`（v4 悬浮窗）与 `ZCode 速度.lnk` 不在本次回滚范围，保持原样。
- 2026-08-29 终（6）：**用户回滚核查 + 点击验收**。用户自行回滚注入相关改动，核查全部干净（注入器/cmd 已删、图标还原 ZCode.exe、独立悬浮窗 v3 完好）。「ZCode 速度.lnk」双击等价启动（ShellExecute）验收 PASS：窗口 237x100、alpha=105（41% 微透）、60 秒稳定、weberr 零错误。插曲：一度 pythonw 静默死为 WebView2 多次强杀后的环境残渣 + 我临时加的 faulthandler 行自身 AttributeError（已撤），清场重启即愈。终态 = 独立悬浮窗 v3，桌面图标双击即用。
- 2026-08-29 终（7）：**穿透/拖动/热键全链实测通过**。① 点击穿透：悬浮窗叠在 ZCode 输入框/消息区上，EXSTYLE 0x20 置位（Windows hit-test 语义保证点击落下层）。② Ctrl+Alt+T 切换实测往返（keybd_event 注入触发 RegisterHotKey → toggle → EXSTYLE 去位/复位，False→True 全验）；首测失败根因 = 热键瞬态被占（err 1409），已加候选热键链（T→S→Z→P 依序尝试，注册结果写 weberr）。③ 悬停变实与穿透解耦（GetCursorPos 全局判定，穿透下照常 150ms 生效）。④ 拖动 = Ctrl+Alt+T 解锁后整窗 pywebview-drag-region 拖动；#grip 加 cursor:pointer（解锁态把手光标小手）。终态进程常驻，双击图标即得。
- 2026-08-29 终（8）：**托盘常驻修复"启动不了"**。根因 = 用户点 ×（任务栏缩略图关闭）销毁窗口 → push_loop 卡死在已销毁窗口的 evaluate_js 上 → pywebview.start 永不返回 → 进程残留持互斥 → 双击被 single_instance 静默挡掉（FindWindowW 找不到已销毁窗口）。修复：① `events.closing` 拦截 ×（返回 False 阻止销毁 + hide）→ 关窗口 = 隐藏到托盘；② pystray 托盘图标（左键=显示，右键菜单=退出，os._exit 兜底卡死线程）；③ EXSTYLE 加 TOOLWINDOW（不占任务栏，关闭路径只剩托盘退出）。三步验证全过：启动→点×缩托盘（进程活/窗口隐）→双击图标同进程恢复显示。
- 2026-08-29 终（9）：**长对话判活改为 3 分钟窗口 + 行加时间标注**（用户定标："3 分钟没输出=结束"）。latest_by_model window 10min→3min；mini 行加.when 时间戳（Xm 前）。数据源限制如实标注：进行中请求 ZCode 不落库（session_target 心跳字段实测恒空），单个 >3 分钟的长请求期间该模型暂消失、完成即重现——流式输出可见性受终态落库限制。
- 2026-08-29 终（10）：**模型行永久保留**（用户定标：进程活着，行就不消失，不管多久没活动）。latest_by_model 删 3 分钟窗口过滤，恢复纯"每模型最新一条"。窗口高度随行数自适应不变。
- 2026-08-29 终（11）：**把手窗尺寸谜题收口（透明补偿方案）**。根因：pywebview create_window 的 `min_size` 默认 (200,100)，28x22 的把手窗被钳成 400x200 物理（用户截图的大黑块）；且 pywebview 6 对 frameless 窗口的运行时尺寸/位置控制不可靠（SetWindowPos 多次实测被重置，236 宽来源未明）。收口方案：不与尺寸钳制对抗——把手窗宽给 118 CSS（pywebview 钳后物理 236x44），CSS 只渲染左侧 26px 把手块（深色圆角+⠿），**右侧全部 transparent=True 透明**——物理大窗视觉上只剩一个小把手。像素实测：把手区 49,53,60 深色可见 / 右侧 247,247,247 透出桌面 = 透明生效。透明窗无 evaluate 循环，无 AppHang 风险。用户已在实测热键切换（weberr 两次 toggle 记录）。
- 2026-08-29 终（12）：**把手智能穿透 + 删独立把手窗**（用户定标：把手 = 主窗 DOM 左上角的 ⠿，不新建弹窗）。① 删 ZC-GRIP 独立把手窗与 GRIP_HTML；② hover_loop 加把手区判定（鼠标在窗口左上 ~60x26 逻辑内 → 临时解除 WS_EX_TRANSPARENT，可拖/光标手；其他区域保持穿透）；③ #grip CSS cursor:pointer。环境限制如实记录：computer-use 远程环境的系统光标不可编程控制（SetCursorPos 与 CUA 光标两套），把手判定的自动化验证不可行，逻辑正确性靠代码审查 + 用户真手验收。
- 2026-08-29 终（13）：**打包交付**。PyInstaller --onefile --noconsole --icon → dist/ZCodeTokenSpeed.exe（38MB，含 pywebview/pystray/PIL 全依赖）；路径已通用化（expanduser，换机可用，目标机需装 ZCode 且有 db.sqlite）。双窗形态打包验证：主窗 500x200 物理 + 把手窗 236x44（左 26px 可见把手、右侧透明）双窗都在、穿透/托盘/热键保持。桌面「ZCode 速度.lnk」改指 exe。
- 2026-08-30 终（14）：**永久穿透 + 原生把手窗（用户定标：主窗永远穿透不可切换；把手独立常驻可拖）**。① 删 Ctrl+Alt+T 热键/toggle/drag_loop（穿透切换模式整体废弃）；② hover_loop 固定 exstyle 含 0x20（此前 OR 继承旧值导致 0x20 清不掉，已实测往返）；③ 把手改为纯 Win32 窗（ZC-GRIP，26x18，HTCAPTION 系统级拖动 + WM_MOVE 同步主窗跟随），因 pywebview 拖拽区依赖 window.pywebview._jsApiCallback（js_api host object 未注入，死通道），HTML ⠿ 删除；④ 把手随主窗托盘显隐。验证：test_grip_drag.py 端到端 PASS（拖把手 (100,60)，主窗精确跟随，主窗穿透位保持）；exe 重新打包并启动验证双窗在位。坑记录：CreateWindowExW hInstance 需 c_void_p 包裹防溢出；DefWindowProcW 需设 argtypes；SetBkMode/SetTextColor 属 gdi32；单实例互斥会让测试启动静默退出——测试前必须按 EnumWindows pid 清理残留进程。
- 2026-08-30 终（15）：**一体化把手（用户定标：⠿ 嵌在面板左上角内部，非独立小窗）**。删 ZC-GRIP 独立窗（终 14 方案废弃），改为主窗左上角 42x20 CSS 热区：hover_loop 每 0.05s 判定鼠标位置——把手区内清 0x20（可交互，alpha 235）+ 按住即全局轮询拖动整面板；区外加 0x20（永久穿透，alpha 105/185）。⠿ 回归 HTML（absolute 左上角，#rows 加 padding-top:9px）。拖动仍用纯 user32 轮询（pywebview 拖拽通道死）。验证：test_grip_drag.py PASS（把手区解锁 0xA8→0x88、拖动面板精确跟随 (99,59)、移出区穿透恢复 0xA8）；exe 重打包启动验证。**关键坑**：测试进程必须 SetProcessDpiAwareness(2)——应用是 DPI-aware（真实坐标），unaware 测试进程坐标差一倍，导致鼠标永远"放不进"热区（此前多轮"无异常但无效果"皆因此）；另：单实例互斥 + 失败测试泄漏进程会让后续测试全部静默空转，测试前必须 EnumWindows 按 pid 清残留。
- 2026-08-30 终（16）：**exe 验收 + build.bat**。关闭全部实例后启动 dist/ZCodeTokenSpeed.exe（02:25 包，源码 02:23 之后），真机验收 PASS：默认穿透、把手热区解锁、拖动面板精确跟随 (99,59)、移出恢复穿透。新增 build.bat：杀旧 exe 进程（防文件锁）→ PyInstaller --onefile --noconsole --icon → 输出 dist\ZCodeTokenSpeed.exe；内容即此前四次验证过的命令行。规则：无用户明确指令不得打包（已记忆）。
- 2026-08-30 终（17）：**托盘「详情」弹窗**。菜单顺序：详情/显示/退出。on_detail 用独立只读连接取最近 50 条 + 今日汇总，生成静态快照 HTML（detail_html：时间/模型/供应商/速度/首字/生成时长/tokens输出+推理/状态，今日汇总标题行），webview.create_window 680x760 普通窗（带标题栏，可关可拖，非穿透、非置顶常驻——on_top 置顶）。主面板零改动。验证：--self-check、detail_html 真数据 10 行断言、--detail 钩子端到端（同托盘点击代码路径）主窗穿透保持 + 详情窗 680x760 在位；机制前置验证 create_window after start 可行。未打包（等用户指令）。
- 2026-08-30 终（18）：**详情面板改按会话聚合**（用户定标：不要逐条请求；会话用标题不用 id）。新增 session_stats(db, limit=30)：model_usage 按 session_id 分组（加权 总tokens÷总生成窗口，含回退行标 *），LEFT JOIN session 表取 title；detail_html 列改为 会话标题/速度/请求数/tokens/最近活动，保留今日汇总。验证：真数据 30 会话、标题中文正常、--self-check、--detail 端到端主窗穿透保持 + 详情窗在位。未打包（等指令）。
- 2026-08-30 终（19）：**详情窗实时刷新**。push_loop 每秒循环里对每个打开的详情窗重查 session_stats+today_summary，evaluate_js document.write(detail_html(...)) 整页重写（复用渲染函数，零逻辑重复）；窗口已关 → 异常吞掉并从列表移除。验证：--detail 端到端 15s（≥10 次刷新）窗口存活、零错误日志、主窗穿透保持。未打包（等指令）。
- 2026-08-30 终（20）：**详情窗会话 30 分钟活性窗口**（用户定标：30min 无新活动不显示）。session_stats 加 HAVING MAX(COALESCE(completed_at,started_at)) >= now-30min；进行中请求按 started_at 计入，长请求期间会话不消失。验证：真数据 30→1 会话（仅当前活跃会话）、断言无越窗泄漏、--self-check。未打包（等指令）。
- 2026-08-30 终（21）：**详情窗白底 + 首字 + 请求/分**。白底深字主题（#ffffff/#1f2937）；首字 = 会话内 AVG(first_token_at-started_at)；请求/分 = 请求数 ÷ 会话活跃跨度（last-first），单请求会话 0.0；速度色改 绿≥80/黄≥50/红<50（仅详情窗，简略面板仍 60/30 未动）。验证：真数据断言列齐全 + --self-check。未打包（等指令）。
- 2026-08-30 终（22）：**主面板同套 30 分钟活性窗口**（用户改标：久未使用的模型不再显示，推翻终 10 的"永久保留"）。push_loop 对 avg_by_model 结果按最新活动时间过滤（completed_at 空取 started_at，进行中请求不受影响）。验证：真数据 2 模型 → glm-5.3-flash（超时）被滤掉、当前活跃模型保留、--self-check。未打包（等指令）。
- 2026-08-30 终（23）：**主面板恢复 3 分钟活性窗口**（用户指正：30min 是我改错的，原设计是终 9 的 3 分钟）。push_loop cutoff 30min→3min；详情面板 30min 不变（终 20 用户原话定标）。验证：真数据只剩当前活跃 GLM-5.3-Flash、--self-check。未打包（等指令）。
- 2026-08-30 终（24）：**任务栏图标 + 面板常驻看门狗 + 重新打包**。① set_window_icon：LoadImageW(token_speed.ico) + WM_SETICON 大小双设，修源码运行时窗口/任务栏显示 python 默认图标的问题（exe 版本无害）；② panel_watchdog：窗口被销毁后 3s 内自动重建（含图标），实测 WM_CLOSE 被 closing 事件拦截本就不销毁，看门狗为销毁类路径兜底——进程活着面板必在；③ build.bat 重打包，exe 启动验证穿透 + WM_GETICON 有值。托盘详情/白底/首字/请求分/80-50配色/3min窗口 全部进包。
- 2026-08-30 终（25）：**舍弃无首字行，去掉 \***（用户定标：5 条里缺首字的直接不算，只算有精确首字的）。avg_by_model 重写：只收 calc 结果为精确首字（mark=""）的行，gen_ms 直接 sum(done-ft)；self-check 加"无首字行被舍弃"断言。真数据：GLM-5.3-Flash 34.4 无星（原 29.3*）。详情面板 session_stats 口径未动（仍含回退标 *）。未重新打包。
- 2026-08-30 终（26）：**简略面板不占任务栏**（用户定标：任务栏 T 图标只在详情面板打开时出现）。hover_loop exstyle 显式清 WS_EX_APPWINDOW(0x40000)——TOOLWINDOW 与 APPWINDOW 并存时后者强制显示任务栏按钮；详情窗为普通窗自带任务栏按钮，关闭即消失，符合"图标只在详情时出现"。逐 0.5s 实测 20 帧 ex=0x900A8 稳定无 APPWINDOW。坑记录：测量前必须清残留进程，否则 FindWindowW 抓到旧代码窗口得出假阳性。未重新打包。
- 2026-08-30 终（27）：**项目迁移 talk → C:\Users\木\Desktop\TPS**（用户定标：talk 内无关文件多）。迁移文件：token_speed.py / build.bat / token_speed.ico / token-speed-v2-plan.md / ZCodeTokenSpeed.spec / dist\ZCodeTokenSpeed.exe / 三个 test_*.py。build.bat 用 %~dp0 相对路径、图标用 __file__ 相对定位，均无需改代码。桌面「ZCode 速度.lnk」改指 TPS\dist\ZCodeTokenSpeed.exe。--self-check 新位置通过。talk 内遗留旧物（bench/benchmark/deeptutor 等）未动。
- 2026-08-30 终（28）：**TPS 重新打包 + 迁移收尾**。build.bat 在新位置重打包（含终 25 去星、终 26 任务栏隐藏），exe 启动验证：穿透/任务栏按钮隐藏/图标全部在位。talk 内项目残留清零（build 目录、__pycache__ 缓存删除），迁移完成。
- 2026-08-30 终（29）：**UI 全面重设计 = 方案 A「纸面印刷」（用户比稿三个亮色方向后选定；design_preview.html 留档比稿）**。全部改动在渲染层，窗口机制（穿透/把手/托盘/单实例/看门狗）零改动。改动项：① 简略面板 HTML 重写：暖白纸卡 #fffdf8 + 边框 #e6e2d8 圆角 12、发丝线 #f0ece1 行分隔、Bahnschrift 大数字 19px tabular、速度语义色亮色化（≥80 绿 #0a8f46 / ≥50 黄 #d97706 / <50 红 #dc2626，简略+详情统一 80/50 阈值）、每行 2px 速度条（满格=120 t/s）、供应商列改弹性填充（flex:1 1 0 + min-width:0，空间不足先截供应商、保模型名与数字）；② 详情窗渲染拆为 detail_page（外壳）+ detail_inner（活动区），push_loop 由每秒 document.write 整页重写改为只替换 #live.innerHTML——滚动位置不再每秒跳顶；③ 高度自适应改按 document.body.offsetHeight 实测（新卡有边框/内边距，#rows 偏高不再等于全高）；④ 亮色卡在深色壁纸需更实才可读：分层窗口 alpha 默认 105→150、悬停 185→220（把手 235 不变，可回调）；⑤ 清理：删 tkinter 时代无引用死代码（BG/CARD/LINE/FG/DIM/GOOD/MIDC/SLOW + speed_tag）；session_stats 的 tok 加 `or 0` 防 None 进千分位格式化；self_check 新增 detail_inner 渲染断言（原 avg_by_model 断言保留）。验证：--self-check OK；用真实函数生成快照页 Chrome 截图核对——详情窗 = 方案 A 版式（大数字统计行/发丝线会话列表/色条），简略面板名称完整+供应商填充+数字右对齐。⚠️ 未验证：真机运行（用户当前实例是旧 exe，单实例互斥下新旧不能并存）——`python token_speed.py` 或重打包后生效；未打包（等用户指令）。
- 2026-08-31 终（30）：**真机验收 + 去方框（用户定标：面板不要外框，直接一体颜色）**。① 真机验证通过：杀旧 exe 实例 → `python token_speed.py` 起新实例，截屏确认简略面板新 UI 渲染（dots3-note-prev / GLM-5.3-Flash 真实数据 + 语义色 + 速度条）、穿透/把手/托盘正常、窗口自愈透明模式正常；② 用户指出简略+详情都有"外框方框"——根因 = body/#live 的 border + border-radius + box-shadow（详情页 = 米色 #e9e5da 底上浮白色圆角卡）。修法：简略 body 删 border/radius，详情 body 与 #live 统一 #fffdf8、#live 删 border/radius/shadow 且 height:100vh——两面板均为整窗一体色无边框；③ 排障记录：GetDC 读分层窗口像素恒返 (240,240,240) 不可用作渲染判断（WebView2 画在独立合成面），以截屏为准；SetWindowPos 移动 frameless 窗被 pywebview 重置（终 11 已知坑，复现）；临时加 x,y 定位验完已还原。④ 关键机制：详情窗 CSS 外壳在创建时固化，push_loop 只换 #live——**样式改动后已开着的详情窗必须关掉从托盘重开**才生效。验证：--self-check OK、Chrome 渲染新详情页确认一体色无边框、真机简略面板（右上 GLM-5.3-Flash 31.2 绿）无边框。未打包（等用户指令）。
- 2026-08-31 终（31）：**源码热更新 + 重打包（用户定标：改了就要随时变，做不到就自动重打包——两者都做了）**。① 热更新：run_web 记源码 mtime，push_loop 每秒检查，变了就 hot_reload()——exec 磁盘源码（__name__="tps_hot" 防触发 main()）+ globals().update 换全部函数/常量 + w.load_html(HTML) 重载简略面板 + 各详情窗 load_html(detail_html(...))；LOG 全局在 exec 前保存、update 后还原。**边界：仅源码运行生效；exe 打包后主脚本不落盘，改源码对 exe 无效，仍需重打包**。② 修复 2 个热更 bug（真机验证抓到）：hot_reload 内赋值 LOG 缺 `global` 声明 → UnboundLocalError；restore 顺序错（update 会覆盖回 None）——已修。③ 修复打包启动即死：PyInstaller onefile 下 `os.path.getmtime(__file__)` FileNotFoundError（主脚本不在 _MEIPASS 落盘）→ src_file = __file__ if exists else None，exe 下热更整体禁用。④ build.bat 重打包 + 验证：exe detached 启动窗口在位（child 111MB webview 已加载）；最终态 = 源码实例常驻（热更活跃，touch mtime 验证窗口存活、weberr 无新错）。**后续工作流：改 UI → 保存 → 源码实例 1 秒内自动生效；要更新 exe 时跑 build.bat**。未发现遗留问题。
- 2026-08-31 终（32）：**新应用图标（用户定标 V1「等宽锐角 S-bolt」：闪电折线书写 S 骨架，比稿 process 见 design_icon.html/icon_candidates.png）**。① 图标生成链：SVG（128 viewBox：纸卡 #fffdf8 + 发丝边 #e9e5da + 朱红 #ff4d2e 折线 S-bolt）→ Chrome 无头 --default-background-color=00000000 出 256px 透明 PNG（断言：圆角外 alpha=0、卡底 (255,253,248)）→ Pillow `save(ico, sizes=6 档)` → token_speed_new.ico（16/32/48/64/128/256 全档）。② 旧 token_speed.ico 备份为 token_speed.ico.bak 后替换。③ 托盘图标改读同一枚 ico（PIL Image.open + resize 64，异常回退旧手绘圆圈 T）——托盘/窗口/快捷方式三处同一视觉。④ build.bat 加 `--add-data "token_speed.ico;."`：exe 内托盘从 _MEIPASS 读真图标（此前 exe 托盘只有手绘回退版），顺带让 set_window_icon 在 exe 下也能命中文件。⑤ build.bat 重打包（新 ico 嵌入 exe 资源）；源码实例重启，窗口在位。⚠️ 桌面 lnk 图标若显示旧图 = Windows 图标缓存，刷新桌面/重启 explorer 即新。
- 2026-08-31 终（33）：**详情窗图标修复 + 项目改名 Token Details（用户定标）**。① 详情窗左上角仍是旧 python 默认图标——根因：set_window_icon 只在启动时按主窗标题设一次，后建的详情窗从没被设。修法：set_window_icon 改常驻线程，EnumWindows 3s 一轮，凡标题 ∈ (TITLE, DETAIL_TITLE) 的可见窗口一律 WM_SETICON（后开详情窗/看门狗重建窗全覆盖）。exe 不受影响（图标来自 exe 资源）。② 项目改名 Token Details：新增 TITLE/DETAIL_TITLE 常量（HTML 是普通字符串不能 f-string，其 <title> 用同值字面量；detail_html 是 f-string 用 {DETAIL_TITLE}），替换全部 FindWindowW/create_window/托盘 tooltip/titles 元组；互斥量 ZCodeTokenSpeed_Mutex → TokenDetails_Mutex；docstring 更新。**关键约束：HTML <title> 必须与 TITLE 完全一致，否则 WebView2 document.title 覆盖窗口标题 → FindWindowW 全链失效（终 68 教训）**。③ build.bat：--name TokenDetails（dist\TokenDetails.exe），旧 ZCodeTokenSpeed.exe/.spec 成为孤儿待删；桌面「ZCode 速度.lnk」→「Token Details.lnk」指向新 exe（Token Monitor.lnk 是别的程序未动）。验证：--self-check OK；重启后 FindWindowW("Token Details")=True、旧标题窗消失、lnk 已重指。⚠️ 旧 exe 残留：dist\ZCodeTokenSpeed.exe + ZCodeTokenSpeed.spec 未删（等用户指令）。
