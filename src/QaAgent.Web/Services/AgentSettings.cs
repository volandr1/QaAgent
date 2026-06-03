using System.Text.Json;
using QaAgent.App;
using QaAgent.Core;

namespace QaAgent.Web.Services;

/// <summary>
/// Налаштування агента, редаговані з UI. ПЕРСИСТЯТЬСЯ у artifacts/ui-settings.json
/// (вибраний таргет, модель, endpoint, Telegram, користувацькі таргети) — переживають перезапуск.
/// </summary>
public sealed class AgentSettings
{
    private readonly string _file;
    private readonly List<TargetConfig> _targets;

    public AgentSettings(WorkspacePaths paths)
    {
        _file = Path.Combine(paths.Artifacts, "ui-settings.json");
        _targets = new List<TargetConfig>(TargetConfig.BuiltIn);
        Load();
    }

    public string TargetName { get; set; } = TargetConfig.BuiltIn[0].Name;
    public string OllamaModel { get; set; } =
        Environment.GetEnvironmentVariable("QA_OLLAMA_MODEL") ?? "qwen2.5-coder:7b";
    public string OllamaEndpoint { get; set; } =
        Environment.GetEnvironmentVariable("QA_OLLAMA_ENDPOINT") ?? "http://localhost:11434";
    public string? TelegramToken { get; set; } = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
    public string? TelegramChatId { get; set; } = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID");

    public IReadOnlyList<TargetConfig> Targets => _targets;
    public TargetConfig CurrentTarget =>
        _targets.FirstOrDefault(t => t.Name.Equals(TargetName, StringComparison.OrdinalIgnoreCase)) ?? _targets[0];

    public TargetConfig AddCustom(string swaggerUrl, string? name = null, string authScheme = "none")
    {
        var target = TargetConfig.FromUrl(swaggerUrl, name, authScheme);
        var existing = _targets.FirstOrDefault(t => t.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is null) _targets.Add(target);
        else target = existing;
        TargetName = target.Name;
        Save();
        return target;
    }

    public void RemoveCustom(string name)
    {
        if (TargetConfig.BuiltIn.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        _targets.RemoveAll(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (TargetName.Equals(name, StringComparison.OrdinalIgnoreCase))
            TargetName = TargetConfig.BuiltIn[0].Name;
        Save();
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

    // ---- персистентність ----

    private sealed record CustomTarget(string Name, string SwaggerUrl, string? AuthScheme = "none");
    private sealed record Persisted(
        string? TargetName, string? OllamaModel, string? OllamaEndpoint,
        string? TelegramToken, string? TelegramChatId, List<CustomTarget>? CustomTargets);

    public void Save()
    {
        try
        {
            var customs = _targets
                .Where(t => TargetConfig.BuiltIn.All(b => !b.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(t => new CustomTarget(t.Name, t.SwaggerUrl, t.AuthScheme)).ToList();

            var data = new Persisted(TargetName, OllamaModel, OllamaEndpoint, TelegramToken, TelegramChatId, customs);
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* не критично */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var data = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_file));
            if (data is null) return;

            if (data.CustomTargets is not null)
                foreach (var c in data.CustomTargets)
                    if (!_targets.Any(t => t.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
                        _targets.Add(TargetConfig.FromUrl(c.SwaggerUrl, c.Name, c.AuthScheme ?? "none"));

            if (!string.IsNullOrWhiteSpace(data.TargetName)) TargetName = data.TargetName;
            if (!string.IsNullOrWhiteSpace(data.OllamaModel)) OllamaModel = data.OllamaModel;
            if (!string.IsNullOrWhiteSpace(data.OllamaEndpoint)) OllamaEndpoint = data.OllamaEndpoint;
            if (data.TelegramToken is not null) TelegramToken = data.TelegramToken;
            if (data.TelegramChatId is not null) TelegramChatId = data.TelegramChatId;
        }
        catch { /* пошкоджений файл — ігноруємо */ }
    }
}
