# Issue #15-#22 完成度审计

审计日期：2026-08-16。审计基线为本地 `main` 的多宿主实现与主题修复提交链；GitHub `origin/main` 尚未包含这些本地提交。

## 公共验证证据

- `dotnet run --project tests/ExHyperV.Tests/ExHyperV.Tests.csproj -c Release`：226/226 通过。
- `dotnet build src/ExHyperV.csproj -c Release`：使用仓库已有生成式 COM interop 验证入口，0 错误、247 个既有警告。
- 32 个产品 XAML 文件可解析；相关变更文件均为 UTF-8 无 BOM；冲突标记与 `git diff --check` 通过。
- `10.0.0.6` 报告：`.codex-tasks/remote-host-management/raw/controlled-host-postmerge-readonly-20260816.json`。13 阶段中 7 通过、6 安全跳过、0 失败，四个危险开关均为 `false`。
- 视觉审计发现旧截图中的暗色日志正文为黑色、亮色禁用连接按钮对比度不足；代码已改用主题主文字色和禁用时 `Secondary` 外观，并新增发布收敛测试。2026-08-16 用户手工确认修复后的亮色/暗色、宽/窄四种状态均通过；未恢复已停止的桌面自动化。

## #15 多宿主身份与会话注册表

| 验收要求 | 直接证据 | 结论 |
| --- | --- | --- |
| 本机身份稳定，远程身份来自配置 ID | `HostIdentity_UsesProfileIdInsteadOfPresentation` | 已证明 |
| VM 身份包含宿主与 VM ID | `VmIdentity_ScopesVmIdToOwningHost` | 已证明 |
| 启动时固定包含不可断开的本机 | `HostRegistry_StartsWithFixedLocalSession`；`HostSessionRegistry` 拒绝本机断开 | 已证明 |
| 本机、远程 A、远程 B 同时存在于不可变快照 | `HostRegistry_ConnectsTwoRemoteHostsWithoutReplacingLocal` | 已证明 |
| 每宿主独立维护状态、通道、代次和能力 | `HostRegistry_ReconnectAdvancesOnlyTargetHostGeneration`；`Capabilities_MatricesCompareByValue` | 已证明 |
| 选择配置不改变后端目标 | `Sessions_SelectingProfileDoesNotActivateOrAdvanceGeneration`；连接页仅更新选中项 | 已证明 |
| 原单宿主流程继续构建和测试 | 226/226；Release/WPF 0 错误 | 已证明 |

## #16 本机与首台远程宿主并列管理

| 验收要求 | 直接证据 | 结论 |
| --- | --- | --- |
| 每次连接执行最新完整诊断 | `HostConnectionPage_RefreshesDiagnosticBeforeEveryConnect` | 已证明 |
| 错误密码与权限不足分别提示 | `Diagnostics_WindowsCredentialValidatorClassifiesBadPassword`；`Diagnostics_ValidatedCredentialTurnsWmiDenialIntoPermissionPrompt` | 已证明 |
| WMI 成功后追加远程组且本机保持第一 | `HostRegistry_ConnectsTwoRemoteHostsWithoutReplacingLocal`；`VmGroups_ProjectLocalThenRemoteAThenRemoteB` | 已证明 |
| 2179 失败只禁用控制台 | `Diagnostics_WmiSuccessAndTcpFailureIsPartiallyAvailable`；`Console_Unavailable2179RejectsCapture` | 已证明 |
| VM 读取和生命周期按所属宿主路由 | `HostRouter_ReadsUseExplicitLocalOrRemoteHostId`；`VmPage_RoutesReadsAndWritesByOwningHost` | 已证明 |
| 本机和远程复用布局、颜色和按钮样式 | 单一 `VirtualMachinesPage.xaml` 详情模板；`Release_RemoteSurfacesUseThemeResources` | 已证明 |
| 永久不支持项隐藏，暂时不可用项置灰并解释 | `Capabilities_PermanentRemoteVmActionsAreHidden`；`Capabilities_TemporaryUnavailableVmActionsExplainReason` | 已证明 |
| 危险确认展示宿主和 VM | `VirtualMachinesPageViewModel` 电源确认同时使用 `HostGroup.DisplayName` 与 `instance.Name` | 已证明 |

## #17 多台远程宿主与操作隔离

| 验收要求 | 直接证据 | 结论 |
| --- | --- | --- |
| 本机、A、B 同时显示 | `VmGroups_ProjectLocalThenRemoteAThenRemoteB` | 已证明 |
| 远程分组按连接顺序稳定排列 | 同上，明确断言 Order 0/1/2 | 已证明 |
| 每宿主独立刷新 VM | `VmPage_RemoteGroupsRefreshIndependently` | 已证明 |
| A 写租约不阻止 B | `HostRouter_TwoRemoteHostsKeepWritesAndGenerationsIndependent` | 已证明 |
| A 旧代次不能污染 A 新会话或 B | `HostRouter_WritesAndStaleResultsAreScopedToTargetHost` | 已证明 |
| 相同 VM 名称/ID 不串组 | `VmIdentity_ScopesVmIdToOwningHost`；`Console_SameVmIdIsScopedByHost` | 已证明 |
| 切换宿主选择会清除原宿主选择 | `VmSelection_SwitchingHostReplacesOriginalScope` | 已证明 |
| 批量命令只能取得单宿主目标 | `VmSelection_MixedHostsRejectedAndCaptureHasOneHostId` | 已证明 |

## #18 单宿主旧数据与独立自动重连

| 验收要求 | 直接证据 | 结论 |
| --- | --- | --- |
| 连接丢失只标记目标宿主旧数据/重连 | `HostRegistry_TargetLossKeepsStaleDataAndOtherHostUsable` | 已证明 |
| 旧数据保留，写入和控制台禁用 | 同上；`VmGroup_StaleSessionRetainsRowsAndExplainsStatus` | 已证明 |
| 其他宿主读写、控制台和选择不受影响 | `HostRegistry_TargetLossKeepsStaleDataAndOtherHostUsable` | 已证明 |
| 每宿主最多一个重连任务 | `Reconnect_OnlyOneAttemptRunsAtATime`；`HostRegistry_TwoHostsReconnectIndependently` | 已证明 |
| 可取消且有上限的退避 | `Reconnect_BackoffGrowsAndCaps`；`Reconnect_UserCanStopWithoutLocalFallback` | 已证明 |
| 用户可立即重试或停止目标重连 | `HostRegistry_ImmediateRetryTargetsOnlyRequestedHost`；`ReconnectUi_TargetsSelectedHostAndRefreshesRecoveredGroup` | 已证明 |
| 成功只推进目标代次并刷新能力/VM | `Reconnect_SuccessPublishesFreshGeneration`；注册表恢复测试 | 已证明 |
| 长期失败不切回本机或移除分组 | `Reconnect_UserCanStopWithoutLocalFallback`；旧数据分组保留测试 | 已证明 |

## #19 安全断开宿主及其控制台

| 验收要求 | 直接证据 | 结论 |
| --- | --- | --- |
| 主按钮显示连接、连接中或断开 | `HostConnectionPage_UsesSharedRegistryWithoutLocalSwitch` 的 XAML/ViewModel 合同 | 已证明 |
| 无“切回本机”，本机会话始终存在 | `Release_NoGlobalActiveHostCompatibilityPath`；`HostRegistry_StartsWithFixedLocalSession` | 已证明 |
| 写操作使目标断开置灰并解释原因 | `Disconnect_ActiveWriteBlocksOnlyOwningHost`；动态 `ConnectionActionToolTip` | 已证明 |
| 控制台按宿主/VM 登记并注销 | `ConsoleRegistry_TracksByHostAndVmAndActivatesExisting`；`ConsoleRegistry_UnregisterRequiresMatchingWindow` | 已证明 |
| 无控制台时直接断开 | `DisconnectWorkflow_NoConsoleDisconnectsWithoutConfirmation` | 已证明 |
| 有控制台时确认后只关闭目标窗口 | `DisconnectWorkflow_ConfirmationClosesOnlyTargetHost` | 已证明 |
| 取消确认零副作用 | `DisconnectWorkflow_CancelHasZeroSideEffects` | 已证明 |
| 主动断开停止目标任务、移除分组并保留配置 | `Disconnect_CommitRemovesOnlyTargetAndReleasesSession`；`VmPage_DisconnectRemovesOnlyTargetRemoteGroup`；实机主动断开阶段 | 已证明 |

## #20 当前宿主实时脱敏日志

| 验收要求 | 直接证据 | 结论 |
| --- | --- | --- |
| 磁盘和订阅者共享一次脱敏后的条目 | `Logging_AppLogSharesOneSanitizedEntryWithDiskAndFeed` | 已证明 |
| 条目包含宿主、时间、级别、来源和错误分类 | `Logging_StructuredEntryIsImmutableAndSanitized` | 已证明 |
| 诊断步骤实时逐条到达 | `Logging_DiagnosticStepsStreamWithHostAndErrorCategory` | 已证明 |
| 日志 Tab 只显示当前宿主 | `LoggingUi_SelectionReplacesHostScopeAndOldSubscription` | 已证明 |
| 每宿主默认最多 2,000 条 | `HostLogFeed` 默认 `maxEntriesPerHost: 2000`；`Logging_FeedKeepsBoundedHistoryPerHost` | 已证明 |
| 默认跟随，向上滚动后暂停 | `LoggingUi_PausePersistsUntilReturnToLatest`；`OnLiveLogScrollChanged` | 已证明 |
| “回到最新”图标恢复跟随 | `LoggingUi_XamlUsesVirtualizedListAndScrollPause` | 已证明 |
| 程序目录 logs，100 MiB x 2 | `Logging_WritesExpectedPathAndUtf8WithoutBom`；`Logging_RotatesAtLimitAndKeepsOnlyTwoFiles` | 已证明 |
| 密码、令牌和凭据不会进入磁盘/UI | `Logging_RedactsMessagesPropertiesAndCredentialObjects`；结构化条目测试 | 已证明 |

## #21 按诊断结果提供设置检查与修复

| 验收要求 | 直接证据 | 结论 |
| --- | --- | --- |
| 无可处理发现时隐藏入口 | `RepairEntry_HealthyDiagnosticHasNoActionOrGuidance`；页面无常驻预检按钮 | 已证明 |
| 入口绑定当前宿主和最新诊断 | `RepairEntry_ContextRejectsEditedHostAndNewerDiagnostic` | 已证明 |
| 打开入口先做只读预检 | `Preflight_ReadOnlyPipelineReturnsOrderedChineseEvidence`；`OpenRepairAsync` 调用预检 | 已证明 |
| 展示实时日志和完整修改清单 | `Preflight_LogsStreamBeforeReadOnlyReportCompletes`；预览 XAML/VM | 已证明 |
| Public 网络必须明确选择 | `PreflightViewModel_PublicNetworkChangeRequiresExplicitChoice` | 已证明 |
| 非精确“确认”执行零修改 | `Configuration_WrongConfirmationPerformsNoReadOrWrite` | 已证明 |
| 仅执行最小权限/防火墙修改并生成精确回滚 | `Configuration_CompilerKeepsLeastPrivilegeAndCidrScope`；回滚与漂移保护测试 | 已证明 |
| 执行后重新诊断两通道并刷新宿主 | `Configuration_ExactConfirmationAppliesVerifiesAndDiagnoses`；`Capabilities_PostConfigurationDiagnosticRefreshesActiveChannels` | 已证明 |
| 无法安全处理的问题只给中文引导 | `RepairEntry_InvalidCredentialProvidesGuidanceOnly`；`RepairEntry_MissingNamespaceProvidesGuidanceOnly` | 已证明 |

## #22 发布收敛

| 验收要求 | 直接证据 | 结论 |
| --- | --- | --- |
| 无全局活动宿主决定 VM/控制台目标 | `Release_NoGlobalActiveHostCompatibilityPath`；显式 HostId 路由测试 | 已证明 |
| 删除旧兼容接口、切回本机和远程专属模板 | 旧文件不存在；发布收敛测试；单一 VM 模板 | 已证明 |
| 启动只加载本机，远程配置手动连接 | 注册表启动测试；README/用户指南；实机配置保存后主动连接 | 已证明 |
| 确定性测试覆盖本机+A+B、隔离、重连、断开 | #17-#19 对应测试组，226/226 | 已证明 |
| 本机 + 10.0.0.6 真实诊断、VM、控制台、断开 | post-merge 只读报告 7 通过、0 失败 | 已证明 |
| 亮暗色、宽窄窗口无黑字、错位或重叠 | 代码与合同测试覆盖主题修复；2026-08-16 用户手工确认四种状态下文字可读且无错位、重叠或截断 | 已证明 |
| README、隐私说明、用户指南和任务记录一致 | `Release_UserDocsDescribeMultiHostBehavior`；人工复读三份文档 | 已证明 |
| Release、全部测试、XAML、UTF-8 和差异检查通过 | 公共验证证据 | 已证明 |
| 配置、回滚、VM 写和故障注入保持独立精确确认 | 配置/runner 精确确认测试；实机报告四个危险开关均为 false | 已证明 |

## 结论

#15-#22 共 66/66 条验收要求均已有当前代码、自动化、实机或用户人工复核证据。本地实施与验收完成；GitHub Issues 继续保持 OPEN，等待后续 push、远端评审与合并流程。
