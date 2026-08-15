# Issue #15 进度

## 当前状态

- 状态：DONE
- 当前切片：全部完成。
- TDD 阶段：RED→GREEN 完成。
- 最新验证：192/192；Release 构建 0 错误、248 个既有警告；UTF-8 无 BOM 与差异检查通过。

## 已确认边界

- 通过公开值对象和 `IHostSessionRegistry.Current` 快照验证行为。
- 不通过反射、私有字段或内部锁验证实现。
- 当前 Issue 保留 `IActiveHostSessionCoordinator` 兼容路径，最终由 #22 收缩。

## 已完成

- 切片 1：`HostId.Local`、基于 `HostProfile.Id` 的远程身份，以及宿主范围内的 `VmKey` 已完成 RED→GREEN。
- 切片 2：`IHostSessionRegistry.Current` 启动时发布唯一且排在首位的本机会话，快照复制并校验宿主集合不变量。
- 切片 3：注册表复用现有连接器和快照加载器，将两台远程宿主原子追加到固定本机之后；旧快照保持不变。
- 切片 4：按 `HostId` 路由操作 stamp 和连接丢失；A 重连只推进 A 的代次，B 的 stamp 保持有效。
- 完成门槛：确定性测试、产品 Release 构建、编码和差异检查全部通过。
- 默认 `dotnet` 不包含 SDK；后续验证固定使用 `.codex-tasks/tooling/dotnet-sdk-8/dotnet.exe`。

## 恢复点

1. #15 已完成，无待恢复步骤。
2. 父任务从 #16 继续，#20 同时处于无阻塞 frontier。
