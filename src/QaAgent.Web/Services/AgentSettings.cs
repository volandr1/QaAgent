using QaAgent.App;
using QaAgent.Core;

namespace QaAgent.Web.Services;

/// <summary>
/// Налаштування агента, редаговані з UI: вибраний таргет (API), модель Ollama, Telegram.
/// Будує <see cref="QaAgentService"/> під вибраний таргет і застосовує env для репортерів.
/// </summary>
public sealed class AgentSettings
{
    public string TargetName { get; set; } = TargetConfig.BuiltIn[0].Name;

    public string OllamaModel { get; set; } =
        Environment.GetEnvironmentVariable("QA_OLLAMA_MODEL") ?? "qwen2.5-coder:7b";
    public string OllamaEndpoint { get; set; } =
        Environment.GetEnvironmentVariable("QA_OLLAMA_ENDPOINT") ?? "http://localhost:11434";
    public string? TelegramToken { get; set; } = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
    public string? TelegramChatId { get; set; } = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID");

    private readonly List<TargetConfig> _targets = new(TargetConfig.BuiltIn);

    public IReadOnlyList<TargetConfig> Targets => _targets;
    public TargetConfig CurrentTarget =>
        _targets.FirstOrDefault(t => t.Name.Equals(TargetName, StringComparison.OrdinalIgnoreCase)) ?? _targets[0];

    /// <summary>Додає (або обирає наявний) таргет для довільного Swagger URL і робить його активним.</summary>
    public TargetConfig AddCustom(string swaggerUrl, string? name = null)
    {
        var target = TargetConfig.FromUrl(swaggerUrl, name);
        var existing = _targets.FirstOrDefault(t => t.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is null) _targets.Add(target);
        else target = existing;
        TargetName = target.Name;
        return target;
    }

    public void ApplyEnv()
    {
        Environment.SetEnvironmentVariable("QA_OLLAMA_MODEL", OllamaModel);
        Environment.SetEnvironmentVariable("QA_OLLAMA_ENDPOINT", OllamaEndpoint);
        if (!string.IsNullOrWhiteSpace(TelegramToken))
            Environment.SetEnvironmentVariable("TELEGRAM_BOT_TOKEN", TelegramToken);
        if (!string.IsNullOrWhiteSpace(TelegramChatId))
            Environment.SetEnvironmentVariable("TELEGRAM_CHAT_ID", TelegramChatId);
    }

    public QaAgentService CreateService(WorkspacePaths paths)
    {
        ApplyEnv();
        return new QaAgentService(paths, CurrentTarget,
            new OllamaOptions { Model = OllamaModel, Endpoint = OllamaEndpoint });
    }
}
