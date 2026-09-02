# Token Details v5 执行计划 — 工具切换（ZCode / Codex）

用户决定：①全局切换（面板+详情一起）；②Codex 与 ZCode 完全相同的计算方式与口径。
前置调研已验证：Codex 数据在 `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`；
`token_count` 事件带 `info.total_token_usage`（累计：input 含 cached / cached / cache_write / output / reasoning）；
`compacted` 事件 = 一次压缩；无 sqlite 用量表。

## 核心思路（最小改动）

适配层把近 8 天 rollout JSONL 解析成**与 ZCode model_usage 同构的内存 sqlite**（含 session 表），
所有现有查询（today_summary / today_by_model / week_daily / compact_stats / session_stats /
avg_by_model / fetch_recent）零改动复用；口径自动一致（计费缓存×0.1、7 日图、压缩 3 天活跃）。

字段映射：
- `token_count` 相邻事件差分 = 该段用量（累计值差分）；事件时间戳间隔 ≈ 生成窗口（估算，含工具/空闲）
- `input_tokens - cached_input_tokens` → input（对齐 zcode：input 不含缓存）；cached → cache_read；cache_write → cache_creation
- `compacted` → query_source='compact' 的 0 token 标记行（compact_stats 计数用，速度/总量自动排除）
- 会话标题 = basename(session_meta.payload.cwd)
- provider 一律 'Codex'；模型取最近 turn_context.payload.model

切换机制：详情窗头部加 `<select id="tool">`（ZCode/Codex），**Python 每秒 evaluate_js 读值**（复用现有轮询，
不加 js 桥）；cur_tool 全局变量，面板+详情+三页全部跟随。

## 改动（全部 token_speed.py）

1. [ ] `CODEX_SESSIONS/CODEX_CACHE` 常量 + `_iso_ms/_parse_rollout/codex_db()`（~100 行）
2. [ ] `today_by_model` 加 `AND model_id!=''`（滤掉 compact 标记行）
3. [ ] `detail_data(db, provs, tool)` 透传 tool；`page_spd` 平均首字对 codex 显示 "-"（无首字计时）
4. [ ] `detail_html` 头部加 tool 下拉
5. [ ] push_loop：轮询下拉值 → cur_tool → sdb/sprovs 切换数据源
6. [ ] on_detail / hot_reload 按 cur_tool 取数
7. [ ] self_check：fake rollout 解析断言（差分/去缓存/compact 标记/时长）

## 验证

- `--self-check`（rollout 单测）
- 离线：codex_db() 真实文件 → 行数/token 总量对账（数学上差分和=末次累计）
- 活窗 a11y：切 Codex → 页面数据变（当前 codex 停用 8 天，预期为空态但不崩）；切回 ZCode 恢复
- 重建 exe

## 边界

- Codex 速度为间隔估算（含空闲），口径上与 ZCode 同公式；首字无数据 → "-"
- codex 未安装/无文件 → 空库空态，不崩
- 解析失败单个文件 → errlog 跳过
