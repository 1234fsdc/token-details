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
