using ExHyperV.Tools;

using System.Diagnostics;

namespace ExHyperV.Services
{
    public static class VmPowerService
    {
        private sealed record VmRuntimeState(ushort EnabledState, int ProcessId);

        private static readonly TimeSpan PowerRequestTimeout = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan PowerOffWaitTimeout = TimeSpan.FromSeconds(5);

        // RequestStateChange 状态码（来自 VMComputerSystemState 枚举，ILspy 反编译确认）
        // 2  = Running（启动）
        // 3  = PowerOff（强制关机）
        // 4  = Stopping（软关机，需要 Integration Services）
        // 6  = Saved（保存状态）
        // 9  = Paused（挂起）
        // 10 = Starting（从 Off/Saved 启动，对应 Reboot 场景）
        // 11 = Reset（硬重置）

        // 回传引擎真实成败：RequestStateChange 经 WmiApi.InvokeAsync 会等异步 Job 完成并带回其 ErrorDescription。
        // 返回 ApiResponse 而非 void：启动失败(配置错误/资源不足/GPU 分区不可用等)不再静默，调用方据此弹错。
        public static async Task<ApiResponse> ExecuteControlActionAsync(
            string vmName,
            string action,
            WmiContext? context = null,
            CancellationToken cancellationToken = default)
        {
            context ??= WmiContext.Local;
            VmLifecycleActionSupport support = VmLifecycleActionPolicy.Evaluate(action, isRemote: !context.IsLocal);
            if (support == VmLifecycleActionSupport.Unknown)
                return ApiResponse.Fail("不支持的虚拟机电源操作。" );
            if (support == VmLifecycleActionSupport.UnsupportedOnRemote)
                return ApiResponse.Fail("远程宿主仅支持启动、正常关机、强制关机和重启。" );

            string wql = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'";

            switch (action)
            {
                case "Start":
                    Task<ApiResponse> StartAsync() => WmiApi.InvokeAsync(
                        wql,
                        "RequestStateChange",
                        p => p["RequestedState"] = (ushort)2,
                        ctx: context,
                        cancellationToken: cancellationToken);
                    // AzureFeatureSet 是本机注册表状态；远程启动必须保留远程 WMI 上下文，不能修改客户端注册表。
                    return context.IsLocal
                        ? await HostAzureFeatureSetService.RunTemporarilyDisabledAsync(StartAsync)
                        : await StartAsync();

                case "TurnOff":
                    return await ForceTurnOffAsync(vmName, wql, context, cancellationToken);

                case "Stop":
                    // 先尝试软关机（4），失败再强制关机（3）
                    var stopResult = await WmiApi.InvokeAsync(wql, "RequestStateChange",
                        p => p["RequestedState"] = (ushort)4,
                        ctx: context,
                        cancellationToken: cancellationToken);
                    return stopResult.Success || !context.IsLocal
                        ? stopResult
                        : await ForceTurnOffAsync(vmName, wql, context, cancellationToken);

                case "Restart":
                    // 先尝试软重启（10），失败再硬重置（11）
                    var restartResult = await WmiApi.InvokeAsync(wql, "RequestStateChange",
                        p => p["RequestedState"] = (ushort)10,
                        ctx: context,
                        cancellationToken: cancellationToken);
                    return restartResult.Success ? restartResult
                        : await WmiApi.InvokeAsync(
                            wql,
                            "RequestStateChange",
                            p => p["RequestedState"] = (ushort)11,
                            ctx: context,
                            cancellationToken: cancellationToken);

                case "Save":
                    return await WmiApi.InvokeAsync(wql, "RequestStateChange",
                        p => p["RequestedState"] = (ushort)6,
                        ctx: context,
                        cancellationToken: cancellationToken);

                case "Suspend":
                    return await WmiApi.InvokeAsync(wql, "RequestStateChange",
                        p => p["RequestedState"] = (ushort)9,
                        ctx: context,
                        cancellationToken: cancellationToken);

                default:
                    return ApiResponse.Fail("不支持的虚拟机电源操作。" );
            }
        }

        private static async Task<ApiResponse> ForceTurnOffAsync(
            string vmName,
            string wql,
            WmiContext context,
            CancellationToken cancellationToken)
        {
            var requestTask = WmiApi.InvokeAsync(wql, "RequestStateChange",
                p => p["RequestedState"] = (ushort)3,
                ctx: context,
                cancellationToken: cancellationToken);

            ApiResponse? requestResult = null;
            if (await Task.WhenAny(requestTask, Task.Delay(PowerRequestTimeout, cancellationToken)) == requestTask)
                requestResult = await requestTask;
            else
                ObserveFault(requestTask);

            if (requestResult?.Success == true &&
                await WaitForPoweredOffAsync(vmName, PowerOffWaitTimeout, context, cancellationToken))
                return ApiResponse.Ok();

            var state = await GetRuntimeStateAsync(vmName, TimeSpan.FromSeconds(3), context, cancellationToken);
            if (state is null)
            {
                if (!context.IsLocal)
                {
                    return requestResult is { Success: false }
                        ? requestResult
                        : ApiResponse.Fail($"无法确认远程虚拟机 {vmName} 的关机状态。" );
                }
                return requestResult is { Success: false }
                    ? requestResult
                    : ApiResponse.Fail(Properties.Resources.Error_VmPower_WorkerNotFound);
            }

            if (state.EnabledState == 3)
                return ApiResponse.Ok();

            if (!context.IsLocal)
            {
                return requestResult is { Success: false }
                    ? requestResult
                    : ApiResponse.Fail($"远程虚拟机 {vmName} 未在超时前进入关机状态，未执行任何本机进程回退。" );
            }

            if (state.ProcessId <= 0)
            {
                return requestResult is { Success: false }
                    ? requestResult
                    : ApiResponse.Fail(Properties.Resources.Error_VmPower_WorkerNotFound);
            }

            try
            {
                using var process = Process.GetProcessById(state.ProcessId);
                if (!process.ProcessName.Equals("vmwp", StringComparison.OrdinalIgnoreCase))
                {
                    return ApiResponse.Fail(
                        string.Format(Properties.Resources.Error_VmPower_InvalidWorkerProcess, state.ProcessId));
                }

                process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException)
            {
                // The worker exited between the WMI query and Process.GetProcessById.
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail(
                    string.Format(Properties.Resources.Error_VmPower_TerminateWorkerFailed, vmName, ex.Message),
                    exception: ex);
            }

            return await WaitForPoweredOffAsync(vmName, PowerOffWaitTimeout, context, cancellationToken)
                ? ApiResponse.Ok()
                : ApiResponse.Fail(string.Format(Properties.Resources.Error_VmPower_StillNotOff, vmName));
        }

        private static async Task<bool> WaitForPoweredOffAsync(
            string vmName,
            TimeSpan timeout,
            WmiContext context,
            CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = await GetRuntimeStateAsync(
                    vmName,
                    TimeSpan.FromSeconds(2),
                    context,
                    cancellationToken);
                if (state?.EnabledState == 3)
                    return true;

                await Task.Delay(300, cancellationToken);
            }

            return false;
        }

        private static async Task<VmRuntimeState?> GetRuntimeStateAsync(
            string vmName,
            TimeSpan timeout,
            WmiContext context,
            CancellationToken cancellationToken)
        {
            string safeName = WmiApi.Escape(vmName);
            var queryTask = WmiApi.QueryFirstAsync(
                $"SELECT EnabledState, ProcessID FROM Msvm_ComputerSystem WHERE ElementName = '{safeName}'",
                obj => new VmRuntimeState(
                    Convert.ToUInt16(obj["EnabledState"] ?? (ushort)0),
                    Convert.ToInt32(obj["ProcessID"] ?? 0)),
                ctx: context);

            if (await Task.WhenAny(queryTask, Task.Delay(timeout, cancellationToken)) != queryTask)
            {
                ObserveFault(queryTask);
                return null;
            }

            var response = await queryTask;
            return response.HasData ? response.Data : null;
        }

        private static void ObserveFault(Task task)
        {
            _ = task.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }
}
