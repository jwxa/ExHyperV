using ExHyperV.Services.Remote.Consoles;
using ExHyperV.Services.Remote.Sessions;

namespace ExHyperV.IntegrationTests;

internal sealed record PartialAvailabilityEvidence(
    bool ManagementReadSucceeded,
    bool VmReadAvailable,
    bool VmWriteAvailable,
    bool ConsoleUnavailable,
    string ConsoleUnavailableReason,
    bool ConsoleCaptureRejected)
{
    public bool IsComplete =>
        ManagementReadSucceeded
        && VmReadAvailable
        && VmWriteAvailable
        && ConsoleUnavailable
        && ConsoleCaptureRejected
        && ConsoleUnavailableReason.Contains("TCP 2179", StringComparison.Ordinal);
}

internal static class PartialAvailabilityAcceptance
{
    public static PartialAvailabilityEvidence Evaluate(
        ActiveHostCoordinatorSnapshot snapshot,
        bool managementReadSucceeded,
        HostConsoleSessionCapture capture)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(capture);
        HostCapability read = snapshot.Capabilities[HostCapabilityKind.VmRead];
        HostCapability write = snapshot.Capabilities[HostCapabilityKind.VmWrite];
        HostCapability console = snapshot.Capabilities[HostCapabilityKind.VmConsole];
        return new PartialAvailabilityEvidence(
            managementReadSucceeded,
            read.CanExecute,
            write.CanExecute,
            !console.CanExecute
            && console.ReasonCode == HostCapabilityReasonCode.ConsoleChannelUnavailable,
            console.Reason,
            !capture.Succeeded
            && capture.Message.Contains("TCP 2179", StringComparison.Ordinal));
    }
}
