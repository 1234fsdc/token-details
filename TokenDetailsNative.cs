// Token Details 原生版（2026-10-05 用户改定：完全重写，性能最优）。
// 单进程 C#（.NET Framework 4.x，系统自带 csc）：UIA 内联 + SQLite P/Invoke
// （sqlite3.dll 取自本机 Python 运行时，随包分发）+ GDI+ 分层窗口 overlay + 托盘。
// 语义逐条对照 token_speed.py（见 docs/native-rewrite-plan.md 语义保持清单）。
// 编译：build_native.bat（Git Bash 下 csc 选项用 - 前缀，/r 会被 MSYS 转路径）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Collections.Generic;

class TDN
{
    // ---- 常量（与 token_speed.py 一致）----
    const string ZCodeTitle = "ZCode";
    const string OverlayTitle = "Token Details ZCode";
    const string MutexName = "TokenDetails_Mutex";
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string DbPath = Home + "\\.zcode\\cli\\db\\db.sqlite";
    const string ErrLogPath = "C:/Users/Public/weberr.log";
    const int OverlayW = 178, OverlayH = 32;      // 逻辑尺寸（CSS px）
    const int OverlayGap = 40;                    // 与“选择打开方式”按钮的间距
    const int SpeedWindow = 8;                    // 每模型加权窗口（条）
    const int SpeedQueryLimit = 50;               // session_speed_data 的查询 LIMIT（TABLE_N）
    static readonly Color InkValue = Color.FromArgb(255, 0x5F, 0x63, 0x68);   // 数字 #5f6368
    static readonly Color InkMuted = Color.FromArgb(255, 0x9A, 0xA1, 0xAB);   // 单位 #9aa1ab
    static readonly Color InkEmpty = Color.FromArgb(255, 0xB9, 0xBF, 0xC7);   // 空态 #b9bfc7

    // ---- 日志（与 errlog 同文件同格式）----
    static readonly object LogLock = new object();
    static void ErrLog(string tag, string msg)
    {
        try
        {
            lock (LogLock)
            {
                if (File.Exists(ErrLogPath) && new FileInfo(ErrLogPath).Length > 1000000)
                    File.WriteAllText(ErrLogPath, "");
                File.AppendAllText(ErrLogPath,
                    string.Format("[{0:HH:mm:ss}] [{1}] {2}\r\n", DateTime.Now, tag, msg),
                    Encoding.UTF8);
            }
        }
        catch { }
    }

    // ================= SQLite P/Invoke（只读） =================
    const int SQLITE_OPEN_READONLY = 0x01;
    const int SQLITE_ROW = 100;

    [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_open_v2(byte[] fn, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int len, out IntPtr stmt, IntPtr tail);
    [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_step(IntPtr stmt);
    [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_close(IntPtr db);
    [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr sqlite3_errmsg(IntPtr db);
    [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_bind_int64(IntPtr stmt, int i, long v);
    [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_bind_double(IntPtr stmt, int i, double v);
    [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_bind_text(IntPtr stmt, int i, byte[] v, int n, IntPtr dtor);
    [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_column_type(IntPtr stmt, int i);
    [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern long sqlite3_column_int64(IntPtr stmt, int i);
    [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern double sqlite3_column_double(IntPtr stmt, int i);
    [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr sqlite3_column_text(IntPtr stmt, int i);
    [DllImport("sqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_column_bytes(IntPtr stmt, int i);

    static IntPtr SqlTransient = new IntPtr(-1);
    static IntPtr DbH = IntPtr.Zero;

    static byte[] Utf8z(string s) { return Encoding.UTF8.GetBytes(s + "\0"); }

    static bool DbOpen()
    {
        IntPtr h;
        int rc = sqlite3_open_v2(Utf8z(DbPath), out h, SQLITE_OPEN_READONLY, IntPtr.Zero);
        if (rc != 0)
        {
            ErrLog("db", "open rc=" + rc);
            if (h != IntPtr.Zero) sqlite3_close(h);
            return false;
        }
        DbH = h;
        return true;
    }

    static string ColText(IntPtr stmt, int i)
    {
        int bn = sqlite3_column_bytes(stmt, i);
        IntPtr p = sqlite3_column_text(stmt, i);
        if (p == IntPtr.Zero || bn <= 0) return "";
        byte[] b = new byte[bn];
        Marshal.Copy(p, b, 0, bn);
        return Encoding.UTF8.GetString(b);
    }

    // rows[i] = object[]；TEXT→string、INTEGER→long、FLOAT→double、NULL→null
    static object[][] Query(string sql, params object[] args) { return QueryH(DbH, sql, args); }
    static object[][] QueryH(IntPtr db, string sql, params object[] args)
    {
        IntPtr stmt;
        int rc = sqlite3_prepare_v2(db, Utf8z(sql), -1, out stmt, IntPtr.Zero);
        if (rc != 0)
        {
            ErrLog("db", "prepare " + Marshal.PtrToStringAnsi(sqlite3_errmsg(db)));
            return new object[0][];
        }
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                object a = args[i];
                if (a is int) sqlite3_bind_int64(stmt, i + 1, (int)a);
                else if (a is long) sqlite3_bind_int64(stmt, i + 1, (long)a);
                else if (a is double) sqlite3_bind_double(stmt, i + 1, (double)a);
                else
                {
                    byte[] b = Encoding.UTF8.GetBytes((string)a);
                    sqlite3_bind_text(stmt, i + 1, b, b.Length, SqlTransient);
                }
            }
            object[][] rows = new object[32][];
            int n = 0;
            while (sqlite3_step(stmt) == SQLITE_ROW)
            {
                int cols = sqlite3_column_count(stmt);
                object[] r = new object[cols];
                for (int c = 0; c < cols; c++)
                {
                    int t = sqlite3_column_type(stmt, c);
                    if (t == 5) r[c] = null;              // NULL
                    else if (t == 1) r[c] = sqlite3_column_int64(stmt, c);
                    else if (t == 2) r[c] = sqlite3_column_double(stmt, c);
                    else r[c] = ColText(stmt, c);
                }
                if (n == rows.Length) Array.Resize(ref rows, rows.Length * 2);
                rows[n++] = r;
            }
            object[][] outp = new object[n][];
            Array.Copy(rows, outp, n);
            return outp;
        }
        finally { sqlite3_finalize(stmt); }
    }
    static string S(object v) { return v == null ? null : (v is string ? (string)v : v.ToString()); }
    static long L(object v) { return v == null ? 0 : (v is long ? (long)v : Convert.ToInt64(v)); }

    // ============ 统计口径（对照 calc/_elapsed_ms/avg_by_model/session_speed_data） ============
    // COLS: id, provider_id, model_id, status, started_at, first_token_at,
    //       completed_at, duration_ms, output_tokens, reasoning_tokens
    static long ElapsedMs(object[] r)
    {
        long s = L(r[4]), d = L(r[6]);
        return (s > 0 && d > s) ? d - s : 0;   // duration_ms 不兜底，缺/逆序时间戳不参与
    }
    static long RowTok(object[] r) { return L(r[8]) + L(r[9]); }

    class ModelAgg { public string Model; public string Provider; public string Tps; public long Last; }

    // ---- 无回报模型估算（对照 _est_map：窗口内 >=3 条完成行且输出回报合计 <20 tok
    //      判为"无回报"，用 part 正文字符数 x 0.6 折算；数据驱动不点名模型） ----
    const double TokPerChar = 0.6;
    static Dictionary<string, long> EstMap(IntPtr db, object[][] rows)
    {
        Dictionary<string, long[]> stat = new Dictionary<string, long[]>();
        for (int i = 0; i < rows.Length; i++)
        {
            object[] r = rows[i];
            if (S(r[3]) != "completed") continue;
            string m = S(r[2]);
            if (m == null) continue;
            long[] st;
            if (!stat.TryGetValue(m, out st)) { st = new long[2]; stat[m] = st; }
            st[0]++; st[1] += RowTok(r);
        }
        Dictionary<string, bool> silent = new Dictionary<string, bool>();
        foreach (KeyValuePair<string, long[]> kv in stat)
            if (kv.Value[0] >= 3 && kv.Value[1] < 20) silent[kv.Key] = true;
        Dictionary<string, long> empty = new Dictionary<string, long>();
        if (silent.Count == 0) return empty;
        // usage.id 内嵌 message.id：msg_ 起截取、去掉尾部 _n
        Dictionary<string, string> midOf = new Dictionary<string, string>();
        for (int i = 0; i < rows.Length; i++)
        {
            object[] r = rows[i];
            string m = S(r[2]);
            string uid = S(r[0]);
            if (m == null || uid == null || !silent.ContainsKey(m)) continue;
            int j = uid.IndexOf("msg_");
            if (j < 0) continue;
            string tail = uid.Substring(j);
            int k = tail.LastIndexOf('_');
            if (k >= 0) tail = tail.Substring(0, k);
            midOf[uid] = tail;
        }
        if (midOf.Count == 0) return empty;
        Dictionary<string, long> c2t = new Dictionary<string, long>();
        List<string> mids = new List<string>(midOf.Values);
        try
        {
            for (int j = 0; j < mids.Count; j += 400)
            {   // IN 子句分批（对照 400 批）
                int n = Math.Min(400, mids.Count - j);
                StringBuilder sq = new StringBuilder(
                    "SELECT p.message_id, CAST(COALESCE(SUM("
                    + "LENGTH(COALESCE(json_extract(p.data,'$.text'),''))"
                    + "+LENGTH(COALESCE(json_extract(p.data,'$.state.input'),''))"
                    + "),0)*" + TokPerChar.ToString("0.0#") + " AS INT) "
                    + "FROM part p WHERE p.message_id IN (");
                object[] args = new object[n];
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) sq.Append(',');
                    sq.Append('?');
                    args[i] = mids[j + i];
                }
                sq.Append(") GROUP BY 1");
                object[][] rs = QueryH(db, sq.ToString(), args);
                for (int i = 0; i < rs.Length; i++) c2t[S(rs[i][0])] = L(rs[i][1]);
            }
        }
        catch (Exception) { return empty; }   // part 表异常 → 退回无估算
        Dictionary<string, long> est = new Dictionary<string, long>();
        foreach (KeyValuePair<string, string> kv in midOf)
        {
            long t;
            if (c2t.TryGetValue(kv.Value, out t) && t > 0) est[kv.Key] = t;
        }
        return est;
    }
    // 行的有效 token：回报优先，0 时用估算（对照 calc 的 est 分支）
    static long EffTok(object[] r, Dictionary<string, long> est)
    {
        long tok = RowTok(r);
        if (tok <= 0 && est != null)
        {
            long et;
            if (est.TryGetValue(S(r[0]), out et)) tok = et;
        }
        return tok;
    }

    static ModelAgg[] SessionSpeed(string sid, string modelHint, string providerHint)
    {
        if (String.IsNullOrEmpty(sid) || DbH == IntPtr.Zero) return new ModelAgg[0];
        string cur = modelHint == null ? "" : modelHint.Trim();
        string prov = providerHint == null ? "" : providerHint.Trim();
        if (cur.Length == 0)
        {   // 无 hint：取该会话最新 assistant 消息的模型（对照 Python 回退查询）
            object[][] m = Query("SELECT COALESCE(json_extract(data,'$.modelId'), "
                + "json_extract(data,'$.modelID')), COALESCE(json_extract(data,'$.providerId'), "
                + "json_extract(data,'$.providerID')) FROM message "
                + "WHERE session_id=? AND json_extract(data,'$.role')='assistant' "
                + "ORDER BY time_updated DESC LIMIT 1", sid);
            if (m.Length > 0)
            {
                cur = CanonicalMessageModel(S(m[0][0]), S(m[0][1]));
                if (prov.Length == 0) prov = S(m[0][1]) ?? "";
            }
        }
        string q = "SELECT id, provider_id, model_id, status, started_at, first_token_at, "
            + "completed_at, duration_ms, output_tokens, reasoning_tokens FROM model_usage "
            + "WHERE session_id=? AND status='completed' AND query_source!='compact' ";
        List<object> argList = new List<object>(); argList.Add(sid);
        if (cur.Length > 0)
        {
            // 新旧库有时把 provider 前缀写进 model_id，有时拆到 provider_id；两种
            // 只有在 provider 同时匹配时都接受，避免跨 provider 串模型。
            q += "AND (model_id=? OR (model_id=provider_id || '/' || ?)) ";
            argList.Add(cur); argList.Add(cur);
        }
        if (prov.Length > 0) { q += "AND provider_id=? "; argList.Add(prov); }
        q += "ORDER BY COALESCE(completed_at, started_at) DESC LIMIT " + SpeedQueryLimit;
        object[][] rows = Query(q, argList.ToArray());
        Dictionary<string, long> est = EstMap(DbH, rows);
        Dictionary<object[], long> eff = new Dictionary<object[], long>();
        Dictionary<string, List<object[]>> by = new Dictionary<string, List<object[]>>();
        for (int i = 0; i < rows.Length; i++)
        {
            object[] r = rows[i];
            long tok = EffTok(r, est), el = ElapsedMs(r);
            if (tok <= 0 || el <= 0) continue;   // 缺时间戳/无 usage 的行不计速度
            eff[r] = tok;
            string mdl = S(r[2]) ?? "?";
            if (!by.ContainsKey(mdl)) by[mdl] = new List<object[]>();
            by[mdl].Add(r);
        }
        List<ModelAgg> res = new List<ModelAgg>();
        foreach (string mdl in by.Keys)
        {
            List<object[]> lst = by[mdl];
            int per = Math.Min(SpeedWindow, lst.Count);
            long tot = 0, el = 0;
            for (int i = 0; i < per; i++) { tot += eff[lst[i]]; el += ElapsedMs(lst[i]); }
            if (el <= 0) continue;
            ModelAgg a = new ModelAgg();
            a.Model = mdl; a.Provider = S(lst[0][1]) ?? "";
            a.Tps = (tot * 1000.0 / el).ToString("0.0");
            a.Last = L(lst[0][6]) != 0 ? L(lst[0][6]) : L(lst[0][4]);
            res.Add(a);
        }
        res.Sort(delegate (ModelAgg x, ModelAgg y) { return y.Last.CompareTo(x.Last); });
        return res.ToArray();
    }

    static string FindSessionByTitle(string title)
    {
        if (String.IsNullOrEmpty(title) || DbH == IntPtr.Zero) return "";
        object[][] r = Query("SELECT id FROM session WHERE title=? AND time_archived IS NULL "
            + "ORDER BY time_updated DESC LIMIT 1", title);
        if (r.Length > 0) return S(r[0][0]);
        string basep = title.Replace("...", "…").TrimEnd('…');
        if (basep.Length < 8) return "";   // 太短的前缀会误配到无关会话
        string like = basep.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        r = Query("SELECT id FROM session WHERE title LIKE ? ESCAPE '\\' "
            + "AND time_archived IS NULL ORDER BY time_updated DESC LIMIT 1", like);
        return r.Length > 0 ? S(r[0][0]) : "";
    }

    static string FallbackSession(long nowMs)
    {
        object[][] r = Query("SELECT id FROM session WHERE time_archived IS NULL "
            + "AND time_updated >= ? ORDER BY time_updated DESC LIMIT 1", nowMs - 10 * 60 * 1000);
        return r.Length > 0 ? S(r[0][0]) : "";
    }

    static long SessionLastAct(string sid)
    {
        object[][] r = Query("SELECT MAX(time_updated) FROM (SELECT time_updated FROM part "
            + "WHERE session_id=? ORDER BY rowid DESC LIMIT 200)", sid);
        return r.Length > 0 ? L(r[0][0]) : 0;
    }

    static RunInfo RunningModelForSession(string sid, string uiModel, string uiProvider)
    {
        if (String.IsNullOrEmpty(sid) || DbH == IntPtr.Zero) return null;
        long cutoff = UnixMs() - 30 * 60 * 1000L;
        object[][] rows = Query("SELECT COALESCE(json_extract(m.data,'$.modelId'), "
            + "json_extract(m.data,'$.modelID')), COALESCE(json_extract(m.data,'$.providerId'), "
            + "json_extract(m.data,'$.providerID')), MIN(m.time_created) "
            + "FROM message m WHERE m.session_id=? "
            + "AND json_extract(m.data,'$.role')='assistant' AND m.time_created>=? "
            + "AND (json_extract(m.data,'$.finish') IS NULL OR json_extract(m.data,'$.finish')='started') "
            + "AND NOT EXISTS (SELECT 1 FROM model_usage u WHERE u.assistant_message_id=m.id "
            + "OR u.id LIKE '%' || m.id || '%') GROUP BY 1,2", sid, cutoff);
        List<RunInfo> leafCandidates = new List<RunInfo>();
        string leaf = CanonicalUiModel(uiModel);
        for (int i = 0; i < rows.Length; i++)
        {
            string raw = S(rows[i][0]) ?? "";
            string prov = S(rows[i][1]) ?? "";
            string canonical = CanonicalMessageModel(raw, prov);
            RunInfo candidate = new RunInfo();
            candidate.Model = canonical; candidate.Prov = prov; candidate.Started = L(rows[i][2]);
            bool exact = raw == uiModel || canonical == uiModel;
            bool providerOk = uiProvider.Length == 0 || prov == uiProvider;
            if (exact && providerOk) return candidate;
            if (leaf.Length > 0 && canonical == leaf && providerOk)
            {
                bool duplicate = false;
                for (int j = 0; j < leafCandidates.Count; j++)
                    if (leafCandidates[j].Model == candidate.Model
                        && leafCandidates[j].Prov == candidate.Prov) duplicate = true;
                if (!duplicate) leafCandidates.Add(candidate);
            }
        }
        if (leafCandidates.Count == 1) return leafCandidates[0];
        if (leaf.Length == 0 && rows.Length == 1)
        {
            RunInfo only = new RunInfo();
            only.Model = CanonicalMessageModel(S(rows[0][0]), S(rows[0][1]));
            only.Prov = S(rows[0][1]) ?? ""; only.Started = L(rows[0][2]);
            return only;
        }
        return null;
    }

    static bool SessionLatestModelMatches(string sid, string model, string provider)
    {
        if (String.IsNullOrEmpty(sid) || String.IsNullOrEmpty(model)) return false;
        object[][] r = Query("SELECT COALESCE(json_extract(data,'$.modelId'), "
            + "json_extract(data,'$.modelID')), COALESCE(json_extract(data,'$.providerId'), "
            + "json_extract(data,'$.providerID')) FROM message WHERE session_id=? "
            + "AND json_extract(data,'$.role')='assistant' ORDER BY time_updated DESC LIMIT 1", sid);
        if (r.Length == 0) return false;
        string p = S(r[0][1]) ?? "";
        return CanonicalMessageModel(S(r[0][0]), p) == model
            && (provider.Length == 0 || p == provider);
    }

    static bool SessionHasAnyRunningRequest(string sid)
    {
        if (String.IsNullOrEmpty(sid) || DbH == IntPtr.Zero) return false;
        long cutoff = UnixMs() - 30 * 60 * 1000L;
        return Query("SELECT 1 FROM message m WHERE m.session_id=? "
            + "AND json_extract(m.data,'$.role')='assistant' AND m.time_created>=? "
            + "AND (json_extract(m.data,'$.finish') IS NULL OR json_extract(m.data,'$.finish')='started') "
            + "AND NOT EXISTS (SELECT 1 FROM model_usage u WHERE u.assistant_message_id=m.id "
            + "OR u.id LIKE '%' || m.id || '%') LIMIT 1", sid, cutoff).Length > 0;
    }

    static bool SessionHasRunningRequest(string sid, string modelId)
    {
        return RunningModelForSession(sid, modelId, "") != null;
    }

    // ================= UIA（对照 token_watcher.cs 逻辑） =================
    static AutomationElement UiaRoot, TitleEl, ModelEl, AnchorEl;
    static long Hwnd0;
    static int LastWalkMs, LastReadTc;
    static DateTime LastWalk = DateTime.MinValue;
    // watcher 输出槽位（原 stdout 协议 → 字段）
    static string CurTitle = "", CurModel = "", CurProvider = "";
    static int[] CurAnchor;   // 屏幕物理像素 [l,t,w,h]
    // binder 粘滞槽位（对照 overlay_bind_loop）
    static int LastDbChgTc;   // 最近 DB/UI 变化（rowid/标题/模型）tick，驱动自适应轮询
    static long LastMuChk = -1, LastPrChk = -1;
    static string LastTiChk, LastMoChk;
    static string LastTitle = "", LastModel = "", LastProvider = "";
    static double TitleEmptySince;
    static bool EverAnchored;
    static int[] OverlayAnchor;

    // ^[^/\s]+/[^/\s]+$ ：provider/model 格式（避免引正则引擎）
    static bool IsModelName(string s)
    {
        if (String.IsNullOrEmpty(s)) return false;
        int slash = s.IndexOf('/');
        if (slash <= 0 || slash >= s.Length - 1) return false;
        for (int i = 0; i < s.Length; i++)
            if (char.IsWhiteSpace(s[i])) return false;
        return true;
    }
    static string CanonicalUiModel(string s)
    {
        if (String.IsNullOrEmpty(s)) return "";
        int slash = s.IndexOf('/');
        return (slash >= 0 ? s.Substring(slash + 1) : s).Trim();
    }
    static string CanonicalMessageModel(string model, string provider)
    {
        if (String.IsNullOrEmpty(model)) return "";
        if (!String.IsNullOrEmpty(provider))
        {
            string prefix = provider + "/";
            if (model.StartsWith(prefix, StringComparison.Ordinal))
                return model.Substring(prefix.Length).Trim();
        }
        return model.Trim();
    }
    static string ProviderFromUiModel(string s)
    {
        if (String.IsNullOrEmpty(s)) return "";
        int slash = s.IndexOf('/');
        return slash > 0 ? s.Substring(0, slash).Trim() : "";
    }
    static string StripModel(string s)
    {
        // UIA button is provider/model; model itself may contain additional '/'.
        return CanonicalUiModel(s);
    }

    // 窗口发现缓存：EnumWindows 全遍历不能每 tick 跑（CPU 热点），2s 有效期 + 失效即重找
    static System.Diagnostics.Stopwatch ProfSw = System.Diagnostics.Stopwatch.StartNew();
    static double PUia, PBind, POvl; static int PTicks, PWalks, PFast, PDirty;
    static void Prof(string ph, double ms)
    {
        if (ph == "uia") PUia += ms; else if (ph == "bind") PBind += ms; else POvl += ms;
        if (ProfSw.Elapsed.TotalSeconds >= 60)
        {
            ErrLog("perf", "60s ticks=" + PTicks + " walks=" + PWalks + " fast=" + PFast
                + " dirty=" + PDirty + " uia=" + (int)PUia + "ms bind=" + (int)PBind + "ms ovl=" + (int)POvl
                + "ms model=" + (LastModel.Length > 0 ? LastModel : "-") + " tps=" + PayTps
                + " page=" + DetailPage + " vis=" + (DetailVisible ? 1 : 0)
                + " clicks=" + PDown + "/" + PHit + "@" + PHitX + "," + PHitY
                + " tab=" + TabRects[1].X + "-" + (TabRects[1].X + TabRects[1].W) + "/" + TabRects[1].Y + "-" + (TabRects[1].Y + TabRects[1].H)
                + " dbchg=" + (Environment.TickCount - LastDbChgTc) + "ms");
            PUia = 0; PBind = 0; POvl = 0; PTicks = 0; PWalks = 0; PFast = 0; PDirty = 0;
            GC.Collect(); ProfSw.Restart();
        }
    }
    static IntPtr ZHwnd; static DateTime LastWinFind = DateTime.MinValue;
    static IntPtr EnsureZWindow()
    {
        if (ZHwnd != IntPtr.Zero && IsWindow(ZHwnd) && IsWindowVisible(ZHwnd)
            && (DateTime.Now - LastWinFind).TotalMilliseconds < 2000) return ZHwnd;
        LastWinFind = DateTime.Now;
        ZHwnd = FindZCodeWindow();
        return ZHwnd;
    }

    static void ClearRefs() { TitleEl = null; ModelEl = null; AnchorEl = null; }

    static void Walk(AutomationElement r)
    {
        bool found = false;
        string model = ""; int[] anchor = null;
        double modelTop = double.MinValue;
        AutomationElement newT = null, newM = null, newA = null;
        PropertyCondition bc = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
        AutomationElementCollection buttons = r.FindAll(TreeScope.Descendants, bc);
        foreach (AutomationElement e in buttons)
        {
            try
            {
                System.Windows.Rect rc = e.Current.BoundingRectangle;
                if (e.Current.IsOffscreen) continue;
                string n = e.Current.Name ?? "";
                // 模型按钮取最靠底部：真按钮在底栏，弹层条目在它上方（防串味）
                if (rc.Width > 100 && IsModelName(n) && rc.Top > modelTop)
                { modelTop = rc.Top; model = n; newM = e; found = true; }
                if (anchor == null && n == "选择打开方式")
                {
                    anchor = new int[] { (int)rc.Left, (int)rc.Top, (int)rc.Width, (int)rc.Height };
                    newA = e; found = true;
                }
            }
            catch { }
        }
        PropertyCondition tc = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
        AutomationElementCollection texts = r.FindAll(TreeScope.Descendants, tc);
        if (texts.Count == 0)
            texts = r.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        string title = "";
        foreach (AutomationElement e in texts)
        {
            try
            {
                string pn = e.Current.ControlType.ProgrammaticName ?? "";
                if (pn.IndexOf("Text") < 0) continue;
                System.Windows.Rect rc = e.Current.BoundingRectangle;
                if (e.Current.IsOffscreen) continue;
                string n = e.Current.Name ?? "";
                int ln = n.Length;
                if (ln < 2 || ln >= 200 || ln <= title.Length) continue;
                // 只认锚点同行带（无 blanket 兜底：聊天文本会劫持标题）
                if (anchor != null && rc.Width >= 30 && Math.Abs(rc.Top - anchor[1]) <= 25
                    && (rc.Left + rc.Width) <= anchor[0] + 10 && rc.Left >= 60)
                { title = n; newT = e; found = true; }
            }
            catch { }
        }
        PWalks++;
        if (found)
        {   // 部分失败保留旧引用：只覆盖本轮真正找到的
            if (newT != null) TitleEl = newT;
            if (newM != null) ModelEl = newM;
            if (newA != null) AnchorEl = newA;
        }
        else ClearRefs();
        CurTitle = title ?? "";
        CurProvider = ProviderFromUiModel(model);
        CurModel = CanonicalUiModel(model);
        CurAnchor = anchor;
    }

    static void UiaFail()
    {
        UiaRoot = null; ClearRefs();
        CurTitle = ""; CurModel = ""; CurProvider = ""; CurAnchor = null;
    }

    static void UiaTick()
    {
        try
        {
            long hwnd = EnsureZWindow().ToInt64();
            if (hwnd == 0) { UiaFail(); return; }
            if (hwnd != Hwnd0) { Hwnd0 = hwnd; UiaRoot = null; }
            double age = (DateTime.Now - LastWalk).TotalMilliseconds;
            // 快路径三引用健在 → 兜底 Walk 放宽到 15s（变化靠快路径轮询）；引用缺失才 3s 紧追
            int bs = LastWalkMs > 3000 ? 15000
                : (TitleEl != null && ModelEl != null && AnchorEl != null) ? 15000 : 3000;
            if (UiaRoot == null || age > bs)
            {
                if (UiaRoot == null) UiaRoot = AutomationElement.FromHandle(new IntPtr(Hwnd0));
                int t0 = Environment.TickCount;
                Walk(UiaRoot);
                LastWalkMs = Environment.TickCount - t0;
                LastWalk = DateTime.Now;
            }
            else if (TitleEl != null && AnchorEl != null)
            {
                // P4 自适应轮询：DB/UI 活跃期 300ms（对齐旧 binder 0.3s 节拍），空闲 1s
                int iv = (Environment.TickCount - LastDbChgTc < 15000) ? 300 : 1000;
                if (Environment.TickCount - LastReadTc < iv) return;
                LastReadTc = Environment.TickCount; PFast++;
                try
                {
                    System.Windows.Rect ar = AnchorEl.Current.BoundingRectangle;
                    CurAnchor = new int[] { (int)ar.Left, (int)ar.Top, (int)ar.Width, (int)ar.Height };
                    CurTitle = TitleEl.Current.Name ?? "";
                    string rawModel = ModelEl != null ? (ModelEl.Current.Name ?? "") : "";
                    CurProvider = ProviderFromUiModel(rawModel);
                    CurModel = CanonicalUiModel(rawModel);
                }
                catch { UiaFail(); }   // 元素被重渲染换掉 → 立即重走
            }
        }
        catch (Exception e) { UiaFail(); ErrLog("uia", e.Message); }
    }

    // ================= 绑定（对照 overlay_bind_loop，含全部语义） =================
    static object[] LastKey = null;             // (title, model, mu, pr, bucket)
    static double LastZeroLog, LastHintLog;     // 诊断日志限频
    static string PayTps = "0";                 // overlay 显示值
    static Color PayColor = InkEmpty;
    static bool PayHasData;

    static long UnixMs() { return (long)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalMilliseconds); }
    static double NowS() { return UnixMs() / 1000.0; }

    static bool KeyEq(object[] a, object[] b)
    {
        for (int i = 0; i < a.Length; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }

    static void BindTick()
    {
        if (DbH == IntPtr.Zero) return;
        try
        {
            string title, model, provider;
            if (CurAnchor != null)
            {
                OverlayAnchor = CurAnchor;
                EverAnchored = true;
                // resolve_title：毛刺沿用上次值，持续空 >4s 接受空（宁可 0 不跟错会话）
                string t = CurTitle;
                if (t.Length > 0) { LastTitle = t; TitleEmptySince = 0; }
                else
                {
                    double now = NowS();
                    if (LastTitle.Length > 0)
                    {
                        // Python resolve_title：首次空读也沿用健康标题，4s 后才接受空。
                        if (TitleEmptySince == 0) TitleEmptySince = now;
                        if (now - TitleEmptySince <= 4.0) t = LastTitle;
                        else t = "";
                    }
                    else
                    {
                        if (TitleEmptySince == 0) TitleEmptySince = now;
                        t = "";
                    }
                }
                title = t;
                model = CurModel.Length > 0 ? CurModel : LastModel;
                provider = CurProvider.Length > 0 ? CurProvider : LastProvider;
                if (CurModel.Length > 0) LastModel = CurModel;
                if (CurProvider.Length > 0) LastProvider = CurProvider;
            }
            else
            {
                title = LastTitle; model = LastModel; provider = LastProvider;   // watcher 全空沿用健康值
            }

            object[][] m1 = Query("SELECT MAX(rowid) FROM model_usage");
            long mu = m1.Length > 0 ? L(m1[0][0]) : 0;
            object[][] p1 = Query("SELECT MAX(rowid) FROM part");
            long pr = p1.Length > 0 ? L(p1[0][0]) : 0;
            double nowS = NowS();
            if (mu != LastMuChk || pr != LastPrChk || title != LastTiChk || model != LastMoChk)
            {
                LastDbChgTc = Environment.TickCount;
                LastMuChk = mu; LastPrChk = pr; LastTiChk = title; LastMoChk = model;
            }
            object[] key = new object[] { title, model, provider, mu, pr, (long)(nowS / 10) };
            if (LastKey != null && KeyEq(key, LastKey)) return;
            PDirty++;

            string sid = FindSessionByTitle(title);
            if (sid.Length == 0 && !EverAnchored) sid = FallbackSession(UnixMs());   // 仅冷启动
            RunInfo activeRun = RunningModelForSession(sid, model, provider);
            bool confirmedRun = activeRun != null;
            string historyModel = confirmedRun ? activeRun.Model : model;
            string historyProvider = confirmedRun ? activeRun.Prov : provider;
            ModelAgg[] models = SessionSpeed(sid, historyModel, historyProvider);
            // 模型提示可能被弹层旧值污染；只有没有可确认的运行模型时才允许旧回退。
            if (models.Length == 0 && model.Length > 0 && !confirmedRun)
            {
                if (!SessionHasAnyRunningRequest(sid))
                    models = SessionSpeed(sid, "", "");
                if (models.Length > 0 && nowS - LastHintLog > 60)
                {
                    LastHintLog = nowS;
                    ErrLog("bind-zero", "model hint '" + model + "' 在会话无完成记录，回退最近模型");
                }
            }
            if (confirmedRun && models.Length == 0)
            {
                // 已确认当前模型在运行，但没有该模型可验证的完成历史：保持 0，
                // 禁止沿用同会话其他模型速度。
                models = new ModelAgg[0];
            }
            string reason = "";
            if (models.Length > 0)
            {
                long lastDone = models[0].Last;
                long nowMs = (long)(nowS * 1000);
                if (lastDone < nowMs - 90 * 1000 && !confirmedRun)
                {
                    long act = SessionLastAct(sid);
                    if (act < nowMs - 90 * 1000)
                    {
                        reason = string.Format("90s空闲归零 sid={0} 最近完成{1}s前 part活动{2}s前",
                            sid, (nowMs - lastDone) / 1000, (nowMs - act) / 1000);
                        models = new ModelAgg[0];
                    }
                }
                LastKey = key;
            }
            else if (title.Length == 0)
                reason = "标题空（UIA 读不到超TTL）model=" + model;
            else if (sid.Length == 0)
                reason = "标题无匹配会话 title=" + title;
            else
                reason = "会话无有效请求 sid=" + sid + " model=" + model;
            // 诊断：只在有速度→0 的瞬间记原因，60s 限频
            if (reason.Length > 0 && PayHasData && nowS - LastZeroLog > 60)
            {
                LastZeroLog = nowS;
                ErrLog("bind-zero", reason);
            }
            if (models.Length > 0)
            {
                PayTps = models[0].Tps; PayColor = InkValue; PayHasData = true;
            }
            else
            {
                PayTps = "0"; PayColor = InkEmpty; PayHasData = false;
            }
        }
        catch (Exception e) { ErrLog("bind", e.Message); }
    }

    // ============ overlay + 托盘（原生层叠窗口，对照 draw_overlay/overlay_position） ============
    delegate IntPtr WndProcD(IntPtr h, uint m, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; public POINT(int a, int b) { x = a; y = b; } }
    [StructLayout(LayoutKind.Sequential)]
    struct SIZE2 { public int cx, cy; public SIZE2(int a, int b) { cx = a; cy = b; } }
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int px, py; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSW
    {
        public uint style; public IntPtr proc; public int cbExtra, wndExtra;
        public IntPtr inst, icon, cursor, brush;
        public IntPtr menuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string name;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BLENDF { public byte op, flags, alpha, fmt; public BLENDF(byte a) { op = 0; flags = 0; alpha = a; fmt = 1; } }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NID
    {
        public int cbSize; public IntPtr hWnd; public uint uID, flags, cbMsg;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string tip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int l, t, r, b; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassW(ref WNDCLASSW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr h, uint a, uint b, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dcS, ref POINT pt, ref SIZE2 sz, IntPtr dcM, ref POINT ptSrc, uint key, ref BLENDF bl, uint fl);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, ref RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCb cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetCapture(IntPtr h);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool TrackPopupMenu(IntPtr menu, uint flags, int x, int y, uint rsv, IntPtr h, uint rsv2);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr menu, uint fl, IntPtr id, string txt);
    [DllImport("user32.dll")] static extern bool GetCursorPos(ref POINT p);
    [DllImport("user32.dll")] static extern int GetWindowLongW(IntPtr h, int idx);
    [DllImport("user32.dll")] static extern int SetWindowLongW(IntPtr h, int idx, int val);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int k);
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    static readonly IntPtr HWND_TOP = IntPtr.Zero;
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint fl);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr m);
    [DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint act, uint prm, ref RECT rc, uint ini);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int v);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint msg, ref NID d);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);
    public delegate bool EnumCb(IntPtr h, IntPtr l);

    const uint WS_POPUP = 0x80000000;
    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
              WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8, WS_EX_APPWINDOW = 0x40000;
    const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_SHOWNORMAL = 1;
    const uint ULW_ALPHA = 2, WM_APP = 0x8000, WM_COMMAND = 0x0111;

    static IntPtr OverlayHwnd;
    static uint TrayCbMsg = WM_APP + 1;
    static WndProcD ProcKeeper;   // 防 GC 回收委托
    static bool TrayAdded;
    static IntPtr MenuHandle;
    static Font FontValue, FontUnit;
    static string LastSig;

    static IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == TrayCbMsg)
        {
            if ((uint)(l.ToInt64() & 0xffff) == 0x0205) ShowTrayMenu();   // WM_RBUTTONUP
            return IntPtr.Zero;
        }
        long cmd = w.ToInt64() & 0xffff;
        if (m == WM_COMMAND && cmd == 1) { Quit(); return IntPtr.Zero; }
        if (m == WM_COMMAND && cmd == 2) { TogglePanel(); return IntPtr.Zero; }
        if (m == WM_COMMAND && cmd == 3) { ToggleDetail(); return IntPtr.Zero; }
        if (h == DetailHwnd)
        {
            if (m == WM_LBUTTONDOWN)   // 详情任意位置可拖动；未移动时保留按钮/页签点击
            {
                int px = (short)(l.ToInt64() & 0xffff), py = (short)((l.ToInt64() >> 16) & 0xffff);
                PDown++; PHitX = px; PHitY = py;
                BeginDetailDrag();
                return IntPtr.Zero;
            }
            if (m == WM_MOUSEMOVE)
            {
                MoveDetailDrag();
                return IntPtr.Zero;
            }
            if (m == WM_LBUTTONUP)
            {
                bool dragged = EndDetailDrag();
                if (dragged) return IntPtr.Zero;
                int px = (short)(l.ToInt64() & 0xffff), py = (short)((l.ToInt64() >> 16) & 0xffff);
                if (px >= CloseRect.X && px <= CloseRect.X + CloseRect.W
                    && py >= CloseRect.Y && py <= CloseRect.Y + CloseRect.H)
                {
                    ShowWindow(DetailHwnd, SW_HIDE); DetailVisible = false;
                    return IntPtr.Zero;
                }
                DetailMouseDown(px, py);
                return IntPtr.Zero;
            }
            if (m == 0x020A)   // WM_MOUSEWHEEL
            {
                DetailWheel((short)((w.ToInt64() >> 16) & 0xffff));
                return IntPtr.Zero;
            }
            if (m == 0x0010)   // WM_CLOSE：隐藏不销毁
            { ShowWindow(DetailHwnd, SW_HIDE); DetailVisible = false; return IntPtr.Zero; }
        }
        if (h == PanelHwnd && m == 0x0010)   // 面板同理：外部 WM_CLOSE 只隐藏，销毁会让托盘开关永久失效
        { ShowWindow(PanelHwnd, SW_HIDE); PanelVisible = false; return IntPtr.Zero; }
        return DefWindowProcW(h, m, w, l);
    }

    static void ShowTrayMenu()
    {
        if (MenuHandle != IntPtr.Zero) DestroyMenu(MenuHandle);
        MenuHandle = CreatePopupMenu();
        AppendMenuW(MenuHandle, PanelVisible ? 8u : 0u, (IntPtr)2, "简约面板");   // MF_CHECKED
        AppendMenuW(MenuHandle, DetailVisible ? 8u : 0u, (IntPtr)3, "详情面板");
        AppendMenuW(MenuHandle, 0x800u, IntPtr.Zero, "");
        AppendMenuW(MenuHandle, 0, (IntPtr)1, "退出程序");
        POINT p = default(POINT); GetCursorPos(ref p);
        SetForegroundWindow(OverlayHwnd);
        TrackPopupMenu(MenuHandle, 0x22 /*RIGHTBUTTON|BOTTOMALIGN*/, p.x, p.y, 0, OverlayHwnd, 0);
    }

    static void Quit()
    {
        try
        {
            if (TrayAdded)
            {
                NID d = new NID(); d.cbSize = Marshal.SizeOf(typeof(NID));
                d.hWnd = OverlayHwnd; d.uID = 1;
                Shell_NotifyIconW(2, ref d);   // NIM_DELETE
            }
            ErrLog("exit", "quit");
        }
        finally { Environment.Exit(0); }
    }

    static void DrawOverlay(IntPtr hwnd, int x, int y, int w, int h, string value, Color rgb)
    {
        using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                int fvp = Math.Max(8, (int)Math.Round(h * 14.0 / 32));
                int fup = Math.Max(8, (int)Math.Round(h * 12.0 / 32));
                if (FontValue == null || (int)FontValue.Size != fvp)
                { if (FontValue != null) FontValue.Dispose(); FontValue = SafeValueFont(fvp); }
                if (FontUnit == null || (int)FontUnit.Size != fup)
                { if (FontUnit != null) FontUnit.Dispose(); FontUnit = new Font("Segoe UI", fup, FontStyle.Regular, GraphicsUnit.Pixel); }
                const string unit = "t/s";
                int gap = Math.Max(1, (int)Math.Round(5.0 * h / 32));
                SizeF wu = g.MeasureString(unit, FontUnit, PointF.Empty, StringFormat.GenericTypographic);
                float xR = Math.Max(0, w - wu.Width - gap - (float)Math.Round(2.0 * h / 32));
                float cy = h / 2f;
                SizeF vv = g.MeasureString(value, FontValue, PointF.Empty, StringFormat.GenericTypographic);
                using (SolidBrush bv = new SolidBrush(rgb))
                    g.DrawString(value, FontValue, bv, xR - vv.Width, cy - vv.Height / 2f,
                                 StringFormat.GenericTypographic);
                using (SolidBrush bu = new SolidBrush(InkMuted))
                    g.DrawString(unit, FontUnit, bu, xR + gap, cy - wu.Height / 2f,
                                 StringFormat.GenericTypographic);
            }
            Blt(hwnd, bmp, w, h, x, y, 255);
        }
    }

    // 位图 -> 分层窗口（预乘 alpha + ULW）。globalAlpha = 整窗常数 alpha（对照
    // SetLayeredWindowAttributes 125/190/220 三态），overlay 恒 255。
    static void Blt(IntPtr hwnd, Bitmap bmp, int w, int h, int x, int y, byte globalAlpha)
    {
        Rectangle rect = new Rectangle(0, 0, w, h);
        BitmapData bd = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* p = (byte*)bd.Scan0;
            for (int i = 0; i < w * h; i++)
            {
                byte a = p[i * 4 + 3];
                if (a != 255)
                {
                    p[i * 4] = (byte)(p[i * 4] * a / 255);
                    p[i * 4 + 1] = (byte)(p[i * 4 + 1] * a / 255);
                    p[i * 4 + 2] = (byte)(p[i * 4 + 2] * a / 255);
                }
            }
        }
        bmp.UnlockBits(bd);

        IntPtr dcS = GetDC(IntPtr.Zero);
        if (dcS != IntPtr.Zero)
        {
            try
            {
                IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                try
                {
                    IntPtr dcM = CreateCompatibleDC(dcS);
                    IntPtr oldB = SelectObject(dcM, hBmp);
                    POINT dst = new POINT(x, y), src = new POINT(0, 0);
                    SIZE2 sz = new SIZE2(w, h);
                    BLENDF bl = new BLENDF(globalAlpha);
                    UpdateLayeredWindow(hwnd, dcS, ref dst, ref sz, dcM, ref src, 0, ref bl, ULW_ALPHA);
                    SelectObject(dcM, oldB);
                    DeleteDC(dcM);
                }
                finally { DeleteObject(hBmp); }
            }
            finally { ReleaseDC(IntPtr.Zero, dcS); }
        }
    }

    static Font SafeValueFont(int px)
    {
        try { return new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel); }
        catch { return new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel); }
    }

    // 可见的 ZCode 主窗：前台优先，否则面积最大（对照 find_zcode_window）
    static IntPtr FindZCodeWindow()
    {
        IntPtr fg = GetForegroundWindow();
        IntPtr best = IntPtr.Zero; long bestArea = -1;
        List<IntPtr> fgHit = new List<IntPtr>();
        List<IntPtr> all = new List<IntPtr>();
        List<long> areas = new List<long>();
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h)) return true;
            StringBuilder sb = new StringBuilder(256);
            GetWindowTextW(h, sb, 256);
            string t = sb.ToString().Trim();
            if (t == ZCodeTitle || t.StartsWith(ZCodeTitle + " "))
            {
                RECT r = new RECT();
                if (GetWindowRect(h, ref r))
                {
                    long area = (long)Math.Max(0, r.r - r.l) * Math.Max(0, r.b - r.t);
                    all.Add(h); areas.Add(area);
                    if (h == fg) fgHit.Add(h);
                }
            }
            return true;
        }, IntPtr.Zero);
        if (fgHit.Count > 0) return fgHit[0];
        for (int i = 0; i < all.Count; i++)
            if (areas[i] > bestArea) { bestArea = areas[i]; best = all[i]; }
        return best;
    }

    static void OverlaySync()
    {
        try
        {
            IntPtr z = EnsureZWindow();
            bool fg = z != IntPtr.Zero && GetForegroundWindow() == z && !IsIconic(z);
            if (!fg || OverlayAnchor == null || OverlayHwnd == IntPtr.Zero)
            {
                if (OverlayHwnd != IntPtr.Zero && IsWindowVisible(OverlayHwnd)) ShowWindow(OverlayHwnd, SW_HIDE);
                LastSig = null;
                return;
            }
            uint dpi = GetDpiForWindow(z);
            double scale = dpi / 96.0;
            int pw = (int)Math.Round(OverlayW * scale), ph = (int)Math.Round(OverlayH * scale);
            int ax = OverlayAnchor[0], ay = OverlayAnchor[1], ah = OverlayAnchor[3];
            int x = ax - (int)Math.Round(OverlayGap * scale) - pw;
            int y = ay + (int)Math.Round((ah - ph) / 2.0);
            string sig = PayTps + "|" + PayColor + "|" + x + "|" + y + "|" + pw + "|" + ph;
            if (sig != LastSig)
            {
                DrawOverlay(OverlayHwnd, x, y, pw, ph, PayTps, PayColor);
                LastSig = sig;
            }
            if (!IsWindowVisible(OverlayHwnd)) ShowWindow(OverlayHwnd, SW_SHOWNOACTIVATE);
        }
        catch (Exception e) { ErrLog("overlay", e.Message); }
    }

    // ================= 主循环（单线程：UIA + 绑定 + overlay + 消息泵） =================
    static void CreateOverlayWindow()
    {
        ProcKeeper = new WndProcD(WndProc);
        WNDCLASSW wc = new WNDCLASSW();
        wc.proc = Marshal.GetFunctionPointerForDelegate(ProcKeeper);
        wc.inst = Marshal.GetHINSTANCE(typeof(TDN).Module);
        wc.name = "TokenDetailsOverlay";
        RegisterClassW(ref wc);
        uint ex = (uint)(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST);
        OverlayHwnd = CreateWindowExW(ex, "TokenDetailsOverlay", OverlayTitle, WS_POPUP,
            0, 0, OverlayW, OverlayH, IntPtr.Zero, IntPtr.Zero, wc.inst, IntPtr.Zero);
        NID nid = new NID();
        nid.cbSize = Marshal.SizeOf(typeof(NID));
        nid.hWnd = OverlayHwnd; nid.uID = 1;
        nid.flags = 0x1 | 0x2 | 0x4;   // MESSAGE|ICON|TIP
        nid.cbMsg = TrayCbMsg;
        nid.hIcon = MakeIcon();
        nid.tip = "Token Details";
        Shell_NotifyIconW(0, ref nid);   // NIM_ADD
        TrayAdded = true;
    }

    static IntPtr MakeIcon()
    {
        // 优先用 exe 同目录的 token_speed.ico（与桌面/Startup 快捷方式同源）
        try
        {
            string dir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string ico = System.IO.Path.Combine(dir, "token_speed.ico");
            if (System.IO.File.Exists(ico))
            {
                IntPtr h = LoadImage(IntPtr.Zero, ico, 1 /*IMAGE_ICON*/, 16, 16, 0x10 /*LR_LOADFROMFILE*/);
                if (h != IntPtr.Zero) return h;
            }
        }
        catch { }
        using (Bitmap b = new Bitmap(16, 16))
        {
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.FromArgb(0x5F, 0x63, 0x68));
                using (Font f = new Font("Segoe UI", 9, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush br = new SolidBrush(Color.White))
                    g.DrawString("T", f, br, 3, 1);
            }
            return b.GetHicon();
        }
    }

    static void Pump()
    {
        MSG m;
        while (PeekMessage(out m, IntPtr.Zero, 0, 0, 1))   // PM_REMOVE
        {
            TranslateMessage(ref m);
            DispatchMessage(ref m);
        }
    }

    static void Worker()
    {
        try { CreateOverlayWindow(); }
        catch (Exception e) { ErrLog("init", "overlay: " + e.GetType().Name + " " + e.Message); }
        try { DbOpen(); }
        catch (Exception e) { ErrLog("init", "db: " + e.GetType().Name + " " + e.Message); }
        try { LoadProvNames(); CreatePanelWindow(); }
        catch (Exception e) { ErrLog("init", "panel: " + e.GetType().Name + " " + e.Message); }
        try { CreateDetailWindow(); }
        catch (Exception e) { ErrLog("init", "detail: " + e.GetType().Name + " " + e.Message); }
        int tick = 0;
        while (true)
        {
            try
            {
                if (DbH == IntPtr.Zero && tick % 5 == 0) DbOpen();
                double t0 = ProfSw.Elapsed.TotalMilliseconds;
                UiaTick(); double t1 = ProfSw.Elapsed.TotalMilliseconds;
                BindTick(); double t2 = ProfSw.Elapsed.TotalMilliseconds;
                OverlaySync();
                PanelDataTick(false);
                PanelHoverTick();
                try { CodexDb(); } catch (Exception ec) { ErrLog("codex", ec.Message); }
                DetailDataTick();
                if (DetailRedraw && DetailVisible)
                { DetailRedraw = false; try { DrawDetail(); } catch (Exception e2) { ErrLog("detail", e2.Message); } }
                PUia += t1 - t0; PBind += t2 - t1; PTicks++; Prof("tick", 0);
            }
            catch (Exception e) { ErrLog("loop", e.GetType().Name + ": " + e.Message); }
            Pump();
            Thread.Sleep(100);
            tick++;
        }
    }

    // ================= 简略面板（原生 GDI 版，对照 HTML 简略面板） =================
    class PanelRow { public string Model, Prov, Tps; public int TpsV; public bool Run; }
    class RunInfo { public string Model, Prov; public long Started; }

    static IntPtr PanelHwnd;
    static bool PanelVisible;
    static byte PanelAlpha = 125;                 // 125 空闲 / 190 悬停 / 220 把手（对照三态）
    static PanelRow[] PanelRows = new PanelRow[0];
    static string PanelSig = "";
    static int PanelX = -1, PanelY = -1;
    static int LastPanelCalc;
    static Font PanelFName, PanelFProv, PanelFVal, PanelFEmpty, PanelFGrip;
    static uint PanelFontDpi;

    static Dictionary<string, string> ProvNames = new Dictionary<string, string>();
    static void LoadProvNames()
    {
        // provider_id -> 可读名（对照 provider_names()：v2 providerRules + 旧 cli providers）
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] paths = new string[] { home + "\\.zcode\\cli\\config.json",
                                            home + "\\.zcode\\v2\\provider_config.json" };
            System.Web.Script.Serialization.JavaScriptSerializer js =
                new System.Web.Script.Serialization.JavaScriptSerializer();
            foreach (string path in paths)
            {
                if (!System.IO.File.Exists(path)) continue;
                Dictionary<string, object> cfg = js.Deserialize<Dictionary<string, object>>(
                    System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8));
                if (cfg == null) continue;
                // 新结构：{config:{providerConfigRules:{providerRules:[{providerId,providerName}]}}}
                object node;
                System.Collections.ArrayList rl = null;
                if (cfg.TryGetValue("config", out node))
                {
                    Dictionary<string, object> c1 = node as Dictionary<string, object>;
                    object node2 = null;
                    if (c1 != null) c1.TryGetValue("providerConfigRules", out node2);
                    Dictionary<string, object> c2 = node2 as Dictionary<string, object>;
                    object rules = null;
                    if (c2 != null) c2.TryGetValue("providerRules", out rules);
                    rl = rules as System.Collections.ArrayList;
                }
                if (rl != null)
                {
                    foreach (object o in rl)
                    {
                        Dictionary<string, object> r = o as Dictionary<string, object>;
                        if (r == null) continue;
                        object nm, pid;
                        if (r.TryGetValue("providerName", out nm) && nm != null
                            && r.TryGetValue("providerId", out pid) && pid != null)
                            ProvNames[pid.ToString()] = nm.ToString();
                    }
                    continue;
                }
                // 旧结构：{providers:{id:{name}}}（可能直接是列表）
                object pv;
                if (!cfg.TryGetValue("providers", out pv) || pv == null)
                    cfg.TryGetValue("provider", out pv);
                System.Collections.ArrayList lst = pv as System.Collections.ArrayList;
                if (lst != null)
                {
                    Dictionary<string, object> map = new Dictionary<string, object>();
                    foreach (object o in lst)
                    {
                        Dictionary<string, object> d = o as Dictionary<string, object>;
                        object id;
                        if (d != null && d.TryGetValue("id", out id) && id != null) map[id.ToString()] = d;
                    }
                    pv = map;
                }
                Dictionary<string, object> provs = pv as Dictionary<string, object>;
                if (provs == null) continue;
                foreach (KeyValuePair<string, object> kv in provs)
                {
                    Dictionary<string, object> d = kv.Value as Dictionary<string, object>;
                    object nm;
                    if (d != null && d.TryGetValue("name", out nm) && nm != null)
                        ProvNames[kv.Key] = nm.ToString();
                }
            }
        }
        catch (Exception e) { ErrLog("prov", e.Message); }
    }
    static string ProvOf(string pid)
    {
        if (pid == null || pid.Length == 0) return "?";
        string v;
        return ProvNames.TryGetValue(pid, out v) ? v
            : (pid.Length > 8 ? pid.Substring(0, 8) : pid);
    }

    // model_id -> 最新 assistant 活动（生成中也算活动；对照 _last_act）
    static Dictionary<string, long> LastAct()
    {
        Dictionary<string, long> d = new Dictionary<string, long>();
        object[][] rs = Query("SELECT COALESCE(json_extract(data,'$.modelId'), "
            + "json_extract(data,'$.modelID')) mid, MAX(time_updated) FROM message "
            + "WHERE json_extract(data,'$.role')='assistant' GROUP BY mid");
        for (int i = 0; i < rs.Length; i++)
        {
            string m = S(rs[i][0]);
            if (m != null && m.Length > 0) d[m] = L(rs[i][1]);
        }
        return d;
    }

    // 正在运行的模型（对照 running_models：finish 空/'started' 且无 usage 行；30 分钟僵尸窗口）
    static System.Collections.ArrayList RunningModels()
    {
        System.Collections.ArrayList outp = new System.Collections.ArrayList();
        long cutoff = UnixMs() - 30 * 60 * 1000L;
        object[][] rs = Query(
            "SELECT COALESCE(json_extract(m.data,'$.modelId'), json_extract(m.data,'$.modelID')) mid, "
            + "COALESCE(json_extract(m.data,'$.providerId'), json_extract(m.data,'$.providerID')) pid, "
            + "MIN(m.time_created) FROM message m "
            + "WHERE json_extract(m.data,'$.role')='assistant' AND m.time_created >= ? "
            + "AND (json_extract(m.data,'$.finish') IS NULL OR json_extract(m.data,'$.finish')='started') "
            + "AND NOT EXISTS (SELECT 1 FROM model_usage u WHERE u.id LIKE '%' || m.id || '%') "
            + "GROUP BY mid, pid", cutoff);
        for (int i = 0; i < rs.Length; i++)
        {
            string m = S(rs[i][0]);
            if (m == null || m.Length == 0) continue;
            RunInfo r = new RunInfo();
            r.Model = m; r.Prov = S(rs[i][1]) == null ? "" : S(rs[i][1]); r.Started = L(rs[i][2]);
            outp.Add(r);
        }
        return outp;
    }

    // 简略面板行（对照 detail_data 的 models：聚合 + 90s 门控 + 运行中合并）
    static PanelRow[] DetailModels(IntPtr db, bool codex)
    {
        if (db == IntPtr.Zero) return new PanelRow[0];
        long cutoff = UnixMs() - 90 * 1000L;
        string q = "SELECT id, provider_id, model_id, status, started_at, first_token_at, "
            + "completed_at, duration_ms, output_tokens, reasoning_tokens FROM model_usage "
            + "WHERE query_source!='compact' "
            + "ORDER BY COALESCE(completed_at, started_at) DESC LIMIT 50";
        object[][] rows = QueryH(db, q);
        Dictionary<string, long> est = EstMap(db, rows);
        Dictionary<object[], long> eff = new Dictionary<object[], long>();
        Dictionary<string, long> act = codex ? new Dictionary<string, long>() : LastAct();
        List<string> order = new List<string>();
        Dictionary<string, List<object[]>> by = new Dictionary<string, List<object[]>>();
        Dictionary<string, long> lastSeen = new Dictionary<string, long>();
        for (int i = 0; i < rows.Length; i++)
        {
            object[] r = rows[i];
            string mdl = S(r[2]) == null ? "?" : S(r[2]);
            if (!by.ContainsKey(mdl)) { by[mdl] = new List<object[]>(); order.Add(mdl); }
            by[mdl].Add(r);
            long etok = EffTok(r, est);
            if (etok > 0 && ElapsedMs(r) > 0) eff[r] = etok;
            if (!lastSeen.ContainsKey(mdl))
                lastSeen[mdl] = L(r[6]) != 0 ? L(r[6]) : L(r[4]);
        }
        List<PanelRow> items = new List<PanelRow>();
        Dictionary<string, double> tpsMap = new Dictionary<string, double>();
        foreach (string mdl in order)
        {
            long latest = lastSeen[mdl];
            long actT;
            if (act.TryGetValue(mdl, out actT) && actT > latest) latest = actT;
            if (latest < cutoff) continue;   // 90s 无活动不显示
            List<object[]> lst = by[mdl];
            long tot = 0, el = 0; int n = 0;
            for (int i = 0; i < lst.Count && n < 8; i++)
            {
                object[] r = lst[i];
                if (S(r[3]) != "completed") continue;
                long e2 = ElapsedMs(r);
                long tok;
                if (!eff.TryGetValue(r, out tok) || tok <= 0 || e2 <= 0) continue;
                tot += tok; el += e2; n++;
            }
            if (el <= 0) continue;   // 无有效历史且非运行中不显示（运行中由合并段补占位行）
            PanelRow pr = new PanelRow();
            pr.Model = mdl; pr.Prov = ProvOf(S(lst[0][1])); pr.Run = false;
            double tps0 = tot * 1000.0 / el;
            tpsMap[mdl] = tps0;
            pr.Tps = tps0.ToString("0.0"); pr.TpsV = (int)Math.Round(tps0);
            items.Add(pr);
        }
        System.Collections.ArrayList run = codex ? new System.Collections.ArrayList() : RunningModels();
        if (run.Count > 0)
        {
            Dictionary<string, RunInfo> runm = new Dictionary<string, RunInfo>();
            foreach (RunInfo ri in run) if (!runm.ContainsKey(ri.Model)) runm[ri.Model] = ri;
            List<PanelRow> kept = new List<PanelRow>();
            foreach (PanelRow pr in items)
                if (!runm.ContainsKey(pr.Model)) kept.Add(pr);
            foreach (KeyValuePair<string, RunInfo> kv in runm)
            {
                PanelRow pr = new PanelRow();
                pr.Model = kv.Key; pr.Prov = ProvOf(kv.Value.Prov); pr.Run = true;
                double t;
                if (tpsMap.TryGetValue(kv.Key, out t))
                { pr.Tps = t.ToString("0.0"); pr.TpsV = (int)Math.Round(t); }
                else { pr.Tps = "\u2026"; pr.TpsV = 0; }   // 运行中无历史 -> 占位
                kept.Add(pr);
            }
            items = kept;
        }
        return items.ToArray();
    }

    static void PanelDataTick(bool force)
    {
        if (!PanelVisible) return;
        int tc = Environment.TickCount;
        if (LastPanelCalc != 0 && !force && tc - LastPanelCalc < 1000) return;   // 1Hz（面板不可见跳过取数）
        LastPanelCalc = tc;
        try
        {
            PanelRow[] rows = DetailModels(DbH, false);
            if (CodexH != IntPtr.Zero)
            {   // 双源同显（对照 dz.models + dc.models）：zcode 在前
                PanelRow[] cx = DetailModels(CodexH, true);
                PanelRow[] all = new PanelRow[rows.Length + cx.Length];
                rows.CopyTo(all, 0); cx.CopyTo(all, rows.Length);
                rows = all;
            }
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < rows.Length; i++)
                sb.Append(rows[i].Model).Append('|').Append(rows[i].Prov).Append('|')
                  .Append(rows[i].TpsV).Append('|').Append(rows[i].Run ? '1' : '0').Append(';');
            string sig = sb.ToString();
            if (sig != PanelSig) { PanelSig = sig; PanelRows = rows; RedrawPanel(); }
        }
        catch (Exception e) { ErrLog("panel", e.Message); }
    }

    static void TogglePanel()
    {
        PanelVisible = !PanelVisible;
        if (PanelVisible)
        {
            if (PanelX < 0)
            {   // 首次显示：工作区右下角（用户随后可拖到任意位置）
                RECT wa = new RECT();
                if (!SystemParametersInfoW(0x0048, 0, ref wa, 0)) { wa.r = 1200; wa.b = 700; }
                PanelX = wa.r - 270; PanelY = wa.b - 160;
            }
            ShowWindow(PanelHwnd, SW_SHOWNOACTIVATE);
            LastPanelCalc = 0;
            PanelDataTick(true);
        }
        else ShowWindow(PanelHwnd, SW_HIDE);
    }

    static void EnsurePanelFonts(uint dpi)
    {
        if (PanelFontDpi == dpi && PanelFName != null) return;
        PanelFontDpi = dpi;
        double s = dpi / 96.0;
        if (PanelFName != null) PanelFName.Dispose();
        if (PanelFProv != null) PanelFProv.Dispose();
        if (PanelFVal != null) PanelFVal.Dispose();
        if (PanelFEmpty != null) PanelFEmpty.Dispose();
        if (PanelFGrip != null) PanelFGrip.Dispose();
        PanelFName = new Font("Segoe UI", (int)Math.Round(12 * s), FontStyle.Bold, GraphicsUnit.Pixel);
        PanelFProv = new Font("Segoe UI", (int)Math.Round(9 * s), FontStyle.Regular, GraphicsUnit.Pixel);
        try { PanelFVal = new Font("Bahnschrift", (int)Math.Round(19 * s), FontStyle.Bold, GraphicsUnit.Pixel); }
        catch { PanelFVal = new Font("Segoe UI", (int)Math.Round(19 * s), FontStyle.Bold, GraphicsUnit.Pixel); }
        PanelFEmpty = new Font("Segoe UI", (int)Math.Round(11 * s), FontStyle.Regular, GraphicsUnit.Pixel);
        try { PanelFGrip = new Font("Segoe UI Symbol", (int)Math.Round(11 * s), FontStyle.Regular, GraphicsUnit.Pixel); }
        catch { PanelFGrip = new Font("Segoe UI", (int)Math.Round(11 * s), FontStyle.Regular, GraphicsUnit.Pixel); }
    }

    static readonly Color InkPanel = Color.FromArgb(255, 0x11, 0x15, 0x1A);
    static readonly Color MutedPanel = Color.FromArgb(255, 0x4B, 0x55, 0x60);
    static readonly Color HairPanel = Color.FromArgb(70, 70, 78, 88);
    static readonly Color FastC = Color.FromArgb(255, 0x08, 0x7A, 0x3D);
    static readonly Color MidC = Color.FromArgb(255, 0xB4, 0x5F, 0x00);
    static readonly Color SlowC = Color.FromArgb(255, 0xBD, 0x1F, 0x1F);
    static Color SpeedC(int v) { return v >= 80 ? FastC : (v >= 50 ? MidC : SlowC); }

    static void RedrawPanel()
    {
        if (PanelHwnd == IntPtr.Zero) return;
        uint dpi = GetDpiForWindow(PanelHwnd); if (dpi == 0) dpi = 96;
        EnsurePanelFonts(dpi);
        double s = dpi / 96.0;
        int w = (int)Math.Round(250 * s);
        int n = PanelRows.Length;
        int h = n == 0 ? (int)Math.Round(48 * s)
                       : (int)Math.Round((14 + n * 52 + 8) * s);
        using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                StringFormat typ = StringFormat.GenericTypographic;
                if (n == 0)
                {
                    SizeF ew = g.MeasureString("暂无数据", PanelFEmpty, PointF.Empty, typ);
                    using (SolidBrush b = new SolidBrush(MutedPanel))
                        g.DrawString("暂无数据", PanelFEmpty, b, (w - ew.Width) / 2f, (h - ew.Height) / 2f, typ);
                }
                int cx = (int)Math.Round(30 * s), rx = w - (int)Math.Round(16 * s);
                for (int i = 0; i < n; i++)
                {
                    PanelRow r = PanelRows[i];
                    float y0 = (float)Math.Round((14 + i * 52) * s);
                    // 名称 + 供应商（供应商接在名称后，muted 大写）
                    float nameW = g.MeasureString(r.Model == null ? "" : r.Model, PanelFName, PointF.Empty, typ).Width;
                    using (SolidBrush b = new SolidBrush(InkPanel))
                        g.DrawString(r.Model == null ? "" : r.Model, PanelFName, b, cx, y0 + 8 * (float)s, typ);
                    using (SolidBrush b = new SolidBrush(MutedPanel))
                        g.DrawString((r.Prov == null ? "" : r.Prov).ToUpperInvariant(), PanelFProv, b,
                                     cx + nameW + 6 * (float)s, y0 + 12 * (float)s, typ);
                    // 速度值：运行中无历史显示占位；有历史按 fast/mid/slow 着色
                    if (r.Run && r.TpsV == 0)
                    {
                        SizeF vw = g.MeasureString("\u2026", PanelFName, PointF.Empty, typ);
                        using (SolidBrush b = new SolidBrush(MutedPanel))
                            g.DrawString("\u2026", PanelFName, b, rx - vw.Width, y0 + 6 * (float)s, typ);
                    }
                    else
                    {
                        string v = r.Tps;   // 纯数字，不带单位（面板空间紧凑）
                        SizeF vw = g.MeasureString(v, PanelFVal, PointF.Empty, typ);
                        using (SolidBrush b = new SolidBrush(SpeedC(r.TpsV)))
                            g.DrawString(v, PanelFVal, b, rx - vw.Width, y0 + 4 * (float)s, typ);
                    }
                    // 速度条：满格 = 120 t/s（对照 bar 宽度公式）
                    int barW = rx - cx;
                    int fill = Math.Min(100, (int)Math.Round(r.TpsV / 120.0 * 100)) * barW / 100;
                    using (SolidBrush b = new SolidBrush(HairPanel))
                        g.FillRectangle(b, cx, y0 + 36 * (float)s, barW, 2f);
                    if (fill > 0)
                        using (SolidBrush b = new SolidBrush(SpeedC(r.TpsV)))
                            g.FillRectangle(b, cx, y0 + 36 * (float)s, fill, 2f);
                    // 行分隔线（最后一行不画，对照 .row:last-child）
                    if (i < n - 1)
                        using (SolidBrush b = new SolidBrush(HairPanel))
                            g.FillRectangle(b, cx, y0 + 51 * (float)s, barW, 1f);
                }
                // 把手（拖拽热区）：三点盲文字符
                using (SolidBrush b = new SolidBrush(InkPanel))
                    g.DrawString("\u283F", PanelFGrip, b, 8 * (float)s, 6 * (float)s, typ);
            }
            Blt(PanelHwnd, bmp, w, h, PanelX, PanelY, PanelAlpha);
        }
    }

    static void PanelHoverTick()
    {
        // 简略面板三态（对照 hover_loop）：把手区=解除穿透+可拖；其余永久穿透。
        if (!PanelVisible || PanelHwnd == IntPtr.Zero) return;
        try
        {
            POINT pt = default(POINT); GetCursorPos(ref pt);
            RECT rc = new RECT(); GetWindowRect(PanelHwnd, ref rc);
            bool inside = pt.x >= rc.l && pt.x <= rc.r && pt.y >= rc.t && pt.y <= rc.b;
            uint dpi = GetDpiForWindow(PanelHwnd); if (dpi == 0) dpi = 96;
            double s = dpi / 96.0;
            int gx = rc.l + (int)Math.Round(4 * s), gy = rc.t + (int)Math.Round(2 * s);
            bool inGrip = inside && pt.x >= gx && pt.x <= gx + (int)Math.Round(26 * s)
                              && pt.y >= gy && pt.y <= gy + (int)Math.Round(26 * s);
            int ex = GetWindowLongW(PanelHwnd, -20);
            int want = inGrip ? (ex & ~WS_EX_TRANSPARENT) : (ex | WS_EX_TRANSPARENT);
            if (want != ex) SetWindowLongW(PanelHwnd, -20, want);
            byte wantA = inGrip ? (byte)220 : (inside ? (byte)190 : (byte)125);
            if (wantA != PanelAlpha) { PanelAlpha = wantA; RedrawPanel(); }
            if (inGrip && (GetAsyncKeyState(0x01) & 0x8000) != 0)
            {   // 把手拖拽（阻塞子循环，松开即返回；对照 20ms 轮询）
                int x0 = pt.x, y0 = pt.y, wx = rc.l, wy = rc.t;
                while ((GetAsyncKeyState(0x01) & 0x8000) != 0)
                {
                    POINT q = default(POINT); GetCursorPos(ref q);
                    SetWindowPos(PanelHwnd, IntPtr.Zero, wx + q.x - x0, wy + q.y - y0,
                                 0, 0, 0x0001 | 0x0010 | 0x0004);   // NOSIZE|NOACTIVATE|NOZORDER
                    Thread.Sleep(20);
                }
                RECT nr = new RECT(); GetWindowRect(PanelHwnd, ref nr);
                PanelX = nr.l; PanelY = nr.t;
            }
        }
        catch (Exception e) { ErrLog("hover", e.Message); }
    }

    static void CreatePanelWindow()
    {
        WNDCLASSW wc = new WNDCLASSW();
        wc.proc = Marshal.GetFunctionPointerForDelegate(ProcKeeper);
        wc.inst = Marshal.GetHINSTANCE(typeof(TDN).Module);
        wc.name = "TokenDetailsPanel";
        RegisterClassW(ref wc);
        uint ex = (uint)(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST);
        PanelHwnd = CreateWindowExW(ex, "TokenDetailsPanel", "Token Details", WS_POPUP,
            0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, wc.inst, IntPtr.Zero);
    }

    // ================= 详情三页（原生 GDI 版，钛白方案三，对照 docs/preview/detail-style-preview.html） =================
    class SumData { public long N, Tok, TalkTok, TalkMs, Out, Inp, Reason, Cache;
                    public double Ttft, Rpm; }
    class SessData { public string Title, Model; public long Cnt, Usage, Last;
                     public double Tps, Ttft, Rpm; }
    class DmData { public string Model, Prov, Tps; public int TpsV;
                   public long Cnt; public double Rpm; }
    class BmData { public string Model, Prov; public long Tok, Cnt; public int Pct; }
    class TsData { public string Title; public long Cnt, Tok; }
    class CpData { public string Title; public long N, Last; }
    class DayData { public string Label; public long Tok; }
    class DetailData {
        public SumData Sum = new SumData();
        public SessData[] Sess = new SessData[0];
        public DmData[] Dm = new DmData[0];
        public BmData[] Bm = new BmData[0];
        public TsData[] Ts = new TsData[0];
        public CpData[] Cp = new CpData[0];
        public DayData[] Days = new DayData[0];
    }
    class RectI { public int X, Y, W, H; }

    static IntPtr DetailHwnd;
    static bool DetailVisible;
    static bool DetailRedraw;
    static int DetailPage;            // 0 总览 1 速度 2 总量
    static int DetailScroll;
    static int DetailContentH;
    static int DetailX = -1, DetailY = -1;
    static int LastDetailCalc;
    static DetailData DD = new DetailData();
    static double DetailScale = 1.0;
    static Font DFBase, DFSec, DFName, DFMeta, DFVal, DFCardV, DFCardK, DFTab, DFChart, DFSel;
    static uint DetailFontDpi;
    static readonly RectI[] TabRects = new RectI[] { new RectI(), new RectI(), new RectI() };
    static readonly RectI CloseRect = new RectI();

    // 钛白（Titan White 方案三，对照 detail-style-preview.html .theme-titan）：
    // rgba(37,99,160,a) 分隔线均已按白底 #ffffff 预合成
    static readonly Color InkD = Color.FromArgb(255, 0x1B, 0x22, 0x2B);      // #1b222b 正文
    static readonly Color MutedD = Color.FromArgb(255, 0x66, 0x71, 0x7F);    // #66717f 弱化
    static readonly Color PaperD = Color.FromArgb(255, 0xFF, 0xFF, 0xFF);    // #ffffff 白底
    static readonly Color HeadD = Color.FromArgb(255, 0xF6, 0xF8, 0xFA);     // #f6f8fa 头部
    static readonly Color LineD = Color.FromArgb(255, 0xDC, 0xE6, 0xF0);     // 分隔线 rgba(.16)
    static readonly Color HairD = Color.FromArgb(255, 0xE5, 0xEC, 0xF4);     // 行分隔 rgba(.12)
    static readonly Color VermD = Color.FromArgb(255, 0xC2, 0x45, 0x3A);     // #c2453a 总量行
    static readonly Color BarBgD = Color.FromArgb(255, 0xB3, 0xC9, 0xDE);    // 图表底/虚线框 rgba(.35)
    static readonly Color FastD = Color.FromArgb(255, 0x1E, 0x7A, 0x56);     // #1e7a56 快
    static readonly Color MidD = Color.FromArgb(255, 0xB8, 0x79, 0x1D);      // #b8791d 中/琥珀强调
    static readonly Color SlowD = Color.FromArgb(255, 0xC2, 0x45, 0x3A);     // #c2453a 慢
    static readonly Color AccentD = Color.FromArgb(255, 0x25, 0x63, 0xA0);   // #2563a0 钢蓝
    static readonly Color RuleD = Color.FromArgb(255, 0xC2, 0xD3, 0xE4);     // 标题细线 rgba(.28)
    static readonly Color SrcTrackD = Color.FromArgb(255, 0xEE, 0xF2, 0xF7); // #eef2f7 来源轨道
    static readonly Color SrcTrackLineD = Color.FromArgb(255, 0xB2, 0xC7, 0xDD); // 轨道描边 rgba(.3)
    static Color SpdC(double v) { return v >= 80 ? FastD : (v >= 50 ? MidD : SlowD); }

    // 中文计量（对照 _k：亿/万）
    static string Kcn(long n)
    {
        if (n >= 100000000) return (n / 100000000.0).ToString("0.00") + "亿";
        if (n >= 10000) return (n / 10000.0).ToString("0.0").TrimEnd('0').TrimEnd('.') + "万";
        return n.ToString("#,0");
    }
    static long Day0()
    {   // 本地午夜的真实 epoch ms：墙钟午夜按 UTC 计的 ms 减去时区偏移（UTC+8 应减 8h）
        DateTime d = DateTime.Today;
        return (long)(d - new DateTime(1970, 1, 1)).TotalMilliseconds
            - (long)TimeZoneInfo.Local.GetUtcOffset(d).TotalMilliseconds;
    }
    static string FmtMs(long ms)
    {
        return new DateTime(1970, 1, 1).AddMilliseconds(ms).ToLocalTime().ToString("MM-dd HH:mm");
    }

    // ---- 取数（对照 today_summary / session_stats / dmodels / today_by_model / today_sessions / compact_stats / week_daily） ----
    static DetailData RefreshDetail(IntPtr db)
    {
        DetailData d = new DetailData();
        long d0 = Day0();
        double bill = 0.1;   // CACHE_BILL：缓存读按 1 折计费
        // 今日汇总
        object[][] sr = QueryH(db, 
            "SELECT COUNT(CASE WHEN query_source!='compact' THEN 1 END), "
            + "COALESCE(SUM(output_tokens+reasoning_tokens+input_tokens"
            + "+cache_creation_input_tokens+cache_read_input_tokens),0), "
            + "COALESCE(SUM(CASE WHEN query_source!='compact' AND started_at IS NOT NULL "
            + "AND completed_at IS NOT NULL AND completed_at>started_at "
            + "THEN output_tokens+reasoning_tokens END),0), "
            + "COALESCE(SUM(CASE WHEN query_source!='compact' AND started_at IS NOT NULL "
            + "AND completed_at IS NOT NULL AND completed_at>started_at "
            + "THEN completed_at-started_at END),0), "
            + "COALESCE(SUM(output_tokens),0), COALESCE(SUM(input_tokens),0), "
            + "COALESCE(SUM(reasoning_tokens),0), "
            + "COALESCE(SUM(cache_creation_input_tokens+cache_read_input_tokens),0), "
            + "AVG(CASE WHEN query_source!='compact' AND first_token_at IS NOT NULL "
            + "AND started_at IS NOT NULL AND first_token_at>=started_at "
            + "THEN first_token_at-started_at END), "
            + "MIN(CASE WHEN query_source!='compact' THEN completed_at END), "
            + "MAX(CASE WHEN query_source!='compact' THEN completed_at END) "
            + "FROM model_usage WHERE status='completed' AND completed_at >= ?", d0);
        if (sr.Length > 0)
        {
            object[] r = sr[0];
            SumData sm = d.Sum;
            sm.N = L(r[0]); sm.Tok = L(r[1]); sm.TalkTok = L(r[2]); sm.TalkMs = L(r[3]);
            sm.Out = L(r[4]); sm.Inp = L(r[5]); sm.Reason = L(r[6]); sm.Cache = L(r[7]);
            if (r[8] != null) sm.Ttft = Convert.ToDouble(r[8]) / 1000.0;
            long mn = L(r[9]), mx = L(r[10]);
            if (sm.N > 0 && mn > 0 && mx > mn) sm.Rpm = sm.N * 60000.0 / (mx - mn);
        }
        // 会话速度（30 分钟窗口）
        long cutSess = UnixMs() - 30 * 60 * 1000L;
        object[][] ss = QueryH(db, 
            "SELECT u.session_id, COALESCE(s.title, u.session_id), COUNT(*), "
            + "SUM(CASE WHEN u.started_at IS NOT NULL AND u.completed_at IS NOT NULL "
            + "AND u.completed_at>u.started_at THEN u.output_tokens+u.reasoning_tokens END), "
            + "CAST(SUM(u.output_tokens+u.reasoning_tokens+u.input_tokens"
            + "+u.cache_creation_input_tokens+u.cache_read_input_tokens*"
            + bill.ToString("0.0##") + ") AS INT), "
            + "SUM(CASE WHEN u.started_at IS NOT NULL AND u.completed_at IS NOT NULL "
            + "AND u.completed_at>u.started_at THEN u.completed_at-u.started_at END), "
            + "MAX(COALESCE(u.completed_at,u.started_at)), "
            + "AVG(CASE WHEN u.first_token_at IS NOT NULL AND u.started_at IS NOT NULL "
            + "AND u.first_token_at>=u.started_at THEN u.first_token_at-u.started_at END), "
            + "MIN(COALESCE(u.completed_at,u.started_at)), "
            + "(SELECT u2.model_id FROM model_usage u2 WHERE u2.session_id=u.session_id "
            + "AND u2.query_source!='compact' AND u2.status='completed' "
            + "ORDER BY COALESCE(u2.completed_at,u2.started_at) DESC LIMIT 1) "
            + "FROM model_usage u LEFT JOIN session s ON s.id=u.session_id "
            + "WHERE u.query_source!='compact' AND u.task_type!='subagent_child' "
            + "GROUP BY u.session_id "
            + "HAVING MAX(COALESCE(u.completed_at,u.started_at)) >= " + cutSess + " "
            + "ORDER BY 6 DESC LIMIT 30");
        List<SessData> sess = new List<SessData>();
        for (int i = 0; i < ss.Length; i++)
        {
            object[] r = ss[i];
            SessData x = new SessData();
            x.Title = S(r[1]) == null ? S(r[0]) : S(r[1]);
            x.Cnt = L(r[2]);
            long tok = r[3] == null ? 0 : L(r[3]);
            long gen = r[5] == null ? 0 : L(r[5]);
            x.Tps = gen > 0 ? tok * 1000.0 / gen : 0.0;
            x.Usage = r[4] == null ? 0 : L(r[4]);
            x.Last = L(r[6]);
            if (r[7] != null) x.Ttft = Convert.ToDouble(r[7]) / 1000.0;
            long first = L(r[8]);
            double span = (first > 0 && x.Last > first) ? (x.Last - first) / 60000.0 : 0.0;
            x.Rpm = span > 0 ? x.Cnt / span : 0.0;
            x.Model = S(r[9]) == null ? "-" : S(r[9]);
            sess.Add(x);
        }
        d.Sess = sess.ToArray();
        // 每模型速度 · 今日（500 行窗口，无 90s 门控；cnt/rpm 来自今日完成请求）
        object[][] rows5 = QueryH(db, 
            "SELECT id, provider_id, model_id, status, started_at, first_token_at, "
            + "completed_at, duration_ms, output_tokens, reasoning_tokens FROM model_usage "
            + "WHERE query_source!='compact' "
            + "ORDER BY COALESCE(completed_at, started_at) DESC LIMIT 500");
        object[][] stat = QueryH(db, 
            "SELECT model_id, COUNT(*), MIN(COALESCE(completed_at,started_at)), "
            + "MAX(COALESCE(completed_at,started_at)) FROM model_usage "
            + "WHERE status='completed' AND query_source!='compact' "
            + "AND COALESCE(completed_at,started_at)>=? AND model_id!='' "
            + "GROUP BY model_id", d0);
        Dictionary<string, object[]> statMap = new Dictionary<string, object[]>();
        for (int i = 0; i < stat.Length; i++) statMap[S(stat[i][0])] = stat[i];
        Dictionary<string, long> est5 = EstMap(DbH, rows5);
        Dictionary<object[], long> eff5 = new Dictionary<object[], long>();
        List<string> order = new List<string>();
        Dictionary<string, List<object[]>> by = new Dictionary<string, List<object[]>>();
        for (int i = 0; i < rows5.Length; i++)
        {
            string m = S(rows5[i][2]) == null ? "?" : S(rows5[i][2]);
            if (!by.ContainsKey(m)) { by[m] = new List<object[]>(); order.Add(m); }
            by[m].Add(rows5[i]);
            long etok = EffTok(rows5[i], est5);
            if (etok > 0 && ElapsedMs(rows5[i]) > 0) eff5[rows5[i]] = etok;
        }
        List<DmData> dms = new List<DmData>();
        foreach (string m in order)
        {
            object[] latest = by[m][0];
            if ((L(latest[6]) != 0 ? L(latest[6]) : L(latest[4])) < d0) continue;   // 今日才有
            long tot = 0, el = 0; int n = 0;
            for (int i = 0; i < by[m].Count && n < 8; i++)
            {
                object[] r = by[m][i];
                if (S(r[3]) != "completed") continue;
                long e2 = ElapsedMs(r);
                long tk;
                if (!eff5.TryGetValue(r, out tk) || tk <= 0 || e2 <= 0) continue;
                tot += tk; el += e2; n++;
            }
            if (el <= 0) continue;
            DmData x = new DmData();
            x.Model = m; x.Prov = ProvOf(S(latest[1]));
            double tps = tot * 1000.0 / el;
            x.Tps = tps.ToString("0.0"); x.TpsV = (int)Math.Round(tps);
            object[] st;
            if (statMap.TryGetValue(m, out st))
            {
                x.Cnt = L(st[1]);
                long mn = L(st[2]), mx = L(st[3]);
                x.Rpm = (x.Cnt > 1 && mx > mn) ? x.Cnt * 60000.0 / (mx - mn) : 0.0;
            }
            dms.Add(x);
        }
        d.Dm = dms.ToArray();
        // 按模型 · 今日
        object[][] bm = QueryH(db, 
            "SELECT model_id, provider_id, "
            + "CAST(SUM(output_tokens+reasoning_tokens+input_tokens"
            + "+cache_creation_input_tokens+cache_read_input_tokens*"
            + bill.ToString("0.0##") + ") AS INT), COUNT(*) FROM model_usage "
            + "WHERE status='completed' AND completed_at>=? AND model_id!='' "
            + "GROUP BY model_id, provider_id ORDER BY 3 DESC", d0);
        long total = 1;
        for (int i = 0; i < bm.Length; i++) total += L(bm[i][2]);
        List<BmData> bml = new List<BmData>();
        for (int i = 0; i < bm.Length; i++)
        {
            object[] r = bm[i];
            BmData x = new BmData();
            x.Model = S(r[0]); x.Prov = ProvOf(S(r[1]));
            x.Tok = L(r[2]); x.Cnt = L(r[3]);
            x.Pct = (int)Math.Round(x.Tok * 100.0 / total);
            bml.Add(x);
        }
        d.Bm = bml.ToArray();
        // 按会话 · 今日（子代理归并）
        object[][] ts = QueryH(db, 
            "SELECT CASE WHEN u.task_type='subagent_child' THEN '子代理' "
            + "ELSE COALESCE(s.title, u.session_id) END, COUNT(*), "
            + "CAST(SUM(u.output_tokens+u.reasoning_tokens+u.input_tokens"
            + "+u.cache_creation_input_tokens+u.cache_read_input_tokens*"
            + bill.ToString("0.0##") + ") AS INT) "
            + "FROM model_usage u LEFT JOIN session s ON s.id=u.session_id "
            + "WHERE u.status='completed' AND u.completed_at>=? "
            + "GROUP BY 1 ORDER BY 3 DESC LIMIT 20", d0);
        List<TsData> tsl = new List<TsData>();
        for (int i = 0; i < ts.Length; i++)
        {
            TsData x = new TsData();
            x.Title = S(ts[i][0]); x.Cnt = L(ts[i][1]); x.Tok = L(ts[i][2]);
            tsl.Add(x);
        }
        d.Ts = tsl.ToArray();
        // 会话压缩（30 天，未归档）
        long cutCp = UnixMs() - 30L * 86400 * 1000;
        object[][] cp = QueryH(db, 
            "SELECT COALESCE(s.title, u.session_id), COUNT(*), MAX(u.completed_at) "
            + "FROM model_usage u LEFT JOIN session s ON s.id=u.session_id "
            + "WHERE u.query_source='compact' AND u.status='completed' "
            + "AND u.completed_at>=? AND s.time_archived IS NULL "
            + "GROUP BY u.session_id ORDER BY 3 DESC", cutCp);
        List<CpData> cpl = new List<CpData>();
        for (int i = 0; i < cp.Length; i++)
        {
            CpData x = new CpData();
            x.Title = S(cp[i][0]); x.N = L(cp[i][1]); x.Last = L(cp[i][2]);
            cpl.Add(x);
        }
        d.Cp = cpl.ToArray();
        // 近 7 日
        long day0w = d0 - 6L * 86400 * 1000;
        object[][] wd = QueryH(db, 
            "SELECT strftime('%Y-%m-%d', completed_at/1000, 'unixepoch', 'localtime'), "
            + "CAST(SUM(output_tokens+reasoning_tokens+input_tokens"
            + "+cache_creation_input_tokens+cache_read_input_tokens*"
            + bill.ToString("0.0##") + ") AS INT) FROM model_usage "
            + "WHERE status='completed' AND completed_at>=? GROUP BY 1", day0w);
        Dictionary<string, long> sums = new Dictionary<string, long>();
        for (int i = 0; i < wd.Length; i++) sums[S(wd[i][0])] = L(wd[i][1]);
        List<DayData> dls = new List<DayData>();
        for (int i = 0; i < 7; i++)
        {
            DateTime dd = new DateTime(1970, 1, 1).AddMilliseconds(day0w + i * 86400L * 1000);
            DayData x = new DayData();
            x.Label = dd.ToString("MM-dd");
            long t;
            sums.TryGetValue(x.Label, out t);
            x.Tok = t;
            dls.Add(x);
        }
        d.Days = dls.ToArray();
        return d;
    }

    static void DetailDataTick()
    {
        if (!DetailVisible || DbH == IntPtr.Zero) return;
        int tc = Environment.TickCount;
        if (LastDetailCalc != 0 && tc - LastDetailCalc < 1000) return;
        LastDetailCalc = tc;
        IntPtr ddb = (DetailTool == 1 && CodexH != IntPtr.Zero) ? CodexH : DbH;
        try { DD = RefreshDetail(ddb); DrawDetail(); }
        catch (Exception e) { ErrLog("detail", e.Message); }
    }

    static void ToggleDetail()
    {
        DetailVisible = !DetailVisible;
        if (DetailVisible)
        {
            if (DetailX < 0)
            {
                RECT wa = new RECT();
                if (!SystemParametersInfoW(0x0048, 0, ref wa, 0)) { wa.r = 1200; wa.b = 700; }
                DetailX = wa.r - 470; DetailY = wa.t + 40;
            }
            ShowWindow(DetailHwnd, SW_SHOWNORMAL);
            SetForegroundWindow(DetailHwnd);
            SetWindowPos(DetailHwnd, HWND_TOP, DetailX, DetailY, 0, 0,
                         SWP_NOSIZE | SWP_SHOWWINDOW);
            LastDetailCalc = 0;
            DetailDataTick();
        }
        else ShowWindow(DetailHwnd, SW_HIDE);
    }

    static void EnsureDetailFonts(uint dpi)
    {
        if (DetailFontDpi == dpi && DFBase != null) return;
        DetailFontDpi = dpi;
        double s = dpi / 96.0;
        if (DFBase != null) DFBase.Dispose();
        if (DFSec != null) DFSec.Dispose();
        if (DFName != null) DFName.Dispose();
        if (DFMeta != null) DFMeta.Dispose();
        if (DFVal != null) DFVal.Dispose();
        if (DFCardV != null) DFCardV.Dispose();
        if (DFCardK != null) DFCardK.Dispose();
        if (DFTab != null) DFTab.Dispose();
        if (DFChart != null) DFChart.Dispose();
        if (DFSel != null) DFSel.Dispose();
        // 钛白：数字/元信息走 Consolas 等宽，页签与中文界面走 Segoe UI（GDI 对缺字自动回退）
        DFBase = new Font("Segoe UI", (int)Math.Round(13 * s), FontStyle.Regular, GraphicsUnit.Pixel);
        DFSec = new Font("Consolas", (int)Math.Round(10 * s), FontStyle.Regular, GraphicsUnit.Pixel);
        DFName = new Font("Segoe UI", (int)Math.Round(13 * s), FontStyle.Bold, GraphicsUnit.Pixel);
        DFMeta = new Font("Consolas", (int)Math.Round(10.5 * s), FontStyle.Regular, GraphicsUnit.Pixel);
        DFSel = new Font("Consolas", (int)Math.Round(10.5 * s), FontStyle.Bold, GraphicsUnit.Pixel);
        DFVal = new Font("Consolas", (int)Math.Round(16 * s), FontStyle.Bold, GraphicsUnit.Pixel);
        DFCardV = new Font("Consolas", (int)Math.Round(22 * s), FontStyle.Bold, GraphicsUnit.Pixel);
        DFCardK = new Font("Consolas", (int)Math.Round(9 * s), FontStyle.Regular, GraphicsUnit.Pixel);
        DFTab = new Font("Segoe UI", (int)Math.Round(13 * s), FontStyle.Bold, GraphicsUnit.Pixel);
        DFChart = new Font("Consolas", (int)Math.Round(9 * s), FontStyle.Regular, GraphicsUnit.Pixel);
    }

    // 一行：名称 + 右侧 meta 串 + 值 + 条
    static string FitText(Graphics g, string text, Font font, float maxWidth, StringFormat fmt)
    {
        if (text == null || text.Length == 0 || maxWidth <= 0) return "";
        if (g.MeasureString(text, font, PointF.Empty, fmt).Width <= maxWidth) return text;
        const string ell = "\u2026";
        if (g.MeasureString(ell, font, PointF.Empty, fmt).Width > maxWidth) return "";
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            string probe = text.Substring(0, mid) + ell;
            if (g.MeasureString(probe, font, PointF.Empty, fmt).Width <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return text.Substring(0, lo) + ell;
    }

    // 圆角矩形路径（页签胶囊/来源轨道/窗口圆角，radius 单位=物理px）
    static System.Drawing.Drawing2D.GraphicsPath RoundPath(float x, float y, float w, float h, float r)
    {
        System.Drawing.Drawing2D.GraphicsPath p = new System.Drawing.Drawing2D.GraphicsPath();
        if (r <= 0) { p.AddRectangle(new RectangleF(x, y, w, h)); return p; }
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static int DrawRow(Graphics g, int y, int w, int pad, string name, string meta,
                       string val, Color vc, int barPct, Color barC)
    {
        double s = DetailScale;
        float x0 = (float)Math.Round(pad * s);
        float rx = w - (float)Math.Round(26 * s);
        float gap = (float)Math.Round(10 * s);
        StringFormat fmt = StringFormat.GenericTypographic;
        string value = val ?? "";
        SizeF rawValue = g.MeasureString(value, DFVal, PointF.Empty, fmt);
        float valueX = rx - rawValue.Width;
        float metaRight = valueX - gap;
        float metaMax = Math.Min((float)Math.Round(198 * s), (rx - x0) * 0.46f);
        string metaDraw = FitText(g, meta ?? "", DFMeta, metaMax, fmt);
        SizeF metaSize = g.MeasureString(metaDraw, DFMeta, PointF.Empty, fmt);
        float metaX = metaRight - metaSize.Width;
        float nameMax = Math.Max(0, metaX - gap - x0);
        string nameDraw = FitText(g, name ?? "", DFName, nameMax, fmt);
        using (SolidBrush b = new SolidBrush(InkD))
            g.DrawString(nameDraw, DFName, b, x0, y, fmt);
        using (SolidBrush b = new SolidBrush(vc))
            g.DrawString(value, DFVal, b, valueX, y - 2 * (float)s, fmt);
        if (metaDraw.Length > 0)
            using (SolidBrush b = new SolidBrush(MutedD))
                g.DrawString(metaDraw, DFMeta, b, metaX, y + 5 * (float)s, fmt);
        int barY = y + (int)Math.Round(24 * s);
        int hair = Math.Max(1, (int)Math.Round(1 * s));       // 分隔发丝 1 逻辑px
        int barH = Math.Max(2, (int)Math.Round(2 * s));       // 色条 2 逻辑px，底对齐发丝
        using (SolidBrush b = new SolidBrush(HairD))
            g.FillRectangle(b, x0, barY, rx - x0, hair);
        int fill = Math.Max(0, Math.Min(100, barPct)) * ((int)rx - (int)x0) / 100;
        if (fill > 0)
            using (SolidBrush b = new SolidBrush(barC))
                g.FillRectangle(b, x0, barY - barH + hair, fill, barH);
        return y + (int)Math.Round(46 * s);
    }

    // 区段标题：钢蓝等宽字 + 右侧延伸发丝线（对照 .section-title::after）
    static int DrawSec(Graphics g, int y, int w, string text)
    {
        double s = DetailScale;
        StringFormat fmt = StringFormat.GenericTypographic;
        float x0 = (float)Math.Round(26 * s);
        using (SolidBrush b = new SolidBrush(AccentD))
            g.DrawString(text, DFSec, b, x0, y, fmt);
        SizeF tw = g.MeasureString(text, DFSec, PointF.Empty, fmt);
        float ruleX = x0 + tw.Width + (float)Math.Round(8 * s);
        float ruleW = w - (float)Math.Round(26 * s) - ruleX;
        if (ruleW > (float)Math.Round(6 * s))
            using (SolidBrush b = new SolidBrush(RuleD))
                g.FillRectangle(b, ruleX, y + tw.Height / 2f,
                                ruleW, Math.Max(1f, (float)Math.Round(1 * s)));
        return y + (int)Math.Round(24 * s);
    }
    // 空态：虚线框（对照 .empty）
    static int DrawEmpty(Graphics g, int y, int w, string text)
    {
        double s = DetailScale;
        StringFormat fmt = StringFormat.GenericTypographic;
        float x0 = (float)Math.Round(26 * s);
        float rx = w - (float)Math.Round(26 * s);
        float boxH = (float)Math.Round(46 * s);
        using (Pen p = new Pen(BarBgD, Math.Max(1f, (float)Math.Round(1 * s))))
        {
            p.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
            g.DrawRectangle(p, x0, y, rx - x0, boxH);
        }
        SizeF tw = g.MeasureString(text, DFBase, PointF.Empty, fmt);
        using (SolidBrush b = new SolidBrush(MutedD))
            g.DrawString(text, DFBase, b,
                         x0 + (rx - x0 - tw.Width) / 2f,
                         y + (boxH - tw.Height) / 2f, fmt);
        return y + (int)Math.Round(58 * s);
    }

    static int DrawCards(Graphics g, int y, int w, string[] keys, string[] vals, Color[] vc)
    {
        double s = DetailScale;
        int colW = (w - (int)Math.Round(92 * s)) / 3;
        int x = (int)Math.Round(26 * s);
        StringFormat fmt = StringFormat.GenericTypographic;
        for (int i = 0; i < keys.Length; i++)
        {
            if (i > 0)
                using (SolidBrush b = new SolidBrush(LineD))
                    g.FillRectangle(b, x - (int)Math.Round(9 * s), y,
                                    Math.Max(1, (int)Math.Round(1 * s)),
                                    (int)Math.Round(46 * s));
            float maxW = colW - (float)Math.Round(8 * s);
            string key = FitText(g, keys[i], DFCardK, maxW, fmt);
            using (SolidBrush b = new SolidBrush(MutedD))
                g.DrawString(key, DFCardK, b, x, y, fmt);
            string value = vals[i] == null ? "" : vals[i];
            Font valueFont = DFCardV;
            Font compactFont = null;
            float size = DFCardV.Size;
            SizeF valueSize = g.MeasureString(value, valueFont, PointF.Empty, fmt);
            float minSize = (float)Math.Round(13 * s);
            while (valueSize.Width > maxW && size > minSize)
            {
                size -= 1;
                if (compactFont != null) compactFont.Dispose();
                compactFont = new Font(DFCardV.FontFamily, size, DFCardV.Style, GraphicsUnit.Pixel);
                valueFont = compactFont;
                valueSize = g.MeasureString(value, valueFont, PointF.Empty, fmt);
            }
            string valueDraw = FitText(g, value, valueFont, maxW, fmt);
            using (SolidBrush b = new SolidBrush(i < vc.Length ? vc[i] : InkD))
                g.DrawString(valueDraw, valueFont, b, x, y + (int)Math.Round(14 * s), fmt);
            if (compactFont != null) compactFont.Dispose();
            x += colW + (int)Math.Round(18 * s);
        }
        // 卡片区底部发丝线（对照 .kpi-grid border-bottom）
        using (SolidBrush b = new SolidBrush(LineD))
            g.FillRectangle(b, (int)Math.Round(26 * s), y + (int)Math.Round(48 * s),
                            w - (int)Math.Round(52 * s),
                            Math.Max(1, (int)Math.Round(1 * s)));
        return y + (int)Math.Round(58 * s);
    }

    static void DrawDetail()
    {
        if (DetailHwnd == IntPtr.Zero) return;
        uint dpi = GetDpiForWindow(DetailHwnd); if (dpi == 0) dpi = 96;
        EnsureDetailFonts(dpi);
        DetailScale = dpi / 96.0;
        double s = DetailScale;
        int w = (int)Math.Round(440 * s), h = (int)Math.Round(660 * s);
        int headerH = (int)Math.Round(52 * s);
        int contentTop = (int)Math.Round(56 * s);
        using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                StringFormat typ = StringFormat.GenericTypographic;
                // 8px 圆角窗口（对照 --win-radius）：先整窗裁剪，四角保持透明
                using (System.Drawing.Drawing2D.GraphicsPath winPath =
                       RoundPath(0, 0, w, h, (float)Math.Round(8 * s)))
                    g.SetClip(winPath);
                using (SolidBrush pb = new SolidBrush(PaperD))
                    g.FillRectangle(pb, 0, 0, w, h);
                using (SolidBrush hb = new SolidBrush(HeadD))
                    g.FillRectangle(hb, 0, 0, w, headerH);
                float bw = Math.Max(1f, (float)Math.Round(1 * s));
                using (System.Drawing.Drawing2D.GraphicsPath winPath =
                       RoundPath(bw / 2f, bw / 2f, w - bw, h - bw,
                                 (float)Math.Round(8 * s) - bw / 2f))
                using (Pen bp = new Pen(RuleD, bw))
                    g.DrawPath(bp, winPath);
                // 单行头部：页签胶囊（左）+ 来源分段 + 日期 + X（右）
                string[] tabs = new string[] { "总览", "速度", "总量" };
                float tabX = (float)Math.Round(16 * s);
                float tabY = (float)Math.Round(11 * s);
                float tabH = (float)Math.Round(30 * s);
                for (int i = 0; i < 3; i++)
                {
                    bool on = i == DetailPage;
                    SizeF tw = g.MeasureString(tabs[i], DFTab, PointF.Empty, typ);
                    float pw = tw.Width + (float)Math.Round(16 * s);
                    if (on)
                        using (System.Drawing.Drawing2D.GraphicsPath pill =
                               RoundPath(tabX, tabY, pw, tabH, (float)Math.Round(6 * s)))
                        using (SolidBrush b = new SolidBrush(AccentD))
                            g.FillPath(b, pill);
                    using (SolidBrush b = new SolidBrush(on ? Color.White : MutedD))
                        g.DrawString(tabs[i], DFTab, b,
                                     tabX + (pw - tw.Width) / 2f,
                                     tabY + (tabH - tw.Height) / 2f, typ);
                    TabRects[i].X = (int)Math.Round(tabX);
                    TabRects[i].Y = (int)tabY;
                    TabRects[i].W = (int)Math.Ceiling(pw);
                    TabRects[i].H = (int)tabH;
                    tabX += pw + (float)Math.Round(4 * s);
                }
                // X 关闭（右缘，36x34 热区）
                CloseRect.X = w - (int)Math.Round(42 * s);
                CloseRect.Y = (int)Math.Round(9 * s);
                CloseRect.W = (int)Math.Round(36 * s);
                CloseRect.H = (int)Math.Round(34 * s);
                using (Pen p = new Pen(MutedD, (float)Math.Max(1.5, 1.5 * s)))
                {
                    float cx = CloseRect.X + CloseRect.W / 2f;
                    float cy = CloseRect.Y + CloseRect.H / 2f;
                    g.DrawLine(p, cx - 5.5f * (float)s, cy - 5.5f * (float)s,
                                  cx + 5.5f * (float)s, cy + 5.5f * (float)s);
                    g.DrawLine(p, cx + 5.5f * (float)s, cy - 5.5f * (float)s,
                                  cx - 5.5f * (float)s, cy + 5.5f * (float)s);
                }
                // 日期（钢蓝，紧贴 X 左侧）
                string stamp = DateTime.Now.ToString("MM-dd");
                SizeF stampSize = g.MeasureString(stamp, DFMeta, PointF.Empty, typ);
                float dateX = CloseRect.X - (float)Math.Round(6 * s) - stampSize.Width;
                using (SolidBrush b = new SolidBrush(AccentD))
                    g.DrawString(stamp, DFMeta, b, dateX,
                                 (headerH - stampSize.Height) / 2f, typ);
                // 来源分段：紧凑轨道 + 选中钢蓝填充（对照 .theme-titan .source-tabs）
                // 轨道宽按粗体测宽固定，切换来源时轨道尺寸不抖动
                string[] tools = new string[] { "ZCode", "Codex" };
                float segH = (float)Math.Round(26 * s);
                float segPad = (float)Math.Round(3 * s);
                float segGap = (float)Math.Round(2 * s);
                float segIn = (float)Math.Round(1 * s);
                float[] bwv = new float[2];
                for (int i = 0; i < 2; i++)
                    bwv[i] = g.MeasureString(tools[i], DFSel, PointF.Empty, typ).Width
                             + (float)Math.Round(16 * s);
                float trackW = bwv[0] + bwv[1] + segGap + segPad * 2 + segIn * 2;
                float trackH = segH + segPad * 2 + segIn * 2;
                float trackX = dateX - (float)Math.Round(8 * s) - trackW;
                float trackY = (headerH - trackH) / 2f;
                using (System.Drawing.Drawing2D.GraphicsPath track =
                       RoundPath(trackX, trackY, trackW, trackH, (float)Math.Round(6 * s)))
                {
                    using (SolidBrush b = new SolidBrush(SrcTrackD))
                        g.FillPath(b, track);
                    using (Pen p = new Pen(SrcTrackLineD, bw))
                        g.DrawPath(p, track);
                }
                float segX = trackX + segIn + segPad;
                for (int i = 0; i < 2; i++)
                {
                    bool on = i == DetailTool;
                    float segY = trackY + segIn + segPad;
                    if (on)
                        using (System.Drawing.Drawing2D.GraphicsPath pill =
                               RoundPath(segX, segY, bwv[i], segH, (float)Math.Round(4 * s)))
                        using (SolidBrush b = new SolidBrush(AccentD))
                            g.FillPath(b, pill);
                    Font sf = on ? DFSel : DFMeta;
                    SizeF tw2 = g.MeasureString(tools[i], sf, PointF.Empty, typ);
                    using (SolidBrush b = new SolidBrush(on ? Color.White : MutedD))
                        g.DrawString(tools[i], sf, b,
                                     segX + (bwv[i] - tw2.Width) / 2f,
                                     segY + (segH - tw2.Height) / 2f, typ);
                    ToolRects[i].X = (int)Math.Round(segX);
                    ToolRects[i].Y = (int)Math.Round(segY);
                    ToolRects[i].W = (int)Math.Ceiling(bwv[i]);
                    ToolRects[i].H = (int)segH;
                    segX += bwv[i] + segGap;
                }
                // 头部底线
                using (SolidBrush b = new SolidBrush(LineD))
                    g.FillRectangle(b, 0, headerH, w,
                                    Math.Max(1, (int)Math.Round(1 * s)));
                // 内容区：滚动态裁剪，绘制 y 随 DetailScroll 上移
                System.Drawing.Drawing2D.GraphicsState gs = g.Save();
                g.SetClip(new Rectangle(0, contentTop, w, h - contentTop));
                int y = contentTop + (int)Math.Round(10 * s) - DetailScroll;
                SumData sm = DD.Sum;
                if (DetailPage == 0)
                {
                    double avg = sm.TalkMs > 0 ? sm.TalkTok * 1000.0 / sm.TalkMs : 0.0;
                    y = DrawCards(g, y, w,
                        new string[] { "今日请求", "平均速度", "今日 TOKENS" },
                        new string[] { sm.N + " 次", string.Format("{0:0.0} t/s", avg),
                                       Kcn(sm.Tok - (long)(sm.Cache * 0.9)) },
                        new Color[] { InkD, SpdC(avg), InkD });
                    y = DrawSec(g, y, w, "每模型速度 · 今日");
                    if (DD.Dm.Length == 0) y = DrawEmpty(g, y, w, "今日暂无数据");
                    for (int i = 0; i < DD.Dm.Length; i++)
                    {
                        DmData m = DD.Dm[i];
                        int pct = Math.Min(100, (int)Math.Round(m.TpsV / 120.0 * 100));
                        y = DrawRow(g, y, w, 26, m.Model,
                            m.Prov + "  " + m.Cnt + " 次  " + string.Format("{0:0.0}", m.Rpm) + " 次/分",
                            m.Tps, SpdC(m.TpsV), pct, SpdC(m.TpsV));
                    }
                    y = DrawSec(g, y, w, "活跃会话 · 30 分钟窗口");
                    if (DD.Sess.Length == 0) y = DrawEmpty(g, y, w, "30 分钟内无活动会话");
                    for (int i = 0; i < DD.Sess.Length; i++)
                    {
                        SessData x = DD.Sess[i];
                        int pct = Math.Min(100, (int)Math.Round(x.Tps / 120.0 * 100));
                        y = DrawRow(g, y, w, 26, x.Title,
                            "首字 " + (x.Ttft > 0 ? string.Format("{0:0.0}s", x.Ttft) : "-")
                            + "  " + string.Format("{0:0.0}", x.Rpm) + " 次/分  " + x.Cnt + " 次  "
                            + (x.Last > 0 ? FmtMs(x.Last) : "-"),
                            string.Format("{0:0.0}", x.Tps), SpdC(x.Tps), pct, SpdC(x.Tps));
                    }
                }
                else if (DetailPage == 1)
                {
                    double avg = sm.TalkMs > 0 ? sm.TalkTok * 1000.0 / sm.TalkMs : 0.0;
                    y = DrawCards(g, y, w,
                        new string[] { "平均Token速度", "平均请求速度", "平均首字速度" },
                        new string[] { string.Format("{0:0.0} t/s", avg),
                                       string.Format("{0:0.0} 次/分", sm.Rpm),
                                       (DetailTool == 1 ? "-" :
                                        (sm.Ttft > 0 ? string.Format("{0:0.0} s", sm.Ttft) : "-")) },
                        new Color[] { SpdC(avg), InkD, InkD });
                    y = DrawSec(g, y, w, "每模型速度 · 今日（最近 8 条加权）");
                    if (DD.Dm.Length == 0) y = DrawEmpty(g, y, w, "今日暂无数据");
                    for (int i = 0; i < DD.Dm.Length; i++)
                    {
                        DmData m = DD.Dm[i];
                        int pct = Math.Min(100, (int)Math.Round(m.TpsV / 120.0 * 100));
                        y = DrawRow(g, y, w, 26, m.Model,
                            m.Prov + "  " + m.Cnt + " 次  " + string.Format("{0:0.0}", m.Rpm) + " 次/分",
                            m.Tps, SpdC(m.TpsV), pct, SpdC(m.TpsV));
                    }
                    y = DrawSec(g, y, w, "会话速度 · 30 分钟窗口");
                    if (DD.Sess.Length == 0) y = DrawEmpty(g, y, w, "30 分钟内无活动会话");
                    for (int i = 0; i < DD.Sess.Length; i++)
                    {
                        SessData x = DD.Sess[i];
                        int pct = Math.Min(100, (int)Math.Round(x.Tps / 120.0 * 100));
                        y = DrawRow(g, y, w, 26, x.Title,
                            x.Model + "  首字 " + (x.Ttft > 0 ? string.Format("{0:0.0}s", x.Ttft) : "-")
                            + "  " + string.Format("{0:0.0}", x.Rpm) + " 次/分  " + Kcn(x.Usage) + " tok",
                            string.Format("{0:0.0}", x.Tps), SpdC(x.Tps), pct, SpdC(x.Tps));
                    }
                }
                else
                {
                    y = DrawCards(g, y, w,
                        new string[] { "今日 TOKENS", "今日请求", "输入输出" },
                        new string[] { Kcn(sm.Tok - (long)(sm.Cache * 0.9)), sm.N + " 次",
                                       Kcn((long)(sm.Inp + sm.Cache * 0.1)) + " / " + Kcn(sm.Out + sm.Reason) },
                        new Color[] { InkD, InkD, InkD });
                    y = DrawSec(g, y, w, "按模型 · 今日");
                    if (DD.Bm.Length == 0) y = DrawEmpty(g, y, w, "今日暂无数据");
                    for (int i = 0; i < DD.Bm.Length; i++)
                    {
                        BmData m = DD.Bm[i];
                        y = DrawRow(g, y, w, 26, m.Model,
                            m.Prov + "  " + m.Tok.ToString("#,0") + " tok  " + m.Cnt + " 次  " + m.Pct + "%",
                            m.Pct + "%", VermD, m.Pct, VermD);
                    }
                    y = DrawSec(g, y, w, "按会话 · 今日");
                    long tsSum = 1;
                    for (int i = 0; i < DD.Ts.Length; i++) tsSum += DD.Ts[i].Tok;
                    if (DD.Ts.Length == 0) y = DrawEmpty(g, y, w, "今日暂无数据");
                    for (int i = 0; i < DD.Ts.Length; i++)
                    {
                        TsData x = DD.Ts[i];
                        int pct = (int)Math.Round(x.Tok * 100.0 / tsSum);
                        y = DrawRow(g, y, w, 26, x.Title,
                            x.Cnt + " 次  " + Kcn(x.Tok) + " tok", Kcn(x.Tok), VermD, pct, VermD);
                    }
                    long cn = 0;
                    for (int i = 0; i < DD.Cp.Length; i++) cn += DD.Cp[i].N;
                    y = DrawSec(g, y, w, "会话压缩 · 30 天内 " + cn + " 次");
                    if (DD.Cp.Length == 0) y = DrawEmpty(g, y, w, "30 天内无压缩会话");
                    for (int i = 0; i < DD.Cp.Length; i++)
                    {
                        CpData x = DD.Cp[i];
                        y = DrawRow(g, y, w, 26, x.Title,
                            x.N + " 次  " + (x.Last > 0 ? FmtMs(x.Last) : "-"),
                            "", InkD, 0, HairD);
                    }
                    bool anyDay = false;
                    for (int i = 0; i < DD.Days.Length; i++) if (DD.Days[i].Tok > 0) { anyDay = true; break; }
                    if (anyDay)
                    {
                        long mx = 1;
                        for (int i = 0; i < DD.Days.Length; i++) if (DD.Days[i].Tok > mx) mx = DD.Days[i].Tok;
                        y = DrawSec(g, y, w, "近 7 日 token");
                        int chW = w - (int)Math.Round(52 * s);
                        int colW = chW / 7;
                        int chartY = y + (int)Math.Round(16 * s);
                        int chartH = (int)Math.Round(46 * s);
                        for (int i = 0; i < 7; i++)
                        {
                            DayData d2 = DD.Days[i];
                            int bh = Math.Max((int)Math.Round(2 * s), (int)Math.Round(d2.Tok * (double)chartH / mx));
                            bool peak = d2.Tok == mx;
                            using (SolidBrush b = new SolidBrush(peak ? VermD : BarBgD))
                                g.FillRectangle(b, (int)Math.Round(26 * s) + i * colW,
                                                chartY + chartH - bh, colW - (int)Math.Round(3 * s), bh);
                            using (SolidBrush b = new SolidBrush(MutedD))
                            {
                                string num = Kcn(d2.Tok);
                                SizeF nw = g.MeasureString(num, DFChart, PointF.Empty, typ);
                                g.DrawString(num, DFChart, b,
                                    (int)Math.Round(26 * s) + i * colW + (colW - nw.Width) / 2f,
                                    chartY - (int)Math.Round(14 * s), typ);
                                SizeF lw = g.MeasureString(d2.Label, DFChart, PointF.Empty, typ);
                                g.DrawString(d2.Label, DFChart, b,
                                    (int)Math.Round(26 * s) + i * colW + (colW - lw.Width) / 2f,
                                    chartY + chartH + (int)Math.Round(4 * s), typ);
                            }
                        }
                        y = chartY + chartH + (int)Math.Round(22 * s);
                    }
                }
                g.Restore(gs);
                DetailContentH = (y + DetailScroll) - (int)Math.Round(56 * s) + (int)Math.Round(10 * s);
                int maxScroll = DetailContentH - (h - (int)Math.Round(56 * s));
                if (maxScroll < 0) maxScroll = 0;
                if (DetailScroll > maxScroll) { DetailScroll = maxScroll; DetailRedraw = true; }
            }
            Blt(DetailHwnd, bmp, w, h, DetailX, DetailY, 255);
        }
    }

    static void CreateDetailWindow()
    {
        WNDCLASSW wc = new WNDCLASSW();
        wc.proc = Marshal.GetFunctionPointerForDelegate(ProcKeeper);
        wc.inst = Marshal.GetHINSTANCE(typeof(TDN).Module);
        wc.name = "TokenDetailsDetail";
        RegisterClassW(ref wc);
        uint ex = (uint)(0x80000 | WS_EX_APPWINDOW);   // 分层普通窗口：仅详情出现在任务栏
        DetailHwnd = CreateWindowExW(ex, "TokenDetailsDetail", "Token Details 详情", WS_POPUP,
            0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, wc.inst, IntPtr.Zero);
    }

    static int PDown, PHit, PHitX, PHitY;
    static bool DetailDragArmed, DetailDragging;
    static int DetailDragStartX, DetailDragStartY, DetailDragOriginX, DetailDragOriginY;
    const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    static void BeginDetailDrag()
    {
        POINT p = default(POINT);
        GetCursorPos(ref p);
        DetailDragStartX = p.x; DetailDragStartY = p.y;
        DetailDragOriginX = DetailX; DetailDragOriginY = DetailY;
        DetailDragArmed = true; DetailDragging = false;
        SetCapture(DetailHwnd);
    }
    static void MoveDetailDrag()
    {
        if (!DetailDragArmed) return;
        POINT p = default(POINT);
        GetCursorPos(ref p);
        int dx = p.x - DetailDragStartX, dy = p.y - DetailDragStartY;
        if (!DetailDragging && Math.Abs(dx) + Math.Abs(dy) >= 6) DetailDragging = true;
        if (DetailDragging)
        {
            DetailX = DetailDragOriginX + dx; DetailY = DetailDragOriginY + dy;
            RECT wa = new RECT();
            if (SystemParametersInfoW(0x0048, 0, ref wa, 0))
            {
                int dpiW = (int)Math.Round(440 * DetailScale), dpiH = (int)Math.Round(660 * DetailScale);
                int minX = wa.l - dpiW + (int)Math.Round(40 * DetailScale);
                int maxX = wa.r - (int)Math.Round(40 * DetailScale);
                int minY = wa.t;
                int maxY = wa.b - (int)Math.Round(40 * DetailScale);
                if (DetailX < minX) DetailX = minX;
                if (DetailX > maxX) DetailX = maxX;
                if (DetailY < minY) DetailY = minY;
                if (DetailY > maxY) DetailY = maxY;
            }
            SetWindowPos(DetailHwnd, HWND_TOP, DetailX, DetailY, 0, 0,
                         SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }
    static bool EndDetailDrag()
    {
        if (!DetailDragArmed) return false;
        bool moved = DetailDragging;
        DetailDragArmed = false; DetailDragging = false;
        ReleaseCapture();
        return moved;
    }
    static void DetailMouseDown(int px, int py)
    {
        for (int i = 0; i < 2; i++)
        {
            if (px >= ToolRects[i].X && px <= ToolRects[i].X + ToolRects[i].W
                && py >= ToolRects[i].Y && py <= ToolRects[i].Y + ToolRects[i].H)
            {
                if (DetailTool != i)
                { DetailTool = i; DetailScroll = 0; LastDetailCalc = 0; DetailDataTick(); }
                return;
            }
        }
        for (int i = 0; i < 3; i++)
        {
            if (px >= TabRects[i].X && px <= TabRects[i].X + TabRects[i].W
                && py >= TabRects[i].Y && py <= TabRects[i].Y + TabRects[i].H)
            {
                PHit++;
                if (DetailPage != i) { DetailPage = i; DetailScroll = 0; DrawDetail(); }
                return;
            }
        }
    }

    static void DetailWheel(int delta)
    {
        DetailScroll -= delta / 120 * 60;
        if (DetailScroll < 0) DetailScroll = 0;
        try { DrawDetail(); } catch (Exception e) { ErrLog("detail", e.Message); }
    }

    // ================= Codex 双源（对照 _parse_rollout/codex_db 适配层） =================
    static IntPtr CodexH;
    static Dictionary<string, DateTime> CodexFiles;
    static DateTime CodexScanAt = DateTime.MinValue;
    static int DetailTool;   // 0=ZCode 1=Codex
    static readonly RectI[] ToolRects = new RectI[] { new RectI(), new RectI() };

    static long IsoMs(string t)
    {
        try
        {
            DateTime d = DateTime.Parse(t, System.Globalization.CultureInfo.InvariantCulture,
                                        System.Globalization.DateTimeStyles.RoundtripKind);
            if (d.Kind == DateTimeKind.Local) d = d.ToUniversalTime();
            return (long)(d - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }
        catch { return 0; }
    }
    static object JGet(Dictionary<string, object> d, string k)
    {
        object v; return d != null && d.TryGetValue(k, out v) ? v : null;
    }
    static Dictionary<string, object> JDict(object v)
    { return v as Dictionary<string, object>; }

    static void CodexInsert(string model, string sid, long start, long done,
        long outTok, long inTok, long reasonTok, long cacheW, long cacheR, string src)
    {
        QueryH(CodexH, "INSERT INTO model_usage VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
            "", sid, "codex", model, "completed", start, start, done,
            Math.Max(0, done - start), outTok,
            Math.Max(0, inTok - cacheR), reasonTok, cacheW, cacheR, src, "interactive");
    }

    static void ParseRollout(string path)
    {
        string sid = "", model = "";
        long prevTs = 0; bool hasPrev = false;
        Dictionary<string, long> prevTot = null;
        List<object[]> rows = new List<object[]>();   // {kind, model, sid, start, ts, dlt}
        foreach (string line in System.IO.File.ReadAllLines(path, Encoding.UTF8))
        {
            Dictionary<string, object> d;
            try { d = JDict(new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(line)); }
            catch { continue; }
            if (d == null) continue;
            string t = S(JGet(d, "type"));
            Dictionary<string, object> pl = JDict(JGet(d, "payload"));
            string tsS = S(JGet(d, "timestamp"));
            long ts = tsS != null ? IsoMs(tsS) : 0;
            if (t == "session_meta")
            {
                sid = S(JGet(pl, "id")) ?? "";
                string cwd = (S(JGet(pl, "cwd")) ?? "").TrimEnd('/', '\\');
                string title = cwd.Length > 0 ? System.IO.Path.GetFileName(cwd) : "";
                if (title.Length == 0) title = sid.Length > 8 ? sid.Substring(0, 8) : "";
                if (title.Length == 0) title = "?";
                if (sid.Length > 0)
                    QueryH(CodexH, "INSERT OR REPLACE INTO session (id, title) VALUES (?,?)", sid, title);
            }
            else if (t == "turn_context")
            {
                string m = S(JGet(pl, "model"));
                if (m != null && m.Length > 0) model = m;
                else if (model.Length == 0) model = "?";
            }
            else if (t == "compacted" && sid.Length > 0 && ts > 0)
            {
                rows.Add(new object[] { "c", "", sid, ts, ts, null });
            }
            else if (t == "event_msg" && pl != null && S(JGet(pl, "type")) == "token_count"
                     && sid.Length > 0 && ts > 0)
            {
                Dictionary<string, object> info = JDict(JGet(pl, "info"));
                Dictionary<string, object> totD = JDict(JGet(info, "total_token_usage"));
                if (totD == null) { prevTs = ts; prevTot = prevTot ?? new Dictionary<string, long>(); continue; }
                Dictionary<string, long> tot = new Dictionary<string, long>();
                foreach (KeyValuePair<string, object> kv in totD)
                    tot[kv.Key] = kv.Value == null ? 0 : Convert.ToInt64(kv.Value);
                Dictionary<string, long> dlt = new Dictionary<string, long>();
                bool any = false;
                foreach (KeyValuePair<string, long> kv in tot)
                {
                    long b = 0;
                    if (prevTot != null && prevTot.ContainsKey(kv.Key)) b = prevTot[kv.Key];
                    long dv = kv.Value - b; if (dv < 0) dv = 0;
                    dlt[kv.Key] = dv;
                    if (dv > 0) any = true;
                }
                if (any)
                {
                    long start = hasPrev ? prevTs : ts;
                    rows.Add(new object[] { "m", model, sid, start, ts, dlt });
                }
                prevTs = ts; hasPrev = true; prevTot = tot;
            }
        }
        foreach (object[] r in rows)
        {
            if ((string)r[0] == "c")
            {
                long ts = (long)r[4];
                QueryH(CodexH, "INSERT INTO model_usage VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                    "", r[2], "codex", "", "completed", ts, ts, ts, 0, 0, 0, 0, 0, 0,
                    "compact", "interactive");
            }
            else
            {
                Dictionary<string, long> dlt = (Dictionary<string, long>)r[5];
                long start = (long)r[3], ts = (long)r[4];
                CodexInsert((string)r[1], (string)r[2], start, ts,
                    Dg(dlt, "output_tokens"),
                    Dg(dlt, "input_tokens"),
                    Dg(dlt, "reasoning_output_tokens"),
                    Dg(dlt, "cache_write_input_tokens"),
                    Dg(dlt, "cached_input_tokens"), "main_turn");
            }
        }
    }
    static long Dg(Dictionary<string, long> d, string k)
    {
        long v; return d.TryGetValue(k, out v) ? v : 0;
    }

    // 近 8 天 rollout -> 内存库；3s 扫描节流 + mtime 集合不变复用（对照 codex_db）
    static void CodexDb()
    {
        DateTime now = DateTime.Now;
        if (CodexH != IntPtr.Zero && (now - CodexScanAt).TotalSeconds < 3.0) return;
        CodexScanAt = now;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dir = home + "\\.codex\\sessions";
        Dictionary<string, DateTime> files = new Dictionary<string, DateTime>();
        if (System.IO.Directory.Exists(dir))
        {
            DateTime cutoff = now - TimeSpan.FromDays(8);
            string[] all;
            try { all = System.IO.Directory.GetFiles(dir, "*.jsonl", System.IO.SearchOption.AllDirectories); }
            catch { all = new string[0]; }
            foreach (string f in all)
            {
                try
                {
                    DateTime m = System.IO.File.GetLastWriteTime(f);
                    if (m >= cutoff) files[f] = m;
                }
                catch { }
            }
        }
        if (CodexH != IntPtr.Zero && CodexFiles != null && files.Count == CodexFiles.Count)
        {
            bool same = true;
            foreach (KeyValuePair<string, DateTime> kv in files)
            {
                DateTime old;
                if (!CodexFiles.TryGetValue(kv.Key, out old) || old != kv.Value) { same = false; break; }
            }
            if (same) return;   // 文件集合与 mtime 全等 → 复用
        }
        CodexFiles = files;
        if (CodexH != IntPtr.Zero) { sqlite3_close(CodexH); CodexH = IntPtr.Zero; }
        int rc = sqlite3_open_v2(Utf8z(":memory:"), out CodexH, 2 | 4, IntPtr.Zero);   // READWRITE|CREATE
        if (rc != 0) { CodexH = IntPtr.Zero; return; }
        QueryH(CodexH, "CREATE TABLE model_usage (id TEXT, session_id TEXT, provider_id TEXT,"
            + " model_id TEXT, status TEXT, started_at INTEGER, first_token_at INTEGER,"
            + " completed_at INTEGER, duration_ms INTEGER, output_tokens INTEGER,"
            + " input_tokens INTEGER, reasoning_tokens INTEGER,"
            + " cache_creation_input_tokens INTEGER, cache_read_input_tokens INTEGER,"
            + " query_source TEXT, task_type TEXT DEFAULT 'interactive')");
        QueryH(CodexH, "CREATE TABLE session (id TEXT PRIMARY KEY, title TEXT, time_archived INTEGER)");
        ProvNames["codex"] = "Codex";
        List<string> paths = new List<string>(files.Keys);
        paths.Sort();
        foreach (string f in paths)
        {
            try { ParseRollout(f); }
            catch (Exception e) { ErrLog("codex-parse", e.Message); }
        }
    }

    static void Main()
    {
        // 单实例（对照 single_instance：二次启动静默退出）
        bool created;
        Mutex mx = new Mutex(true, MutexName, out created);
        if (!created) return;
        try { if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) throw new Exception(); }
        catch
        {
            try { SetProcessDpiAwareness(2); } catch { try { SetProcessDPIAware(); } catch { } }
        }
        AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs a)
        { ErrLog("fatal", a.ExceptionObject != null ? a.ExceptionObject.ToString() : "?"); };
        Thread t = new Thread(Worker);
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Thread.Sleep(Timeout.Infinite);
    }
}
