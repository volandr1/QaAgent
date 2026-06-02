using System.Text.Json;
using QaAgent.App;
using QaAgent.Core;

namespace QaAgent.Web.Services;

/// <summary>
/// Налаштування агента, редаговані з UI: вибраний таргет (API), модель Ollama, Telegram.
/// Користувацькі таргети (додані за URL) ЗБЕРІГАЮТЬСЯ у файл і переживають перезапуск.
/// </summary>
public sealed class AgentSettings
{
    private readonly string _customFile;
    private readonly List<TargetConfig> _targets;

    public AgentSettings(WorkspacePaths paths)
    {
        _customFile = Path.Combine(paths.Artifacts, "custom-targets.json");
        _targets = new List<TargetConfig>(TargetConfig.BuiltIn);
        LoadCustom();
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

    /// <summary>Додає (або обирає наявний) таргет за URL, зберігає на диск і робить активним.</summary>
    public TargetConfig AddCustom(string swaggerUrl, string? name = null)
    {
        var target = TargetConfig.FromUrl(swaggerUrl, name);
        var existing = _targets.FirstOrDefault(t => t.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            _targets.Add(target);
            SaveCustom();
        }
        else target = existing;

        TargetName = target.Name;
        return target;
    }

    /// <summary>Видаляє користувацький таргет (вбудовані не чіпаємо).</summary>
    public void RemoveCustom(string name)
    {
        if (TargetConfig.BuiltIn.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return;
        _targets.RemoveAll(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (TargetName.Equals(name, StringComparison.OrdinalIgnoreCase))
            TargetName = TargetConfig.BuiltIn[0].Name;
        SaveCustom();
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

    // ---- персистентність користувацьких таргетів ----

    private sealed record CustomTarget(string Name, string SwaggerUrl);

    private void LoadCustom()
    {
        try
        {
            if (!File.Exists(_customFile)) return;
            var items = JsonSerializer.Deserialize<List<CustomTarget>>(File.ReadAllText(_customFile)) ?? new();
            foreach (var it in items)
                if (!_targets.Any(t => t.Name.Equals(it.Name, StringComparison.OrdinalIgnoreCase)))
                    _targets.Add(TargetConfig.FromUrl(it.SwaggerUrl, it.Name));
        }
        catch { /* пошкоджений файл — ігноруємо */ }
    }

    private void SaveCustom()
    {
        try
        {
            var builtin = TargetConfig.BuiltIn.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var customs = _targets.Where(t => !builtin.Contains(t.Name))
                .Select(t => new CustomTarget(t.Name, t.SwaggerUrl)).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_customFile)!);
            File.WriteAllText(_customFile, JsonSerializer.Serialize(customs, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* не критично */ }
    }
}
