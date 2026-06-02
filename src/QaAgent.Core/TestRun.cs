namespace QaAgent.Core;

public enum TestOutcome
{
    Passed,
    Failed,
    Skipped,
    Other
}

/// <summary>Результат одного тест-кейса.</summary>
public sealed class TestCaseResult
{
    public string Name { get; set; } = string.Empty;
    public TestOutcome Outcome { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ErrorMessage { get; set; }
    public string? StackTrace { get; set; }
}

/// <summary>Підсумок прогону тестів (розпарсений із TRX).</summary>
public sealed class TestRun
{
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public TimeSpan Duration { get; set; }
    public DateTimeOffset RanAt { get; set; } = DateTimeOffset.UtcNow;

    public List<TestCaseResult> Results { get; set; } = new();

    public IEnumerable<TestCaseResult> Failures => Results.Where(r => r.Outcome == TestOutcome.Failed);
    public bool Success => Failed == 0 && Total > 0;
}
