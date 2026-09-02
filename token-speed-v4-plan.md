# Token Details v4 执行计划 — 详情窗三页拆分 + 压缩记录

设计定稿参照: `preview_v4.html`（2026-08-31 用户确认，仅一处调整：总量页「按模型」块在「会话压缩」块之上）。
前置调研已验证: 压缩事件 = `model_usage.query_source='compact'`，库中现存 113 条，近 7 天 14 次 / 6 个会话。

## 1. 目标与范围

详情窗（DETAIL_TITLE，680×760）拆为三页，头部下拉框切换：

| 页 | 内容 |
|---|---|
| 总览 | 三卡（今日请求/平均速度/今日 tokens）+ 活跃会话·30 分钟（现行为原样） |
| 速度 | 三卡（今日平均速度/活跃模型/平均首字）+ 每模型速度·最近5条加权 + 会话速度·30分钟 |
| 总量 | 三卡（今日 tokens/今日请求/输出:推理）+ **按模型·今日** + **会话压缩·7天** + 今日逐时柱状图 |

**不动**：悬浮简略面板、托盘、看门狗、单实例、热更新机制、窗口尺寸、
`DETAIL_TITLE`（FindWindowW 图标锚点，页面名不进窗口标题）。

## 2. 数据口径（实现前定死，不实现中摇摆）

- **速度口径排除压缩**：`avg_by_model` / `session_stats` / 今日平均速度 / 会话行次数与 tok 均排除 `query_source='compact'`（压缩是慢速后台总结，非对话速度）。
- **总量口径包含压缩**：今日 tokens、按模型、逐时聚合含压缩产出（压缩确实烧 token）。
- **压缩次数只数 `status='completed'`**（库里 71 error / 6 cancelled，失败即没压缩成）。
- **7 天保留 = 查询时过滤** `completed_at >= now-7d`，无自有存储、无清理线程，过期自然消失。
- 排除压缩后速度数值会比现在略高——口径变化属预期，须写进变更文档。

## 3. 改动明细（全部在 token_speed.py）

### 3.1 查询层

- `today_summary(db)` 改条件聚合，一次返回：
  对话次数（非 compact）、tokens 总量（含 compact）、对话 tokens、对话生成窗口、
  `SUM(output)`、`SUM(reasoning)`、平均首字 ms。
  卡片映射：请求=对话次数；tokens=总量；平均速度=对话tokens÷窗口；输出:推理=两 SUM。
- `today_by_model(db)`：今日按 `model_id` 聚合 tokens+次数（含 compact），tokens 降序，占比条 = tokens/总。
- `today_hourly(db)`：今日逐时 tokens，按**本地时**小时桶（`completed_at` 为 epoch ms，需加本地偏移再 /3600000）。
- `compact_stats(db)`：
  ```sql
  SELECT COALESCE(s.title,u.session_id), COUNT(*), MAX(u.completed_at)
  FROM model_usage u LEFT JOIN session s ON s.id=u.session_id
  WHERE u.query_source='compact' AND u.status='completed' AND u.completed_at>=?
  GROUP BY u.session_id ORDER BY 3 DESC
  ```
  另返回总次数（块标题「7 天内 N 次」）。
- 排除压缩两个落点：`fetch_recent` SQL 加 `AND query_source!='compact'`（喂 avg_by_model/print_once/latest_by_model，零解包改动）；`session_stats` SQL 加同一条件。`COLS` 不动。

### 3.2 渲染层

- `detail_inner` 拆成 `page_ov / page_spd / page_tok` 三个渲染函数（dict 输入 → HTML，标题照旧 `html.escape`）。
- `detail_html` 变壳：`<select>`（总览/速度/总量）+ 三个 `<div class="page">`（默认仅总览可见）+ 一行 JS 切 `hidden`；样式沿用现有纸面色系，新增 select / share 条（朱红）/ 逐时条样式。
- 速度页每模型行**复用 push_loop 已算的 items**（悬浮面板同源数据，零重复计算）。
- 逐时柱状图 Python 侧拼 `<i style="height:%">`（24 桶 0..当前小时，峰值朱红），不引 JS 图表。

### 3.3 push_loop

每秒（仅当详情窗可见时）多查 today_summary/by_model/compact/hourly，
三个渲染函数的 HTML 打成一个 JSON，`evaluate_js` 一次塞三个容器；
外层滚动容器不被替换，滚动位置保留（沿用现 #live 模式）。

### 3.4 self_check

- `detail_inner` 断言改为三渲染函数断言（转义/数字/条宽各一条）。
- 加一条：compact 行不出现在 avg_by_model 输入（构造含 query_source 的假行——若解包不动则改为断言 fetch SQL 字符串，实现时取更朴素的那种）。

### 3.5 文档

- 实现当日追加 `changelog-2026-08-31.md`（次日实现则新建 `changelog-<日期>.md`）。
- 头注释 docstring：版本说明与「计划:」行指向本文件。
- `preview_v4.html` 保留为设计定稿，随仓库提交。

## 4. 执行顺序（可断点续做，做完一项勾一项）

1. [x] 查询层 5 个函数/改动 → `python token_speed.py --self-check`
2. [x] 三个渲染函数 + detail_html 壳 → self-check；源码实例热更 ~1s 生效
3. [x] push_loop 接线（三页 payload）→ `python token_speed.py --detail` 观察 4s 后弹窗、每秒刷新
4. [x] 验证（见 §5）→ 全过（2026-08-31，含活窗三页切换实测）
5. [x] 变更文档 + 头注释更新（changelog-2026-08-31.md 追加 v4 节）
6. [x] `build.bat` 重打包 exe（已预授权）+ dist 冒烟（PyInstaller 同参数直跑重建；exe `--detail` 启动，PrintWindow 像素检查详情窗/面板正常；速度页加权口径独立 SQL 复算一致 57.68=57.68）

## 5. 验证方式与成功标准

验证（本环境图片通道不可用，禁截图直读）：
- `--self-check` / `--once` 通过。
- PrintWindow + 像素统计：纸色底占比、三页各自区块存在性、下拉切换前后像素差异确认切换生效。
- 压缩块数字与直查 SQL 对账：当前基准 = 近 7 天 14 次 / 6 会话（「测试」5 次居首）。
- 速度页抽查一个模型：手算最近 5 条加权 tps 与页面一致。

成功标准：
- 三页即时切换、每秒刷新、滚动不跳；
- 压缩块与 SQL 对账一致；
- 悬浮面板行为与现在无肉眼可见差异（仅速度数值因排除压缩略升）。

## 6. 风险与边界

- 压缩数据依赖 ZCode 自家库保留 7 天内行（其清理周期远长于 7 天，低风险）。
- 下拉选择不持久化：每次打开默认总览（`html=` 页面 localStorage 不可靠，热更后同样回到默认——开发路径，可接受）。
- `session.time_compacting` / `session_entry` 均无压缩计数，勿走那条路（已排查，全库 0 值/无事件）。
