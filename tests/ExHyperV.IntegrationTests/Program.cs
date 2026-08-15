using System.Text;
using ExHyperV.IntegrationTests;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

if (!IntegrationOptions.IsEnabled())
{
    Console.WriteLine(
        "SKIP 未启用受控宿主集成验收。仅当 EXHYPERV_INTEGRATION_RUN=确认 时才会访问网络。 ");
    return 0;
}

IntegrationOptions options;
try
{
    options = IntegrationOptions.Load();
}
catch (IntegrationOptionException ex)
{
    Console.Error.WriteLine($"配置错误：{ex.Message}");
    return 2;
}

using var totalCancellation = new CancellationTokenSource(options.TotalTimeout);
var runner = new ControlledHostAcceptanceRunner(options);
AcceptanceReport report = await runner.RunAsync(totalCancellation.Token);
string reportPath;
try
{
    reportPath = await report.WriteAsync(options.ReportPath, CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"写入验收报告失败：{AcceptanceReport.Safe(ex.Message)}");
    return 3;
}

Console.WriteLine($"RESULT status={report.OverallStatus} stages={report.Stages.Count} report={reportPath}");
return report.HasFailures ? 1 : 0;
