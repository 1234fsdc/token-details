# -*- coding: utf-8 -*-
"""Token Details（原 ZCode Token 速度监控）v3 — 图形窗口

用法:
  python token_speed.py              图形窗口（表格+柱状图+今日汇总）
  python token_speed.py --once       打印一帧数据后退出
  python token_speed.py --self-check 跑 tps 计算断言

数据源: ~/.zcode/cli/db/db.sqlite 的 model_usage 表（ZCode 回复完成后落库）
计划: token-speed-v2-plan.md
"""
import ctypes
import ctypes.wintypes as wintypes
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
CHART_N = 20   # 柱状图条数
POLL_MS = 1000
LOG = None  # --log-file PATH：内容变化时追加一帧，测试观察口
TITLE = "Token Details"          # 主窗/托盘名（HTML <title> 必须与其一致，FindWindowW 锚点）
DETAIL_TITLE = "Token Details 详情"


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
    """最近 limit 条请求，完成时间新→旧。id 列是 TEXT 不可排序，按时间排。"""
    return db.execute(
        f"SELECT {COLS} FROM model_usage "
        f"ORDER BY COALESCE(completed_at, started_at) DESC LIMIT ?",
        (limit,)).fetchall()


def today_summary(db):
    day0 = int(datetime.now().replace(hour=0, minute=0, second=0,
                                      microsecond=0).timestamp() * 1000)
    return db.execute(
        """SELECT COUNT(*), COALESCE(SUM(output_tokens+reasoning_tokens), 0),
                  COALESCE(SUM(CASE WHEN first_token_at IS NOT NULL
                                    THEN completed_at - first_token_at
                                    ELSE duration_ms END), 0)
           FROM model_usage WHERE status='completed' AND completed_at >= ?""",
        (day0,)).fetchone()  # (次数, tokens, 生成窗口ms)


def provider_names():
    """provider_id -> 可读名，启动时读一次。名称分散在 cli 和 v2 两份 config。"""
    names = {}
    for path in (r"C:/Users/木/.zcode/cli/config.json",
                 r"C:/Users/木/.zcode/v2/config.json"):
        try:
            cfg = json.load(open(path, encoding="utf-8"))
        except Exception:
            continue
        provs = cfg.get("providers") or cfg.get("provider") or {}
        if isinstance(provs, list):
            provs = {p.get("id", "?"): p for p in provs if isinstance(p, dict)}
        for pid, p in provs.items():
            if isinstance(p, dict) and p.get("name"):
                names[pid] = p["name"]
    return names


def calc(row):
    """row -> (tps, ttft_s, mark)；mark='*' 表示回退 duration（含排队）。"""
    (_id, _prov, _model, status, started, ft, done, dur, out, reason) = row
    if status != "completed" or not done:
        return None
    tok = (out or 0) + (reason or 0)
    if ft and done > ft:
        ttft = (ft - started) / 1000.0 if started and ft >= started else None
        return tok * 1000.0 / (done - ft), ttft, ""
    if dur and dur > 0:
        return tok * 1000.0 / dur, None, "*"
    return None


def fmt_cells(row, provs):
    """row -> (时间, 模型, 供应商, 速度str, tps数值, 首字, tokens, status)"""
    (_id, prov_id, model, status, started, ft, done, dur, out, reason) = row
    prov = provs.get(prov_id, (prov_id or "?")[:8])
    when = datetime.fromtimestamp(done / 1000).strftime("%H:%M") if done else "-"
    tok_s = f"{out or 0}+{reason or 0}"
    if status != "completed":
        return when, model or "?", prov, status, 0.0, "-", tok_s, status
    c = calc(row)
    if c is None:
        return when, model or "?", prov, "无数据", 0.0, "-", tok_s, status
    tps, ttft, mark = c
    return (when, model or "?", prov, f"{tps:.1f}{mark}", tps,
            f"{ttft:.1f}s" if ttft is not None else "-", tok_s, status)



def self_check():
    r = ("x", "p", "m", "completed", 1000, 2000, 3000, 2500, 500, 0)
    tps, ttft, mark = calc(r)
    assert abs(tps - 500) < 0.01 and abs(ttft - 1.0) < 0.01 and mark == ""
    r2 = ("x", "p", "m", "completed", 1000, None, 3000, 2000, 500, 0)
    tps2, ttft2, mark2 = calc(r2)
    assert abs(tps2 - 250) < 0.01 and ttft2 is None and mark2 == "*"
    assert calc(("x", "p", "m", "error", 1000, None, 3000, 100, 10, 0)) is None
    assert calc(("x", "p", "m", "completed", 1000, None, 3000, 0, 500, 0)) is None
    # avg_by_model：同模型两条不同会话请求 → 合并加权平均（1000ms/500tok=500 + 2000ms/1000tok=500 → 1500ms/1500tok=500）
    now = 100000
    a = ("x", "p", "glm", "completed", now, now + 1000, now + 2000, 0, 500, 0)
    b = ("y", "p", "glm", "completed", now + 3000, now + 4000, now + 6000, 0, 1000, 0)
    c = ("z", "p", "glm", "completed", now + 7000, None, now + 8000, 1000, 500, 0)
    aggs = avg_by_model([b, c, a])  # c 无精确首字 → 舍弃，只算 a+b
    assert len(aggs) == 1 and abs(aggs[0]["tps"] - 500.0) < 0.01 and aggs[0]["mark"] == "", aggs
    # detail_inner：会话行 + 今日统计 + 速度条渲染
    inner = detail_inner([{"sid": "s", "title": "测试会话", "cnt": 2, "tok": 1500,
                           "tps": 500.0, "mark": "", "last": 100000,
                           "ttft": 1.0, "rpm": 2.0}], {}, 3, 1500, 3000)
    assert "测试会话" in inner and "500.0" in inner and "width:100%" in inner
    print("self-check OK")


def print_once(db):
    provs = provider_names()
    for row in latest_by_model(fetch_recent(db, limit=TABLE_N)):
        c = fmt_cells(row, provs)
        print(f"{c[1]}  [{c[2]}]  {c[0]}\n  {c[3]} t/s  首字 {c[5]}  {c[6]} tok")
    n, tok, ms = today_summary(db)
    avg = tok * 1000.0 / ms if ms else 0.0
    print(f"— 今日: {n} 次 · {tok} tok · 均 {avg:.1f} t/s")


def latest_by_model(rows, window_ms=3 * 60 * 1000):
    """rows 已按时间新→旧，每模型取最新一条 → 每模型一行。
    3 分钟无新完成回复 = 结束，行消失（用户定标 B）。"""
    seen = {}
    cutoff = time.time() * 1000 - window_ms
    for row in rows:
        done = row[6] or row[4]
        if done and done < cutoff:
            break
        seen.setdefault(row[2], row)
    return list(seen.values())


def avg_by_model(rows, per=5):
    """每模型聚合最近 per 条有精确首字时间的请求：速度 = 总 tokens ÷ 总生成窗口。
    缺 first_token_at 的请求直接舍弃（回退 duration 含排队会虚低），不再标 *。"""
    by = {}
    for row in rows:
        c = calc(row)
        if c and c[2] == "":  # 只要精确首字行
            by.setdefault(row[2], []).append(row)
    out = []
    for model, lst in by.items():
        recent = lst[:per]
        tot_tok = sum((r[8] or 0) + (r[9] or 0) for r in recent)
        gen_ms = sum(r[6] - r[5] for r in recent)
        if gen_ms > 0:
            out.append({"row": recent[0], "tps": tot_tok * 1000.0 / gen_ms,
                        "mark": ""})
    return out


def session_stats(db, limit=30, window_ms=30 * 60 * 1000):
    """按会话聚合 token 速度（加权：总 tokens ÷ 总生成窗口），标题取 session.title。
    30 分钟无新活动的会话不显示；有回退行（无精确首字时间，含排队）的会话标 *。"""
    cutoff = time.time() * 1000 - window_ms
    rows = db.execute(
        """SELECT u.session_id,
                  COALESCE(s.title, u.session_id),
                  COUNT(*),
                  SUM(u.output_tokens + u.reasoning_tokens),
                  SUM(CASE WHEN u.first_token_at IS NOT NULL AND u.completed_at IS NOT NULL
                                AND u.completed_at > u.first_token_at
                           THEN u.completed_at - u.first_token_at ELSE u.duration_ms END),
                  MAX(COALESCE(u.completed_at, u.started_at)),
                  SUM(CASE WHEN u.first_token_at IS NULL THEN 1 ELSE 0 END),
                  AVG(CASE WHEN u.first_token_at IS NOT NULL AND u.started_at IS NOT NULL
                                AND u.first_token_at >= u.started_at
                           THEN u.first_token_at - u.started_at END),
                  MIN(COALESCE(u.completed_at, u.started_at))
           FROM model_usage u LEFT JOIN session s ON s.id = u.session_id
           GROUP BY u.session_id
           HAVING MAX(COALESCE(u.completed_at, u.started_at)) >= ?
           ORDER BY 6 DESC LIMIT ?""", (cutoff, limit)).fetchall()
    out = []
    for sid, title, cnt, tok, gen_ms, last, fallbacks, ttft_ms, first in rows:
        tps = tok * 1000.0 / gen_ms if gen_ms else 0.0
        span_min = (last - first) / 60000.0 if first and last and last > first else 0.0
        out.append({"sid": sid, "title": title or sid, "cnt": cnt, "tok": tok or 0,
                    "tps": tps, "mark": "*" if fallbacks else "", "last": last,
                    "ttft": ttft_ms / 1000.0 if ttft_ms else None,
                    "rpm": cnt / span_min if span_min else 0.0})
    return out


def detail_inner(rows, provs, n, tok, ms):
    """详情窗活动区（今日统计 + 会话列表）。push_loop 每秒只替换 #live，
    不再整页 document.write（滚动位置不丢）。"""
    body = ""
    for s in rows:
        cls = "fast" if s["tps"] >= 80 else ("mid" if s["tps"] >= 50 else "slow")
        when = datetime.fromtimestamp(s["last"] / 1000).strftime("%m-%d %H:%M") \
            if s["last"] else "-"
        ttft = f"{s['ttft']:.1f}s" if s["ttft"] is not None else "-"
        w = min(100, round(s["tps"] / 120 * 100))  # bar 满格 = 120 t/s
        body += (f"<div class='row'><div class='t1'><span class='nm'>{s['title']}</span>"
                 f"<span class='meta'><span>首字 {ttft}</span>"
                 f"<span>{s['rpm']:.1f} 次/分</span><span>{s['cnt']} 次</span>"
                 f"<span>{s['tok']:,} tok</span><span>{when}</span></span>"
                 f"<span class='v {cls}'>{s['tps']:.1f}{s['mark']}</span></div>"
                 f"<div class='bar'><i class='{cls}' style='width:{w}%'></i></div></div>")
    avg = tok * 1000.0 / ms if ms else 0.0
    avg_cls = "fast" if avg >= 80 else ("mid" if avg >= 50 else "slow")
    return (f"<div class='head'><span class='t'>速度详情</span><span class='dot'></span>"
            f"<span class='d'>{datetime.now().strftime('%m-%d')} · 实时刷新</span></div>"
            f"<div class='stats'>"
            f"<div class='stat'><div class='k'>今日请求</div>"
            f"<div class='v'>{n}<small> 次</small></div></div>"
            f"<div class='stat'><div class='k'>平均速度</div>"
            f"<div class='v {avg_cls}'>{avg:.1f}<small> t/s</small></div></div>"
            f"<div class='stat'><div class='k'>今日 tokens</div>"
            f"<div class='v'>{tok:,}</div></div>"
            f"</div><div class='sec'>活跃会话 · 30 分钟窗口</div>"
            + (body or "<div class='empty'>30 分钟内无活动会话</div>"))


def detail_html(rows, provs, n, tok, ms):
    """详情弹窗整页（打开瞬间渲染，方案 A「纸面印刷」亮色）；此后每秒只换 #live。"""
    return f"""<!doctype html><html><head><meta charset="utf-8"><title>{DETAIL_TITLE}</title><style>
*{{margin:0;box-sizing:border-box}}
:root{{--ink:#1a1d22;--muted:#9a978c;--hair:#f0ece1;--line:#e6e2d8;
--fast:#0a8f46;--mid:#d97706;--slow:#dc2626;
--num:Bahnschrift,"Segoe UI Variable Display","Segoe UI",sans-serif}}
html{{color-scheme:light}}
body{{font-family:"Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;color:var(--ink);
background:#fffdf8;font-size:13px}}
::-webkit-scrollbar{{width:8px}}
::-webkit-scrollbar-thumb{{background:#d9d2c4;border-radius:4px}}
#live{{height:100vh;overflow-y:auto;padding:20px 26px}}
.head{{display:flex;align-items:center;gap:8px;padding-bottom:14px}}
.head .t{{font-size:15px;font-weight:700}}
.head .dot{{width:7px;height:7px;border-radius:50%;background:#ff4d2e;
box-shadow:0 0 0 3px rgba(255,77,46,.15)}}
.head .d{{margin-left:auto;font-size:11px;color:var(--muted);font-variant-numeric:tabular-nums}}
.stats{{display:flex;border-top:1px solid #e9e5da;border-bottom:1px solid #e9e5da}}
.stat{{flex:1;padding:14px 18px 13px;border-left:1px solid #e9e5da}}
.stat:first-child{{padding-left:2px;border-left:none}}
.k{{font-size:9.5px;color:var(--muted);text-transform:uppercase;letter-spacing:1.8px;margin-bottom:5px}}
.stat .v{{font-family:var(--num);font-size:30px;font-weight:700;
font-variant-numeric:tabular-nums;line-height:1}}
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
.empty{{color:var(--muted);text-align:center;padding:40px 0}}
</style></head><body><div id="live">{detail_inner(rows, provs, n, tok, ms)}</div></body></html>"""


HTML = """<!doctype html><html><head><meta charset="utf-8">
<title>Token Details</title><style>
*{margin:0;box-sizing:border-box}
:root{--ink:#1a1d22;--muted:#9a978c;--hair:#f0ece1;--line:#e6e2d8;
--fast:#0a8f46;--mid:#d97706;--slow:#dc2626;
--num:Bahnschrift,"Segoe UI Variable Display","Segoe UI",sans-serif}
html{color-scheme:light}
body{font-family:"Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;color:var(--ink);
background:#fffdf8;overflow:hidden;user-select:none;min-height:52px}
#grip{position:absolute;top:5px;left:9px;color:#b3ac9a;font-size:10px;
letter-spacing:2px;cursor:pointer;line-height:1}
#rows{padding:24px 16px 7px}
.row{padding:10px 2px;border-bottom:1px solid var(--hair)}
.row:last-child{border-bottom:none;padding-bottom:3px}
.l1{display:flex;align-items:baseline;gap:8px}
.nm{font-size:12px;font-weight:600;letter-spacing:.1px;min-width:0;flex:0 1 auto;
overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.prov{font-size:9px;color:var(--muted);text-transform:uppercase;letter-spacing:1px;
white-space:nowrap;flex:1 1 0;min-width:0;
overflow:hidden;text-overflow:ellipsis}
.v{margin-left:auto;font-family:var(--num);font-size:19px;font-weight:700;
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
function update(d){
  var h="";
  for(const r of d.rows){
    var c=cls(r.tpsV), w=Math.min(100,Math.round(r.tpsV/120*100));
    h+=`<div class="row"><div class="l1"><span class="nm">${r.model}</span><span class="prov">${r.prov}</span><span class="v ${c}">${r.tps}<small> t/s</small></span></div><div class="bar"><i class="${c}" style="width:${w}%"></i></div></div>`;
  }
  document.getElementById("rows").innerHTML=h||'<div class="empty">暂无数据</div>';
}
</script></body></html>"""



def run_web(db):
    import threading
    import webview
    provs = provider_names()

    u32 = ctypes.WinDLL("user32", use_last_error=True)
    dpi = ctypes.WinDLL("user32", use_last_error=True).GetDpiForSystem() or 96

    class POINT(ctypes.Structure):
        _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]

    class RECT(ctypes.Structure):
        _fields_ = [("l", ctypes.c_long), ("t", ctypes.c_long),
                    ("r", ctypes.c_long), ("b", ctypes.c_long)]

    GRIP_W, GRIP_H = 42, 20  # 把手热区（CSS px，面板左上角）

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
                    in_grip = (inside and pt.x <= rc.l + round(GRIP_W * dpi / 96)
                               and pt.y <= rc.t + round(GRIP_H * dpi / 96))
                    ex = u32.GetWindowLongW(mh, -20) & 0xFFFFFFFF
                    # 简略面板不占任务栏：TOOLWINDOW 生效必须显式清 APPWINDOW，
                    # 否则两者并存时 APPWINDOW 强制显示任务栏按钮
                    ex = (ex & ~0x40000 & 0xFFFFFFFF) | 0x80000 | 0x80 | 0x20
                    if in_grip:
                        ex &= ~0x20 & 0xFFFFFFFF  # 把手区可交互（显式清位）
                    else:
                        ex |= 0x20  # 其余区域穿透
                    u32.SetWindowLongW(mh, -20, ex)
                    # 亮色卡在深色壁纸上要够实才能读：默认 150 / 悬停 220 / 把手 235
                    u32.SetLayeredWindowAttributes(
                        mh, 0, 235 if in_grip else (220 if inside else 150), 2)
                    if in_grip and u32.GetAsyncKeyState(0x01) & 0x8000:
                        x0, y0, wx, wy = pt.x, pt.y, rc.l, rc.t
                        while u32.GetAsyncKeyState(0x01) & 0x8000:
                            time.sleep(0.02)
                            u32.GetCursorPos(ctypes.byref(pt))
                            u32.SetWindowPos(mh, None, wx + pt.x - x0,
                                             wy + pt.y - y0, 0, 0,
                                             0x0001 | 0x0010)  # NOSIZE|NOACTIVATE
            except Exception as e:
                try:
                    with open(r'C:/Users/Public/weberr.log', 'a', encoding='utf-8') as f:
                        f.write(f"[hover] {type(e).__name__}: {e}\n")
                except Exception:
                    pass
            time.sleep(0.05)

    def push_loop():
        # evaluate_js 推送（修线程后已验证 60s 稳定）；行数固定为启动时值
        fails = 0
        time.sleep(1.5)
        while True:
            try:
                if src_file and os.path.getmtime(src_file) != last_src[0]:  # 热更新检查
                    last_src[0] = os.path.getmtime(src_file)
                    hot_reload()
                    log_frame("hot-reload ok")
                rows = fetch_recent(db, limit=TABLE_N)
                cutoff = time.time() * 1000 - 3 * 60 * 1000  # 3min 无活动不显示（终 9 定标）
                items = []
                for agg in avg_by_model(rows):
                    r = agg["row"]
                    if (r[6] or r[4] or 0) < cutoff:
                        continue
                    c = fmt_cells(agg["row"], provs)  # 最新条提供 when/tok 等展示
                    items.append({"model": agg["row"][2], "prov": c[2],
                                  "when": c[0], "tps": f'{agg["tps"]:.1f}{agg["mark"]}',
                                  "tpsV": round(agg["tps"]), "ttft": c[5],
                                  "tok": c[6]})
                payload = json.dumps({"rows": items}, ensure_ascii=False)
                w.evaluate_js(f"update({payload})")
                # 详情窗实时刷新：重查会话统计，整页重写（复用 detail_html，零渲染逻辑重复）
                alive = []
                for dw in detail_win:
                    try:
                        if dw.hidden:
                            alive.append(dw)
                            continue
                        sess = session_stats(db)
                        n, tok, ms = today_summary(db)
                        inner = json.dumps(detail_inner(sess, provs, n, tok, ms))
                        dw.evaluate_js(
                            f"document.getElementById('live').innerHTML={inner};")
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
                fails = 0
            except Exception as e:
                fails += 1
                if fails > 3:  # 隐藏期间推送失败属预期，不再自愈弹窗
                    pass
                try:
                    import io as _io
                    with open(r'C:/Users/Public/weberr.log', 'a', encoding='utf-8') as f:
                        f.write(f"[{datetime.now().strftime('%H:%M:%S')}] {type(e).__name__}: {e}\n")
                except Exception:
                    pass
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
        sess = session_stats(db)
        n, tok, ms = today_summary(db)
        for dw in detail_win:
            try:
                dw.load_html(detail_html(sess, provs, n, tok, ms))
            except Exception:
                pass  # 窗口刚关闭 → 下轮清列表

    def on_detail(icon=None, item=None):
        # 详情弹窗：打开瞬间的快照。独立只读连接，避免与 push_loop 并发共用同一连接
        try:
            d = connect()
            sessions = session_stats(d)
            n, tok, ms = today_summary(d)
            d.close()
            win = webview.create_window(
                DETAIL_TITLE, html=detail_html(sessions, provs, n, tok, ms),
                width=680, height=760, on_top=True)
            detail_win.append(win)  # 持引用防 GC
        except Exception as e:
            try:
                with open(r'C:/Users/Public/weberr.log', 'a', encoding='utf-8') as f:
                    f.write(f"[detail] {type(e).__name__}: {e}\n")
            except Exception:
                pass

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
                try:
                    with open(r'C:/Users/Public/weberr.log', 'a', encoding='utf-8') as f:
                        f.write(f"[watchdog] {type(e).__name__}: {e}\n")
                except Exception:
                    pass

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
        LOG = sys.argv[sys.argv.index("--log-file") + 1]
    run_web(db)


if __name__ == "__main__":
    main()
