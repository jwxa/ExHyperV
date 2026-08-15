using System.Management;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using ExHyperV.Tools;

namespace ExHyperV.Services.Remote.Vms;

internal static class HostConnectionFailureClassifier
{
    public static bool IsConnectionLoss(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException or SocketException)
                return true;
            if (current is ApiResponseException response
                && response.ErrorSource == ApiErrorSource.Wmi
                && (response.Code == (int)ManagementStatus.Timedout
                    || response.Code == (int)ManagementStatus.TransportFailure
                    || response.Code == (int)ManagementStatus.ServerTooBusy))
                return true;
            if (current is ManagementException management
                && management.ErrorCode is ManagementStatus.Timedout
                    or ManagementStatus.TransportFailure
                    or ManagementStatus.ServerTooBusy)
                return true;
            if (current is COMException com
                && (uint)com.HResult is 0x800706BA // RPC_S_SERVER_UNAVAILABLE
                    or 0x800706BE                  // RPC_S_CALL_FAILED
                    or 0x80010108)                 // RPC_E_DISCONNECTED
                return true;
        }
        return false;
    }
}
