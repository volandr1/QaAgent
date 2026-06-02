namespace QaAgent.Core;

public enum FindingSeverity
{
    Info,
    Warning,
    Bug
}

/// <summary>Окрема знахідка агента (баг, попередження документації тощо).</summary>
public sealed class ReportFinding
{
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public FindingSeverity Severity { get; set; } = FindingSeverity.Info;
}

/// <summary>Зведений звіт одного циклу роботи агента.</summary>
public sealed class RunReport
{
    public string ApiTitle { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.Now;

    public TestRun? TestRun { get; set; }

    public List<string> SchemaChanges { get; set; } = new();

    public bool SelfHealingApplied { get; set; }
    public List<string> HealedFiles { get; set; } = new();

    public List<ReportFinding> Findings { get; set; } = new();

    /// <summary>Стислий AI-аналіз прогону (резюме + ймовірні причини + рекомендації).</summary>
    public string? AiAnalysis { get; set; }

    public bool Success { get; set; }
    public int BugCount => Findings.Count(f => f.Severity == FindingSeverity.Bug);
}
