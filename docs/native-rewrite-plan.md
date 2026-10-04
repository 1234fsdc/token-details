# 原生重写方案：单进程 C#（性能最优解）

用户 2026-10-05 改定：完全重构重写，性能开销最小，不按最小改动方案。

## 现状开销（实测）
- PyInstaller onefile：38MB exe 每次启动自解压；Python 运行时 ~140MB 常驻
- WebView2/pywebview 面板：msedgewebview2 进程树 2-4 个进程、150-400MB（面板打开时）
- 双进程：pythonw + token_watcher.exe
- PIL 渲染 overlay 位图
- UIA 150ms 轮询快路径

## 目标架构
单进程 C# exe（.NET Framework 4.x，csc 现编译，与 token_watcher.cs 同管线）：
1. UIA 逻辑从 token_watcher.cs 并入（元素引用快路径/锚点带标题/最底部模型按钮/自适应重走）
2. SQLite 直读：P/Invoke sqlite3.dll（取自本机 Python 运行时，随包分发）
3. overlay：GDI+ UpdateLayeredWindow 每像素 alpha（视觉不变）
4. 简略/详情面板：原生 Win32 自绘，无 WebView2
5. UIA 事件驱动优先（PropertyChanged 订阅）+ 自适应轮询兜底
6. 单线程 tick，无锁

## 预期效果
| 指标 | 现状 | 重写后 |
|---|---|---|
| 内存 | ~140MB+34MB+WebView2 树 150-400MB | 单进程 15-25MB |
| CPU | 空闲 1-2% | 空闲 ~0%、流式 0.5-1.5% |
| 启动 | 数秒 | <200ms |
| 磁盘 | 38MB | ~2MB |
| 进程数 | 3-6 | 1 |

## 语义保持清单（移植时逐条对照 token_speed.py）
- [x] 90s 空闲归零，判活用 part 最新 200 行 MAX(time_updated)
- [x] 标题：精确匹配 + 省略号剥离前缀 LIKE（≥8 字符）+ 仅冷启动 fallback_session（10 分钟窗口）
- [x] 标题过滤：锚点同行带（Top±25、右缘不越锚点、Left≥60），无 blanket 兜底
- [x] 模型按钮：最靠底部 provider/model 匹配
- [x] resolve_title 4s TTL 消歧
- [x] 模型读数校验：hint 在会话无完成记录 → 回退会话最近模型（message 最新 assistant 行）
- [x] 单实例互斥 TokenDetails_Mutex，二次启动静默退出
- [x] bind-zero 诊断日志（有速度→0 瞬间、60s 限频；hint 回退日志）
- [x] overlay 只在 ZCode 前台显示；锚定"选择打开方式"按钮左侧
- [x] 托盘：简略面板开关/详情/退出；面板不可见时跳过取数
- [x] 速度口径：completed+非 compact，8 条窗口加权 (out+reason)/elapsed，provider 前缀剥离
- [x] 简略面板（P2：托盘开关/1Hz/90s 门控/running 合并/把手拖拽/三态 alpha）
- [x] 详情三页（P3：标签页/滚动/全套口径移植）
- [x] 换装 dist + 桌面/Startup 快捷方式指向 native（P5，2026-10-05）
- [ ] codex 双源（二期）
- [ ] part 字符估算 _est_map（二期：无回报模型的估算行）
- [ ] UIA 事件驱动（P4，性能优化，待用户验收 P1-P3 后做）

## 阶段
P1 核心骨架（SQLite+统计+overlay+托盘+互斥+UIA 内联）→ 对照旧版实拍
P2 简略面板原生
P3 详情三页原生
P4 UIA 事件驱动
P5 换装 dist（旧 exe 留 .bak）+ 系统级实测（进程数/内存/CPU）

## 进度日志
- 2026-10-05 04:0x P2/P3/P5 完成（提交 70a7005、718c3e2）：
  - P2 简略面板：托盘菜单三项（简略面板勾选开关/详情/退出）、GDI 渲染对照 HTML 样式、BLENDF 整窗 alpha 三态、把手拖拽穿透、1Hz 取数 sig 重绘。功能链路验证通过（WM_COMMAND 切换、250x74 单行运行中模型、高度公式吻合）。
  - P3 详情三页：440x660 分层窗口、自绘标签页+滚轮+SetClip 滚动、数据全套移植（today_summary/session_stats/dmodels/bm/tsess/cp/days）。打开/切换/滚动/1Hz 刷新验证通过。
  - 两个关键坑：①csc 无 BOM 按 GBK 读源码——P1/P2 中文字面量在 exe 里是乱码，.cs 改 UTF-8 BOM；②详情窗口漏 WS_EX_LAYERED → ULW 静默失败。
  - P5 换装：桌面 + Startup 快捷方式已指向 dist\TokenDetailsNative.exe（回读验证），旧 dist\TokenDetails.exe 保留作回退（含 codex 双源，native 二期才移植）。
  - ⚠️ 未验证：像素级视觉（面板/详情/菜单中文显示）——凌晨锁屏 BitBlt 被拒，待用户回来看；长跑内存趋势；真实重启自启。
  - 性能结论（P1 实测延续）：单进程 ~39MB 私有 / CPU 4.2-4.5% 单核（流式期间），对照旧版 142MB / 6.5%。
- 2026-10-05 03:0x 方案落盘，P1 开工
- 2026-10-05 03:5x P1 完成并实测验收：
  - 口径验证：同一时刻同一会话 Python `session_speed_data` 与 native 均报 **35.4 t/s**，完全一致。此前"33.0 vs 44.5 不一致"是对比对象错误——native 绑定的是当时可见会话（sess_e0d49d03），Python 查的是另一会话（sess_706ece69）。
  - model hint 为空 = 与 Python 行为对等：ZCode 模型按钮只显示 `GLM-5.3-Flash`（无 provider 斜杠前缀），Python 正则 `^[^/\s]+/[^/\s]+$` 同样匹配不上，两版都走 message 表回退且结果一致。
  - CPU 实测 **4.2-4.5% 单核**（旧版全程 6.5%）：初版 8.8% 超标，根因是 FindZCodeProc（全进程枚举）+ FindZCodeWindow（EnumWindows）每 100ms tick 各跑一次，改为 2s 缓存 + IsWindow 失效重找后达标。新增 60s 一条的 perf 哨兵日志（ticks/walks/fast/dirty/相位耗时/model/tps）。
  - 内存实测私有 **~39MB**（旧版 ≈142MB）：每分钟 GC.Collect() 防 RCW 堆积（旧 watcher 膨胀 105MB 教训）。长跑趋势待观察 ⚠️。
  - 与计划预期差异（如实记录）：CPU 未达"空闲~0%/流式 0.5-1.5%"——UIA 全树 Walk 每次 ~470ms 墙钟（多为跨进程阻塞等待，自身 CPU 低），流式期间元素频繁死亡触发重走，walks 17/60s；空闲时自适应 8s 生效后会更低。uia 相位 ~8.5s/60s 墙钟是 P4 事件驱动的主要动机。内存 39MB 高于预期 15-25MB，主要为 UIA RCW + GDI 位图。
  - build_native.bat 修好（LF→CRLF + dir /s /b 找 GAC，cmd for /r 对无通配符文件名有怪癖），双击可重建，实测 BUILD_OK。
