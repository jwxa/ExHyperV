# 多宿主会话管理进度

## 当前状态

- 设计状态：已确认。
- 跟踪规格：[GitHub Issue #14](https://github.com/jwxa/ExHyperV/issues/14)，标签 `ready-for-agent`。
- 实施 Issues：[#15](https://github.com/jwxa/ExHyperV/issues/15) 至 [#22](https://github.com/jwxa/ExHyperV/issues/22)，均为 OPEN 且带 `ready-for-agent`。
- 实施状态：进行中。
- 已完成：[#15 扩展宿主身份与多会话注册表](https://github.com/jwxa/ExHyperV/issues/15)、[#16 本机与首台远程并列管理](https://github.com/jwxa/ExHyperV/issues/16)、[#17 多台远程宿主与隔离操作](https://github.com/jwxa/ExHyperV/issues/17)、[#18 旧数据与每宿主独立自动重连](https://github.com/jwxa/ExHyperV/issues/18)。
- 下一任务：[#19 安全断开宿主及其控制台](https://github.com/jwxa/ExHyperV/issues/19)。
- 其他 frontier：[#20 当前宿主实时脱敏日志](https://github.com/jwxa/ExHyperV/issues/20)。
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
- 已建立 #16 的 Full Single 子任务；测试接缝沿用已确认的 `IHostOperationRouter`、宿主 VM 分组和诊断后注册表连接。
- #16 已完成：本机固定第一组，首台远程宿主在最新诊断后追加；VM 读取、写入和控制台按所属 `HostId` 路由，本机与远程复用同一视觉；200/200 测试、Release 构建、UTF-8 和差异检查通过。
- #16 受控宿主只读验收通过：`10.0.0.6` 的 WMI/DCOM、TCP 2179、1 台 VM 查询和控制台捕获成功；7 项通过、6 项安全跳过、0 失败，所有危险开关为 `false`。
- 已建立 #17 的 Full Single 子任务；测试边界覆盖三宿主稳定顺序、每宿主独立刷新、远程 A/B 操作隔离和单宿主选择域。
- #17 已完成：全部宿主按稳定顺序投影，每宿主并行刷新并持有独立监控与取消范围；A/B 写租约和代次相互隔离，跨宿主选择收敛为单一 `HostId`；204/204 测试、Release 构建、UTF-8、凭据模式和差异检查通过。
- 已建立 #18 的 Full Single 子任务；测试边界覆盖目标宿主旧数据隔离、A/B 并行重连、按 `HostId` 立即重试/停止和 VM 分组旧数据提示。
- #18 已完成：目标宿主旧数据、写入/控制台门禁和唯一重连任务与其他宿主隔离；立即重试、停止和恢复均按 `HostId` 路由；VM 分组通过现有警告色和 Tooltip 说明旧数据；209/209 测试、Release 构建、UTF-8、凭据模式和差异检查通过。

## 下一步

1. 开始 #19，实现安全主动断开及控制台确认事务。
2. #20 保持为并行可领取 frontier。
3. 每个任务完成后立即更新 `SUBTASKS.csv` 和本文件，不集中到最后补记。

## 注意事项

- 现有 `.codex-tasks/remote-host-management` 是第一阶段历史和实机证据，不迁移、不删除。
- 未经用户对相应危险动作的精确中文 `确认`，不得执行远程配置、回滚、VM 写操作或故障注入。
