# 变更记录 2026-08-31 — 全项目代码审查修复

审查范围：HEAD 全量（OCR 委托规则 + 逐文件人工审查）。共 8 项发现，全部修复。

## token_speed.py

| # | 改动 | 原因 |
|---|------|------|
| 1 | `provider_names()` 硬编码 `C:/Users/木/...` → 改用 `CFG` 常量 + `_home` 拼接 | 换机/换用户 provider 名静默退化；`CFG` 原本是死常量 |
| 2 | `json.load(open(...))` → `with open` | 句柄规范 |
| 3 | `detail_inner` 会话标题加 `html.escape()` | 标题含 `< > &` 会毁掉详情窗渲染；self-check 增加转义断言 |
| 4 | 新增 `errlog(tag, exc)` 统一 4 处调试日志写入，超 1MB 截断 | weberr.log 无限增长（持续故障下 1 行/秒） |
| 5 | 删除死代码：`if fails > 3: pass`、push_loop 的 `fails` 计数、run_web 顶层无用 `dpi`、`import io as _io`、`detail_inner/detail_html` 无用的 `provs` 参数（含全部调用点） | 死代码 |
| 6 | `--log-file` 缺参数值时显式报错退出 | 原来抛 IndexError，noconsole exe 下静默崩溃 |

## 测试 / 打包

| # | 改动 | 原因 |
|---|------|------|
| 7 | 删除 `test_toggle.py`、`test_drag.py`、`ZCodeTokenSpeed.spec` | 前两个测的是已删除的 Ctrl+Alt+T 热键功能；spec 是旧项目名的重复品且 build.bat 不消费 |
| 8 | `test_grip_drag.py`：窗口标题改 `Token Details`，启动路径改脚本同目录 | 旧标题/旧路径（Desktop\talk）导致测试必然失败 |

## 验证

- ✅ `py_compile` 通过；`--self-check` OK（含新转义断言）；`--once` 正常读库出数
- ✅ `test_grip_drag.py` PASS：把手悬停解锁穿透 → 拖动面板 (90,54) → 离开热区恢复穿透
- ✅ 详情窗实测：live 会话标题正常渲染，亮色底 66% 符合设计，统计数字正确；push_loop 运行期间 errlog 无新异常
- ✅ `build.bat` 重建 exe 成功，`dist\TokenDetails.exe` 已启动并接管面板窗口（PID 30248）
- ✅ errlog 截断分支实测：临时文件 1,000,001B → 截断为 36B 且新条目在；小文件不截断、旧内容保留 + 追加
- ✅ exe 内详情窗实测：`TokenDetails.exe --detail`（与托盘点击同代码路径）真启动，PrintWindow 抓窗验证——亮色底 92%，标题/统计/列表区均有内容，配色与设计一致（ink #1a1d22 / mid 橙）；WM_CLOSE 后详情窗关闭、面板存活（push_loop 清理路径正常）

---

# v4 详情窗三页拆分 + 会话压缩记录（token-speed-v4-plan.md 执行）

计划：token-speed-v4-plan.md；设计定稿：preview_v4.html。改动全部在 token_speed.py。

## 数据口径（v4 定死）

| 口径 | 规则 |
|------|------|
| 速度 | 排除 `query_source='compact'`（后台压缩总结不是对话速度）→ fetch_recent / session_stats / 今日平均速度 |
| 总量 | 含压缩产出 → 今日 tokens / 按模型 / 逐时 / 输出:推理 |
| 压缩次数 | 只数 `status='completed'`（库里 71 error / 6 cancelled，失败即没压缩成） |
| 7 天保留 | 查询时过滤 completed_at，无自有存储、无清理线程 |

## token_speed.py

| # | 改动 | 说明 |
|---|------|------|
| 1 | `fetch_recent` / `session_stats` 加 `WHERE query_source!='compact'` | 速度口径排除压缩，COLS 解包不动 |
| 2 | `today_summary` 改条件聚合，返回 dict（n/tok/talk_tok/talk_ms/out/reason/ttft） | 一次查询供全部三卡；新增输出:推理拆分与平均首字 |
| 3 | 新增 `today_by_model` / `today_hourly`（本地时 24 桶）/ `compact_stats`（7 天） | 总量页三块的数据源 |
| 4 | `detail_inner` 删除，拆为 `page_ov / page_spd / page_tok` + `_sc/_k/_cards/_sess_row` | 详情三页各自渲染 |
| 5 | `detail_html` 变三页壳：头部 `<select>`（总览/速度/总量）+ 三个 page 容器，JS 一行切 hidden | 头部/下拉不随秒刷新重写，选择与滚动位置保留 |
| 6 | 新增 `detail_data(db, provs)` | 面板 items 与详情三页同源一次取数（push_loop/打开/热更共用） |
| 7 | push_loop：详情可见时三页 HTML 打包 evaluate_js 逐容器替换 | 外层滚动容器不被替换 |
| 8 | self_check 重写断言：三页渲染 + 内存库验证 compact 三口径 | fetch_recent 排除 / today_summary 双轨 / compact_stats 只数 completed |

## 验证（全部通过）

- ✅ `py_compile` / `--self-check`（含新口径断言）/ `--once` 正常出数
- ✅ 离线三页渲染与直查 SQL 对账一致（压缩块总数、会话标题、次数逐条核对）
- ✅ 源码实例替换后实测详情窗（--detail 钩子，与托盘点击同代码路径）：
  总览默认页三卡+活跃会话正常；下拉切「速度」→ 三卡+每模型速度+会话速度；
  切「总量」→ 三卡+按模型(4 行)+会话压缩·7 天 4 次(4 会话)+今日逐时；切回「总览」正常，
  期间每秒刷新不中断（数值持续跳动）
- ✅ PrintWindow 像素检查：详情窗纸面底 83-84%、墨色文字、朱红像素（总览 260 → 总量 4062，占比条/峰值柱在渲染）
- ✅ errlog 运行全程无新错误（尾 3 条为昨晚旧记录）
- ✅ exe 冒烟：PyInstaller（与 build.bat 同参数）重建 dist\TokenDetails.exe（15:41），exe `--detail` 启动，PrintWindow 详情窗 680x760 纸面底 83% / 面板 94% 正常；速度页加权口径独立 SQL 复算一致（57.68 = 57.68）
- v4 后续微调：详情窗默认 680x760 → 340x380（用户反馈太大，减半）；重建 exe 并以 --detail 实测窗口尺寸 340x380 正常。
- 微调：详情窗宽度 340 → 510（x1.5，用户要求加宽）；重建 exe 实测 510x380 正常。
- 微调：详情窗高度 380 → 570（x1.5），最终 510x570；重建 exe 实测正常。
- 微调（用户看图反馈）：速度页会话行加模型名（session_stats 增加该会话最新一条非 compact 完成请求的 model_id 子查询），「N 次」改为「N.N 次/分」（rpm 本就有，渲染换掉 cnt）；self-check 补转义/格式断言；重建 exe 实测速度页行显示「glm-5.3-flash · 首字 10.4s · 0.9 次/分 · 64.5*」。
- 微调：速度页会话行模型名加粗（font-weight:600）；重建 exe 冒烟正常。
- 微调（速度页三卡 + 压缩保留规则）：①「今日平均速度」→「平均Token速度」、「平均首字」→「平均首字速度」；②「活跃模型」→「平均请求速度」= 今日对话请求数 ÷ 今日首末请求跨度分钟（today_summary 增 MIN/MAX 完成时间，rpm 字段）；③压缩块保留规则从「压缩事件 7 天内」改为「会话 3 天内有新对话才显示，超期整行丢弃，次数=该会话全部完成压缩」，self_check 补过期会话丢弃断言。重建 exe 冒烟正常。
- 微调：总量页按模型行加供应商（today_by_model 按 model_id+provider_id 分组，经 provider_names 映射可读名）；离线对账 + 活窗 a11y 实测（glm-5.3-flash/B.ai、dots3-note-prev/Dots 等）。
- 数据口径核实（用户问询）：今日请求/tokens 为 completed_at ≥ 本地零点的时间过滤直查 ZCode 库，与工具启动时间无关——直接 SQL 与管线数字一致（428 次/416,031 tok，今天第一条请求 00:01，远早于工具启动）；悬浮面板（3 分钟）与活跃会话列表（30 分钟）是设计上的实时窗口，非全天口径。
- 修复（用户看图反馈）：今日逐时柱状图不显示——柱子 <i> 标签漏了 </i> 闭合，24 根柱被浏览器嵌套解析成一个元素；补闭合 + self_check 加闭合断言（数据本身一直有：24 桶全天 token）。重建 exe 冒烟正常。
- 微调（用户问 5000 万 vs 43 万后）：总量页第三卡「输出 : 推理」→「输入输出」（输入/输出，/ 分隔）——此前只算 output+reasoning（今日 43 万），未显示的输入侧才是大头（今日输入 6,247 万 + 缓存读 5,311 万，今日全口径 1.16 亿；历史合计 74.8 亿）；_k 增 M 档；self_check 断言同步。重建 exe。
- 微调：总量页第三卡「输出 : 推理」→「输入输出」（输入/输出，/ 分隔，_k 增 M 档显示 62.5M 级输入量）——背景：用户问 5000 万 vs 43 万，查明 43 万=今日输出+推理，今日输入 6,247 万+缓存读 5,311 万（全口径 1.16 亿），历史合计 74.8 亿；self_check 内存表同步加 input_tokens 列。重建 exe。
- 微调（用户：想要这台电脑总共用了多少）：总量页三卡改为「总 TOKENS（全历史全口径：输入+输出+推理+缓存，7.5B）/ 总请求（24,516）/ 今日输入输出」；新增 total_summary()，_k 增 B 档；今日口径保留在总览页。重建 exe。
- 微调（用户：不要全部历史，都要今天的总量）：总量页三卡定为「今日 TOKENS（今日全口径：输出+推理+输入+缓存）/ 今日请求 / 输入输出（输入侧=输入+缓存，输出侧=输出+推理，/ 分隔）」；删除全历史 total_summary；修复 tok+reason 重复计数；self_check 内存表加缓存两列。重建 exe。
- 微调：总览页「今日 tokens」卡同步改全口径（输出+推理+输入+缓存，与总量页一致，M 档显示）；桌面 lnk 核实指向 dist\TokenDetails.exe 无误。重建 exe --detail 验证。
- 微调（用户：项目里和 token 有关的全部换今天全口径）：today_summary 的 tok、today_by_model、today_hourly 三处 SUM 改为输出+推理+输入+缓存；速度口径 talk_tok 保持输出+推理（除以生成窗口才是 t/s）。重建 exe。
- 微调（用户看图反馈）：总量页「今日逐时」→「近 7 日 token」柱状图，7 根柱（本地日界、全口径、含 compact），每柱顶部标当日总量（_k 格式），下方日期轴 08-25…08-31；today_hourly 删除换 week_daily；self_check 断言同步。重建 exe。
- 微调（用户）：总览页活跃会话行去掉「N tok」（会话级窄口径误导）；session_stats 相应删 SUM(output+reasoning) 列与 tok 字段（页面不再消费）。重建 exe。
- 微调（用户）：总览页活跃会话行去掉「N tok」显示（session_stats 的 SUM 保留——加权速度仍需要它，只删展示层）。重建 exe。
- 微调（用户）：计量单位改中文——_k 的 M/B/k 全换 亿/万（140.7M→1.41亿、456k→45.6万、70000000→7000万，去尾零）；t/s、次/分 保留。self_check 断言同步。重建 exe。
- 微调（用户）：计量单位改中文——_k 的 M/B/k 全换 亿/万（140.7M→1.41亿、456k→45.6万、70000000→7000万，去尾零）；t/s、次/分 保留。self_check 断言同步（700.15 舍入为 700.1）。重建 exe。
- 修复（用户：显示 279M 而非 7000 多万）：今日 TOKENS 卡重复计数——tok 已含输入/缓存，卡片公式又加一遍 inp+cache（279M≈2×140M）；改为直接显示 tok。中文单位已上线。另查明用户 7000 多万≈计费口径（缓存读×0.1 折算 83.3M），raw 口径 1.46 亿与之差异属正常，是否切计费口径待用户定。重建 exe。
- 微调（用户拍板）：今日 TOKENS 切计费口径——缓存读×0.1（CACHE_BILL 常量，通用 1 折），总览+总量两卡与近 7 日柱状图同步（SQL 侧直接折算）；输入输出卡仍为原始 token（计费与原始同值域不同，不折）。重建 exe。
- 微调（用户拍板）：今日 TOKENS 切计费口径——缓存读×0.1（CACHE_BILL 常量，通用 1 折），总览+总量两卡与近 7 日柱状图同步（SQL 侧直接折算）。重建 exe。
- 微调（用户）：全部 token 显示统一计费口径（缓存读×0.1）——按模型今日、输入输出卡的输入侧（输入+缓存×0.1）、--once CLI 输出同步；速度口径 talk_tok 保持原始（除生成窗口才是 t/s）。重建 exe。
- v5 工具切换（token-speed-v5-plan.md）：详情窗头部新增工具下拉（ZCode/Codex），push_loop 每秒轮询下拉值切数据源（不加 js 桥），面板/详情/三页全局跟随。Codex 适配层：近 8 天 rollout JSONL → 与 zcode model_usage 同构的内存 sqlite（token_count 相邻差分=段用量、input 拆 cached 对齐口径、compacted→compact 标记行、会话标题=cwd 基名），全部现有查询/口径零改动复用；codex 无首字计时 → 平均首字卡显示 "-"；today_by_model 滤空模型。验证：self_check 假 rollout 单测 + 真实文件差分恒等式对账（每文件 output 和=末次累计，33463/38680/12691/11698 全对）+ 活窗 a11y 双向切换（Codex 空态正确、切回 ZCode 数据恢复）。exe 已重建。
- 微调：①面板 running 中的模型即时显示「运行中…」（此前要等首条完成落库才出现，切换模型后看起来"很慢"）；②速度页头部新增「按模型/按会话」分类下拉（仅速度页可见），按会话行显示计费用量（_k 格式）；self_check 同步。重建 exe。
- 修正：分类切换（按模型/按会话）属总量页而非速度页——速度页恢复双段原样；总量页头部「按模型/按会话」下拉控制今日分类块，按会话为今日各会话计费用量+占比条（新查询 today_sessions）；速度页按会话行仍显示计费用量。push_loop 轮询 tcat 值。self_check 同步。重建 exe。
- 修正：分类切换（按模型/按会话）归总量页——速度页恢复双段原样；总量页「按会话 · 今日」为各会话计费用量+占比条（today_sessions 新查询），push_loop 轮询 tcat；self_check 同步。重建 exe。
- 修复（用户：正在运行的模型面板不显示）：ZCode 回合结束才写 model_usage 且从不写 running 行——上一轮 running 补丁对 ZCode 无效。改用实时信号 live_model()：message 表流式落库（实测回合中每秒增长），取最新 assistant 消息的 modelID，未出现在面板列表即插「运行中…」行（复用 run:1 渲染）；codex 缓存库无 message 表返回 None。self_check 补断言。重建 exe。
