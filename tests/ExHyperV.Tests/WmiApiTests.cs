using ExHyperV.Tools;

internal static class WmiApiTests
{
    public static IEnumerable<(string Name, Action Run)> All =>
    [
        ("WmiApi_RemoteMethodCallsInheritContextTimeout", RemoteMethodCallsInheritContextTimeout),
        ("WmiApi_LocalMethodCallsKeepDefaultTimeout", LocalMethodCallsKeepDefaultTimeout),
        ("WmiApi_RemoteQueriesInheritContextTimeout", RemoteQueriesInheritContextTimeout),
        ("WmiApi_LocalQueriesKeepDefaultTimeout", LocalQueriesKeepDefaultTimeout),
        ("WmiApi_QueryOperationContextPreservesPolicyStore", QueryOperationContextPreservesPolicyStore),
        ("WmiApi_RemoteObjectReadsInheritContextTimeout", RemoteObjectReadsInheritContextTimeout),
        ("WmiApi_LocalObjectReadsKeepDefaultTimeout", LocalObjectReadsKeepDefaultTimeout),
        ("WmiApi_ClearedContextCannotRepopulateConnectionCache", ClearedContextCannotRepopulateConnectionCache)
    ];

    private static void RemoteMethodCallsInheritContextTimeout()
    {
        TimeSpan timeout = TimeSpan.FromMilliseconds(1234);
        WmiContext context = WmiContext.RemoteCurrentWindowsIdentity("10.0.0.6", timeout);

        System.Management.InvokeMethodOptions? options = WmiApi.CreateInvokeMethodOptions(context);

        TestAssert.NotNull(options, "Remote WMI method invocation did not create timeout options.");
        TestAssert.Equal(timeout, options!.Timeout);
    }

    private static void LocalMethodCallsKeepDefaultTimeout()
    {
        System.Management.InvokeMethodOptions? options = WmiApi.CreateInvokeMethodOptions(WmiContext.Local);

        TestAssert.Null(options, "Local WMI method invocation unexpectedly imposed a timeout.");
    }

    private static void RemoteQueriesInheritContextTimeout()
    {
        TimeSpan timeout = TimeSpan.FromMilliseconds(2345);
        WmiContext context = WmiContext.RemoteCurrentWindowsIdentity("10.0.0.6", timeout);

        System.Management.EnumerationOptions? options = WmiApi.CreateEnumerationOptions(context);

        TestAssert.NotNull(options, "Remote WMI query did not create timeout options.");
        TestAssert.Equal(timeout, options!.Timeout);
        TestAssert.False(options.ReturnImmediately, "Remote WMI query unexpectedly enabled deferred enumeration.");
    }

    private static void LocalQueriesKeepDefaultTimeout()
    {
        System.Management.EnumerationOptions? options = WmiApi.CreateEnumerationOptions(WmiContext.Local);

        TestAssert.Null(options, "Local WMI query unexpectedly imposed a timeout.");
    }

    private static void QueryOperationContextPreservesPolicyStore()
    {
        System.Management.EnumerationOptions? options = WmiApi.CreateEnumerationOptions(
            WmiContext.Local,
            new Dictionary<string, object> { ["PolicyStore"] = "SystemDefaults" });

        TestAssert.NotNull(options, "A WMI operation context did not create enumeration options.");
        TestAssert.Equal("SystemDefaults", (string)options!.Context["PolicyStore"]);
        TestAssert.False(options.ReturnImmediately, "Policy-store queries must complete before mapping their results.");
    }

    private static void RemoteObjectReadsInheritContextTimeout()
    {
        TimeSpan timeout = TimeSpan.FromMilliseconds(3456);
        WmiContext context = WmiContext.RemoteCurrentWindowsIdentity("10.0.0.6", timeout);

        System.Management.ObjectGetOptions? options = WmiApi.CreateObjectGetOptions(context);

        TestAssert.NotNull(options, "Remote WMI object read did not create timeout options.");
        TestAssert.Equal(timeout, options!.Timeout);
    }

    private static void LocalObjectReadsKeepDefaultTimeout()
    {
        System.Management.ObjectGetOptions? options = WmiApi.CreateObjectGetOptions(WmiContext.Local);

        TestAssert.Null(options, "Local WMI object read unexpectedly imposed a timeout.");
    }

    private static void ClearedContextCannotRepopulateConnectionCache()
    {
        WmiContext context = WmiContext.RemoteCurrentWindowsIdentity("10.0.0.6", TimeSpan.FromMilliseconds(10));

        WmiConnectionCache.Clear(context);

        Assert.Throws<ObjectDisposedException>(() => WmiConnectionCache.GetManagementScope(WmiScope.HyperV, context));
    }
}
