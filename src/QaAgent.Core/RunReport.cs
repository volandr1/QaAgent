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

/// <summary>Покриття ендпоінтів тестами (скільки з усіх мають згенеровані тести).</summary>
public sealed class CoverageInfo
{
    public int TotalEndpoints { get; set; }
    public int CoveredEndpoints { get; set; }
    public int Percent => TotalEndpoints == 0 ? 0 : (int)Math.Round(100.0 * CoveredEndpoints / TotalEndpoints);

    /// <summary>Сигнатури ендпоінтів без тестів (Method Path).</summary>
    public List<string> Uncovered { get; set; } = new();

    /// <summary>Кількість тест-кейсів за типом сценарію (positive/negative/auth/boundary/...).</summary>
    public Dictionary<string, int> ByType { get; set; } = new();
}

/// <summary>Зведений звіт одного циклу роботи агента.</summary>
public sealed class RunReport
{
    public CoverageInfo? Coverage { get; set; }

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
