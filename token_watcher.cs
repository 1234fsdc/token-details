// Token Details 原生 UIA watcher（用户 2026-10-02 改定：替代 PowerShell 版）。
// 协议与 token_speed.py 里 UIA_WATCH_SCRIPT 完全一致：stdout 逐行 JSON
// {title, model, anchor, ws}，变更才发行，走树前强制心跳，父进程死自退。
// 编译（Windows 自带 csc，无需 SDK）：
//   csc /nologo /target:exe /optimize+ /r:<GAC>\UIAutomationClient.dll
//       /r:<GAC>\UIAutomationTypes.dll /out:token_watcher.exe token_watcher.cs
// 逻辑与 UIA_WATCH_SCRIPT 保持一致，两处改动要同步。
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

class TokenWatcher
{
    static AutomationElement root, anchorEl, titleEl, modelEl;
    static DateTime lastWalk = DateTime.MinValue;
    static long lastWalkMs = 0;
    static long hwnd0 = 0;
    static string lastLine = "";
    static int parentPid, tick;
    static readonly Process Self = Process.GetCurrentProcess();
    static readonly Regex ModelRe = new Regex("^[^/\\s]+/[^/\\s]+$");

    static StreamWriter Out;

    static string J(string s)
    {
        if (s == null) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c < 32) sb.AppendFormat("\\u{0:x4}", (int)c);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static void Emit(string title, string model, int[] anchor, bool force)
    {
        string a = anchor == null ? "null"
            : "[" + anchor[0] + "," + anchor[1] + "," + anchor[2] + "," + anchor[3] + "]";
        long ws = Self.PrivateMemorySize64 / 1048576;
        string line = "{\"title\":\"" + J(title) + "\",\"model\":\"" + J(model)
            + "\",\"anchor\":" + a + ",\"ws\":" + ws + "}";
        if (force || line != lastLine)
        {
            Out.WriteLine(line);
            lastLine = line;
        }
    }

    // 与 UIA_WATCH_SCRIPT 的 Walk 保持一致：走树一无所获才清缓存引用
    // （Text 空转不清，防运行中会话被空标题 TTL 误归零），部分失败保留旧引用。
    static void Walk(AutomationElement r)
    {
        bool found = false;
        string model = ""; int[] anchor = null;
        var bc = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
        var buttons = r.FindAll(TreeScope.Descendants, bc);
        foreach (AutomationElement e in buttons)
        {
            try
            {
                var rc = e.Current.BoundingRectangle;
                if (e.Current.IsOffscreen) continue;
                string n = e.Current.Name ?? "";
                if (model == "" && rc.Width > 100 && ModelRe.IsMatch(n))
                { model = n; modelEl = e; found = true; }
                if (anchor == null && n == "选择打开方式")
                {
                    anchor = new[] { (int)rc.Left, (int)rc.Top, (int)rc.Width, (int)rc.Height };
                    anchorEl = e; found = true;
                }
            }
            catch { }
        }
        var tc = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
        var texts = r.FindAll(TreeScope.Descendants, tc);
        if (texts.Count == 0)
            texts = r.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        string title = "";
        foreach (AutomationElement e in texts)
        {
            try
            {
                string pn = e.Current.ControlType.ProgrammaticName ?? "";
                if (pn.IndexOf("Text") < 0) continue;
                var rc = e.Current.BoundingRectangle;
                if (e.Current.IsOffscreen) continue;
                string n = e.Current.Name ?? "";
                int ln = n.Length;
                if (ln < 2 || ln >= 200 || ln <= title.Length) continue;
                bool ok = false;
                if (anchor != null && rc.Width >= 30 && Math.Abs(rc.Top - anchor[1]) <= 25
                    && (rc.Left + rc.Width) <= anchor[0] + 10 && rc.Left >= 60) ok = true;
                if (!ok && rc.Top >= 0 && rc.Top <= 120 && rc.Left >= 60 && rc.Left < 2500
                    && rc.Width >= 120 && rc.Width <= 900 && ln >= 8) ok = true;
                if (ok) { title = n; titleEl = e; found = true; }
            }
            catch { }
        }
        if (!found) { anchorEl = null; titleEl = null; modelEl = null; }
        Emit(title, model, anchor, false);
    }

    static void Main()
    {
        // 无控制台进程：直接接管 stdout 标准流，固定 UTF-8（Python 侧 utf-8-sig 解码）
        Out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        int.TryParse(Environment.GetEnvironmentVariable("TDS_PARENT") ?? "", out parentPid);

        while (true)
        {
            try
            {
                Process p = null;
                foreach (var x in Process.GetProcessesByName("ZCode"))
                    if (x.MainWindowHandle != IntPtr.Zero) { p = x; break; }
                if (p == null) throw new Exception("ZCode main window not found");
                long hwnd = p.MainWindowHandle.ToInt64();
                if (hwnd != hwnd0) { hwnd0 = hwnd; root = null; }

                double age = (DateTime.Now - lastWalk).TotalMilliseconds;
                // 兜底间隔自适应：上次走树 >3s（ZCode 流式渲染可达 12s+）拉长到 15s
                int bs = lastWalkMs > 3000 ? 15000 : 3000;
                if (root == null || age > bs)
                {
                    if (root == null)
                        root = AutomationElement.FromHandle(new IntPtr(hwnd0));
                    // 走树前强制心跳带最后已知值：慢走期间 binder 不拿空标题
                    string ht = "", hm = "";
                    try
                    {
                        if (titleEl != null) ht = titleEl.Current.Name ?? "";
                        if (modelEl != null) hm = modelEl.Current.Name ?? "";
                    }
                    catch { }
                    Emit(ht, hm, null, true);
                    var t0 = DateTime.Now;
                    Walk(root);
                    lastWalkMs = (long)(DateTime.Now - t0).TotalMilliseconds;
                    lastWalk = DateTime.Now;
                }
                else if (titleEl != null && anchorEl != null)
                {
                    try
                    {
                        var ar = anchorEl.Current.BoundingRectangle;
                        string mn = modelEl != null ? (modelEl.Current.Name ?? "") : "";
                        Emit(titleEl.Current.Name ?? "", mn,
                             new[] { (int)ar.Left, (int)ar.Top, (int)ar.Width, (int)ar.Height }, false);
                    }
                    catch
                    {
                        // 元素被重渲染换掉（读即抛）→ 清引用防死元素紧循环
                        root = null; anchorEl = null; titleEl = null; modelEl = null;
                        Emit("", "", null, false);
                    }
                }
                else
                {
                    Emit("", "", null, false);  // 上次全量没找齐关键元素：等兜底重走
                }
            }
            catch
            {
                root = null; anchorEl = null; titleEl = null; modelEl = null;
                Emit("", "", null, false);
            }
            tick++;
            if (tick % 20 == 0 && parentPid > 0)
            {
                try { Process.GetProcessById(parentPid); }
                catch { return; }  // pythonw 已死 → 自退，不留孤儿
            }
            Thread.Sleep(150);
        }
    }
}
