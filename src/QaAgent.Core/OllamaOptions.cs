namespace QaAgent.Core;

/// <summary>
/// Налаштування підключення до локальної Ollama та параметри генерації.
/// </summary>
public sealed class OllamaOptions
{
    /// <summary>Базовий URL Ollama API (env QA_OLLAMA_ENDPOINT).</summary>
    public string Endpoint { get; set; } =
        Environment.GetEnvironmentVariable("QA_OLLAMA_ENDPOINT") ?? "http://localhost:11434";

    /// <summary>Ідентифікатор моделі (env QA_OLLAMA_MODEL; як у `ollama list`).</summary>
    public string Model { get; set; } =
        Environment.GetEnvironmentVariable("QA_OLLAMA_MODEL") ?? "deepseek-coder-v2:latest";

    /// <summary>Низька температура для стабільної генерації коду.</summary>
    public float Temperature { get; set; } = 0.1f;
}
