# 多宿主会话管理进度

## 当前状态

- 设计状态：已确认。
- 跟踪规格：[GitHub Issue #14](https://github.com/jwxa/ExHyperV/issues/14)，标签 `ready-for-agent`。
- 实施 Issues：[#15](https://github.com/jwxa/ExHyperV/issues/15) 至 [#22](https://github.com/jwxa/ExHyperV/issues/22)，均为 OPEN 且带 `ready-for-agent`。
- 实施状态：进行中。
- 已完成：[#15 扩展宿主身份与多会话注册表](https://github.com/jwxa/ExHyperV/issues/15)。
- 当前 frontier：[#16 本机与首台远程并列管理](https://github.com/jwxa/ExHyperV/issues/16) 与 [#20 当前宿主实时脱敏日志](https://github.com/jwxa/ExHyperV/issues/20)。
- 规格来源：`doc/multi-host-management-spec.md`。
- 设计来源：`doc/multi-host-management-design.md`。

## 已完成

- 完成现有单活动宿主架构、VM 列表、控制台、日志和配置预检路径分析。
- 确认本机固定、多远程并行、按宿主分组、每宿主重连、显式断开、实时日志和按需修复的产品决策。
- 生成多宿主实施波次、依赖关系、风险处理和验收门槛。
- 按纵向切片发布 GitHub Issues #15-#22，并回读确认父项、标签和阻塞引用。
- 在开始新实现前提交第一阶段代码与多宿主规格，基线提交为 `cfcd068` 和 `b286342`。
- 已建立 #15 的 Full Single 子任务，公开测试边界沿用已确认规格中的 `HostId`、`VmKey` 和 `IHostSessionRegistry` 快照。
- #15 已完成：建立稳定宿主/VM 身份、固定本机会话、两台远程并存、不可变快照和每宿主代次隔离；192/192 测试与 Release 构建通过。

## 下一步

1. 按 Issue 编号进入 #16，将本机与首台远程宿主并列接入 VM 管理路径。
2. #16 完成后推进 #17；#20 保持为并行可领取 frontier。
3. 每个任务完成后立即更新 `SUBTASKS.csv` 和本文件，不集中到最后补记。

## 注意事项

- 现有 `.codex-tasks/remote-host-management` 是第一阶段历史和实机证据，不迁移、不删除。
- 未经用户对相应危险动作的精确中文 `确认`，不得执行远程配置、回滚、VM 写操作或故障注入。
