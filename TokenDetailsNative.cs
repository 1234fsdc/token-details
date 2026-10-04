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
    static object[][] Query(string sql, params object[] args)
    {
        IntPtr stmt;
        int rc = sqlite3_prepare_v2(DbH, Utf8z(sql), -1, out stmt, IntPtr.Zero);
        if (rc != 0)
        {
            ErrLog("db", "prepare " + Marshal.PtrToStringAnsi(sqlite3_errmsg(DbH)));
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

    class ModelAgg { public string Model; public string Tps; public long Last; }

    static ModelAgg[] SessionSpeed(string sid, string modelHint)
    {
        if (String.IsNullOrEmpty(sid) || DbH == IntPtr.Zero) return new ModelAgg[0];
        string cur = modelHint == null ? "" : modelHint.Trim();
        if (cur.Length == 0)
        {   // 无 hint：取该会话最新 assistant 消息的模型（对照 Python 回退查询）
            object[][] m = Query("SELECT COALESCE(json_extract(data,'$.modelId'), "
                + "json_extract(data,'$.modelID')) FROM message "
                + "WHERE session_id=? AND json_extract(data,'$.role')='assistant' "
                + "ORDER BY time_updated DESC LIMIT 1", sid);
            cur = m.Length > 0 ? (S(m[0][0]) ?? "") : "";
        }
        string q = "SELECT id, provider_id, model_id, status, started_at, first_token_at, "
            + "completed_at, duration_ms, output_tokens, reasoning_tokens FROM model_usage "
            + "WHERE session_id=? AND status='completed' AND query_source!='compact' ";
        object[] args;
        if (cur.Length > 0) { q += "AND model_id=? "; args = new object[] { sid, cur }; }
        else args = new object[] { sid };
        q += "ORDER BY COALESCE(completed_at, started_at) DESC LIMIT " + SpeedQueryLimit;
        object[][] rows = Query(q, args);
        Dictionary<string, List<object[]>> by = new Dictionary<string, List<object[]>>();
        for (int i = 0; i < rows.Length; i++)
        {
            object[] r = rows[i];
            long tok = RowTok(r), el = ElapsedMs(r);
            if (tok <= 0 || el <= 0) continue;   // 缺时间戳/无 usage 的行不计速度
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
            for (int i = 0; i < per; i++) { tot += RowTok(lst[i]); el += ElapsedMs(lst[i]); }
            if (el <= 0) continue;
            ModelAgg a = new ModelAgg();
            a.Model = mdl;
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

    // ================= UIA（对照 token_watcher.cs 逻辑） =================
    static AutomationElement UiaRoot, TitleEl, ModelEl, AnchorEl;
    static long Hwnd0;
    static int LastWalkMs, LastReadTc;
    static DateTime LastWalk = DateTime.MinValue;
    // watcher 输出槽位（原 stdout 协议 → 字段）
    static string CurTitle = "", CurModel = "";
    static int[] CurAnchor;   // 屏幕物理像素 [l,t,w,h]
    // binder 粘滞槽位（对照 overlay_bind_loop）
    static string LastTitle = "", LastModel = "";
    static double TitleEmptySince;
    static bool EverAnchored;
    static int[] OverlayAnchor;

    // ^[^/\s]+/[^/\s]+$ ：provider/model 格式（避免引正则引擎）
    static bool IsModelName(string s)
    {
        int slash = -1;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '/') { if (slash >= 0) return false; slash = i; }
            else if (char.IsWhiteSpace(c)) return false;
        }
        return slash > 0 && slash < s.Length - 1;
    }
    static string StripModel(string s)
    {
        int i = s.LastIndexOf('/');
        return i >= 0 ? s.Substring(i + 1) : s;
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
                + "ms model=" + (LastModel.Length > 0 ? LastModel : "-") + " tps=" + PayTps);
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
        CurModel = StripModel(model);
        CurAnchor = anchor;
    }

    static void UiaFail()
    {
        UiaRoot = null; ClearRefs();
        CurTitle = ""; CurModel = ""; CurAnchor = null;
    }

    static void UiaTick()
    {
        try
        {
            long hwnd = EnsureZWindow().ToInt64();
            if (hwnd == 0) { UiaFail(); return; }
            if (hwnd != Hwnd0) { Hwnd0 = hwnd; UiaRoot = null; }
            double age = (DateTime.Now - LastWalk).TotalMilliseconds;
            // 快路径三引用健在 → Walk 只是恢复机制，放宽到 8s；引用缺失才 3s 紧追
            int bs = LastWalkMs > 3000 ? 15000
                : (TitleEl != null && ModelEl != null && AnchorEl != null) ? 8000 : 3000;
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
                if (Environment.TickCount - LastReadTc < 150) return;
                LastReadTc = Environment.TickCount; PFast++;
                try
                {
                    System.Windows.Rect ar = AnchorEl.Current.BoundingRectangle;
                    CurAnchor = new int[] { (int)ar.Left, (int)ar.Top, (int)ar.Width, (int)ar.Height };
                    CurTitle = TitleEl.Current.Name ?? "";
                    CurModel = ModelEl != null ? StripModel(ModelEl.Current.Name ?? "") : "";
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
            string title, model;
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
                    if (LastTitle.Length > 0 && TitleEmptySince > 0
                        && now - TitleEmptySince <= 4.0) t = LastTitle;
                    else { if (TitleEmptySince == 0) TitleEmptySince = now; t = ""; }
                }
                title = t;
                model = CurModel.Length > 0 ? CurModel : LastModel;
                if (CurModel.Length > 0) LastModel = CurModel;
            }
            else
            {
                title = LastTitle; model = LastModel;   // watcher 全空沿用健康值
            }

            object[][] m1 = Query("SELECT MAX(rowid) FROM model_usage");
            long mu = m1.Length > 0 ? L(m1[0][0]) : 0;
            object[][] p1 = Query("SELECT MAX(rowid) FROM part");
            long pr = p1.Length > 0 ? L(p1[0][0]) : 0;
            double nowS = NowS();
            object[] key = new object[] { title, model, mu, pr, (long)(nowS / 10) };
            if (LastKey != null && KeyEq(key, LastKey)) return;
            PDirty++;

            string sid = FindSessionByTitle(title);
            if (sid.Length == 0 && !EverAnchored) sid = FallbackSession(UnixMs());   // 仅冷启动
            ModelAgg[] models = SessionSpeed(sid, model);
            // 模型读数校验：hint 在会话无任何完成记录 → 读数不可信，回退最近模型
            if (models.Length == 0 && model.Length > 0)
            {
                models = SessionSpeed(sid, "");
                if (models.Length > 0 && nowS - LastHintLog > 60)
                {
                    LastHintLog = nowS;
                    ErrLog("bind-zero", "model hint '" + model + "' 在会话无完成记录，回退最近模型");
                }
            }
            string reason = "";
            if (models.Length > 0)
            {
                long lastDone = models[0].Last;
                long nowMs = (long)(nowS * 1000);
                if (lastDone < nowMs - 90 * 1000)
                {
                    long act = SessionLastAct(sid);
                    if (act < nowMs - 90 * 1000)
                    {
                        reason = string.Format("90s空闲归零 sid={0} 最近完成{1}s前 part活动{2}s前",
                            sid, (nowMs - lastDone) / 1000, (nowMs - act) / 1000);
                        models = new ModelAgg[0];
                    }
                }
                LastKey = key;   // 与 Python 一致：有数据（含归零）才更新脏标记
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
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool TrackPopupMenu(IntPtr menu, uint flags, int x, int y, uint rsv, IntPtr h, uint rsv2);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr menu, uint fl, IntPtr id, string txt);
    [DllImport("user32.dll")] static extern bool GetCursorPos(ref POINT p);
    [DllImport("user32.dll")] static extern int GetWindowLongW(IntPtr h, int idx);
    [DllImport("user32.dll")] static extern int SetWindowLongW(IntPtr h, int idx, int val);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int k);
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
    public delegate bool EnumCb(IntPtr h, IntPtr l);

    const uint WS_POPUP = 0x80000000;
    const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
              WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
    const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
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
        return DefWindowProcW(h, m, w, l);
    }

    static void ShowTrayMenu()
    {
        if (MenuHandle != IntPtr.Zero) DestroyMenu(MenuHandle);
        MenuHandle = CreatePopupMenu();
        AppendMenuW(MenuHandle, PanelVisible ? 8u : 0u, (IntPtr)2, "简略面板");   // MF_CHECKED
        AppendMenuW(MenuHandle, 3u, (IntPtr)3, "详情");   // MF_GRAYED|MF_DISABLED，P3 解锁
        AppendMenuW(MenuHandle, 0x800u, IntPtr.Zero, "");
        AppendMenuW(MenuHandle, 0, (IntPtr)1, "退出");
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
    static PanelRow[] DetailModels()
    {
        if (DbH == IntPtr.Zero) return new PanelRow[0];
        long cutoff = UnixMs() - 90 * 1000L;
        string q = "SELECT id, provider_id, model_id, status, started_at, first_token_at, "
            + "completed_at, duration_ms, output_tokens, reasoning_tokens FROM model_usage "
            + "WHERE query_source!='compact' "
            + "ORDER BY COALESCE(completed_at, started_at) DESC LIMIT 50";
        object[][] rows = Query(q);
        Dictionary<string, long> act = LastAct();
        List<string> order = new List<string>();
        Dictionary<string, List<object[]>> by = new Dictionary<string, List<object[]>>();
        Dictionary<string, long> lastSeen = new Dictionary<string, long>();
        for (int i = 0; i < rows.Length; i++)
        {
            object[] r = rows[i];
            string mdl = S(r[2]) == null ? "?" : S(r[2]);
            if (!by.ContainsKey(mdl)) { by[mdl] = new List<object[]>(); order.Add(mdl); }
            by[mdl].Add(r);
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
                long tok = RowTok(r), e2 = ElapsedMs(r);
                if (tok <= 0 || e2 <= 0) continue;
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
        System.Collections.ArrayList run = RunningModels();
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
            PanelRow[] rows = DetailModels();
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
                        string v = r.Tps + " t/s";
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
                                 0, 0, 0x0001 | 0x0010);   // NOSIZE|NOACTIVATE
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
