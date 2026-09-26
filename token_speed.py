# -*- coding: utf-8 -*-
"""Token Details（原 ZCode Token 速度监控）v4 — 图形窗口

用法:
  python token_speed.py              图形窗口（悬浮面板 + 详情三页：总览/速度/总量）
  python token_speed.py --once       打印一帧数据后退出
  python token_speed.py --self-check 跑 tps 计算与三页渲染断言

数据源: ~/.zcode/cli/db/db.sqlite 的 model_usage 表（ZCode 回复完成后落库）
口径: 速度统计排除 compact（后台压缩总结），总量统计包含；压缩记录=query_source='compact'
计划: token-speed-v4-plan.md
"""
import ctypes
import html
import os
import json
import sqlite3
import sys
import time
from datetime import datetime

_home = os.path.expanduser("~")
DB = _home + "/.zcode/cli/db/db.sqlite"
CFG = _home + "/.zcode/cli/config.json"
COLS = ("id, provider_id, model_id, status, started_at, first_token_at, "
        "completed_at, duration_ms, output_tokens, reasoning_tokens")
TABLE_N = 50   # 表格行数
CACHE_BILL = 0.1  # 缓存读按 1 折计费（供应商通用口径），ponytail: 若换供应商比例不同再提成配置
TOK_PER_CHAR = 0.6  # 字符→token 估算系数（2026-09-23 对回报正常的模型标定，0.3~1.6 的中位）
CHART_N = 20   # 柱状图条数
LOG = None  # --log-file PATH：内容变化时追加一帧，测试观察口
TITLE = "Token Details"          # 主窗/托盘名（HTML <title> 必须与其一致，FindWindowW 锚点）
DETAIL_TITLE = "Token Details 详情"
ERRLOG = r"C:/Users/Public/weberr.log"  # 调试日志，errlog() 内超 1MB 截断


def errlog(tag, exc):
    try:
        if os.path.exists(ERRLOG) and os.path.getsize(ERRLOG) > 1_000_000:
            open(ERRLOG, "w").close()
        with open(ERRLOG, "a", encoding="utf-8") as f:
            f.write(f"[{datetime.now().strftime('%H:%M:%S')}] [{tag}] "
                    f"{type(exc).__name__}: {exc}\n")
    except Exception:
        pass


TRACE = r"C:/Users/Public/panel_trace.log"


def trace(text):
    """面板行集合每次变化记一行快照——转瞬即逝的显示事后可查（超 1MB 清空）。"""
    try:
        if os.path.exists(TRACE) and os.path.getsize(TRACE) > 1_000_000:
            open(TRACE, "w").close()
        with open(TRACE, "a", encoding="utf-8") as f:
            f.write(f"[{datetime.now().strftime('%m-%d %H:%M:%S')}] {text}\n")
    except Exception:
        pass


def log_frame(text):
    if not LOG:
        return
    with open(LOG, "a", encoding="utf-8") as f:
        f.write(f"[{datetime.now().strftime('%H:%M:%S')}]\n{text}\n")


def connect():
    # 连接在主线程创建、loop 子线程查询：check_same_thread=False（实际单线程串行使用）
    db = sqlite3.connect(f"file:{DB}?mode=ro", uri=True, check_same_thread=False)
    db.execute("PRAGMA busy_timeout=2000")
    return db


def fetch_recent(db, limit=CHART_N):
    """最近 limit 条请求，完成时间新→旧。id 列是 TEXT 不可排序，按时间排。
    排除 compact：后台压缩总结不是对话速度（v4 口径）。"""
    return db.execute(
        f"SELECT {COLS} FROM model_usage WHERE query_source!='compact' "
        f"ORDER BY COALESCE(completed_at, started_at) DESC LIMIT ?",
        (limit,)).fetchall()


def _day0():
    return int(datetime.now().replace(hour=0, minute=0, second=0,
                                      microsecond=0).timestamp() * 1000)


def today_summary(db):
    """今日汇总，一次条件聚合。速度口径排除 compact（talk_*），总量口径含 compact。"""
    (n, tok, talk_tok, talk_ms, out, inp, reason, cache, ttft_ms, min_t, max_t) = db.execute(
        """SELECT COUNT(CASE WHEN query_source!='compact' THEN 1 END),
                  COALESCE(SUM(output_tokens+reasoning_tokens+input_tokens
                               +cache_creation_input_tokens+cache_read_input_tokens), 0),
                  COALESCE(SUM(CASE WHEN query_source!='compact'
                                    THEN output_tokens+reasoning_tokens END), 0),
                  COALESCE(SUM(CASE WHEN query_source!='compact'
                                    THEN CASE WHEN started_at IS NOT NULL
                                                   AND completed_at IS NOT NULL
                                                   AND completed_at > started_at
                                              THEN completed_at - started_at
                                              ELSE duration_ms END END), 0),
                  COALESCE(SUM(output_tokens), 0),
                  COALESCE(SUM(input_tokens), 0),
                  COALESCE(SUM(reasoning_tokens), 0),
                  COALESCE(SUM(cache_creation_input_tokens+cache_read_input_tokens), 0),
                  AVG(CASE WHEN query_source!='compact' AND first_token_at IS NOT NULL
                                AND started_at IS NOT NULL AND first_token_at >= started_at
                           THEN first_token_at - started_at END),
                  MIN(CASE WHEN query_source!='compact' THEN completed_at END),
                  MAX(CASE WHEN query_source!='compact' THEN completed_at END)
           FROM model_usage WHERE status='completed' AND completed_at >= ?""",
        (_day0(),)).fetchone()
    rpm = 0.0
    if n and min_t and max_t and max_t > min_t:  # 平均请求速度 = 次数 ÷ 活跃跨度(分)
        rpm = n * 60000.0 / (max_t - min_t)
    return {"n": n, "tok": tok, "talk_tok": talk_tok, "talk_ms": talk_ms,
            "out": out, "inp": inp, "reason": reason, "cache": cache,
            "ttft": ttft_ms / 1000.0 if ttft_ms else None, "rpm": rpm}


def provider_names():
    """provider_id -> 可读名。cli config.json（providers 字典）+
    v2 provider_config.json（providerRules 列表，2026-09 ZCode 起新落点）。"""
    names = {}
    cfg_paths = (CFG, _home + "/.zcode/v2/provider_config.json")
    for path in cfg_paths:
        try:
            with open(path, encoding="utf-8") as f:
                cfg = json.load(f)
        except Exception:
            continue
        # 新结构：{config:{providerConfigRules:{providerRules:[{providerId,providerName}]}}}
        rules = (((cfg.get("config") or {}).get("providerConfigRules") or {})
                 .get("providerRules"))
        if isinstance(rules, list):
            for r in rules:
                if isinstance(r, dict) and r.get("providerName"):
                    names[r["providerId"]] = r["providerName"]
            continue
        # 旧结构：{providers: {id: {name}}}（可能直接是列表）
        provs = cfg.get("providers") or cfg.get("provider") or {}
        if isinstance(provs, list):
            provs = {p.get("id", "?"): p for p in provs if isinstance(p, dict)}
        for pid, p in provs.items():
            if isinstance(p, dict) and p.get("name"):
                names[pid] = p["name"]
    return names


# ---- Codex 适配层：rollout JSONL → zcode 形状的 model_usage，全部查询零改动复用 ----
CODEX_SESSIONS = os.path.expanduser("~/.codex/sessions")
CODEX_CACHE = {"db": None, "mtimes": None}  # mtime 集合不变则复用，变化则全量重建


def _iso_ms(s):
    return int(datetime.fromisoformat(s.replace("Z", "+00:00")).timestamp() * 1000)


def _parse_rollout(mdb, path):
    """单个 Codex rollout → model_usage 行 + session 行。token_count 是累计值，
    相邻事件差分=该段用量，事件间隔≈生成窗口（估算，含工具/空闲时间）；
    input 拆掉 cached 对齐 zcode 语义（input 不含缓存）。compacted→compact 标记行。"""
    sid, model, prev_ts, prev_tot = "", "", None, None
    rows = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            try:
                d = json.loads(line)
            except ValueError:
                continue
            t = d.get("type")
            p = d.get("payload") or {}
            ts = _iso_ms(d["timestamp"]) if d.get("timestamp") else None
            if t == "session_meta":
                sid = p.get("id") or ""
                title = os.path.basename((p.get("cwd") or "").rstrip("\\/")) \
                    or sid[:8] or "?"
                if sid:
                    mdb.execute("INSERT OR REPLACE INTO session (id, title) VALUES (?,?)",
                                (sid, title))
            elif t == "turn_context":
                model = p.get("model") or model or "?"
            elif t == "compacted" and sid and ts:
                rows.append(("c", "", sid, ts, ts, {}))
            elif t == "event_msg" and p.get("type") == "token_count" and sid and ts:
                tot = (p.get("info") or {}).get("total_token_usage") or {}
                base = prev_tot or {k: 0 for k in tot}
                dlt = {k: max(0, tot.get(k, 0) - base.get(k, 0)) for k in tot}
                if any(dlt.values()):
                    start = prev_ts if prev_ts is not None else ts
                    rows.append(("m", model, sid, start, ts, dlt))
                prev_ts, prev_tot = ts, tot
    for kind, mdl, s2, start, ts, dlt in rows:
        if kind == "c":
            mdb.execute("INSERT INTO model_usage VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                        ("", s2, "codex", "", "completed", ts, ts, ts, 0,
                         0, 0, 0, 0, 0, "compact", "interactive"))
        else:
            mdb.execute("INSERT INTO model_usage VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                        ("", s2, "codex", mdl, "completed", start, start, ts,
                         max(0, ts - start),
                         dlt.get("output_tokens", 0),
                         max(0, dlt.get("input_tokens", 0)
                             - dlt.get("cached_input_tokens", 0)),
                         dlt.get("reasoning_output_tokens", 0),
                         dlt.get("cache_write_input_tokens", 0),
                         dlt.get("cached_input_tokens", 0), "main_turn",
                         "interactive"))


def codex_db():
    """近 8 天 Codex rollout → 内存 sqlite；sessions 目录不存在/无文件返回空库。"""
    cutoff = time.time() - 8 * 86400
    files = {}
    for root, _dirs, names in os.walk(CODEX_SESSIONS):
        for n in names:
            if n.endswith(".jsonl"):
                p = os.path.join(root, n)
                try:
                    m = os.path.getmtime(p)
                except OSError:
                    continue
                if m >= cutoff:
                    files[p] = m
    if CODEX_CACHE["db"] is not None and files == CODEX_CACHE["mtimes"]:
        return CODEX_CACHE["db"]
    mdb = sqlite3.connect(":memory:", check_same_thread=False)
    mdb.execute("CREATE TABLE model_usage (id TEXT, session_id TEXT, provider_id TEXT,"
                " model_id TEXT, status TEXT, started_at INTEGER, first_token_at INTEGER,"
                " completed_at INTEGER, duration_ms INTEGER, output_tokens INTEGER,"
                " input_tokens INTEGER, reasoning_tokens INTEGER,"
                " cache_creation_input_tokens INTEGER, cache_read_input_tokens INTEGER,"
                " query_source TEXT, task_type TEXT DEFAULT 'interactive')")
    mdb.execute("CREATE TABLE session (id TEXT PRIMARY KEY, title TEXT, time_archived INTEGER)")
    for p in sorted(files):
        try:
            _parse_rollout(mdb, p)
        except Exception as e:
            errlog("codex-parse", e)
    CODEX_CACHE["db"], CODEX_CACHE["mtimes"] = mdb, files
    return mdb


def _est_map(db, rows):
    """无回报模型的 token 估算：usage行id -> 估算输出 token 数。
    某些供应商 API 不回报输出 usage（如 Agnes：数百条记录 output 恒 0），
    速度无法按精确值计算——用该请求 part 正文字符数 × TOK_PER_CHAR 折算。
    数据驱动判定（不点名模型）：窗口内 ≥3 条完成行且输出回报合计 <20 tok
    才算"无回报"，之后换供应商回报正常会自动回到精确值。
    usage.id 内嵌 message.id（usage_model_<source>_<msgid>_<n>，2026-09 实测）；
    part 表异常/无 msg id 时该模型自然退回无估算，不报错。"""
    stat = {}  # model -> [完成数, 输出回报合计]
    for r in rows:
        if r[3] == "completed":
            s = stat.setdefault(r[2], [0, 0])
            s[0] += 1
            s[1] += (r[8] or 0) + (r[9] or 0)
    silent = {m for m, (n, tot) in stat.items() if n >= 3 and tot < 20}
    if not silent:
        return {}
    mid_of = {}  # usage_id -> message_id
    for r in rows:
        if r[2] in silent and r[0]:
            i = r[0].find("msg_")
            if i >= 0:
                mid_of[r[0]] = r[0][i:].rsplit("_", 1)[0]
    try:
        c2t = {}
        mids = sorted(set(mid_of.values()))
        for j in range(0, len(mids), 400):  # IN 子句分批
            chunk = mids[j:j + 400]
            c2t.update(db.execute(
                "SELECT p.message_id, CAST(COALESCE(SUM("
                "LENGTH(COALESCE(json_extract(p.data,'$.text'),''))"
                "+LENGTH(COALESCE(json_extract(p.data,'$.state.input'),''))"
                "),0)*? AS INT) FROM part p WHERE p.message_id IN (%s)"
                " GROUP BY 1" % ",".join("?" * len(chunk)),
                (TOK_PER_CHAR, *chunk)).fetchall())
    except sqlite3.Error:
        return {}  # part 表缺失（codex 内存库等）→ 无估算
    return {uid: c2t[mid] for uid, mid in mid_of.items() if c2t.get(mid)}


def _elapsed_ms(row):
    """请求完整耗时：从模型请求开始到完成，贴近用户等待体感。
    duration_ms 是旧库/异常时间戳的兜底。"""
    started, done, dur = row[4], row[6], row[7]
    if started and done and done > started:
        return done - started
    return dur or 0


def calc(row, est=None):
    """row -> (tps, ttft_s, mark)。tps 使用请求完整耗时，贴近体感。
    est：无回报模型的估算 token 表（见 _est_map），真实回报恒 0 时替代。"""
    (_id, _prov, _model, status, started, ft, done, dur, out, reason) = row
    if status != "completed" or not done:
        return None
    tok = (out or 0) + (reason or 0)
    if status == "completed" and not tok and est:
        tok = est.get(_id, 0)
    elapsed = _elapsed_ms(row)
    if not tok or elapsed <= 0:  # 供应商未回报 usage 或缺少有效时长
        return None
    ttft = (ft - started) / 1000.0 if ft and started and ft >= started else None
    return tok * 1000.0 / elapsed, ttft, ""


def fmt_cells(row, provs, est=None):
    """row -> (时间, 模型, 供应商, 速度str, tps数值, 首字, tokens, status)"""
    (_id, prov_id, model, status, started, ft, done, dur, out, reason) = row
    prov = provs.get(prov_id, (prov_id or "?")[:8])
    when = datetime.fromtimestamp(done / 1000).strftime("%H:%M") if done else "-"
    tok_s = f"{out or 0}+{reason or 0}"
    if status != "completed":
        return when, model or "?", prov, status, 0.0, "-", tok_s, status
    c = calc(row, est)
    if c is None:
        return when, model or "?", prov, "无数据", 0.0, "-", tok_s, status
    tps, ttft, mark = c
    return (when, model or "?", prov, f"{tps:.1f}{mark}", tps,
            f"{ttft:.1f}s" if ttft is not None else "-", tok_s, status)



def self_check():
    r = ("x", "p", "m", "completed", 1000, 2000, 3000, 2500, 500, 0)
    tps, ttft, mark = calc(r)
    assert abs(tps - 250) < 0.01 and abs(ttft - 1.0) < 0.01 and mark == ""
    # 体感口径回归：首字后仍需 1 秒，但完整请求耗时 10 秒 → 50 t/s，不得算成 500 t/s
    r_wall = ("x", "p", "m", "completed", 1000, 2000, 11000, 10000, 500, 0)
    tps_wall, _, _ = calc(r_wall)
    assert abs(tps_wall - 50) < 0.01
    r2 = ("x", "p", "m", "completed", 1000, None, 3000, 2000, 500, 0)
    tps2, ttft2, mark2 = calc(r2)
    assert abs(tps2 - 250) < 0.01 and ttft2 is None and mark2 == ""
    assert calc(("x", "p", "m", "error", 1000, None, 3000, 100, 10, 0)) is None
    assert calc(("x", "p", "m", "completed", None, None, None, 0, 500, 0)) is None
    assert calc(("x", "p", "m", "completed", 1000, 2000, 3000, 0, 0, 0)) is None  # 空 usage 不算速度
    # 无回报模型估算（Agnes 类供应商）：完成行 output 恒 0 → part 正文字符×TOK_PER_CHAR
    sdb = sqlite3.connect(":memory:")
    sdb.execute("CREATE TABLE model_usage (id, session_id, provider_id, model_id, status,"
                " started_at, first_token_at, completed_at, duration_ms, output_tokens,"
                " input_tokens, reasoning_tokens, cache_creation_input_tokens,"
                " cache_read_input_tokens, query_source)")
    sdb.execute("CREATE TABLE part (message_id TEXT, data TEXT)")
    for i in range(4):  # 无回报模型：4 条完成行 output=0，正文各 100 字符 → 估算 60 tok/条
        sdb.execute("INSERT INTO model_usage (id, session_id, provider_id, model_id, status,"
                    " started_at, first_token_at, completed_at, output_tokens,"
                    " reasoning_tokens, query_source)"
                    " VALUES (?,?,?,?,?,?,?,?,?,?,?)",
                    (f"usage_model_main_turn_msg_s{i}_0", "s", "p", "silent", "completed",
                     1000 + i * 5000, 2000 + i * 5000, 3000 + i * 5000,
                     0, 0, "main_turn"))
        sdb.execute("INSERT INTO part VALUES (?, ?)",
                    (f"msg_s{i}", json.dumps({"text": "x" * 100})))
    sdb.execute("INSERT INTO model_usage (id, session_id, provider_id, model_id, status,"  # 有回报模型
                " started_at, first_token_at, completed_at, output_tokens,"
                " reasoning_tokens, query_source) VALUES (?,?,?,?,?,?,?,?,?,?,?)",
                ("usage_model_main_turn_msg_r0_0", "s", "p", "reported", "completed",
                 1000, 2000, 3000, 50, 0, "main_turn"))
    srows = sdb.execute(
        "SELECT id, provider_id, model_id, status, started_at, first_token_at,"
        " completed_at, duration_ms, output_tokens, reasoning_tokens FROM model_usage"
        ).fetchall()
    est = _est_map(sdb, srows)
    assert est and all(v == 60 for v in est.values()) and len(est) == 4, est
    assert "usage_model_main_turn_msg_r0_0" not in est  # 有回报模型不估算
    tps_e, ttft_e, mark_e = calc(srows[0], est)  # 60 tok / (3000-1000)ms = 30 t/s
    assert abs(tps_e - 30) < 0.01 and abs(ttft_e - 1.0) < 0.01 and mark_e == ""
    sa = [x for x in avg_by_model(srows, est=est) if x["row"][2] == "silent"]
    assert len(sa) == 1 and abs(sa[0]["tps"] - 30) < 0.01 and sa[0]["mark"] == "", sa
    assert calc(srows[0]) is None  # 不传 est → 依旧无速度（原行为不变）
    sdb.execute("DROP TABLE part")
    assert _est_map(sdb, srows) == {}  # part 表缺失 → 退回无估算
    # avg_by_model：同模型三条请求全部按完整耗时合并（2000tok / 6000ms = 333.3 t/s）
    now = 100000
    a = ("x", "p", "glm", "completed", now, now + 1000, now + 2000, 0, 500, 0)
    b = ("y", "p", "glm", "completed", now + 3000, now + 4000, now + 6000, 0, 1000, 0)
    c = ("z", "p", "glm", "completed", now + 7000, None, now + 8000, 1000, 500, 0)
    aggs = avg_by_model([b, c, a])
    assert len(aggs) == 1 and abs(aggs[0]["tps"] - 333.3333333) < 0.01 and aggs[0]["mark"] == "", aggs
    # 三页渲染：转义 / 数字 / 条宽 / 压缩与逐时块
    D = {"sum": {"n": 3, "tok": 1500, "talk_tok": 1500, "talk_ms": 3000,
                 "out": 1200, "inp": 7_000_000, "reason": 300, "cache": 0,
                 "ttft": 1.0, "rpm": 2.5},
         "sess": [{"sid": "s", "title": "测<b>&试", "cnt": 2, "usage": 5_600_000,
                   "tps": 500.0, "mark": "", "last": 100000,
                   "ttft": 1.0, "rpm": 2.0, "model": "glm<b>"}],
         "models": [{"model": "m<b>", "prov": "p", "when": "10:00",
                     "tps": "500.0", "tpsV": 500, "ttft": "1.0s", "tok": "500+0"}],
         "dmodels": [{"model": "m<b>", "prov": "p", "when": "10:00",
                      "tps": "500.0", "tpsV": 500, "cnt": 3, "rpm": 2.5}],
         "bm": [{"model": "m", "prov": "p", "tok": 1500, "cnt": 3, "pct": 100}],
         "cmp": [{"title": "测", "n": 2, "last": 100000}],
         "tsess": [{"title": "测2", "cnt": 3, "tok": 5_600_000}],
         "days": [{"label": f"08-{25 + i:02d}",
                   "tok": 5_000_000 if i == 6 else (100 if i == 3 else 0)}
                  for i in range(7)]}
    ov, spd, tok = page_ov(D), page_spd(D), page_tok(D)
    # 今日 TOKENS 计费口径：tok(1500) - cache(0)*0.9 = 1,500
    assert "1,500" in tok
    ov, spd, tok = page_ov(D), page_spd(D), page_tok(D)  # 速度页恒双段
    assert "会话速度" in spd and "每模型速度 · 今日" in spd
    ov, spd, tok = page_ov(D), page_spd(D), page_tok(D)
    assert "今日 TOKENS" in tok and "今日请求" in tok
    assert "700万<small> / </small>1,500" in tok
    assert "30 天内 2 次" in tok
    assert "class=peak" in tok and "0%'></i>" in tok  # 柱须闭合，否则嵌套成一个元素
    assert "近 7 日 token" in tok and "500万" in tok and "08-31" in tok  # 顶部数值+日期轴
    tok = page_tok(D)
    assert "按会话 · 今日" in tok and "按模型 · 今日" in tok and "测2" in tok and "560万" in tok
    assert "30 天内无压缩会话" in page_tok({**D, "cmp": []})
    # compact 口径（内存库）：fetch_recent 排除 / today_summary 双轨 /
    # compact_stats 只统计 30 天内的压缩记录，超期会话整行丢弃
    mdb = sqlite3.connect(":memory:")
    mdb.execute("CREATE TABLE model_usage (id, session_id, provider_id, model_id, status,"
                " started_at, first_token_at, completed_at, duration_ms, output_tokens,"
                " input_tokens, reasoning_tokens, cache_creation_input_tokens,"
                " cache_read_input_tokens, query_source, task_type TEXT DEFAULT 'interactive')")
    mdb.execute("CREATE TABLE session (id, title, time_archived)")
    now = int(time.time() * 1000)
    for rid, sid, ft, done, qs in (
            ("a", "s", now - 4000, now - 1000, "main_turn"),
            ("b", "s", now - 1500, now - 1000, "compact"),
            ("c", "s", now - 1200, now - 1000, "compact"),
            ("d", "old", now - 4 * 86400 * 1000 - 1000,
             now - 4 * 86400 * 1000, "main_turn"),
            ("e", "old", now - int(31.5 * 86400 * 1000) - 1000,
             now - int(31.5 * 86400 * 1000), "compact")):
        mdb.execute("INSERT INTO model_usage (id, session_id, provider_id, model_id,"
                    " status, started_at, first_token_at, completed_at, duration_ms,"
                    " output_tokens, input_tokens, reasoning_tokens,"
                    " cache_creation_input_tokens, cache_read_input_tokens, query_source)"
                    " VALUES (?,?,?,?,?,?,?,?,0,0,0,100,0,0,?)",
                    (rid, sid, "p", "m", "completed", now - 5000, ft, done, qs))
    assert [r[0] for r in fetch_recent(mdb)] == ["a", "d"]
    s = today_summary(mdb)
    assert s["n"] == 1 and s["tok"] == 300 and s["talk_tok"] == 100, s
    mdb.execute("INSERT INTO session VALUES ('s', 's', NULL)")
    c = compact_stats(mdb)
    assert len(c) == 1 and c[0]["n"] == 2 and c[0]["title"] == "s", c
    mdb.execute("UPDATE session SET time_archived=? WHERE id='s'", (now,))
    assert compact_stats(mdb) == []  # 归档会话的压缩记录隐藏（还原即恢复）
    # 子代理归并：全部 task_type='subagent_child' 合成一条"子代理"（不单独显示）
    mdb.execute("INSERT INTO model_usage (id, session_id, provider_id, model_id, status,"
                " started_at, completed_at, duration_ms, output_tokens, input_tokens,"
                " reasoning_tokens, cache_creation_input_tokens, cache_read_input_tokens,"
                " query_source, task_type)"
                " VALUES ('f','s2','p','m','completed',?,?,-6000,50,0,0,0,0,"
                " 'main_turn','subagent_child')", (now - 6000, now - 2000))
    ts = today_sessions(mdb)
    sub = [x for x in ts if x["title"] == "子代理"]
    assert len(sub) == 1 and sub[0]["cnt"] == 1, ts
    assert all(x["title"] != "s2" for x in ts)  # 子代理不单独成行
    # Codex 适配：fake rollout（token_count 累计差分 / input 去缓存 / compacted 标记）
    import tempfile
    from datetime import timedelta, timezone as _tz
    t = datetime.now(_tz.utc).replace(microsecond=0)
    iso = lambda dt: dt.strftime("%Y-%m-%dT%H:%M:%S.000Z")
    tu = lambda k: {"input_tokens": k, "cached_input_tokens": k * 10 // 11,
                    "cache_write_input_tokens": 0, "output_tokens": k // 10,
                    "reasoning_output_tokens": k // 100, "total_tokens": 0}
    tmp = os.path.join(tempfile.gettempdir(), "tps_rollout_test.jsonl")
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(json.dumps({"type": "session_meta", "timestamp": iso(t),
                            "payload": {"id": "s1", "cwd": r"C:\x\proj_a"}}) + "\n")
        f.write(json.dumps({"type": "turn_context", "timestamp": iso(t),
                            "payload": {"model": "gpt-x"}}) + "\n")
        f.write(json.dumps({"type": "event_msg", "timestamp": iso(t + timedelta(seconds=60)),
                            "payload": {"type": "token_count",
                                        "info": {"total_token_usage": tu(1100)}}}) + "\n")
        f.write(json.dumps({"type": "compacted", "timestamp": iso(t + timedelta(seconds=120)),
                            "payload": {"message": ""}}) + "\n")
        f.write(json.dumps({"type": "event_msg", "timestamp": iso(t + timedelta(seconds=300)),
                            "payload": {"type": "token_count",
                                        "info": {"total_token_usage": tu(2200)}}}) + "\n")
    cdb = sqlite3.connect(":memory:")
    cdb.execute("CREATE TABLE model_usage (id TEXT, session_id TEXT, provider_id TEXT,"
                " model_id TEXT, status TEXT, started_at INTEGER, first_token_at INTEGER,"
                " completed_at INTEGER, duration_ms INTEGER, output_tokens INTEGER,"
                " input_tokens INTEGER, reasoning_tokens INTEGER,"
                " cache_creation_input_tokens INTEGER, cache_read_input_tokens INTEGER,"
                " query_source TEXT, task_type TEXT DEFAULT 'interactive')")
    cdb.execute("CREATE TABLE session (id TEXT PRIMARY KEY, title TEXT, time_archived INTEGER)")
    _parse_rollout(cdb, tmp)
    os.remove(tmp)
    rows = cdb.execute("SELECT model_id, output_tokens, input_tokens,"
                       " cache_read_input_tokens, duration_ms, query_source"
                       " FROM model_usage ORDER BY completed_at").fetchall()
    assert [r[5] for r in rows] == ["main_turn", "compact", "main_turn"], rows
    assert rows[0][0] == "gpt-x" and rows[0][1] == 110 and rows[0][2] == 100 \
        and rows[0][3] == 1000 and rows[0][4] == 0, rows[0]
    assert rows[2][1] == 110 and rows[2][2] == 100 and rows[2][3] == 1000 \
        and rows[2][4] == 240000, rows[2]  # 首条无窗口=0，次条 60s→300s=240s
    cs = compact_stats(cdb)
    assert len(cs) == 1 and cs[0]["n"] == 1 and cs[0]["title"] == "proj_a", cs
    # running_models 旧库分支（无 data/finish 列）：按最近活动近似，30 分钟窗口
    assert running_models(cdb) == []
    cdb.execute("CREATE TABLE message (role TEXT, modelID TEXT, providerID TEXT,"
                " time_created INTEGER)")
    now = int(time.time() * 1000)
    cdb.execute("INSERT INTO message VALUES ('assistant', 'glm-old', 'p', ?)",
                (now - 40 * 60 * 1000,))  # 超窗 → 不算运行中
    assert running_models(cdb) == []
    cdb.execute("INSERT INTO message VALUES ('assistant', 'glm-live', 'p', ?)", (now,))
    assert running_models(cdb) == [{"model": "glm-live", "prov": "p", "started": now}]
    # running_models/_last_act 的 data-JSON 模式（生产库真实结构）：
    # finish 为空/'started' 且 model_usage 无对应行 = 运行中；
    # usage 行在请求**结束**时落库（故 running 的行查不到 usage）。
    jdb = sqlite3.connect(":memory:")
    jdb.execute("CREATE TABLE message (id TEXT, time_created INTEGER, time_updated INTEGER, data TEXT)")
    jdb.execute("CREATE TABLE model_usage (id TEXT)")
    jdb.execute("INSERT INTO message VALUES (?, ?, ?, ?)",  # 超窗残行 → 不算运行中
                ("msg_old", now - 40 * 60 * 1000, now - 40 * 60 * 1000,
                 json.dumps({"role": "assistant", "modelID": "glm-old", "providerID": "p"})))
    assert running_models(jdb) == []
    jdb.execute("INSERT INTO message VALUES (?, ?, ?, ?)",  # 未 finish 且无 usage → 运行中
                ("msg_live", now, now, json.dumps({"role": "assistant", "modelID": "glm-live",
                                                   "providerID": "p"})))
    assert running_models(jdb) == [{"model": "glm-live", "prov": "p", "started": now}]
    # 结束回合：finish 落定 + usage 行出现 → 立刻不再显示
    jdb.execute("UPDATE message SET time_updated=?, data=? WHERE id='msg_live'",
                (now, json.dumps({"role": "assistant", "modelID": "glm-live",
                                  "providerID": "p", "finish": "stop"})))
    jdb.execute("INSERT INTO model_usage VALUES (?)",
                ("usage_model_main_turn_msg_live_0",))
    assert running_models(jdb) == []
    assert sorted(_last_act(jdb).items()) == [("glm-live", now), ("glm-old", now - 40 * 60 * 1000)]
    # 新键名（2026-09 ZCode 起落库为 modelId/providerId）
    jdb2 = sqlite3.connect(":memory:")
    jdb2.execute("CREATE TABLE message (id TEXT, time_created INTEGER, time_updated INTEGER, data TEXT)")
    jdb2.execute("CREATE TABLE model_usage (id TEXT)")
    jdb2.execute("INSERT INTO message VALUES (?, ?, ?, ?)",  # 已 finish + 有 usage → 不算
                 ("msg_done", now - 1000, now, json.dumps({"role": "assistant",
                   "modelId": "nex-done", "providerId": "p", "finish": "stop"})))
    jdb2.execute("INSERT INTO model_usage VALUES (?)", ("usage_model_main_turn_msg_done_0",))
    jdb2.execute("INSERT INTO message VALUES (?, ?, ?, ?)",  # 流式中 → 运行中
                 ("msg_l1", now, now, json.dumps({"role": "assistant", "modelId": "nex-live",
                                                  "providerId": "p", "finish": None})))
    jdb2.execute("INSERT INTO message VALUES (?, ?, ?, ?)",  # 并发第二会话也在生成
                 ("msg_l2", now, now, json.dumps({"role": "assistant", "modelId": "qwen-live",
                                                  "providerId": "p2", "finish": "started"})))
    assert sorted(r["model"] for r in running_models(jdb2)) == ["nex-live", "qwen-live"]
    # 僵尸行：finish=NULL 且无 usage 但 30 分钟前创建 → 不再显示
    jdb2.execute("INSERT INTO message VALUES (?, ?, ?, ?)",
                 ("msg_zombie", now - 31 * 60 * 1000, now - 31 * 60 * 1000,
                  json.dumps({"role": "assistant", "modelId": "zombie",
                              "providerId": "p", "finish": None})))
    assert sorted(r["model"] for r in running_models(jdb2)) == ["nex-live", "qwen-live"]
    # 长回合：created 超窗但仍在更新 → _last_act 判活（90s 窗口不过期）
    jdb2.execute("INSERT INTO message VALUES (?, ?, ?, ?)",
                 ("msg_lt", now - 300 * 1000, now - 5 * 1000,
                  json.dumps({"role": "assistant", "modelId": "long-turn",
                              "providerId": "p", "finish": "tool-calls"})))
    assert _last_act(jdb2)["long-turn"] == now - 5 * 1000
    assert _last_act(cdb) == {}  # 无 data 列 → 退回空
    print("self-check OK")


def print_once(db):
    provs = provider_names()
    est = _est_map(db, fetch_recent(db, limit=500))
    for row in latest_by_model(fetch_recent(db, limit=TABLE_N)):
        c = fmt_cells(row, provs, est)
        print(f"{c[1]}  [{c[2]}]  {c[0]}\n  {c[3]} t/s  首字 {c[5]}  {c[6]} tok")
    s = today_summary(db)
    avg = s["talk_tok"] * 1000.0 / s["talk_ms"] if s["talk_ms"] else 0.0
    billed = s["tok"] - s["cache"] * (1 - CACHE_BILL)
    print(f"— 今日: {s['n']} 次 · {billed:,.0f} tok · 均 {avg:.1f} t/s")


def latest_by_model(rows, window_ms=90 * 1000):
    """rows 已按时间新→旧，每模型取最新一条 → 每模型一行。
    90 秒无新完成回复 = 结束，行消失（用户定标，2026-09-11 由 30s 改，与面板同步）。"""
    seen = {}
    cutoff = time.time() * 1000 - window_ms
    for row in rows:
        done = row[6] or row[4]
        if done and done < cutoff:
            break
        seen.setdefault(row[2], row)
    return list(seen.values())


def avg_by_model(rows, per=5, est=None):
    """每模型聚合最近 per 条：总 token ÷ 总请求耗时。
    请求耗时从 started_at 到 completed_at，包含首字前等待/思考；
    没有有效时间戳时回退 duration_ms。est 用于无回报模型的字符估算。"""
    by = {}
    for row in rows:
        c = calc(row, est)
        if c:
            by.setdefault(row[2], []).append((row, c))
    out = []
    for model, lst in by.items():
        recent = [(r, c) for r, c in lst][:per]
        mark = ""
        if not recent:
            continue
        tot_tok = sum((r[8] or 0) + (r[9] or 0) or (est or {}).get(r[0], 0)
                      for r, _ in recent)
        elapsed_ms = sum(_elapsed_ms(r) for r, _ in recent)
        if elapsed_ms > 0:
            out.append({"row": recent[0][0], "tps": tot_tok * 1000.0 / elapsed_ms,
                        "mark": mark})
    return out


def session_stats(db, limit=30, window_ms=30 * 60 * 1000):
    """按会话聚合 token 速度（总 tokens ÷ 请求完整耗时），标题取 session.title。
    30 分钟无新活动的会话不显示；排除 compact（v4 速度口径）。"""
    cutoff = time.time() * 1000 - window_ms
    rows = db.execute(
        """SELECT u.session_id,
                  COALESCE(s.title, u.session_id),
                  COUNT(*),
                  SUM(u.output_tokens + u.reasoning_tokens),
                  CAST(SUM(u.output_tokens + u.reasoning_tokens + u.input_tokens
                      + u.cache_creation_input_tokens
                      + u.cache_read_input_tokens*?) AS INT),
                  SUM(CASE WHEN u.started_at IS NOT NULL AND u.completed_at IS NOT NULL
                                AND u.completed_at > u.started_at
                           THEN u.completed_at - u.started_at ELSE u.duration_ms END),
                  MAX(COALESCE(u.completed_at, u.started_at)),
                  0,
                  AVG(CASE WHEN u.first_token_at IS NOT NULL AND u.started_at IS NOT NULL
                                AND u.first_token_at >= u.started_at
                           THEN u.first_token_at - u.started_at END),
                  MIN(COALESCE(u.completed_at, u.started_at)),
                  (SELECT u2.model_id FROM model_usage u2
                   WHERE u2.session_id = u.session_id AND u2.query_source!='compact'
                         AND u2.status='completed'
                   ORDER BY COALESCE(u2.completed_at, u2.started_at) DESC LIMIT 1)
           FROM model_usage u LEFT JOIN session s ON s.id = u.session_id
           WHERE u.query_source!='compact' AND u.task_type!='subagent_child'
           GROUP BY u.session_id
           HAVING MAX(COALESCE(u.completed_at, u.started_at)) >= ?
           ORDER BY 6 DESC LIMIT ?""", (CACHE_BILL, cutoff, limit)).fetchall()
    out = []
    for sid, title, cnt, tok, usage, gen_ms, last, fallbacks, ttft_ms, first, model in rows:
        tps = tok * 1000.0 / gen_ms if gen_ms else 0.0
        span_min = (last - first) / 60000.0 if first and last and last > first else 0.0
        out.append({"sid": sid, "title": title or sid, "cnt": cnt,
                    "tps": tps, "mark": "*" if fallbacks else "", "last": last,
                    "ttft": ttft_ms / 1000.0 if ttft_ms else None,
                    "rpm": cnt / span_min if span_min else 0.0,
                    "model": model or "-", "usage": usage or 0})
    return out


def today_by_model(db, provs=None):
    """今日按模型 tokens/次数（含 compact），tokens 降序，pct=占比，prov=供应商可读名。"""
    rows = db.execute(
        """SELECT model_id, provider_id,
                  CAST(SUM(output_tokens+reasoning_tokens+input_tokens
                      +cache_creation_input_tokens+cache_read_input_tokens*?) AS INT), COUNT(*)
           FROM model_usage WHERE status='completed' AND completed_at >= ?
             AND model_id != ''
           GROUP BY model_id, provider_id ORDER BY 3 DESC""",
        (CACHE_BILL, _day0())).fetchall()
    total = sum(r[2] or 0 for r in rows) or 1
    return [{"model": m, "prov": (provs or {}).get(p, (p or "?")[:8]),
             "tok": t or 0, "cnt": c,
             "pct": round((t or 0) * 100.0 / total)} for m, p, t, c in rows]


def week_daily(db, days=7):
    """近 7 天逐日全口径 token（本地日界，含 compact），缺数据的天补 0。"""
    day0 = _day0() - (days - 1) * 86400 * 1000
    sums = dict(db.execute(
        """SELECT strftime('%Y-%m-%d', completed_at/1000, 'unixepoch', 'localtime'),
                  CAST(SUM(output_tokens+reasoning_tokens+input_tokens
                      +cache_creation_input_tokens
                      +cache_read_input_tokens*?) AS INT)
           FROM model_usage WHERE status='completed' AND completed_at >= ?
           GROUP BY 1""", (CACHE_BILL, day0)))
    out = []
    for i in range(days):
        d = datetime.fromtimestamp((day0 + i * 86400 * 1000) / 1000)
        out.append({"label": d.strftime("%m-%d"),
                    "tok": sums.get(d.strftime("%Y-%m-%d"), 0) or 0})
    return out


def compact_stats(db, days=30):
    """按会话的压缩次数（query_source='compact' 且 completed）。
    只统计/显示最近 days 天内的压缩记录，超期不显示（用户 2026-09-13 改定）。
    已归档会话（session.time_archived 非空）不显示；还原（清空该列）后自动恢复。"""
    cutoff = int((time.time() - days * 86400) * 1000)
    rows = db.execute(
        """SELECT COALESCE(s.title, u.session_id), COUNT(*), MAX(u.completed_at)
           FROM model_usage u LEFT JOIN session s ON s.id = u.session_id
           WHERE u.query_source='compact' AND u.status='completed'
                 AND u.completed_at >= ? AND s.time_archived IS NULL
           GROUP BY u.session_id ORDER BY 3 DESC""", (cutoff,)).fetchall()
    return [{"title": t, "n": n, "last": last} for t, n, last in rows]


def running_models(db, window_ms=30 * 60 * 1000):
    """所有正在运行的模型（并发会话逐个返回，不止最新一条）。
    判据：assistant 消息 finish 为空/'started'，且 model_usage 里没有对应行——
    usage 行在一次模型请求**结束**时才落库（completed/error/cancelled 都有），
    运行期间（思考/生成/工具间隙）查不到行。长思考回合不受 90s 窗口限制。
    30 分钟仍无 usage 行视为崩溃遗留僵尸行（实测单次请求最长 5.5 分钟），
    不显示"运行中"。旧键名 modelID/providerID 与新键名 modelId/providerId 都认。"""
    cutoff = int(time.time() * 1000) - window_ms
    try:
        cols = {r[1] for r in db.execute("PRAGMA table_info(message)")}
        if "data" in cols:
            # started=最早未结束请求的创建时刻（同模型并发时取最长在跑的请求）
            rows = db.execute(
                """SELECT COALESCE(json_extract(m.data,'$.modelId'),
                                   json_extract(m.data,'$.modelID')) AS mid,
                          COALESCE(json_extract(m.data,'$.providerId'),
                                   json_extract(m.data,'$.providerID')) AS pid,
                          MIN(m.time_created) AS started
                   FROM message m
                   WHERE json_extract(m.data, '$.role')='assistant'
                     AND m.time_created >= ?
                     AND (json_extract(m.data,'$.finish') IS NULL
                          OR json_extract(m.data,'$.finish')='started')
                     AND NOT EXISTS (SELECT 1 FROM model_usage u
                                     WHERE u.id LIKE '%' || m.id || '%')
                   GROUP BY mid, pid""", (cutoff,)).fetchall()
            return [{"model": m, "prov": p or "", "started": st}
                    for m, p, st in rows if m]
        rows = db.execute(
            """SELECT modelID, providerID, MIN(time_created) FROM message
               WHERE role='assistant' AND modelID IS NOT NULL AND time_created >= ?
               GROUP BY modelID, providerID""",
            (cutoff,)).fetchall()  # 旧库无 data/finish 列：按最近活动近似
    except sqlite3.Error:
        return []
    return [{"model": r[0], "prov": r[1] or "", "started": r[2]} for r in rows if r[0]]


def _last_act(db):
    """model_id -> 最新 assistant 活动时间（生成中也算活动）。表缺失返回 {}。
    用 time_updated 而非 time_created：流式/工具调用期间行会持续刷新，
    长回合（created 已超过 90s 仍在生成）不会误判为不活跃。"""
    try:
        # 键名兼容：新库 $.modelId，旧库 $.modelID
        return dict(db.execute(
            """SELECT COALESCE(json_extract(data,'$.modelId'),
                               json_extract(data,'$.modelID')) AS mid,
                      MAX(time_updated)
               FROM message WHERE json_extract(data,'$.role')='assistant'
               GROUP BY mid""").fetchall())
    except sqlite3.Error:
        return {}


def detail_data(db, provs, tool="zcode"):
    """面板 + 详情三页共用的一次取数（push_loop 每秒 / 详情打开 / 热更同源）。"""
    cutoff = time.time() * 1000 - 90 * 1000  # 90s 无活动不显示（用户 2026-09-11 改定）
    last_act = _last_act(db)  # 按最后任何活动判活：长回合生成中行不消失，落库即显速度
    rows = fetch_recent(db, limit=TABLE_N)
    rows500 = fetch_recent(db, limit=500)
    est = _est_map(db, rows500)  # 无回报模型（如 Agnes）按 part 字符估算 token
    items = []
    for agg in avg_by_model(rows, est=est):
        r = agg["row"]
        if max(r[6] or r[4] or 0, last_act.get(agg["row"][2]) or 0) < cutoff:
            continue
        c = fmt_cells(agg["row"], provs, est)  # 最新条提供 when/tok 等展示
        items.append({"model": agg["row"][2], "prov": c[2], "when": c[0],
                      "tps": f'{agg["tps"]:.1f}{agg["mark"]}',
                      "tpsV": round(agg["tps"]), "ttft": c[5], "tok": c[6]})
    # 详情页"每模型速度 · 今日"：一天内用过的全部模型，不受 90s 窗口限制（用户 2026-09-13 改定）
    d0 = _day0()
    # 每模型今日总请求次数与平均请求速度（次数÷活跃跨度，排除 compact，与速度口径一致）
    stat = {m: (c, mn, mx) for m, c, mn, mx in db.execute(
        """SELECT model_id, COUNT(*), MIN(COALESCE(completed_at, started_at)),
                  MAX(COALESCE(completed_at, started_at)) FROM model_usage
           WHERE status='completed' AND query_source!='compact'
                 AND COALESCE(completed_at, started_at)>=? AND model_id!=''
           GROUP BY model_id""", (d0,))}
    aggs500 = avg_by_model(rows500, est=est)
    ditems = []
    for agg in aggs500:
        r = agg["row"]
        if (r[6] or r[4] or 0) < d0:
            continue
        c = fmt_cells(r, provs, est)
        cnt, mn, mx = stat.get(r[2], (0, None, None))
        rpm = cnt * 60000.0 / (mx - mn) if cnt > 1 and mx and mx > mn else 0.0
        ditems.append({"model": r[2], "prov": c[2], "when": c[0],
                       "tps": f'{agg["tps"]:.1f}{agg["mark"]}',
                       "tpsV": round(agg["tps"]), "cnt": cnt, "rpm": rpm})
    # 正在生成的模型立刻可见（message 表实时落库，model_usage 要等回合结束）。
    # 用户 2026-09-23：运行中也要显示数字且不加任何标记——面板上静止 = 看起来坏了。
    # 生成期间没有任何实时 token 计数（实测 message/model_usage/日志均无），
    # 数字 = 该模型最近 5 次加权均速（回合结束即替换为真实值，界面无差别）；
    # 首次请求无历史可参考时显示"首次请求"占位。运行中优先于刚完成的旧速度行。
    hist = {a["row"][2]: a for a in aggs500}
    runm = {}
    for lm in running_models(db):
        runm.setdefault(lm["model"], lm)  # 同模型并发取最早开始的请求
    if runm:
        items = [i for i in items if i["model"] not in runm]
        for m, lm in runm.items():
            a = hist.get(m)
            items.append({"model": m,
                          "prov": (provs or {}).get(lm["prov"], (lm["prov"] or "?")[:8]),
                          "when": datetime.fromtimestamp(lm["started"] / 1000).strftime("%H:%M")
                          if lm["started"] else "…",
                          "tps": f'{a["tps"]:.1f}' if a else "…",
                          "tpsV": round(a["tps"]) if a else 0, "ttft": "-",
                          "tok": "-", "run": 1})
    return {"sum": today_summary(db), "sess": session_stats(db), "models": items,
            "dmodels": ditems,
            "bm": today_by_model(db, provs), "cmp": compact_stats(db),
            "days": week_daily(db), "tsess": today_sessions(db), "tool": tool}


def _sc(v):
    return "fast" if v >= 80 else ("mid" if v >= 50 else "slow")


def _k(n):
    """中文计量：亿/万。70000000→7000万，45600→4.6万，1.4亿。"""
    if n >= 1e8:
        return f"{n / 1e8:.2f}亿"
    if n >= 1e4:
        return f"{n / 1e4:.1f}".rstrip("0").rstrip(".") + "万"
    return f"{n:,}"


def _cards(pairs):
    return "<div class='stats'>" + "".join(
        f"<div class='stat'><div class='k'>{k}</div><div class='v'>{v}</div></div>"
        for k, v in pairs) + "</div>"


def _sess_row(s, full=True):
    cls = _sc(s["tps"])
    ttft = f"{s['ttft']:.1f}s" if s["ttft"] is not None else "-"
    if full:
        when = datetime.fromtimestamp(s["last"] / 1000).strftime("%m-%d %H:%M") \
            if s["last"] else "-"
        meta = (f"<span>首字 {ttft}</span><span>{s['rpm']:.1f} 次/分</span>"
                f"<span>{s['cnt']} 次</span><span>{when}</span>")
    else:
        meta = (f"<span style='font-weight:600'>{html.escape(s['model'])}</span>"
                f"<span>首字 {ttft}</span>"
                f"<span>{s['rpm']:.1f} 次/分</span>"
                f"<span>{_k(s.get('usage') or 0)} tok</span>")
    w = min(100, round(s["tps"] / 120 * 100))  # bar 满格 = 120 t/s
    return (f"<div class='row'><div class='t1'><span class='nm'>{html.escape(s['title'])}</span>"
            f"<span class='meta'>{meta}</span>"
            f"<span class='v {cls}'>{s['tps']:.1f}{s['mark']}</span></div>"
            f"<div class='bar'><i class='{cls}' style='width:{w}%'></i></div></div>")


def _model_rows(models):
    """每模型速度行（详情页共用）：模型/供应商/时刻/速度 + 条宽。"""
    out = ""
    for m in models:
        cls = _sc(m["tpsV"])
        w = min(100, round(m["tpsV"] / 120 * 100))
        out += (f"<div class='row'><div class='t1'><span class='nm'>{html.escape(m['model'])}</span>"
                f"<span class='meta'><span>{html.escape(m['prov'])}</span>"
                f"<span>{m.get('cnt', 0)} 次</span><span>{m.get('rpm', 0.0):.1f} 次/分</span>"
                f"<span>{m['when']}</span></span>"
                f"<span class='v {cls}'>{m['tps']}</span></div>"
                f"<div class='bar'><i class='{cls}' style='width:{w}%'></i></div></div>")
    return out


def page_ov(D):
    """总览页：今日三卡 + 每模型速度（今日） + 活跃会话。"""
    s, sess, models = D["sum"], D["sess"], D.get("dmodels") or D["models"]
    avg = s["talk_tok"] * 1000.0 / s["talk_ms"] if s["talk_ms"] else 0.0
    body = "".join(_sess_row(x) for x in sess)
    return (_cards([("今日请求", f"{s['n']}<small> 次</small>"),
                    ("平均速度",
                     f"<span class='{_sc(avg)}'>{avg:.1f}</span><small> t/s</small>"),
                    ("今日 TOKENS",
                     _k(s["tok"] - int(s["cache"] * (1 - CACHE_BILL))))])
            + "<div class='sec'>每模型速度 · 今日</div>"
            + (_model_rows(models) or "<div class='empty'>今日暂无数据</div>")
            + "<div class='sec'>活跃会话 · 30 分钟窗口</div>"
            + (body or "<div class='empty'>30 分钟内无活动会话</div>"))


def page_spd(D, mode="m"):
    """速度页：速度三卡 + 每模型速度（今日） + 会话速度。"""
    s, sess, models = D["sum"], D["sess"], D.get("dmodels") or D["models"]
    avg = s["talk_tok"] * 1000.0 / s["talk_ms"] if s["talk_ms"] else 0.0
    ttft_v = ("-" if D.get("tool") == "codex"  # codex 无首字计时
              else (f"{s['ttft']:.1f}<small> s</small>"
                    if s["ttft"] is not None else "-"))
    body = _model_rows(models)
    sbody = "".join(_sess_row(x, full=False) for x in sess)
    return (_cards([("平均Token速度",
                     f"<span class='{_sc(avg)}'>{avg:.1f}</span><small> t/s</small>"),
                    ("平均请求速度", f"{s['rpm']:.1f}<small> 次/分</small>"),
                    ("平均首字速度", ttft_v)])
            + "<div class='sec'>每模型速度 · 今日（最近 5 条加权）</div>"
            + (body or "<div class='empty'>今日暂无数据</div>")
            + "<div class='sec'>会话速度 · 30 分钟窗口</div>"
            + (sbody or "<div class='empty'>30 分钟内无活动会话</div>"))


def today_sessions(db, limit=20):
    """今日按会话计费用量（含 compact），billed 降序。
    全部子代理（task_type='subagent_child'）归并为一条"子代理"（用户 2026-09-13 改定）。"""
    rows = db.execute(
        """SELECT CASE WHEN u.task_type='subagent_child' THEN '子代理'
                       ELSE COALESCE(s.title, u.session_id) END,
                  COUNT(*),
                  CAST(SUM(u.output_tokens+u.reasoning_tokens+u.input_tokens
                      +u.cache_creation_input_tokens
                      +u.cache_read_input_tokens*?) AS INT)
           FROM model_usage u LEFT JOIN session s ON s.id = u.session_id
           WHERE u.status='completed' AND u.completed_at >= ?
           GROUP BY 1 ORDER BY 3 DESC LIMIT ?""",
        (CACHE_BILL, _day0(), limit)).fetchall()
    return [{"title": ti, "cnt": c, "tok": t or 0} for ti, c, t in rows]


def page_tok(D):
    """总量页：总量三卡 + 按模型 · 今日 + 按会话 · 今日 + 会话压缩 + 近 7 日。"""
    s, bm, cmp_, days, tsess = D["sum"], D["bm"], D["cmp"], D["days"], D["tsess"]
    mbody = "".join(
        f"<div class='row'><div class='t1'><span class='nm'>{html.escape(m['model'])}</span>"
        f"<span class='meta'><span>{html.escape(m['prov'])}</span>"
        f"<span>{m['tok']:,} tok</span><span>{m['cnt']} 次</span>"
        f"<span>{m['pct']}%</span></span></div>"
        f"<div class='bar'><i class='share' style='width:{m['pct']}%'></i></div></div>"
        for m in bm)
    sbody = "".join(
        f"<div class='row'><div class='t1'><span class='nm'>{html.escape(x['title'])}</span>"
        f"<span class='meta'><span>{x['cnt']} 次</span>"
        f"<span>{_k(x['tok'])} tok</span></span></div>"
        f"<div class='bar'><i class='share' style='width:{round(x['tok'] * 100 / max(sum(y['tok'] for y in tsess) or 1, 1))}%'></i></div></div>"
        for x in tsess)
    first = ("<div class='sec'>按模型 · 今日</div>"
             + (mbody or "<div class='empty'>今日暂无数据</div>")
             + "<div class='sec'>按会话 · 今日</div>"
             + (sbody or "<div class='empty'>今日暂无数据</div>"))
    cn = sum(c["n"] for c in cmp_)
    cbody = "".join(
        f"<div class='row'><div class='t1'><span class='nm'>{html.escape(c['title'])}</span>"
        f"<span class='meta'><span>{c['n']} 次</span>"
        f"<span>{datetime.fromtimestamp(c['last'] / 1000).strftime('%m-%d %H:%M')}</span></span></div></div>"
        for c in cmp_)
    dsec = ""
    if any(d["tok"] for d in days):
        mx = max(d["tok"] for d in days)
        nums = "".join(f"<span>{_k(d['tok'])}</span>" for d in days)
        bars = "".join(f"<i style='height:{round(d['tok'] * 100 / mx)}%'"
                       + (" class=peak" if d["tok"] == mx else "") + "></i>"
                       for d in days)
        labs = "".join(f"<span>{d['label']}</span>" for d in days)
        dsec = ("<div class='sec'>近 7 日 token</div>"
                f"<div class='hnums'>{nums}</div>"
                f"<div class='hours'>{bars}</div>"
                f"<div class='haxis'>{labs}</div>")
    return (_cards([("今日 TOKENS", _k(s["tok"] - int(s["cache"] * (1 - CACHE_BILL)))),
                    ("今日请求", f"{s['n']}<small> 次</small>"),
                    ("输入输出",
                     f"{_k(s['inp'] + s['cache'] * CACHE_BILL)}<small> / </small>"
                     f"{_k(s['out'] + s['reason'])}")])
            + first
            + f"<div class='sec'>会话压缩 · 30 天内 {cn} 次</div>"
            + (cbody or "<div class='empty'>30 天内无压缩会话</div>")
            + dsec)


def detail_html(D):
    """详情弹窗整页：头部下拉 + 三页容器（打开瞬间渲染，方案 A「纸面印刷」亮色）。
    此后每秒只换三个容器的内层，头部不重写——下拉选择不丢、滚动位置不丢。"""
    return f"""<!doctype html><html><head><meta charset="utf-8"><title>{DETAIL_TITLE}</title><style>
*{{margin:0;box-sizing:border-box}}
:root{{--ink:#1a1d22;--muted:#9a978c;--hair:#f0ece1;--line:#e6e2d8;
--fast:#0a8f46;--mid:#d97706;--slow:#dc2626;--verm:#ff4d2e;
--num:Bahnschrift,"Segoe UI Variable Display","Segoe UI",sans-serif}}
html{{color-scheme:light}}
body{{font-family:"Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;color:var(--ink);
background:#fffdf8;font-size:13px}}
::-webkit-scrollbar{{width:8px}}
::-webkit-scrollbar-thumb{{background:#d9d2c4;border-radius:4px}}
#live{{height:100vh;overflow-y:auto;padding:20px 26px}}
.head{{display:flex;align-items:center;gap:8px;padding-bottom:14px}}
.head select{{font:inherit;font-size:15px;font-weight:700;color:var(--ink);
background:#fffdf8;border:1px solid var(--line);border-radius:4px;padding:2px 6px;
outline:none;cursor:pointer}}
.head .dot{{width:7px;height:7px;border-radius:50%;background:var(--verm);
box-shadow:0 0 0 3px rgba(255,77,46,.15)}}
.head .d{{margin-left:auto;font-size:11px;color:var(--muted);font-variant-numeric:tabular-nums}}
.stats{{display:flex;border-top:1px solid #e9e5da;border-bottom:1px solid #e9e5da}}
.stat{{flex:1;padding:14px 18px 13px;border-left:1px solid #e9e5da}}
.stat:first-child{{padding-left:2px;border-left:none}}
.k{{font-size:9.5px;color:var(--muted);text-transform:uppercase;letter-spacing:1.8px;margin-bottom:5px}}
.stat .v{{font-family:var(--num);font-size:20px;font-weight:700;
font-variant-numeric:tabular-nums;line-height:1;white-space:nowrap}}
.stat .v small{{font-size:11px;color:var(--muted);font-weight:400;font-family:"Segoe UI",sans-serif}}
.sec{{margin:18px 2px 2px;font-size:9.5px;color:var(--muted);
text-transform:uppercase;letter-spacing:1.8px}}
.row{{padding:11px 2px 12px;border-bottom:1px solid var(--hair)}}
.row:last-child{{border-bottom:none}}
.t1{{display:flex;align-items:baseline;gap:8px}}
.nm{{font-size:13px;font-weight:600;min-width:0;flex:0 1 auto;
overflow:hidden;text-overflow:ellipsis;white-space:nowrap}}
.meta{{margin-left:auto;display:flex;gap:14px;font-size:10.5px;color:var(--muted);
font-variant-numeric:tabular-nums;white-space:nowrap;flex:none}}
.t1 .v{{margin-left:16px;font-family:var(--num);font-size:17px;font-weight:700;
font-variant-numeric:tabular-nums;white-space:nowrap}}
.bar{{height:2px;background:var(--hair);margin-top:8px;border-radius:2px;overflow:hidden}}
.bar i{{display:block;height:100%;border-radius:2px;transition:width .45s ease}}
.fast{{color:var(--fast)}}.mid{{color:var(--mid)}}.slow{{color:var(--slow)}}
i.fast{{background:var(--fast)}}i.mid{{background:var(--mid)}}i.slow{{background:var(--slow)}}
i.share{{background:var(--verm)}}
.hnums{{display:flex;gap:3px;margin:8px 2px 0}}
.hnums span{{flex:1;text-align:center;font-family:var(--num);font-size:8.5px;
font-variant-numeric:tabular-nums;overflow:hidden;white-space:nowrap}}
.hours{{display:flex;align-items:flex-end;gap:3px;height:46px;margin:2px 2px 4px}}
.hours i{{flex:1;background:#d9d2c4;border-radius:2px 2px 0 0;min-height:2px}}
.hours i.peak{{background:var(--verm)}}
.haxis{{display:flex;font-size:9px;color:var(--muted);margin:2px 2px 0}}
.haxis span{{flex:1;text-align:center}}
.empty{{color:var(--muted);text-align:center;padding:40px 0}}
</style></head><body><div id="live">
<div class="head"><select id="tool"><option value="zcode" selected>ZCode</option><option value="codex">Codex</option></select>
<select onchange="for(var p of document.querySelectorAll('.page'))p.hidden=p.id!==this.value">
<option value="pg-ov" selected>总览</option><option value="pg-spd">速度</option><option value="pg-tok">总量</option></select>
<span class="dot"></span><span class="d">{datetime.now().strftime('%m-%d')} · 实时刷新</span></div>
<div class="page" id="pg-ov">{page_ov(D)}</div>
<div class="page" id="pg-spd" hidden>{page_spd(D)}</div>
<div class="page" id="pg-tok" hidden>{page_tok(D)}</div>
</div></body></html>"""


HTML = """<!doctype html><html><head><meta charset="utf-8">
<title>Token Details</title><style>
*{margin:0;box-sizing:border-box}
:root{--ink:#11151a;--muted:#4b5560;--hair:rgba(70,78,88,.28);--line:rgba(70,78,88,.34);
--fast:#087a3d;--mid:#b45f00;--slow:#bd1f1f;
--num:Bahnschrift,"Segoe UI Variable Display","Segoe UI",sans-serif}
html{color-scheme:light;background:transparent}
body{font-family:"Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;color:var(--ink);
background:transparent;overflow:hidden;user-select:none;min-height:52px}
#grip{position:absolute;top:12px;left:8px;color:#11151a;font-size:10px;
letter-spacing:2px;cursor:pointer;line-height:1}
#rows{padding:0 16px 7px}
.row:first-child{padding-left:14px}
.row{padding:10px 2px;border-bottom:1px solid var(--hair)}
.row:last-child{border-bottom:none;padding-bottom:10px}
.l1{display:flex;align-items:baseline;gap:8px}
.nm{font-size:12px;font-weight:700;letter-spacing:.1px;min-width:0;flex:0 1 auto;
color:var(--ink);text-shadow:0 0 1px rgba(255,255,255,.72);
overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.prov{font-size:9px;color:var(--muted);font-weight:600;text-transform:uppercase;letter-spacing:1px;
white-space:nowrap;flex:1 1 0;min-width:0;
overflow:hidden;text-overflow:ellipsis}
.v{margin-left:auto;font-family:var(--num);font-size:19px;font-weight:800;
color:var(--ink);text-shadow:0 0 1px rgba(255,255,255,.72);
font-variant-numeric:tabular-nums;white-space:nowrap;flex:none}
.v small{font-size:9px;color:var(--muted);font-family:"Segoe UI",sans-serif;font-weight:400}
.bar{height:2px;background:var(--hair);margin-top:7px;border-radius:2px;overflow:hidden}
.bar i{display:block;height:100%;border-radius:2px;transition:width .45s ease}
.fast{color:var(--fast)}.mid{color:var(--mid)}.slow{color:var(--slow)}
i.fast{background:var(--fast)}i.mid{background:var(--mid)}i.slow{background:var(--slow)}
.empty{color:var(--muted);font-size:11px;text-align:center;padding:14px 0 10px}
</style></head><body>
<div id="grip">⠿</div>
<div id="rows"><div class="empty">连接数据中…</div></div>
<script>
function cls(v){return v>=80?"fast":(v>=50?"mid":"slow")}
function esc(s){return String(s).replace(/&/g,"&amp;").replace(/</g,"&lt;").replace(/>/g,"&gt;")}
function update(d){
  var h="";
  for(const r of d.rows){
    var c=cls(r.tpsV), w=Math.min(100,Math.round(r.tpsV/120*100));
    // 运行中行与完成行同样式渲染数字（无历史时 tps="…" 置灰占位）
    var v=(r.run&&r.tpsV===0)?"<span style='color:#9a978c;font-size:12px;font-weight:400'>…</span>"
                            :r.tps+"<small> t/s</small>";
    h+=`<div class="row"><div class="l1"><span class="nm">${esc(r.model)}</span><span class="prov">${esc(r.prov)}</span><span class="v ${c}">${v}</span></div><div class="bar"><i class="${c}" style="width:${w}%"></i></div></div>`;
  }
  document.getElementById("rows").innerHTML=h||'<div class="empty">暂无数据</div>';
}
</script></body></html>"""



def run_web(db):
    import threading
    import webview
    provs = provider_names()

    u32 = ctypes.WinDLL("user32", use_last_error=True)

    class POINT(ctypes.Structure):
        _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]

    class RECT(ctypes.Structure):
        _fields_ = [("l", ctypes.c_long), ("t", ctypes.c_long),
                    ("r", ctypes.c_long), ("b", ctypes.c_long)]

    # 把手热区（CSS px，首行模型左侧的 ⠿，与 HTML 里 #grip 位置对齐）
    GRIP_X, GRIP_Y, GRIP_W, GRIP_H = 4, 8, 26, 26

    def hover_loop():
        # 三态：悬停把手区=解除穿透+可拖；面板其余=永久穿透。
        # 拖拽靠全局鼠标状态轮询（pywebview 拖拽通道在此环境不通），启动后独立运行。
        time.sleep(2)
        while True:
            try:
                mh = u32.FindWindowW(None, TITLE)
                if mh:
                    pt, rc = POINT(), RECT()
                    u32.GetCursorPos(ctypes.byref(pt))
                    u32.GetWindowRect(mh, ctypes.byref(rc))
                    dpi = u32.GetDpiForWindow(mh) or 96
                    inside = rc.l <= pt.x <= rc.r and rc.t <= pt.y <= rc.b
                    gx = rc.l + round(GRIP_X * dpi / 96)
                    gy = rc.t + round(GRIP_Y * dpi / 96)
                    in_grip = (inside and gx <= pt.x <= gx + round(GRIP_W * dpi / 96)
                               and gy <= pt.y <= gy + round(GRIP_H * dpi / 96))
                    ex = u32.GetWindowLongW(mh, -20) & 0xFFFFFFFF
                    # 简略面板不占任务栏：TOOLWINDOW 生效必须显式清 APPWINDOW，
                    # 否则两者并存时 APPWINDOW 强制显示任务栏按钮
                    ex = (ex & ~0x40000 & 0xFFFFFFFF) | 0x80000 | 0x80 | 0x20
                    if in_grip:
                        ex &= ~0x20 & 0xFFFFFFFF  # 把手区可交互（显式清位）
                    else:
                        ex |= 0x20  # 其余区域穿透
                    u32.SetWindowLongW(mh, -20, ex)
                    # WebView2 透明背景需要宿主分层 alpha 才能稳定显示；默认 125 比旧版 150 更透。
                    # 文字颜色/字重/描边已单独增强，降低背景透明度时保持可读。
                    u32.SetLayeredWindowAttributes(
                        mh, 0, 220 if in_grip else (190 if inside else 125), 2)
                    if in_grip and u32.GetAsyncKeyState(0x01) & 0x8000:
                        x0, y0, wx, wy = pt.x, pt.y, rc.l, rc.t
                        while u32.GetAsyncKeyState(0x01) & 0x8000:
                            time.sleep(0.02)
                            u32.GetCursorPos(ctypes.byref(pt))
                            u32.SetWindowPos(mh, None, wx + pt.x - x0,
                                             wy + pt.y - y0, 0, 0,
                                             0x0001 | 0x0010)  # NOSIZE|NOACTIVATE
            except Exception as e:
                errlog("hover", e)
            time.sleep(0.05)

    def push_loop():
        # evaluate_js 推送（修线程后已验证 60s 稳定）；行数固定为启动时值
        last_sig = [""]  # 上次推送的行签名，变化才写 trace
        time.sleep(1.5)
        while True:
            try:
                if src_file and os.path.getmtime(src_file) != last_src[0]:  # 热更新检查
                    last_src[0] = os.path.getmtime(src_file)
                    hot_reload()
                    log_frame("hot-reload ok")
                # 工具切换：读详情窗下拉当前值（有可见详情窗才更新，面板全局跟随）
                tool = None
                for dw in detail_win:
                    try:
                        if not dw.hidden:
                            v = dw.evaluate_js(
                                "var e=document.getElementById('tool');e?e.value:''")
                            if v:
                                tool = v
                            break
                    except Exception:
                        pass
                if tool:
                    cur_tool[0] = tool
                sdb = codex_db() if cur_tool[0] == "codex" else db
                sprovs = ({"codex": "Codex"} if cur_tool[0] == "codex"
                          else provider_names())  # 每秒重读：配置可能后于面板启动更新
                D = detail_data(sdb, sprovs, cur_tool[0])  # 面板与详情三页同源
                sig = str([(m["model"], m["prov"], m["tps"]) for m in D["models"]])
                if sig != last_sig[0]:  # 行集合变了才记，转瞬即逝的显示可回查
                    last_sig[0] = sig
                    trace(sig)
                payload = json.dumps({"rows": D["models"]}, ensure_ascii=False)
                w.evaluate_js(f"update({payload})")
                # 详情窗实时刷新：三页 HTML 打包逐容器替换（头部/下拉不重写，选择保留）
                pages = json.dumps({"ov": page_ov(D), "spd": page_spd(D),
                                    "tok": page_tok(D)},
                                   ensure_ascii=False)
                alive = []
                for dw in detail_win:
                    try:
                        if dw.hidden:
                            alive.append(dw)
                            continue
                        dw.evaluate_js(
                            "var p=" + pages + ";"
                            "document.getElementById('pg-ov').innerHTML=p.ov;"
                            "document.getElementById('pg-spd').innerHTML=p.spd;"
                            "document.getElementById('pg-tok').innerHTML=p.tok;")
                        alive.append(dw)
                    except Exception:
                        pass  # 窗口已关闭 → 移除
                detail_win[:] = alive
                if not w.hidden:  # 缩到托盘时页面不可见，跳过推送与调高
                    # 高度随内容自适应：亮色卡有边框/内边距，按 body 整体实测
                    h_css = w.evaluate_js("document.body.offsetHeight")
                    hwnd = u32.FindWindowW(None, TITLE)
                    if hwnd and h_css:
                        dpi = u32.GetDpiForWindow(hwnd) or 96
                        u32.SetWindowPos(hwnd, None, 0, 0,
                                         round(250 * dpi / 96),
                                         round(h_css * dpi / 96), 0x0002)
            except Exception as e:
                errlog("push", e)  # 隐藏期间推送失败属预期，仅记日志
            time.sleep(1.0)

    def tray_loop():
        # 系统托盘：左键单击=显示窗口，右键菜单=退出
        import os
        import pystray
        from PIL import Image, ImageDraw
        try:  # 托盘用与窗口/快捷方式同一枚 ico；exe 由 --add-data 带入 _MEIPASS
            img = Image.open(os.path.join(
                os.path.dirname(os.path.abspath(__file__)),
                "token_speed.ico")).convert("RGBA").resize((64, 64))
        except Exception:
            img = Image.new("RGB", (64, 64), "#10151e")
            d = ImageDraw.Draw(img)
            d.ellipse((16, 16, 48, 48), outline="#b7ead4", width=3)
            d.text((24, 22), "T", fill="#b7ead4")

        def on_show(icon, item):
            w.show()

        def on_quit(icon, item):
            icon.stop()
            os._exit(0)  # push_loop 可能卡在已销毁窗口的 COM 上，_exit 兜底

        menu = pystray.Menu(
            pystray.MenuItem("详情", on_detail),
            pystray.MenuItem("显示", on_show, default=True),
            pystray.MenuItem("退出", on_quit))
        pystray.Icon("zcode_tps", img, TITLE, menu).run()

    w = webview.create_window(TITLE, html=HTML, width=250,
                              height=118, frameless=True, on_top=True,
                              transparent=True)
    w.events.closing += lambda: (w.hide(), False)[1]  # × = 隐藏到托盘（False=阻止销毁）
    detail_win = []
    cur_tool = ["zcode"]  # 工具切换：详情窗下拉设置，面板全局跟随（push_loop 每秒轮询）
    # 热更新只在源码运行时可用：exe 打包后主脚本不落盘（__file__ 指向不存在路径）
    src_file = __file__ if os.path.exists(__file__) else None
    last_src = [os.path.getmtime(src_file) if src_file else 0]

    def hot_reload():
        global LOG
        # 重新执行磁盘上的源码，替换本模块全局（HTML/detail_html/函数全换新），
        # 再整页重载所有窗口。__name__ 用别名防触发底部 main()。
        flag = LOG
        ns = {"__name__": "tps_hot", "__file__": src_file}
        exec(compile(open(src_file, encoding="utf-8").read(), src_file, "exec"), ns)
        globals().update(ns)
        LOG = flag  # exec 把模块级 LOG 重置为 None，update 后还原 --log-file 状态
        w.load_html(HTML)
        if cur_tool[0] == "codex":
            D = detail_data(codex_db(), {"codex": "Codex"}, "codex")
        else:
            D = detail_data(db, provs, "zcode")
        for dw in detail_win:
            try:
                dw.load_html(detail_html(D))
            except Exception:
                pass  # 窗口刚关闭 → 下轮清列表

    def on_detail(icon=None, item=None):
        # 详情弹窗：打开瞬间的快照。zcode 用独立只读连接，codex 用共享缓存库
        try:
            if cur_tool[0] == "codex":
                D = detail_data(codex_db(), {"codex": "Codex"}, "codex")
            else:
                d = connect()
                D = detail_data(d, provs, "zcode")
                d.close()
            win = webview.create_window(
                DETAIL_TITLE, html=detail_html(D),
                width=510, height=570, on_top=False)
            detail_win.append(win)  # 持引用防 GC
        except Exception as e:
            errlog("detail", e)

    def set_window_icon():
        # 源码运行时窗口/任务栏图标是 python.exe 默认的，WM_SETICON 换成本项目 ico；
        # exe 版图标已嵌入，此函数同样无害。
        # 常驻轮询：主窗 + 后建的详情窗（含看门狗重建）凡标题匹配一律设图标。
        ico = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           "token_speed.ico")
        if not os.path.exists(ico):
            return
        for _ in range(50):
            if u32.FindWindowW(None, TITLE):
                break
            time.sleep(0.2)
        else:
            return
        hicon = u32.LoadImageW(None, ico, 1, 0, 0, 0x10)  # IMAGE_ICON, LR_LOADFROMFILE
        if not hicon:
            return
        titles = (TITLE, DETAIL_TITLE)

        def cb(h, _):
            buf = ctypes.create_unicode_buffer(64)
            u32.GetWindowTextW(h, buf, 64)
            if buf.value in titles and u32.IsWindowVisible(h):
                u32.SendMessageW(h, 0x0080, 1, hicon)  # WM_SETICON ICON_BIG
                u32.SendMessageW(h, 0x0080, 0, hicon)  # WM_SETICON ICON_SMALL
            return True

        F = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_long, ctypes.c_long)
        while True:
            u32.EnumWindows(F(cb), 0)
            time.sleep(3)

    def panel_watchdog():
        # 任务栏右键关闭等路径会直接销毁窗口（绕过 closing 事件）。只要进程活着，
        # 3 秒内自动重建简略面板（含图标），面板永不消失。
        nonlocal w
        while True:
            time.sleep(3)
            try:
                if not u32.FindWindowW(None, TITLE):
                    w = webview.create_window(
                        TITLE, html=HTML, width=250,
                        height=118, frameless=True, on_top=True, transparent=True)
                    w.events.closing += lambda: (w.hide(), False)[1]
                    set_window_icon()
            except Exception as e:
                errlog("watchdog", e)

    threading.Thread(target=set_window_icon, daemon=True).start()
    threading.Thread(target=panel_watchdog, daemon=True).start()
    threading.Thread(target=hover_loop, daemon=True).start()
    threading.Thread(target=tray_loop, daemon=True).start()
    if "--detail" in sys.argv:  # 测试钩子：4s 后自动打开详情（同托盘点击代码路径）
        threading.Thread(target=lambda: (time.sleep(4), on_detail()),
                         daemon=True).start()
    webview.start(push_loop)  # 推送循环跑在 pywebview 管理线程，evaluate_js 才安全


def single_instance():
    """已有一个监控窗口时，二次启动只聚焦已有窗口并退出。"""
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    u32 = ctypes.WinDLL("user32", use_last_error=True)
    k32.CreateMutexW(None, False, "TokenDetails_Mutex")
    if ctypes.get_last_error() == 183:  # ERROR_ALREADY_EXISTS
        hwnd = u32.FindWindowW(None, TITLE)
        if hwnd:
            u32.ShowWindow(hwnd, 9)  # SW_RESTORE
            u32.SetForegroundWindow(hwnd)
        sys.exit(0)


def main():
    if "--once" not in sys.argv and "--self-check" not in sys.argv:
        single_instance()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if "--self-check" in sys.argv:
        self_check()
        return
    db = connect()
    try:
        db.execute("SELECT 1 FROM model_usage LIMIT 1")
    except sqlite3.Error as exc:
        sys.exit(f"无法读取 model_usage: {exc}")
    if "--once" in sys.argv:
        print_once(db)
        return
    global LOG
    if "--log-file" in sys.argv:
        i = sys.argv.index("--log-file")
        if i + 1 >= len(sys.argv):
            sys.exit("用法: --log-file 需要一个路径参数")
        LOG = sys.argv[i + 1]
    run_web(db)


if __name__ == "__main__":
    main()
