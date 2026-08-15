# 多宿主会话管理进度

## 当前状态

- 设计状态：已确认。
- 跟踪规格：[GitHub Issue #14](https://github.com/jwxa/ExHyperV/issues/14)，标签 `ready-for-agent`。
- 实施状态：尚未开始。
- 当前任务：`MH-01`。
- 规格来源：`doc/multi-host-management-spec.md`。
- 设计来源：`doc/multi-host-management-design.md`。

## 已完成

- 完成现有单活动宿主架构、VM 列表、控制台、日志和配置预检路径分析。
- 确认本机固定、多远程并行、按宿主分组、每宿主重连、显式断开、实时日志和按需修复的产品决策。
- 生成多宿主实施波次、依赖关系、风险处理和验收门槛。

## 下一步

1. 建立当前确定性测试和 Release 构建基线。
2. 从 `MH-01` 开始，以测试先行方式定义 `HostId`、`VmKey` 和注册表快照。
3. 每个任务完成后立即更新 `SUBTASKS.csv` 和本文件，不集中到最后补记。

## 注意事项

- 现有 `.codex-tasks/remote-host-management` 是第一阶段历史和实机证据，不迁移、不删除。
- 未经用户对相应危险动作的精确中文 `确认`，不得执行远程配置、回滚、VM 写操作或故障注入。
