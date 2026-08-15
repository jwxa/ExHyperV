namespace ExHyperV.Services;

public enum VmLifecycleActionSupport
{
    Supported,
    UnsupportedOnRemote,
    Unknown
}

public static class VmLifecycleActionPolicy
{
    public static VmLifecycleActionSupport Evaluate(string? action, bool isRemote)
    {
        bool known = action is "Start" or "Stop" or "TurnOff" or "Restart" or "Save" or "Suspend";
        if (!known) return VmLifecycleActionSupport.Unknown;

        return isRemote && action is "Save" or "Suspend"
            ? VmLifecycleActionSupport.UnsupportedOnRemote
            : VmLifecycleActionSupport.Supported;
    }
}
