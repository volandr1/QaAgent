using System.Diagnostics;
using System.Xml.Linq;
using QaAgent.Core;

namespace QaAgent.Execution;

/// <summary>
/// Запускає `dotnet test` для тест-проєкту і парсить TRX у <see cref="TestRun"/>.
/// </summary>
public sealed class TestRunner
{
    private static readonly XNamespace Trx =
        "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public sealed record RunOutput(TestRun Run, int ExitCode, string StdOut, string StdErr);

    public async Task<RunOutput> RunAsync(
        string testProjectPath, string resultsDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(resultsDir);
        foreach (var old in Directory.GetFiles(resultsDir, "*.trx"))
            File.Delete(old);

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add(testProjectPath);
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("--logger");
        psi.ArgumentList.Add("trx");
        psi.ArgumentList.Add("--results-directory");
        psi.ArgumentList.Add(resultsDir);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Не вдалося запустити `dotnet test`.");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var trx = Directory.GetFiles(resultsDir, "*.trx")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (trx is null)
            throw new InvalidOperationException(
                $"TRX-файл не знайдено (exit={proc.ExitCode}).\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        return new RunOutput(Parse(trx), proc.ExitCode, stdout, stderr);
    }

    public static TestRun Parse(string trxPath)
    {
        var doc = XDocument.Load(trxPath);
        var run = new TestRun();

        foreach (var r in doc.Descendants(Trx + "UnitTestResult"))
        {
            var tc = new TestCaseResult
            {
                Name = (string?)r.Attribute("testName") ?? string.Empty,
                Outcome = ParseOutcome((string?)r.Attribute("outcome")),
                Duration = TimeSpan.TryParse((string?)r.Attribute("duration"), out var d) ? d : TimeSpan.Zero
            };

            var errorInfo = r.Element(Trx + "Output")?.Element(Trx + "ErrorInfo");
            if (errorInfo is not null)
            {
                tc.ErrorMessage = (string?)errorInfo.Element(Trx + "Message");
                tc.StackTrace = (string?)errorInfo.Element(Trx + "StackTrace");
            }

            run.Results.Add(tc);
        }

        run.Passed = run.Results.Count(r => r.Outcome == TestOutcome.Passed);
        run.Failed = run.Results.Count(r => r.Outcome == TestOutcome.Failed);
        run.Skipped = run.Results.Count(r => r.Outcome == TestOutcome.Skipped);
        run.Total = run.Results.Count;
        run.Duration = TimeSpan.FromTicks(run.Results.Sum(r => r.Duration.Ticks));
        return run;
    }

    private static TestOutcome ParseOutcome(string? outcome) => outcome switch
    {
        "Passed" => TestOutcome.Passed,
        "Failed" => TestOutcome.Failed,
        "NotExecuted" => TestOutcome.Skipped,
        _ => TestOutcome.Other
    };
}
