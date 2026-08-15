using System.Text;
using System.Diagnostics;
using ExHyperV.Services.Remote.Configuration;
using ExHyperV.Services.Remote.Credentials;
using ExHyperV.Services.Remote.Diagnostics;
using ExHyperV.Services.Remote.Preflight;
using ExHyperV.Services.Remote.Profiles;
using ExHyperV.ViewModels;

internal static class ConfigurationTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("Configuration_ConfirmationIsExactOrdinalChinese", ConfirmationIsExactOrdinalChinese),
        ("Configuration_ConfirmationViewModelTracksExactInput", ConfirmationViewModelTracksExactInput),
        ("Configuration_CompilerKeepsLeastPrivilegeAndCidrScope", CompilerKeepsLeastPrivilegeAndCidrScope),
        ("Configuration_PlannerDoesNotProduceUncompilableWmiFirewallPlan", PlannerDoesNotProduceUncompilableWmiFirewallPlan),
        ("Configuration_PlannerRestoresExactSystemDefaultFirewallRules", PlannerRestoresExactSystemDefaultFirewallRules),
        ("Configuration_CompilerRestoresExactSystemDefaultFirewallRules", CompilerRestoresExactSystemDefaultFirewallRules),
        ("Configuration_FirewallRestoreCompensatesPartialFailure", FirewallRestoreCompensatesPartialFailure),
        ("Configuration_FirewallRestoreRollbackSkipsChangedState", FirewallRestoreRollbackSkipsChangedState),
        ("Configuration_GroupMembershipUsesLiteralAccountComparison", GroupMembershipUsesLiteralAccountComparison),
        ("Configuration_CompilerRejectsUnsafeCidrPlan", CompilerRejectsUnsafeCidrPlan),
        ("Configuration_CompiledPowerShellParses", CompiledPowerShellParses),
        ("Configuration_RemoteRunnerCompressesLongCommands", RemoteRunnerCompressesLongCommands),
        ("Configuration_RemoteRunnerRejectsOversizedBeforeSubmission", RemoteRunnerRejectsOversizedBeforeSubmission),
        ("Configuration_ConsoleFirewallScriptsApplyRestoreAndCompensate", ConsoleFirewallScriptsApplyRestoreAndCompensate),
        ("Configuration_BuiltInFirewallEnableCompensatesPartialFailure", BuiltInFirewallEnableCompensatesPartialFailure),
        ("Configuration_NewConsoleRuleRollbackSkipsReplacement", NewConsoleRuleRollbackSkipsReplacement),
        ("Configuration_BuiltInFirewallRollbackSkipsChangedOwnership", BuiltInFirewallRollbackSkipsChangedOwnership),
        ("Configuration_TokenPolicyRollbackSkipsChangedValues", TokenPolicyRollbackSkipsChangedValues),
        ("Configuration_NetworkRollbackSkipsDomainAuthenticated", NetworkRollbackSkipsDomainAuthenticated),
        ("Configuration_RollbackIsUtf8NoBomReverseAndConfirmed", RollbackIsUtf8NoBomReverseAndConfirmed),
        ("Configuration_ExactConfirmationAppliesVerifiesAndDiagnoses", ExactConfirmationAppliesVerifiesAndDiagnoses),
        ("Configuration_RestoredFirewallRulesVerifyBeforeEnable", RestoredFirewallRulesVerifyBeforeEnable),
        ("Configuration_WrongConfirmationPerformsNoReadOrWrite", WrongConfirmationPerformsNoReadOrWrite),
        ("Configuration_StalePreviewPerformsNoWrite", StalePreviewPerformsNoWrite),
        ("Configuration_PartialFailureStopsAndKeepsAppliedRollback", PartialFailureStopsAndKeepsAppliedRollback)
        ,("Configuration_CancellationAfterMutationKeepsRollback", CancellationAfterMutationKeepsRollback)
        ,("Configuration_UnknownRemoteStateIsIncludedInRollback", UnknownRemoteStateIsIncludedInRollback)
        ,("Configuration_RemoteFailureMessageIsRedacted", RemoteFailureMessageIsRedacted)
        ,("Configuration_MultipleNetworksVerifyOneStepAtATime", MultipleNetworksVerifyOneStepAtATime)
        ,("Configuration_RollbackStorageFailureBlocksRemoteMutation", RollbackStorageFailureBlocksRemoteMutation)
        ,("Configuration_RollbackPrewriteFailureBlocksRemoteMutation", RollbackPrewriteFailureBlocksRemoteMutation)
        ,("Configuration_RollbackIsPersistedBeforeEachRemoteSubmission", RollbackIsPersistedBeforeEachRemoteSubmission)
        ,("Configuration_RollbackRunsUseUniquePaths", RollbackRunsUseUniquePaths)
        ,("Configuration_RollbackDeleteIsRestrictedToLogDirectory", RollbackDeleteIsRestrictedToLogDirectory)
        ,("Configuration_ResultViewModelShowsStepsRollbackAndLogs", ResultViewModelShowsStepsRollbackAndLogs)
    ];

    private static void ConfirmationIsExactOrdinalChinese()
    {
        TestAssert.True(HostConfigurationConfirmation.IsExact("确认"), "Exact confirmation was rejected.");
        foreach (string? value in new[] { null, string.Empty, " 确认", "确认 ", "確認", "确 认", "确认\n" })
            TestAssert.False(HostConfigurationConfirmation.IsExact(value), $"Invalid confirmation was accepted: <{value}>.");
    }

    private static void ConfirmationViewModelTracksExactInput()
    {
        MutableHostState state = MutableHostState.Create();
        (_, HostPreflightPlan plan) = Approved(state);
        var viewModel = new HostConfigurationDialogViewModel(Profile(), plan);

        foreach (string value in new[] { "确认 ", "確認" })
        {
            viewModel.ConfirmationText = value;
            TestAssert.False(viewModel.IsConfirmationExact, $"Dialog accepted invalid confirmation: <{value}>.");
        }
        viewModel.ConfirmationText = "确认";
        TestAssert.True(viewModel.IsConfirmationExact, "Dialog did not enable exact Chinese confirmation.");
    }

    private static void CompilerKeepsLeastPrivilegeAndCidrScope()
    {
        MutableHostState state = MutableHostState.Create();
        (HostPreflightReport report, HostPreflightPlan plan) = Approved(state);

        IReadOnlyList<HostConfigurationCommand> commands = HostConfigurationCommandCompiler.Compile(report, plan);
        string apply = string.Join('\n', commands.Select(command => command.ApplyScript));

        TestAssert.False(apply.Contains("S-1-5-32-544", StringComparison.Ordinal), "Compiler granted the Administrators group.");
        TestAssert.False(apply.Contains("New-LocalUser", StringComparison.OrdinalIgnoreCase), "Compiler created a user.");
        TestAssert.False(apply.Contains("Set-LocalUser", StringComparison.OrdinalIgnoreCase), "Compiler changed a password.");
        TestAssert.False(apply.Contains("dynamicport", StringComparison.OrdinalIgnoreCase), "Compiler modified the dynamic RPC range.");
        TestAssert.Contains("S-1-5-32-580", apply);
        TestAssert.Contains("-LocalPort 2179", apply);
        TestAssert.Contains("10.0.0.0/255.255.255.0", apply);
        TestAssert.Contains("catch", apply);
        TestAssert.Contains("Disable-NetFirewallRule", apply);
        TestAssert.Contains("EXHYPERV_ROLLBACK_REQUIRED", apply);
        TestAssert.False(apply.Contains("-RemoteAddress 'Any'", StringComparison.OrdinalIgnoreCase), "Compiler opened TCP 2179 to Any.");
    }

    private static void PlannerDoesNotProduceUncompilableWmiFirewallPlan()
    {
        MutableHostState state = MutableHostState.Create();
        HostPreflightReport report = new HostPreflightPipeline(new CurrentIdentityResolver(), state)
            .RunAsync(Profile()).GetAwaiter().GetResult();
        report = report with
        {
            Facts = report.Facts with
            {
                Firewall = new HostFirewallSnapshot(
                    WmiBuiltInRulesEnabled: false,
                    HyperVBuiltInRulesEnabled: false,
                    ExHyperVConsole2179RuleEnabled: true,
                    ExHyperVConsole2179RemoteAddresses: ["10.0.0.0/24"],
                    DisabledWmiRuleNames: [],
                    DisabledHyperVRuleNames: [])
            }
        };
        HostPreflightPlanResult result = HostPreflightPlanner.Build(
            report,
            new HostPreflightSelection(
                HostPreflightAccountKind.Local,
                "Administrator",
                [12],
                [12],
                ["10.0.0.0/24"]));

        if (result.IsValid)
            _ = HostConfigurationCommandCompiler.Compile(report, result.Plan!);

        TestAssert.False(result.IsValid, "A missing WMI rule inventory must block preview instead of producing an uncompilable plan.");
        TestAssert.Contains("WMI", string.Join('\n', result.Errors));
        TestAssert.Contains("Hyper-V", string.Join('\n', result.Errors));
    }

    private static void PlannerRestoresExactSystemDefaultFirewallRules()
    {
        (_, HostPreflightPlan plan) = RestorableApproved();
        TestAssert.SequenceEqual(
            [
                HostPreflightChangeKind.AddRemoteManagementUsers,
                HostPreflightChangeKind.EnableLocalAccountTokenFilterPolicy,
                HostPreflightChangeKind.ChangeNetworkToPrivate,
                HostPreflightChangeKind.RestoreWmiFirewallRules,
                HostPreflightChangeKind.EnableWmiFirewallRules,
                HostPreflightChangeKind.RestoreHyperVFirewallRules,
                HostPreflightChangeKind.ConfigureConsole2179FirewallRule
            ],
            plan.Changes.Select(change => change.Kind));
        string details = string.Join('\n', plan.Changes.Select(change => change.Detail));
        TestAssert.Contains("WMI-RPCSS-In-TCP", details);
        TestAssert.Contains("WMI-WINMGMT-In-TCP", details);
        TestAssert.Contains("VIRT-VMMS-RPC-In-NoScope", details);
        TestAssert.Contains("SystemDefaults", details);
        TestAssert.Contains("PersistentStore", details);
    }

    private static void CompilerRestoresExactSystemDefaultFirewallRules()
    {
        (HostPreflightReport report, HostPreflightPlan plan) = RestorableApproved();

        HostConfigurationCommand command = HostConfigurationCommandCompiler.Compile(report, plan)
            .Single(item => item.Kind == HostPreflightChangeKind.RestoreWmiFirewallRules);
        string scripts = command.ApplyScript + "\n" + command.RollbackScript;

        TestAssert.Contains("WMI-RPCSS-In-TCP", scripts);
        TestAssert.Contains("WMI-WINMGMT-In-TCP", scripts);
        TestAssert.Contains("Get-NetFirewallRule", scripts);
        TestAssert.Contains("-Name", scripts);
        TestAssert.Contains("SystemDefaults", scripts);
        TestAssert.Contains("Copy-NetFirewallRule", command.ApplyScript);
        TestAssert.Contains("-NewPolicyStore PersistentStore", command.ApplyScript);
        TestAssert.Contains("Remove-NetFirewallRule", command.RollbackScript);
        TestAssert.False(scripts.Contains("-Group", StringComparison.OrdinalIgnoreCase),
            "Restore scripts selected a broad firewall group.");
        TestAssert.False(scripts.Contains("*", StringComparison.Ordinal),
            "Restore scripts selected firewall rules with a wildcard.");
        TestAssert.False(scripts.Contains("'Any'", StringComparison.OrdinalIgnoreCase),
            "Restore scripts used an unbounded Any selector.");
    }

    private static void FirewallRestoreCompensatesPartialFailure()
    {
        (HostPreflightReport report, HostPreflightPlan plan) = RestorableApproved();
        HostConfigurationCommand command = HostConfigurationCommandCompiler.Compile(report, plan)
            .Single(item => item.Kind == HostPreflightChangeKind.RestoreWmiFirewallRules);
        string apply = Convert.ToBase64String(Encoding.UTF8.GetBytes(command.ApplyScript));
        string script = FirewallRestoreHarness(apply, rollback: null, driftTarget: false, failCopyAt: 2);

        (int exitCode, string output, string error) = RunPowerShellHarness(script, "FirewallRestoreCompensation.ps1");

        TestAssert.True(exitCode == 0, $"Firewall restore compensation harness failed: {error}");
        TestAssert.Contains("ApplyError=simulated copy failure", output);
        TestAssert.Contains("Remaining=0", output);
        TestAssert.Contains("Removed=WMI-RPCSS-In-TCP", output);
    }

    private static void FirewallRestoreRollbackSkipsChangedState()
    {
        (HostPreflightReport report, HostPreflightPlan plan) = RestorableApproved();
        HostConfigurationCommand command = HostConfigurationCommandCompiler.Compile(report, plan)
            .Single(item => item.Kind == HostPreflightChangeKind.RestoreWmiFirewallRules);
        string apply = Convert.ToBase64String(Encoding.UTF8.GetBytes(command.ApplyScript));
        string rollback = Convert.ToBase64String(Encoding.UTF8.GetBytes(command.RollbackScript));
        string script = FirewallRestoreHarness(apply, rollback, driftTarget: true, failCopyAt: 0);

        (int exitCode, string output, string error) = RunPowerShellHarness(script, "FirewallRestoreDrift.ps1");

        TestAssert.True(exitCode == 0, $"Firewall restore drift harness failed: {error}");
        TestAssert.Contains("ApplyError=", output);
        TestAssert.Contains("ApplyError=\r\n", output);
        TestAssert.False(output.Contains("RollbackError=\r\n", StringComparison.Ordinal),
            "Rollback silently accepted a changed copied rule.");
        TestAssert.Contains("Remaining=2", output);
        TestAssert.Contains("Removed=", output);
        TestAssert.Contains("Removed=\r\n", output);
    }

    private static string FirewallRestoreHarness(
        string apply,
        string? rollback,
        bool driftTarget,
        int failCopyAt) => $$"""
        $ErrorActionPreference = 'Stop'
        $script:targets = @{}
        $script:removed = [Collections.Generic.List[string]]::new()
        $script:copyCount = 0
        function New-TestRule([string]$Name, [string]$Store) {
            [pscustomobject]@{
                Name = $Name
                Group = '@FirewallAPI.dll,-34251'
                Direction = 'Inbound'
                Action = 'Allow'
                Enabled = 'False'
                Profile = 'Private, Domain'
                EdgeTraversalPolicy = 'Block'
                LooseSourceMapping = $false
                LocalOnlyMapping = $false
                Owner = ''
                PolicyStoreSource = $Store
                PolicyStoreSourceType = 'Local'
                Protocol = 'TCP'
                LocalPort = @('RPC')
                RemotePort = @('Any')
                IcmpType = @('Any')
                DynamicTarget = 'Any'
                LocalAddress = @('Any')
                RemoteAddress = @('LocalSubnet')
                Program = 'System'
                Package = ''
                Service = 'winmgmt'
                InterfaceAlias = @('Any')
                InterfaceType = 'Any'
                Authentication = 'NotRequired'
                Encryption = 'NotRequired'
                OverrideBlockRules = $false
                LocalUser = 'Any'
                RemoteUser = 'Any'
                RemoteMachine = 'Any'
            }
        }
        $script:sources = @{
            'WMI-RPCSS-In-TCP' = New-TestRule 'WMI-RPCSS-In-TCP' 'SystemDefaults'
            'WMI-WINMGMT-In-TCP' = New-TestRule 'WMI-WINMGMT-In-TCP' 'SystemDefaults'
        }
        function Get-NetFirewallRule {
            [CmdletBinding()]
            param([string]$PolicyStore, [string]$Name)
            if ($PolicyStore -ceq 'SystemDefaults') { return $script:sources[$Name] }
            if ($script:targets.ContainsKey($Name)) { return $script:targets[$Name] }
        }
        function Copy-NetFirewallRule {
            [CmdletBinding()]
            param(
                [Parameter(ValueFromPipeline = $true)] [object]$InputObject,
                [string]$NewPolicyStore)
            process {
                $script:copyCount++
                if ($script:copyCount -eq {{failCopyAt}}) { throw 'simulated copy failure' }
                $script:targets[$InputObject.Name] = New-TestRule $InputObject.Name 'PersistentStore'
            }
        }
        function Remove-NetFirewallRule {
            [CmdletBinding()]
            param([Parameter(ValueFromPipeline = $true)] [object]$InputObject)
            process {
                $script:removed.Add($InputObject.Name)
                $script:targets.Remove($InputObject.Name)
            }
        }
        function Get-NetFirewallAddressFilter {
            [CmdletBinding()]
            param([Parameter(ValueFromPipeline = $true)] [object]$InputObject)
            process { [pscustomobject]@{ LocalAddress = $InputObject.LocalAddress; RemoteAddress = $InputObject.RemoteAddress } }
        }
        function Get-NetFirewallPortFilter {
            [CmdletBinding()]
            param([Parameter(ValueFromPipeline = $true)] [object]$InputObject)
            process { [pscustomobject]@{ Protocol = $InputObject.Protocol; LocalPort = $InputObject.LocalPort; RemotePort = $InputObject.RemotePort; IcmpType = $InputObject.IcmpType; DynamicTarget = $InputObject.DynamicTarget } }
        }
        function Get-NetFirewallApplicationFilter {
            [CmdletBinding()]
            param([Parameter(ValueFromPipeline = $true)] [object]$InputObject)
            process { [pscustomobject]@{ Program = $InputObject.Program; Package = $InputObject.Package } }
        }
        function Get-NetFirewallServiceFilter {
            [CmdletBinding()]
            param([Parameter(ValueFromPipeline = $true)] [object]$InputObject)
            process { [pscustomobject]@{ Service = $InputObject.Service } }
        }
        function Get-NetFirewallInterfaceFilter {
            [CmdletBinding()]
            param([Parameter(ValueFromPipeline = $true)] [object]$InputObject)
            process { [pscustomobject]@{ InterfaceAlias = $InputObject.InterfaceAlias } }
        }
        function Get-NetFirewallInterfaceTypeFilter {
            [CmdletBinding()]
            param([Parameter(ValueFromPipeline = $true)] [object]$InputObject)
            process { [pscustomobject]@{ InterfaceType = $InputObject.InterfaceType } }
        }
        function Get-NetFirewallSecurityFilter {
            [CmdletBinding()]
            param([Parameter(ValueFromPipeline = $true)] [object]$InputObject)
            process {
                [pscustomobject]@{
                    Authentication = $InputObject.Authentication
                    Encryption = $InputObject.Encryption
                    OverrideBlockRules = $InputObject.OverrideBlockRules
                    LocalUser = $InputObject.LocalUser
                    RemoteUser = $InputObject.RemoteUser
                    RemoteMachine = $InputObject.RemoteMachine
                }
            }
        }
        function Invoke-Encoded([string]$Value) {
            $body = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
            & ([ScriptBlock]::Create($body))
        }
        $applyError = ''
        try { Invoke-Encoded '{{apply}}' } catch { $applyError = $_.Exception.Message }
        if ({{(driftTarget ? "$true" : "$false")}} -and $script:targets.ContainsKey('WMI-RPCSS-In-TCP')) {
            $script:targets['WMI-RPCSS-In-TCP'].RemoteAddress = @('10.10.10.0/24')
        }
        $rollbackError = ''
        if ('{{rollback}}') {
            try { Invoke-Encoded '{{rollback}}' } catch { $rollbackError = $_.Exception.Message }
        }
        Write-Output ('ApplyError=' + $applyError)
        Write-Output ('RollbackError=' + $rollbackError)
        Write-Output ('Remaining=' + $script:targets.Count)
        Write-Output ('Removed=' + ($script:removed -join ','))
        """;

    private static void GroupMembershipUsesLiteralAccountComparison()
    {
        MutableHostState state = MutableHostState.Create();
        (HostPreflightReport report, HostPreflightPlan approved) = Approved(state);
        HostPreflightPlan plan = approved with { AccountName = "Admin[*]'User" };

        string scripts = string.Join('\n', HostConfigurationCommandCompiler.Compile(report, plan)
            .Where(command => command.Kind is HostPreflightChangeKind.AddHyperVAdministrators
                or HostPreflightChangeKind.AddRemoteManagementUsers)
            .SelectMany(command => new[] { command.ApplyScript, command.RollbackScript }));

        TestAssert.Contains("Admin[*]''User", scripts);
        TestAssert.Contains(".EndsWith", scripts);
        TestAssert.Contains("OrdinalIgnoreCase", scripts);
        TestAssert.False(scripts.Contains("-ilike", StringComparison.OrdinalIgnoreCase),
            "Group membership still interprets account names as wildcard patterns.");
    }

    private static void CompiledPowerShellParses()
    {
        MutableHostState newRuleState = MutableHostState.Create();
        (HostPreflightReport newRuleReport, HostPreflightPlan newRulePlan) = Approved(newRuleState);
        MutableHostState existingRuleState = MutableHostState.Create();
        existingRuleState.ConsoleRuleEnabled = true;
        existingRuleState.ConsoleRemoteAddresses = ["192.168.50.0/24"];
        (HostPreflightReport existingRuleReport, HostPreflightPlan existingRulePlan) = Approved(existingRuleState);
        (HostPreflightReport restoreReport, HostPreflightPlan restorePlan) = RestorableApproved();
        HostConfigurationCommand[] commands = HostConfigurationCommandCompiler.Compile(newRuleReport, newRulePlan)
            .Concat(HostConfigurationCommandCompiler.Compile(existingRuleReport, existingRulePlan))
            .Concat(HostConfigurationCommandCompiler.Compile(restoreReport, restorePlan))
            .ToArray();

        using var temp = new ConfigurationTempDirectory();
        string parserPath = Path.Combine(temp.Path, "Parse.ps1");
        File.WriteAllText(
            parserPath,
            "param([string]$Path)\n$text=[Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($Path)); $tokens=$null; $errors=$null; [System.Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors) | Out-Null; if ($errors.Count) { $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }; exit 1 }",
            new UTF8Encoding(false));
        for (int index = 0; index < commands.Length; index++)
        {
            Parse(commands[index].ApplyScript, $"Apply-{index:D2}");
            Parse(commands[index].RollbackScript, $"Rollback-{index:D2}");
        }
        var buildWrapper = typeof(WindowsHostConfigurationCommandRunner).GetMethod(
            "BuildWrapper",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("Configuration wrapper compiler was not found.");
        for (int index = 0; index < commands.Length; index++)
        {
            string wrapper = (string)(buildWrapper.Invoke(
                null,
                [commands[index].ApplyScript, commands[index].RollbackScript, "Run_0123456789abcdef"])
                ?? throw new InvalidOperationException("Configuration wrapper compiler returned null."));
            Parse(wrapper, $"RemoteWrapper-{index:D2}");
        }

        void Parse(string script, string name)
        {
            string scriptPath = Path.Combine(temp.Path, name + ".ps1");
            File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", parserPath, scriptPath })
                startInfo.ArgumentList.Add(argument);
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start Windows PowerShell parser.");
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            TestAssert.True(process.ExitCode == 0, $"{name} PowerShell parse failed: {error}");
        }
    }

    private static void RemoteRunnerCompressesLongCommands()
    {
        (HostPreflightReport report, HostPreflightPlan plan) = RestorableApproved();
        HostConfigurationCommand[] commands = HostConfigurationCommandCompiler.Compile(report, plan)
            .Where(command => command.Kind is HostPreflightChangeKind.RestoreWmiFirewallRules
                or HostPreflightChangeKind.RestoreHyperVFirewallRules)
            .ToArray();
        var runnerType = typeof(WindowsHostConfigurationCommandRunner);
        var buildWrapper = runnerType.GetMethod(
            "BuildWrapper",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("Configuration wrapper compiler was not found.");
        var buildCommandLine = runnerType.GetMethod(
            "BuildCommandLine",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("Compressed configuration command-line compiler was not found.");

        foreach (HostConfigurationCommand command in commands)
        {
            string wrapper = (string)(buildWrapper.Invoke(
                null,
                [command.ApplyScript, command.RollbackScript, "Run_0123456789abcdef0123456789abcdef"])
                ?? throw new InvalidOperationException("Configuration wrapper compiler returned null."));
            string commandLine = (string)(buildCommandLine.Invoke(null, [wrapper])
                ?? throw new InvalidOperationException("Configuration command-line compiler returned null."));
            TestAssert.True(commandLine.Length < 32767,
                $"{command.Kind} produced a {commandLine.Length}-character command line.");
        }

        string probeScript =
            "Write-Output ([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('压缩脚本通过')))";
        string probeLine = (string)(buildCommandLine.Invoke(null, [probeScript])
            ?? throw new InvalidOperationException("Configuration command-line compiler returned null."));
        const string prefix = "powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ";
        TestAssert.True(probeLine.StartsWith(prefix, StringComparison.Ordinal), "Unexpected remote PowerShell command prefix.");
        string encoded = probeLine[prefix.Length..];
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded
        })
            startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start compressed PowerShell probe.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        TestAssert.True(process.ExitCode == 0, $"Compressed PowerShell probe failed: {error}");
        TestAssert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("压缩脚本通过")), output);
    }

    private static void RemoteRunnerRejectsOversizedBeforeSubmission()
    {
        byte[] randomBytes = new byte[30000];
        new Random(42).NextBytes(randomBytes);
        string incompressible = Convert.ToBase64String(randomBytes);
        var command = new HostConfigurationCommand(
            HostPreflightChangeKind.RestoreWmiFirewallRules,
            "oversized-command",
            "$value='" + incompressible + "'",
            "$value='" + incompressible + "'");
        var runner = new WindowsHostConfigurationCommandRunner(TimeSpan.FromMilliseconds(1));

        HostConfigurationCommandResult result = runner.RunAsync(
            "invalid.invalid",
            ResolvedHostIdentity.CurrentWindowsIdentity,
            command,
            CancellationToken.None).GetAwaiter().GetResult();

        TestAssert.False(result.Succeeded, "An oversized command was accepted.");
        TestAssert.False(result.MayHaveApplied, "A locally rejected command was classified as remotely submitted.");
        TestAssert.Contains("超过 Windows 进程命令行上限", result.Message);
    }

    private static void CompilerRejectsUnsafeCidrPlan()
    {
        MutableHostState state = MutableHostState.Create();
        (HostPreflightReport report, HostPreflightPlan approved) = Approved(state);
        HostPreflightPlan unsafePlan = approved with { AllowedIpv4Cidrs = ["8.8.8.0/24"] };

        InvalidOperationException? error = null;
        try
        {
            HostConfigurationCommandCompiler.Compile(report, unsafePlan);
        }
        catch (InvalidOperationException ex)
        {
            error = ex;
        }

        TestAssert.NotNull(error, "The command compiler accepted a public TCP 2179 CIDR.");
        TestAssert.Contains("RFC1918", error!.Message);
    }

    private static void ConsoleFirewallScriptsApplyRestoreAndCompensate()
    {
        HostProfile profile = Profile();
        var firewall = new HostFirewallSnapshot(
            true,
            true,
            false,
            ["192.168.50.0/24", "LocalSubnet"],
            [],
            [],
            true,
            "UDP",
            ["3000", "2179"],
            "Block",
            ["Public", "Private"]);
        var report = new HostPreflightReport(
            profile.Id,
            profile.Address,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            new HostPreflightFacts(
                new HostJoinSnapshot("LAB-HV-06", HostJoinKind.Workgroup, "WORKGROUP"),
                [],
                new Dictionary<HostLocalGroupKind, HostLocalGroupSnapshot>(),
                HostTokenFilterPolicyState.Missing,
                [],
                firewall),
            [],
            []);
        var plan = new HostPreflightPlan(
            HostPreflightAccountKind.Local,
            "Administrator",
            [],
            [],
            ["10.0.0.0/24"],
            [new HostPreflightPlannedChange(
                HostPreflightChangeKind.ConfigureConsole2179FirewallRule,
                "更新 ExHyperV TCP 2179 入站规则范围",
                "测试")]);
        HostConfigurationCommand command = HostConfigurationCommandCompiler.Compile(report, plan).Single();

        (int successCode, string successOutput, string successError) = RunFirewallScriptHarness(command, failAddressOnce: false);
        TestAssert.True(successCode == 0, $"Firewall apply/rollback harness failed: {successError}");
        TestAssert.Contains("ApplyError=", successOutput);
        TestAssert.Contains("AfterApply=True|Allow|Private, Domain|TCP|2179|10.0.0.0/255.255.255.0", successOutput);
        TestAssert.Contains("AfterRollback=False|Block|Public, Private|UDP|3000,2179|192.168.50.0/24,LocalSubnet", successOutput);

        (int failureCode, string failureOutput, string failureError) = RunFirewallScriptHarness(command, failAddressOnce: true);
        TestAssert.True(failureCode == 0, $"Firewall compensation harness failed: {failureError}");
        TestAssert.Contains("ApplyError=simulated address failure", failureOutput);
        TestAssert.Contains("AfterApply=False|Block|Public, Private|UDP|3000,2179|192.168.50.0/24,LocalSubnet", failureOutput);

        (int driftCode, string driftOutput, string driftError) = RunFirewallScriptHarness(
            command,
            failAddressOnce: false,
            mutateAfterApply: true);
        TestAssert.True(driftCode == 0, $"Firewall drift harness failed: {driftError}");
        TestAssert.Contains("RollbackError=TCP 2179 规则已被其他操作修改，未覆盖当前状态。", driftOutput);
        TestAssert.Contains("AfterRollback=True|Allow|Private, Domain|TCP|2179|172.16.0.0/24", driftOutput);
    }

    private static (int ExitCode, string Output, string Error) RunFirewallScriptHarness(
        HostConfigurationCommand command,
        bool failAddressOnce,
        bool mutateAfterApply = false)
    {
        using var temp = new ConfigurationTempDirectory();
        string apply = Convert.ToBase64String(Encoding.UTF8.GetBytes(command.ApplyScript));
        string rollback = Convert.ToBase64String(Encoding.UTF8.GetBytes(command.RollbackScript));
        string fail = failAddressOnce ? "$true" : "$false";
        string mutate = mutateAfterApply ? "$true" : "$false";
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            $script:state = [ordered]@{
                Enabled = 'False'
                Action = 'Block'
                Profile = 'Private, Public'
                Protocol = 'UDP'
                LocalPort = @('2179', '3000')
                RemoteAddress = @('LocalSubnet', '192.168.50.0/24')
            }
            $script:failAddressOnce = {{fail}}

            function Get-NetFirewallRule {
                [CmdletBinding()]
                param([string]$Name)
                [pscustomobject]@{
                    Enabled = $script:state.Enabled
                    Action = $script:state.Action
                    Profile = $script:state.Profile
                    Direction = 'Inbound'
                    PolicyStoreSource = 'PersistentStore'
                    PolicyStoreSourceType = 'Local'
                }
            }

            function Set-NetFirewallRule {
                [CmdletBinding()]
                param(
                    [Parameter(ValueFromPipeline = $true)] [object]$InputObject,
                    [object]$Enabled,
                    [object]$Action,
                    [object[]]$Profile)
                process {
                    $script:state.Enabled = $Enabled.ToString()
                    $script:state.Action = $Action.ToString()
                    $script:state.Profile = @($Profile) -join ', '
                }
            }

            function Get-NetFirewallPortFilter {
                [CmdletBinding()]
                param([Parameter(ValueFromPipeline = $true)] [object]$InputObject)
                process {
                    [pscustomobject]@{
                        Protocol = $script:state.Protocol
                        LocalPort = @($script:state.LocalPort)
                    }
                }
            }

            function Set-NetFirewallPortFilter {
                [CmdletBinding()]
                param(
                    [Parameter(ValueFromPipeline = $true)] [object]$InputObject,
                    [object]$Protocol,
                    [object[]]$LocalPort)
                process {
                    $script:state.Protocol = $Protocol.ToString()
                    $script:state.LocalPort = @($LocalPort)
                }
            }

            function Get-NetFirewallAddressFilter {
                [CmdletBinding()]
                param([Parameter(ValueFromPipeline = $true)] [object]$InputObject)
                process {
                    [pscustomobject]@{ RemoteAddress = @($script:state.RemoteAddress) }
                }
            }

            function Set-NetFirewallAddressFilter {
                [CmdletBinding()]
                param(
                    [Parameter(ValueFromPipeline = $true)] [object]$InputObject,
                    [object[]]$RemoteAddress)
                process {
                    if ($script:failAddressOnce) {
                        $script:failAddressOnce = $false
                        throw 'simulated address failure'
                    }
                    $script:state.RemoteAddress = @($RemoteAddress)
                }
            }

            function Invoke-Encoded([string]$Value) {
                $body = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
                & ([ScriptBlock]::Create($body))
            }

            function Format-State {
                @(
                    $script:state.Enabled,
                    $script:state.Action,
                    $script:state.Profile,
                    $script:state.Protocol,
                    (@($script:state.LocalPort) -join ','),
                    (@($script:state.RemoteAddress) -join ',')
                ) -join '|'
            }

            $applyError = ''
            try { Invoke-Encoded '{{apply}}' } catch { $applyError = $_.Exception.Message }
            Write-Output ('ApplyError=' + $applyError)
            Write-Output ('AfterApply=' + (Format-State))
            if (-not {{fail}} -and $applyError.Length -eq 0) {
                if ({{mutate}}) { $script:state.RemoteAddress = @('172.16.0.0/24') }
                $rollbackError = ''
                try { Invoke-Encoded '{{rollback}}' } catch { $rollbackError = $_.Exception.Message }
                Write-Output ('RollbackError=' + $rollbackError)
                Write-Output ('AfterRollback=' + (Format-State))
            }
            """;
        string path = Path.Combine(temp.Path, "FirewallHarness.ps1");
        File.WriteAllText(path, script, new UTF8Encoding(false));
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path })
            startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the firewall command harness.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }

    private static void BuiltInFirewallEnableCompensatesPartialFailure()
    {
        HostProfile profile = Profile();
        var firewall = new HostFirewallSnapshot(
            false,
            true,
            true,
            ["10.0.0.0/24"],
            ["WMI-First", "WMI-Second"],
            [],
            true);
        var report = new HostPreflightReport(
            profile.Id,
            profile.Address,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            new HostPreflightFacts(null, [], new Dictionary<HostLocalGroupKind, HostLocalGroupSnapshot>(),
                HostTokenFilterPolicyState.Missing, [], firewall),
            [],
            []);
        var plan = new HostPreflightPlan(
            HostPreflightAccountKind.Local,
            "Administrator",
            [],
            [],
            ["10.0.0.0/24"],
            [new HostPreflightPlannedChange(
                HostPreflightChangeKind.EnableWmiFirewallRules,
                "启用 Windows 内置 WMI 防火墙规则",
                "测试")]);
        HostConfigurationCommand command = HostConfigurationCommandCompiler.Compile(report, plan).Single();

        using var temp = new ConfigurationTempDirectory();
        string apply = Convert.ToBase64String(Encoding.UTF8.GetBytes(command.ApplyScript));
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $script:states = @{ 'WMI-First' = 'False'; 'WMI-Second' = 'False' }
            $script:groups = @{ 'WMI-First' = '@FirewallAPI.dll,-34251'; 'WMI-Second' = '@FirewallAPI.dll,-34251' }
            $script:driftOnFailure = $false
            function Get-NetFirewallRule {
                [CmdletBinding()]
                param([string]$Name)
                [pscustomobject]@{
                    Name = $Name
                    Enabled = $script:states[$Name]
                    Group = $script:groups[$Name]
                    Direction = 'Inbound'
                    PolicyStoreSource = 'PersistentStore'
                    PolicyStoreSourceType = 'Local'
                }
            }
            function Enable-NetFirewallRule {
                [CmdletBinding()]
                param([Parameter(ValueFromPipeline = $true)] [object]$InputObject)
                process {
                    if ($InputObject.Name -eq 'WMI-Second') {
                        if ($script:driftOnFailure) { $script:groups['WMI-First'] = '@FirewallAPI.dll,-999' }
                        throw 'simulated enable failure'
                    }
                    $script:states[$InputObject.Name] = 'True'
                }
            }
            function Disable-NetFirewallRule {
                [CmdletBinding()]
                param([Parameter(ValueFromPipeline = $true)] [object]$InputObject)
                process { $script:states[$InputObject.Name] = 'False' }
            }
            $body = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{apply}}'))
            foreach ($drift in @($false, $true)) {
                $script:states['WMI-First'] = 'False'
                $script:states['WMI-Second'] = 'False'
                $script:groups['WMI-First'] = '@FirewallAPI.dll,-34251'
                $script:groups['WMI-Second'] = '@FirewallAPI.dll,-34251'
                $script:driftOnFailure = $drift
                $errorText = ''
                try { & ([ScriptBlock]::Create($body)) } catch { $errorText = $_.Exception.Message }
                Write-Output ('Drift=' + $drift + '|ApplyError=' + $errorText)
                Write-Output ('Drift=' + $drift + '|States=' + $script:states['WMI-First'] + '|' + $script:states['WMI-Second'])
            }
            """;
        string path = Path.Combine(temp.Path, "BuiltInFirewallHarness.ps1");
        File.WriteAllText(path, script, new UTF8Encoding(false));
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path })
            startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the built-in firewall command harness.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        TestAssert.True(process.ExitCode == 0, $"Built-in firewall compensation harness failed: {error}");
        TestAssert.Contains("Drift=False|ApplyError=simulated enable failure", output);
        TestAssert.Contains("Drift=False|States=False|False", output);
        TestAssert.Contains("Drift=True|ApplyError=EXHYPERV_ROLLBACK_REQUIRED", output);
        TestAssert.Contains("Drift=True|States=True|False", output);
    }

    private static void NewConsoleRuleRollbackSkipsReplacement()
    {
        MutableHostState state = MutableHostState.Create();
        (HostPreflightReport report, HostPreflightPlan plan) = Approved(state);
        HostConfigurationCommand command = HostConfigurationCommandCompiler.Compile(report, plan)
            .Single(item => item.Kind == HostPreflightChangeKind.ConfigureConsole2179FirewallRule);
        string rollback = Convert.ToBase64String(Encoding.UTF8.GetBytes(command.RollbackScript));
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $script:removed = $false
            function Get-NetFirewallRule {
                [CmdletBinding()]
                param([string]$Name)
                [pscustomobject]@{
                    Enabled = 'True'
                    Action = 'Allow'
                    Profile = 'Private, Domain'
                    Direction = 'Inbound'
                    PolicyStoreSource = 'Domain GPO'
                    PolicyStoreSourceType = 'GroupPolicy'
                }
            }
            function Get-NetFirewallPortFilter {
                [CmdletBinding()]
                param([Parameter(ValueFromPipeline = $true)] [object]$InputObject)
                process { [pscustomobject]@{ Protocol = 'TCP'; LocalPort = @('2179') } }
            }
            function Get-NetFirewallAddressFilter {
                [CmdletBinding()]
                param([Parameter(ValueFromPipeline = $true)] [object]$InputObject)
                process { [pscustomobject]@{ RemoteAddress = @('10.0.0.0/24') } }
            }
            function Remove-NetFirewallRule {
                [CmdletBinding()]
                param([Parameter(ValueFromPipeline = $true)] [object]$InputObject)
                process { $script:removed = $true }
            }
            $body = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{rollback}}'))
            $errorText = ''
            try { & ([ScriptBlock]::Create($body)) } catch { $errorText = $_.Exception.Message }
            Write-Output ('Removed=' + $script:removed)
            Write-Output ('RollbackError=' + $errorText)
            """;

        (int exitCode, string output, string error) = RunPowerShellHarness(script, "NewRuleReplacement.ps1");

        TestAssert.True(exitCode == 0, $"New-rule replacement harness failed: {error}");
        TestAssert.Contains("Removed=False", output);
        TestAssert.False(output.Contains("RollbackError=\r\n", StringComparison.Ordinal),
            "Rollback silently accepted a replacement rule.");
    }

    private static void BuiltInFirewallRollbackSkipsChangedOwnership()
    {
        MutableHostState state = MutableHostState.Create();
        (HostPreflightReport report, HostPreflightPlan plan) = Approved(state);
        HostConfigurationCommand command = HostConfigurationCommandCompiler.Compile(report, plan)
            .Single(item => item.Kind == HostPreflightChangeKind.EnableHyperVFirewallRules);
        string apply = Convert.ToBase64String(Encoding.UTF8.GetBytes(command.ApplyScript));
        string rollback = Convert.ToBase64String(Encoding.UTF8.GetBytes(command.RollbackScript));
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $script:enabled = 'False'
            $script:group = '@%systemroot%\system32\vmms.exe,-210'
            function Get-NetFirewallRule {
                [CmdletBinding()]
                param([string]$Name)
                [pscustomobject]@{
                    Name = $Name
                    Enabled = $script:enabled
                    Group = $script:group
                    Direction = 'Inbound'
                    PolicyStoreSource = 'PersistentStore'
                    PolicyStoreSourceType = 'Local'
                }
            }
            function Enable-NetFirewallRule {
                [CmdletBinding()]
                param([Parameter(ValueFromPipeline = $true)] [object]$InputObject)
                process { $script:enabled = 'True' }
            }
            function Disable-NetFirewallRule {
                [CmdletBinding()]
                param([Parameter(ValueFromPipeline = $true)] [object]$InputObject)
                process { $script:enabled = 'False' }
            }
            function Invoke-Encoded([string]$Value) {
                $body = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
                & ([ScriptBlock]::Create($body))
            }
            $applyError = ''
            try { Invoke-Encoded '{{apply}}' } catch { $applyError = $_.Exception.Message }
            $script:group = '@FirewallAPI.dll,-34251'
            $rollbackError = ''
            try { Invoke-Encoded '{{rollback}}' } catch { $rollbackError = $_.Exception.Message }
            Write-Output ('ApplyError=' + $applyError)
            Write-Output ('RollbackError=' + $rollbackError)
            Write-Output ('Enabled=' + $script:enabled)
            """;

        (int exitCode, string output, string error) = RunPowerShellHarness(script, "BuiltInOwnershipDrift.ps1");

        TestAssert.True(exitCode == 0, $"Built-in ownership harness failed: {error}");
        TestAssert.Contains("ApplyError=", output);
        TestAssert.Contains("ApplyError=\r\n", output);
        TestAssert.False(output.Contains("RollbackError=\r\n", StringComparison.Ordinal),
            "Rollback silently accepted a changed resource group.");
        TestAssert.Contains("Enabled=True", output);
    }

    private static void TokenPolicyRollbackSkipsChangedValues()
    {
        MutableHostState state = MutableHostState.Create();
        (HostPreflightReport report, HostPreflightPlan plan) = Approved(state);
        HostConfigurationCommand command = HostConfigurationCommandCompiler.Compile(report, plan)
            .Single(item => item.Kind == HostPreflightChangeKind.EnableLocalAccountTokenFilterPolicy);
        string rollback = Convert.ToBase64String(Encoding.UTF8.GetBytes(command.RollbackScript));
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $script:value = 0
            $script:removed = $false
            function Get-ItemProperty {
                [CmdletBinding()]
                param([string]$LiteralPath, [string]$Name)
                [pscustomobject]@{ LocalAccountTokenFilterPolicy = $script:value }
            }
            function Remove-ItemProperty {
                [CmdletBinding()]
                param([string]$LiteralPath, [string]$Name, [switch]$Force)
                $script:removed = $true
            }
            $body = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{rollback}}'))
            foreach ($candidate in @(0, 2)) {
                $script:value = $candidate
                $script:removed = $false
                $errorText = ''
                try { & ([ScriptBlock]::Create($body)) } catch { $errorText = $_.Exception.Message }
                Write-Output ('Value=' + $candidate + '|Removed=' + $script:removed + '|Error=' + $errorText)
            }
            """;

        (int exitCode, string output, string error) = RunPowerShellHarness(script, "TokenPolicyDrift.ps1");

        TestAssert.True(exitCode == 0, $"Token-policy drift harness failed: {error}");
        TestAssert.Contains("Value=0|Removed=False|Error=", output);
        TestAssert.Contains("Value=2|Removed=False|Error=", output);
        TestAssert.False(output.Contains("Value=0|Removed=False|Error=\r\n", StringComparison.Ordinal),
            "Rollback accepted token policy value 0.");
        TestAssert.False(output.Contains("Value=2|Removed=False|Error=\r\n", StringComparison.Ordinal),
            "Rollback accepted token policy value 2.");
    }

    private static void NetworkRollbackSkipsDomainAuthenticated()
    {
        MutableHostState state = MutableHostState.Create();
        (HostPreflightReport report, HostPreflightPlan plan) = Approved(state);
        HostConfigurationCommand command = HostConfigurationCommandCompiler.Compile(report, plan)
            .Single(item => item.Kind == HostPreflightChangeKind.ChangeNetworkToPrivate);
        string rollback = Convert.ToBase64String(Encoding.UTF8.GetBytes(command.RollbackScript));
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $script:category = 'DomainAuthenticated'
            $script:changed = $false
            function Get-NetConnectionProfile {
                [CmdletBinding()]
                param([uint32]$InterfaceIndex)
                [pscustomobject]@{ NetworkCategory = $script:category }
            }
            function Set-NetConnectionProfile {
                [CmdletBinding()]
                param([uint32]$InterfaceIndex, [string]$NetworkCategory)
                $script:category = $NetworkCategory
                $script:changed = $true
            }
            $body = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{rollback}}'))
            $errorText = ''
            try { & ([ScriptBlock]::Create($body)) } catch { $errorText = $_.Exception.Message }
            Write-Output ('Changed=' + $script:changed)
            Write-Output ('Category=' + $script:category)
            Write-Output ('RollbackError=' + $errorText)
            """;

        (int exitCode, string output, string error) = RunPowerShellHarness(script, "NetworkCategoryDrift.ps1");

        TestAssert.True(exitCode == 0, $"Network-category drift harness failed: {error}");
        TestAssert.Contains("Changed=False", output);
        TestAssert.Contains("Category=DomainAuthenticated", output);
        TestAssert.False(output.Contains("RollbackError=\r\n", StringComparison.Ordinal),
            "Rollback silently replaced DomainAuthenticated with Public.");
    }

    private static (int ExitCode, string Output, string Error) RunPowerShellHarness(
        string script,
        string fileName)
    {
        using var temp = new ConfigurationTempDirectory();
        string path = Path.Combine(temp.Path, fileName);
        File.WriteAllText(path, script, new UTF8Encoding(false));
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path })
            startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the PowerShell harness.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }

    private static void RollbackIsUtf8NoBomReverseAndConfirmed()
    {
        using var temp = new ConfigurationTempDirectory();
        DateTimeOffset now = new(2026, 8, 14, 0, 30, 0, TimeSpan.FromHours(8));
        var writer = new HostRollbackScriptWriter(temp.Path, () => now);
        HostConfigurationCommand[] applied =
        [
            new(HostPreflightChangeKind.AddRemoteManagementUsers, "第一项", "apply-1", "Write-Output 'rollback-1'"),
            new(HostPreflightChangeKind.ChangeNetworkToPrivate, "第二项", "apply-2", "Write-Output 'rollback-2'")
        ];

        string path = writer.WriteAsync("LAB-HV-06", "10.0.0.6", applied, null, CancellationToken.None).GetAwaiter().GetResult();
        byte[] bytes = File.ReadAllBytes(path);
        string script = new UTF8Encoding(false, true).GetString(bytes);

        TestAssert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "Rollback script contains a UTF-8 BOM.");
        TestAssert.Contains("ConvertFrom-Utf8Base64", script);
        TestAssert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("确认")), script);
        string rollback1 = Convert.ToBase64String(Encoding.UTF8.GetBytes("Write-Output 'rollback-1'"));
        string rollback2 = Convert.ToBase64String(Encoding.UTF8.GetBytes("Write-Output 'rollback-2'"));
        TestAssert.True(script.IndexOf(rollback2, StringComparison.Ordinal) < script.IndexOf(rollback1, StringComparison.Ordinal), "Rollback order is not reversed.");
        TestAssert.Contains("仅撤销本次向导已确认成功的修改", script);
        AssertRollbackRejectsWrongConfirmation(path);
        AssertRollbackAcceptsExactConfirmation(path);
    }

    private static void AssertRollbackRejectsWrongConfirmation(string path)
    {
        (int exitCode, string output, string error) = RunRollbackBody(path, "取消");
        TestAssert.True(exitCode == 2, $"Rollback confirmation gate failed to exit with code 2: {error}");
        TestAssert.Contains("输入不匹配，未执行任何回滚。", output);
    }

    private static void AssertRollbackAcceptsExactConfirmation(string path)
    {
        (int exitCode, string output, string error) = RunRollbackBody(path, "确认");
        TestAssert.True(exitCode == 0, $"Rollback exact confirmation failed: {error}");
        TestAssert.Contains("rollback-2", output);
        TestAssert.Contains("rollback-1", output);
        TestAssert.Contains("本次 ExHyperV 配置修改已回滚。", output);
    }

    private static (int ExitCode, string Output, string Error) RunRollbackBody(string path, string confirmation)
    {
        string bodyPath = Path.Combine(Path.GetDirectoryName(path)!, "rollback-body-test.ps1");
        string body = string.Join(
            Environment.NewLine,
            File.ReadAllLines(path, new UTF8Encoding(false, true))
                .Where(line => !line.StartsWith("#requires", StringComparison.OrdinalIgnoreCase)));
        File.WriteAllText(bodyPath, body, new UTF8Encoding(false));
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", bodyPath })
            startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the rollback script.");
        process.StandardInput.WriteLine(confirmation);
        process.StandardInput.Close();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }

    private static void ExactConfirmationAppliesVerifiesAndDiagnoses()
    {
        MutableHostState state = MutableHostState.Create();
        (HostPreflightReport report, HostPreflightPlan plan) = Approved(state);
        var runner = new MutatingRunner(state);
        var writer = new RecordingRollbackWriter();
        HostConfigurationPipeline pipeline = CreatePipeline(state, runner, writer);

        HostConfigurationReport result = pipeline.ApplyAsync(
            Profile(), null, report, plan, "确认").GetAwaiter().GetResult();

        TestAssert.True(result.Started, "Confirmed configuration did not start.");
        TestAssert.True(result.Succeeded, string.Join("; ", result.Logs));
        TestAssert.Equal(plan.Changes.Count, runner.Commands.Count);
        TestAssert.Equal(plan.Changes.Count, result.Steps.Count);
        TestAssert.True(result.Steps.All(step => step.Succeeded), "A successful apply contains a failed step.");
        TestAssert.Equal(plan.Changes.Count, writer.LastCommands.Count);
        TestAssert.Equal(plan.Changes.Count, writer.WriteCount);
        TestAssert.True(!string.IsNullOrWhiteSpace(result.RollbackScriptPath), "Rollback script path is missing.");
        TestAssert.True(result.Diagnostic?.ManagementAvailable == true, "WMI/DCOM was not rerun after apply.");
        TestAssert.True(result.Diagnostic?.ConsoleAvailable == true, "TCP 2179 was not rerun after apply.");
    }

    private static void RestoredFirewallRulesVerifyBeforeEnable()
    {
        MutableHostState state = CreateRestorableState();
        (HostPreflightReport report, HostPreflightPlan plan) = Approved(state);
        var runner = new MutatingRunner(state);
        var writer = new RecordingRollbackWriter();

        HostConfigurationReport result = CreatePipeline(state, runner, writer).ApplyAsync(
            Profile(), null, report, plan, "确认").GetAwaiter().GetResult();

        TestAssert.True(result.Succeeded, string.Join("; ", result.Logs));
        TestAssert.SequenceEqual(
            [
                HostPreflightChangeKind.AddRemoteManagementUsers,
                HostPreflightChangeKind.EnableLocalAccountTokenFilterPolicy,
                HostPreflightChangeKind.ChangeNetworkToPrivate,
                HostPreflightChangeKind.RestoreWmiFirewallRules,
                HostPreflightChangeKind.EnableWmiFirewallRules,
                HostPreflightChangeKind.RestoreHyperVFirewallRules,
                HostPreflightChangeKind.ConfigureConsole2179FirewallRule
            ],
            runner.Commands.Select(command => command.Kind));
        TestAssert.Equal(0, state.RestorableWmiRuleNames.Count);
        TestAssert.Equal(0, state.RestorableHyperVRuleNames.Count);
        TestAssert.True(state.WmiRulesEnabled, "Restored disabled WMI rules were not enabled.");
        TestAssert.True(state.HyperVRulesEnabled, "Restored enabled Hyper-V rules were not preserved as enabled.");
    }

    private static void WrongConfirmationPerformsNoReadOrWrite()
    {
        MutableHostState state = MutableHostState.Create();
        (HostPreflightReport report, HostPreflightPlan plan) = Approved(state);
        int readsBefore = state.OpenCount;
        var runner = new MutatingRunner(state);
        var writer = new RecordingRollbackWriter();

        HostConfigurationReport result = CreatePipeline(state, runner, writer).ApplyAsync(
            Profile(), null, report, plan, "确认 ").GetAwaiter().GetResult();

        TestAssert.False(result.Started, "Invalid confirmation started configuration.");
        TestAssert.Equal(readsBefore, state.OpenCount);
        TestAssert.Equal(0, runner.Commands.Count);
        TestAssert.Equal(0, writer.WriteCount);
    }

    private static void StalePreviewPerformsNoWrite()
    {
        MutableHostState state = MutableHostState.Create();
        (HostPreflightReport report, HostPreflightPlan plan) = Approved(state);
        state.TokenPolicy = HostTokenFilterPolicyState.Enabled;
        var runner = new MutatingRunner(state);
        var writer = new RecordingRollbackWriter();

        HostConfigurationReport result = CreatePipeline(state, runner, writer).ApplyAsync(
            Profile(), null, report, plan, "确认").GetAwaiter().GetResult();

        TestAssert.True(result.StalePreview, "Changed remote facts did not invalidate the preview.");
        TestAssert.False(result.Started, "A stale preview started configuration.");
        TestAssert.Equal(0, runner.Commands.Count);
        TestAssert.Equal(0, writer.WriteCount);
    }

    private static void PartialFailureStopsAndKeepsAppliedRollback()
    {
        MutableHostState state = MutableHostState.Create();
        (HostPreflightReport report, HostPreflightPlan plan) = Approved(state);
        var runner = new MutatingRunner(state) { FailAt = 2 };
        var writer = new RecordingRollbackWriter();

        HostConfigurationReport result = CreatePipeline(state, runner, writer).ApplyAsync(
            Profile(), null, report, plan, "确认").GetAwaiter().GetResult();

        TestAssert.True(result.Started, "The first successful mutation was not reported.");
        TestAssert.False(result.Succeeded, "Partial failure was reported as success.");
        TestAssert.Equal(2, runner.Commands.Count);
        TestAssert.Equal(2, result.Steps.Count);
        TestAssert.True(result.Steps[0].Succeeded, "The applied prefix was lost.");
        TestAssert.False(result.Steps[1].Succeeded, "The failing step was reported as applied.");
        TestAssert.Equal(1, writer.LastCommands.Count);
        TestAssert.Equal(3, writer.WriteCount);
        TestAssert.True(!string.IsNullOrWhiteSpace(result.RollbackScriptPath), "Partial apply rollback script path is missing.");
    }

    private static void CancellationAfterMutationKeepsRollback()
    {
        MutableHostState state = MutableHostState.Create();
        (HostPreflightReport report, HostPreflightPlan plan) = Approved(state);
        using var cancellation = new CancellationTokenSource();
        var runner = new CancellingRunner(state, cancellation);
        var writer = new RecordingRollbackWriter();

        HostConfigurationReport result = CreatePipeline(state, runner, writer).ApplyAsync(
            Profile(), null, report, plan, "确认", cancellation.Token).GetAwaiter().GetResult();

        TestAssert.True(result.Started, "The completed mutation was not reported after cancellation.");
        TestAssert.False(result.Succeeded, "A cancelled configuration was reported as complete.");
        TestAssert.Equal(1, runner.Commands.Count);
        TestAssert.Equal(1, writer.WriteCount);
        TestAssert.Equal(1, writer.LastCommands.Count);
        TestAssert.Equal(1, result.Steps.Count);
        TestAssert.False(result.Steps[0].Succeeded, "Cancelled verification was reported as verified.");
        TestAssert.Contains("远程命令已完成且回滚已记录", result.Steps[0].Message);
        TestAssert.True(!string.IsNullOrWhiteSpace(result.RollbackScriptPath), "Cancellation lost the rollback script path.");
        TestAssert.Contains("已取消", string.Join('\n', result.Logs));
    }

    private static void UnknownRemoteStateIsIncludedInRollback()
    {
        MutableHostState state = MutableHostState.Create();
        (HostPreflightReport report, HostPreflightPlan plan) = Approved(state);
        var writer = new RecordingRollbackWriter();
        var runner = new UnknownStateRunner();

        HostConfigurationReport result = CreatePipeline(state, runner, writer).ApplyAsync(
            Profile(), null, report, plan, "确认").GetAwaiter().GetResult();

        TestAssert.False(result.Succeeded, "Unknown remote state was reported as success.");
        TestAssert.Equal(1, runner.Commands.Count);
        TestAssert.Equal(1, writer.WriteCount);
        TestAssert.Equal(1, writer.LastCommands.Count);
        TestAssert.True(!string.IsNullOrWhiteSpace(result.RollbackScriptPath), "Unknown remote state did not produce a rollback path.");
        TestAssert.Contains("状态不确定", string.Join('\n', result.Logs));
    }

    private static void RemoteFailureMessageIsRedacted()
    {
        const string secret = "configuration-error-secret";
        MutableHostState state = MutableHostState.Create();
        (HostPreflightReport report, HostPreflightPlan plan) = Approved(state);
        var writer = new RecordingRollbackWriter();
        var runner = new LeakingFailureRunner(secret);

        HostConfigurationReport result = CreatePipeline(state, runner, writer).ApplyAsync(
            Profile(), null, report, plan, "确认").GetAwaiter().GetResult();

        string visibleText = string.Join('\n',
            result.Steps.Select(step => step.Message).Concat(result.Logs));
        TestAssert.False(visibleText.Contains(secret, StringComparison.Ordinal),
            "Configuration results exposed a secret from the remote command error.");
        TestAssert.Contains("[REDACTED]", visibleText);
    }

    private static void RollbackStorageFailureBlocksRemoteMutation()
    {
        MutableHostState state = MutableHostState.Create();
        (HostPreflightReport report, HostPreflightPlan plan) = Approved(state);
        var runner = new MutatingRunner(state);
        var writer = new UnavailableRollbackWriter();

        HostConfigurationReport result = CreatePipeline(state, runner, writer).ApplyAsync(
            Profile(), null, report, plan, "确认").GetAwaiter().GetResult();

        TestAssert.False(result.Started, "Configuration started without writable rollback storage.");
        TestAssert.False(result.Succeeded, "Configuration succeeded without writable rollback storage.");
        TestAssert.Equal(0, runner.Commands.Count);
        TestAssert.Equal(1, writer.VerifyCount);
        TestAssert.Contains("rollback-storage-unavailable", string.Join('\n', result.Logs));
    }

    private static void RollbackPrewriteFailureBlocksRemoteMutation()
    {
        MutableHostState state = MutableHostState.Create();
        (HostPreflightReport report, HostPreflightPlan plan) = Approved(state);
        var runner = new MutatingRunner(state);
        var writer = new FailingPrewriteRollbackWriter();

        HostConfigurationReport result = CreatePipeline(state, runner, writer).ApplyAsync(
            Profile(), null, report, plan, "确认").GetAwaiter().GetResult();

        TestAssert.False(result.Started, "Configuration started after rollback prewrite failed.");
        TestAssert.Equal(0, runner.Commands.Count);
        TestAssert.Equal(1, writer.VerifyCount);
        TestAssert.Equal(1, writer.WriteCount);
        TestAssert.Contains("rollback-prewrite-failed", string.Join('\n', result.Logs));
    }

    private static void RollbackIsPersistedBeforeEachRemoteSubmission()
    {
        MutableHostState state = MutableHostState.Create();
        (HostPreflightReport report, HostPreflightPlan plan) = Approved(state);
        var events = new List<string>();
        var writer = new OrderedRollbackWriter(events);
        var runner = new OrderedMutatingRunner(state, events);

        HostConfigurationReport result = CreatePipeline(state, runner, writer).ApplyAsync(
            Profile(), null, report, plan, "确认").GetAwaiter().GetResult();

        TestAssert.True(result.Succeeded, string.Join("; ", result.Logs));
        TestAssert.Equal(plan.Changes.Count * 2, events.Count);
        for (int index = 0; index < plan.Changes.Count; index++)
        {
            TestAssert.Equal($"rollback:{index + 1}", events[index * 2]);
            TestAssert.Equal($"remote:{plan.Changes[index].Title}", events[index * 2 + 1]);
        }
    }

    private static void RollbackRunsUseUniquePaths()
    {
        using var temp = new ConfigurationTempDirectory();
        DateTimeOffset now = new(2026, 8, 14, 0, 30, 0, TimeSpan.FromHours(8));
        var writer = new HostRollbackScriptWriter(temp.Path, () => now);
        HostConfigurationCommand[] applied =
        [
            new(HostPreflightChangeKind.AddRemoteManagementUsers, "唯一性", "apply", "Write-Output 'rollback'")
        ];

        writer.VerifyAvailableAsync(CancellationToken.None).GetAwaiter().GetResult();
        string first = writer.WriteAsync("LAB-HV-06", "10.0.0.6", applied, null, CancellationToken.None).GetAwaiter().GetResult();
        string second = writer.WriteAsync("LAB-HV-06", "10.0.0.6", applied, null, CancellationToken.None).GetAwaiter().GetResult();

        TestAssert.False(string.Equals(first, second, StringComparison.OrdinalIgnoreCase), "Two rollback runs in one second reused a path.");
        TestAssert.True(File.Exists(first), "First rollback script was overwritten or removed.");
        TestAssert.True(File.Exists(second), "Second rollback script was not written.");
        TestAssert.Equal(0, Directory.GetFiles(temp.Path, ".rollback-write-probe-*.tmp").Length);
    }

    private static void RollbackDeleteIsRestrictedToLogDirectory()
    {
        using var temp = new ConfigurationTempDirectory();
        var writer = new HostRollbackScriptWriter(temp.Path);
        HostConfigurationCommand[] applied =
        [
            new(HostPreflightChangeKind.AddRemoteManagementUsers, "删除测试", "apply", "Write-Output 'rollback'")
        ];
        string path = writer.WriteAsync(
            "LAB-HV-06", "10.0.0.6", applied, null, CancellationToken.None).GetAwaiter().GetResult();

        writer.DeleteAsync(path, CancellationToken.None).GetAwaiter().GetResult();
        TestAssert.False(File.Exists(path), "Rollback delete left the generated script behind.");

        string outside = Path.Combine(Path.GetDirectoryName(temp.Path)!, $"outside-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(outside, "preserve", new UTF8Encoding(false));
        try
        {
            try
            {
                writer.DeleteAsync(outside, CancellationToken.None).GetAwaiter().GetResult();
                throw new InvalidOperationException("Rollback writer deleted a path outside its log directory.");
            }
            catch (InvalidOperationException ex)
            {
                TestAssert.Contains("不在 ExHyperV 日志目录", ex.Message);
            }
            TestAssert.True(File.Exists(outside), "Outside file was deleted despite path validation.");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    private static void MultipleNetworksVerifyOneStepAtATime()
    {
        MutableHostState state = MutableHostState.Create();
        state.NetworkCategories[18] = HostNetworkCategory.Public;
        state.ConsoleRemoteAddresses = ["10.0.0.0/24", "192.168.50.0/24"];
        HostPreflightReport report = new HostPreflightPipeline(new CurrentIdentityResolver(), state)
            .RunAsync(Profile()).GetAwaiter().GetResult();
        HostPreflightPlanResult planResult = HostPreflightPlanner.Build(
            report,
            new HostPreflightSelection(
                HostPreflightAccountKind.Local,
                "Administrator",
                [12, 18],
                [12, 18],
                ["10.0.0.0/24", "192.168.50.0/24"]));
        TestAssert.True(planResult.IsValid, string.Join("; ", planResult.Errors));
        var runner = new MutatingRunner(state);
        var writer = new RecordingRollbackWriter();

        HostConfigurationReport result = CreatePipeline(state, runner, writer).ApplyAsync(
            Profile(), null, report, planResult.Plan!, "确认").GetAwaiter().GetResult();

        TestAssert.True(result.Succeeded, string.Join("; ", result.Logs));
        TestAssert.Equal(2, result.Steps.Count(step => step.Kind == HostPreflightChangeKind.ChangeNetworkToPrivate));
        TestAssert.Equal(HostNetworkCategory.Private, state.NetworkCategories[12]);
        TestAssert.Equal(HostNetworkCategory.Private, state.NetworkCategories[18]);
    }

    private static void ResultViewModelShowsStepsRollbackAndLogs()
    {
        var preflight = new HostPreflightPipeline(new CurrentIdentityResolver(), MutableHostState.Create());
        using var viewModel = new HostPreflightViewModel(preflight);
        viewModel.SetTarget(Profile(), transientCredential: null);
        var report = new HostConfigurationReport(
            true,
            false,
            false,
            [new HostConfigurationStepResult(
                HostPreflightChangeKind.AddRemoteManagementUsers,
                "加入 Remote Management Users",
                true,
                "状态复检通过。")],
            @"D:\Code\ExHyperV\logs\rollback-test.ps1",
            null,
            null,
            ["开始执行。", "已更新回滚脚本。"]);

        viewModel.BeginApply();
        viewModel.CompleteApply(report);

        TestAssert.False(viewModel.IsApplying, "Result view remained in applying state.");
        TestAssert.True(viewModel.HasApplyResult, "Result panel was not enabled.");
        TestAssert.Contains("部分完成", viewModel.ApplySummary);
        TestAssert.Equal(report.RollbackScriptPath, viewModel.RollbackScriptPath);
        TestAssert.Contains("LAB-HV-06（10.0.0.6）", viewModel.RollbackInstruction);
        TestAssert.Contains("目标宿主", viewModel.RollbackInstruction);
        TestAssert.Contains("管理员 PowerShell", viewModel.RollbackInstruction);
        TestAssert.Contains("确认", viewModel.RollbackInstruction);
        TestAssert.Contains("[01] 开始执行。", viewModel.ApplyLogText);
        TestAssert.Contains("[02] 已更新回滚脚本。", viewModel.ApplyLogText);
        TestAssert.Equal("已完成", viewModel.ApplySteps.Single().StatusText);
        TestAssert.Equal("状态复检通过。", viewModel.ApplySteps.Single().Detail);
    }

    private static HostConfigurationPipeline CreatePipeline(
        MutableHostState state,
        IHostConfigurationCommandRunner runner,
        IHostRollbackScriptWriter writer)
    {
        var identity = new CurrentIdentityResolver();
        var preflight = new HostPreflightPipeline(identity, state);
        var diagnostic = new HostDiagnosticPipeline(
            new SuccessfulIpv4Probe(),
            identity,
            new DelegateExplicitCredentialValidator((_, _, _) => Task.FromResult(
                new ExplicitCredentialValidationResult(
                    ExplicitCredentialValidationStatus.Valid,
                    "显式凭据验证通过。"))),
            new SuccessfulWmiProbe(),
            new SuccessfulTcpProbe());
        return new HostConfigurationPipeline(identity, preflight, runner, writer, diagnostic);
    }

    private static (HostPreflightReport Report, HostPreflightPlan Plan) Approved(MutableHostState state)
    {
        HostPreflightReport report = new HostPreflightPipeline(new CurrentIdentityResolver(), state)
            .RunAsync(Profile()).GetAwaiter().GetResult();
        HostPreflightPlanResult result = HostPreflightPlanner.Build(
            report,
            new HostPreflightSelection(
                HostPreflightAccountKind.Local,
                "Administrator",
                [12],
                [12],
                ["10.0.0.0/24"]));
        TestAssert.True(result.IsValid, string.Join("; ", result.Errors));
        return (report, result.Plan!);
    }

    private static (HostPreflightReport Report, HostPreflightPlan Plan) RestorableApproved()
    {
        return Approved(CreateRestorableState());
    }

    private static MutableHostState CreateRestorableState()
    {
        MutableHostState state = MutableHostState.Create();
        state.WmiRulesEnabled = false;
        state.HyperVRulesEnabled = false;
        state.WmiRulesDetected = false;
        state.HyperVRulesDetected = false;
        state.RestorableWmiRuleNames.AddRange(["WMI-RPCSS-In-TCP", "WMI-WINMGMT-In-TCP"]);
        state.RestorableHyperVRuleNames.Add("VIRT-VMMS-RPC-In-NoScope");
        state.DisabledRestorableWmiRuleNames.AddRange(["WMI-RPCSS-In-TCP", "WMI-WINMGMT-In-TCP"]);
        return state;
    }

    private static HostProfile Profile() => new(
        Guid.Parse("06803855-2f0b-46c9-b77a-923aa5d1b5ee"), "LAB-HV-06", "10.0.0.6");

    private sealed class CurrentIdentityResolver : IHostIdentityResolver
    {
        public ResolvedHostIdentity Resolve(HostProfile profile, WindowsCredential? transientCredential) =>
            ResolvedHostIdentity.CurrentWindowsIdentity;
    }

    private sealed class SuccessfulIpv4Probe : IIpv4ReachabilityProbe
    {
        public Task ProbeAsync(string address, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SuccessfulWmiProbe : IWmiDcomProbe
    {
        public Task ProbeAsync(string address, ResolvedHostIdentity identity, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SuccessfulTcpProbe : ITcpPortProbe
    {
        public Task ProbeAsync(string address, int port, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MutatingRunner(MutableHostState state) : IHostConfigurationCommandRunner
    {
        public List<HostConfigurationCommand> Commands { get; } = [];
        public int? FailAt { get; init; }

        public Task<HostConfigurationCommandResult> RunAsync(
            string address,
            ResolvedHostIdentity identity,
            HostConfigurationCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            if (FailAt == Commands.Count)
                return Task.FromResult(new HostConfigurationCommandResult(false, "模拟执行失败"));
            state.Apply(command);
            return Task.FromResult(new HostConfigurationCommandResult(true, "模拟执行成功"));
        }
    }

    private sealed class CancellingRunner(MutableHostState state, CancellationTokenSource cancellation)
        : IHostConfigurationCommandRunner
    {
        public List<HostConfigurationCommand> Commands { get; } = [];

        public Task<HostConfigurationCommandResult> RunAsync(
            string address,
            ResolvedHostIdentity identity,
            HostConfigurationCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            state.Apply(command);
            cancellation.Cancel();
            return Task.FromResult(new HostConfigurationCommandResult(true, "模拟执行成功"));
        }
    }

    private sealed class UnknownStateRunner : IHostConfigurationCommandRunner
    {
        public List<HostConfigurationCommand> Commands { get; } = [];

        public Task<HostConfigurationCommandResult> RunAsync(
            string address,
            ResolvedHostIdentity identity,
            HostConfigurationCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(new HostConfigurationCommandResult(
                false,
                "模拟远程状态未知",
                MayHaveApplied: true));
        }
    }

    private sealed class LeakingFailureRunner(string secret) : IHostConfigurationCommandRunner
    {
        public Task<HostConfigurationCommandResult> RunAsync(
            string address,
            ResolvedHostIdentity identity,
            HostConfigurationCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HostConfigurationCommandResult(
                false,
                $"password={secret} token={secret}"));
    }

    private sealed class RecordingRollbackWriter : IHostRollbackScriptWriter
    {
        public int WriteCount { get; private set; }
        public int VerifyCount { get; private set; }
        public int DeleteCount { get; private set; }
        public IReadOnlyList<HostConfigurationCommand> LastCommands { get; private set; } = [];

        public Task VerifyAvailableAsync(CancellationToken cancellationToken)
        {
            VerifyCount++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            DeleteCount++;
            LastCommands = [];
            return Task.CompletedTask;
        }

        public Task<string> WriteAsync(
            string hostName,
            string hostAddress,
            IReadOnlyList<HostConfigurationCommand> appliedCommands,
            string? existingPath,
            CancellationToken cancellationToken)
        {
            WriteCount++;
            LastCommands = appliedCommands.ToArray();
            return Task.FromResult(existingPath ?? @"D:\Code\ExHyperV\logs\rollback-test.ps1");
        }
    }

    private sealed class OrderedRollbackWriter(List<string> events) : IHostRollbackScriptWriter
    {
        public Task VerifyAvailableAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            events.Add("delete");
            return Task.CompletedTask;
        }

        public Task<string> WriteAsync(
            string hostName,
            string hostAddress,
            IReadOnlyList<HostConfigurationCommand> appliedCommands,
            string? existingPath,
            CancellationToken cancellationToken)
        {
            events.Add($"rollback:{appliedCommands.Count}");
            return Task.FromResult(existingPath ?? @"D:\Code\ExHyperV\logs\rollback-ordered.ps1");
        }
    }

    private sealed class OrderedMutatingRunner(MutableHostState state, List<string> events)
        : IHostConfigurationCommandRunner
    {
        public Task<HostConfigurationCommandResult> RunAsync(
            string address,
            ResolvedHostIdentity identity,
            HostConfigurationCommand command,
            CancellationToken cancellationToken)
        {
            events.Add($"remote:{command.Title}");
            state.Apply(command);
            return Task.FromResult(new HostConfigurationCommandResult(true, "模拟执行成功"));
        }
    }

    private sealed class UnavailableRollbackWriter : IHostRollbackScriptWriter
    {
        public int VerifyCount { get; private set; }

        public Task VerifyAvailableAsync(CancellationToken cancellationToken)
        {
            VerifyCount++;
            throw new IOException("rollback-storage-unavailable");
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Delete must not run after availability failure.");

        public Task<string> WriteAsync(
            string hostName,
            string hostAddress,
            IReadOnlyList<HostConfigurationCommand> appliedCommands,
            string? existingPath,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Write must not run after availability failure.");
    }

    private sealed class FailingPrewriteRollbackWriter : IHostRollbackScriptWriter
    {
        public int VerifyCount { get; private set; }
        public int WriteCount { get; private set; }

        public Task VerifyAvailableAsync(CancellationToken cancellationToken)
        {
            VerifyCount++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Delete must not run when the first prewrite fails.");

        public Task<string> WriteAsync(
            string hostName,
            string hostAddress,
            IReadOnlyList<HostConfigurationCommand> appliedCommands,
            string? existingPath,
            CancellationToken cancellationToken)
        {
            WriteCount++;
            throw new IOException("rollback-prewrite-failed");
        }
    }

    private sealed class MutableHostState : IHostPreflightReader, IHostPreflightReadSession
    {
        public int OpenCount { get; private set; }
        public HostTokenFilterPolicyState TokenPolicy { get; set; } = HostTokenFilterPolicyState.Missing;
        public Dictionary<uint, HostNetworkCategory> NetworkCategories { get; } = new()
        {
            [12] = HostNetworkCategory.Public
        };
        public bool WmiRulesEnabled { get; set; } = true;
        public bool HyperVRulesEnabled { get; set; }
        public bool WmiRulesDetected { get; set; } = true;
        public bool HyperVRulesDetected { get; set; } = true;
        public List<string> RestorableWmiRuleNames { get; } = [];
        public List<string> RestorableHyperVRuleNames { get; } = [];
        public List<string> DisabledRestorableWmiRuleNames { get; } = [];
        public List<string> DisabledRestorableHyperVRuleNames { get; } = [];
        public bool ConsoleRuleEnabled { get; set; }
        public IReadOnlyList<string> ConsoleRemoteAddresses { get; set; } = ["10.0.0.0/24"];
        public HashSet<string> HyperVMembers { get; } = new(StringComparer.OrdinalIgnoreCase) { "LAB-HV-06\\Administrator" };
        public HashSet<string> RemoteManagementMembers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static MutableHostState Create() => new();

        public Task<IHostPreflightReadSession> OpenAsync(
            string address,
            ResolvedHostIdentity identity,
            CancellationToken cancellationToken)
        {
            OpenCount++;
            return Task.FromResult<IHostPreflightReadSession>(this);
        }

        public Task<HostJoinSnapshot> ReadJoinAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HostJoinSnapshot("LAB-HV-06", HostJoinKind.Workgroup, "WORKGROUP"));

        public Task<IReadOnlyList<HostLocalAccount>> ReadEnabledLocalAccountsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HostLocalAccount>>([new HostLocalAccount("Administrator", "S-1-5-21-1-500")]);

        public Task<HostLocalGroupSnapshot> ReadLocalGroupAsync(HostLocalGroupKind group, CancellationToken cancellationToken) =>
            Task.FromResult<HostLocalGroupSnapshot>(group switch
            {
                HostLocalGroupKind.Administrators => new(group, "Administrators", ["LAB-HV-06\\Administrator"]),
                HostLocalGroupKind.HyperVAdministrators => new(group, "Hyper-V Administrators", HyperVMembers.ToArray()),
                _ => new(group, "Remote Management Users", RemoteManagementMembers.ToArray())
            });

        public Task<HostTokenFilterPolicyState> ReadTokenFilterPolicyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(TokenPolicy);

        public Task<IReadOnlyList<HostNetworkSnapshot>> ReadNetworksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HostNetworkSnapshot>>(
                NetworkCategories.Select(item => new HostNetworkSnapshot(
                    item.Key,
                    item.Key == 12 ? "以太网" : "备份网",
                    item.Value,
                    [new HostIpv4Address(item.Key == 12 ? "10.0.0.6" : "192.168.50.6", 24)])).ToArray());

        public Task<HostFirewallSnapshot> ReadFirewallAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HostFirewallSnapshot(
                WmiBuiltInRulesEnabled: WmiRulesEnabled,
                HyperVBuiltInRulesEnabled: HyperVRulesEnabled,
                ExHyperVConsole2179RuleEnabled: ConsoleRuleEnabled,
                ExHyperVConsole2179RemoteAddresses: ConsoleRuleEnabled ? ConsoleRemoteAddresses : [],
                DisabledWmiRuleNames: WmiRulesDetected && !WmiRulesEnabled ? ["WMI-RPCSS-In-TCP"] : [],
                DisabledHyperVRuleNames: HyperVRulesDetected && !HyperVRulesEnabled ? ["Hyper-V-VMMS-In-TCP"] : [],
                ExHyperVConsole2179RuleExists: ConsoleRuleEnabled,
                ExHyperVConsole2179Protocol: "TCP",
                ExHyperVConsole2179LocalPorts: ["2179"],
                ExHyperVConsole2179Action: "Allow",
                ExHyperVConsole2179Profiles: ["Private", "Domain"],
                WmiBuiltInRuleGroupDetected: WmiRulesDetected,
                HyperVBuiltInRuleGroupDetected: HyperVRulesDetected,
                RestorableWmiRuleNames: RestorableWmiRuleNames.ToArray(),
                RestorableHyperVRuleNames: RestorableHyperVRuleNames.ToArray(),
                DisabledRestorableWmiRuleNames: DisabledRestorableWmiRuleNames.ToArray(),
                DisabledRestorableHyperVRuleNames: DisabledRestorableHyperVRuleNames.ToArray()));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Apply(HostConfigurationCommand command)
        {
            switch (command.Kind)
            {
                case HostPreflightChangeKind.AddHyperVAdministrators:
                    HyperVMembers.Add("LAB-HV-06\\Administrator");
                    break;
                case HostPreflightChangeKind.AddRemoteManagementUsers:
                    RemoteManagementMembers.Add("LAB-HV-06\\Administrator");
                    break;
                case HostPreflightChangeKind.EnableLocalAccountTokenFilterPolicy:
                    TokenPolicy = HostTokenFilterPolicyState.Enabled;
                    break;
                case HostPreflightChangeKind.ChangeNetworkToPrivate:
                    uint interfaceIndex = command.Title.Contains("备份网", StringComparison.Ordinal) ? 18u : 12u;
                    NetworkCategories[interfaceIndex] = HostNetworkCategory.Private;
                    break;
                case HostPreflightChangeKind.RestoreWmiFirewallRules:
                    WmiRulesDetected = true;
                    RestorableWmiRuleNames.Clear();
                    if (DisabledRestorableWmiRuleNames.Count == 0) WmiRulesEnabled = true;
                    break;
                case HostPreflightChangeKind.RestoreHyperVFirewallRules:
                    HyperVRulesDetected = true;
                    RestorableHyperVRuleNames.Clear();
                    if (DisabledRestorableHyperVRuleNames.Count == 0) HyperVRulesEnabled = true;
                    break;
                case HostPreflightChangeKind.EnableWmiFirewallRules:
                    WmiRulesEnabled = true;
                    DisabledRestorableWmiRuleNames.Clear();
                    break;
                case HostPreflightChangeKind.EnableHyperVFirewallRules:
                    HyperVRulesEnabled = true;
                    DisabledRestorableHyperVRuleNames.Clear();
                    break;
                case HostPreflightChangeKind.ConfigureConsole2179FirewallRule:
                    ConsoleRuleEnabled = true;
                    break;
            }
        }
    }

    private sealed class ConfigurationTempDirectory : IDisposable
    {
        public ConfigurationTempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ExHyperV.ConfigurationTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
