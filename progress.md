# 进度

- 已完成只读调查：确认运行中显示 0 是 native overlay 运行态保护缺口，不是速度公式设计。
- 已完成修复：同一 session/model 有未结束 assistant 回合且已有历史速度时，跳过错误的 90 秒清零；首次 UIA 空标题沿用健康标题并保留 4 秒 TTL。
- 已完成验证：临时 native 内存 SQLite harness 四场景全部 PASS；Python `--self-check` PASS；`build_native.bat` BUILD_OK；独立 native 进程在真实前台 ZCode 下运行，overlay 可见且无新增 bind-zero。
- 已完成模型身份加固：UIA 多段模型名保留 provider/canonical model；session-scoped 活动模型优先 raw/provider 匹配，叶名仅唯一候选可用；当前模型无历史时不继承同会话另一模型。
- 已完成最终编译与 smoke：`BUILD_OK`、模型隔离三场景 PASS、独立 native 进程响应正常并已停止。
- ⚠️ 未验证：透明分层 overlay 的 `PrintWindow` 文字像素断言受系统限制；真实桌面截图确认 ZCode 前台和 overlay 窗口可见，但该接口不能可靠读出透明文字。
- 收尾完成：变更记录、调查记录和任务计划已更新，准备提交。
- 2026-10-06 根因修复已完成：数据库 session 内唯一活动模型覆盖落后的全局 UIA 模型提示；无 provider 模型按钮支持主内容区识别，侧栏 `durkl261` 排除；native/C# watcher/Python fallback 三路同步。
- 2026-10-06 验证完成：native harness 10/10 PASS；Python `--self-check` PASS；`build_native.bat` BUILD_OK；`token_watcher.cs` 编译退出 0；UTF-8 BOM、`git diff --check`、Python 编译和 codegraph sync/status 均通过。
- 2026-10-06 真实桌面 smoke：新进程 pid 12512 于 18:47:05 启动且响应正常；18:48:05 perf 绑定目标 session 实际 Muse 模型，显示 `14.4 t/s`；18:48:49 无活动超过 90 秒后按设计归零。启动后未再出现 `durkl261` 模型提示。
- ⚠️ 未验证：透明分层 overlay 的 `PrintWindow` 文字像素断言仍受系统限制；其余 UIA、数据库、日志和进程健康证据已完成。
