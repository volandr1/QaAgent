namespace QaAgent.App;

/// <summary>
/// Розвʼязує шляхи робочого простору (корінь рішення, artifacts, generated тощо).
/// Корінь шукається вгору від каталогу запуску за файлом QaAgent.slnx.
/// </summary>
public sealed class WorkspacePaths
{
    public string SolutionRoot { get; }

    public WorkspacePaths(string? solutionRoot = null) =>
        SolutionRoot = solutionRoot ?? FindRoot();

    public string Artifacts => Ensure(Path.Combine(SolutionRoot, "artifacts"));
    public string Reports => Ensure(Path.Combine(Artifacts, "reports"));
    public string TestResults => Ensure(Path.Combine(Artifacts, "test-results"));
    public string TestProject => Path.Combine(SolutionRoot, "generated", "ApiTests", "ApiTests.csproj");

    // ---- Шляхи, скоповані під конкретний таргет (library / petstore / …) ----
    public string ArtifactsFor(string target) => Ensure(Path.Combine(Artifacts, target));
    public string SnapshotFor(string target) => Path.Combine(ArtifactsFor(target), "schema-snapshot.json");
    public string ReportsFor(string target) => Ensure(Path.Combine(ArtifactsFor(target), "reports"));
    public string TestResultsFor(string target) => Ensure(Path.Combine(ArtifactsFor(target), "test-results"));
    public string GeneratedFor(string target) =>
        Ensure(Path.Combine(SolutionRoot, "generated", "ApiTests", "Generated", target));
    public string TestNamespace(string target) => $"ApiTests.Generated.{target}";

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("QaAgent.slnx").Length == 0)
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
