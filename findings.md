# 调查发现

- 截图中的会话 `安卓应用在Windows上运行方案` 对应 `sess_1c386dbf-c268-4fe4-80fe-e2d49d378524`。
- 该会话最新请求曾是 `status=error` 且 token 为 0；此前有多条 completed 请求，最近 8 条可计算速度约 6.48 t/s。
- native 归零路径位于 `BindTick()` 的 90 秒门控：完成时间和 part 活动都旧时清空模型结果。UI 仍可显示“工作中”，因为运行中的 assistant message 在 model_usage 完成前没有可计速行。
- Python 版 `running_models()` + `detail_data()` 已定义正确产品语义：运行中且有历史时继续显示历史均速；首次无历史才显示占位/0。
- native 已有 `RunningModels()`，但仅用于简略面板，overlay 绑定未复用。
- native UIA 首次空标题处理与 Python TTL 语义不一致：首次空读直接清空，而 Python 首次空读沿用最近标题。
- 当前仓库没有 native 数据链自检入口；用临时反射 harness 加内存 SQLite 验证，不能把 Python self-check 当作 native 端到端证明。

## 已落地决策

1. 不把指标改成实时流式速度，避免改变既定口径。
2. 新增按 session/model 判断未结束 assistant 请求的只读 helper，只保护已有历史速度。
3. 30 分钟僵尸窗口、finish+usage 结束条件和真正空闲归零保持不变。
4. 首次 UIA 空标题沿用健康标题，超过 TTL 仍清空，避免旧会话速度长期残留。
5. 临时 harness 四场景全部通过；真实桌面 overlay 可见性和日志回归完成。透明分层窗口的 PrintWindow 文字像素断言因系统限制保留未验证标记。
