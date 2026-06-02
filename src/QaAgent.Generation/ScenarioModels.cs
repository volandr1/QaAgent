using System.Text.Json;

namespace QaAgent.Generation;

/// <summary>
/// Один тест-сценарій — це ДАНІ, які LLM генерує у JSON.
/// C#-код тесту збирається з перевіреного шаблону (renderer), а не самою моделлю,
/// щоб слабша 7b-модель не ламала синтаксис.
/// </summary>
public sealed class TestScenario
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";
    public string Path { get; set; } = string.Empty;
    public Dictionary<string, string> PathParams { get; set; } = new();
    public Dictionary<string, string> QueryParams { get; set; } = new();
    public JsonElement? Body { get; set; }
    public int ExpectedStatus { get; set; } = 200;

    /// <summary>positive | negative | auth | boundary</summary>
    public string Type { get; set; } = "positive";

    /// <summary>Який токен підставити в Authorization: none | client | admin</summary>
    public string Auth { get; set; } = "none";

    /// <summary>Асертити діапазон 4xx (negative — точний код валідації невідомий).</summary>
    public bool ClientErrorRange { get; set; }

    /// <summary>Асертити діапазон 2xx (positive-create — 200/201 залежить від API).</summary>
    public bool SuccessRange { get; set; }

    /// <summary>Асертити «не 5xx» (smoke — ендпоінт існує і не падає).</summary>
    public bool NoServerError { get; set; }
}

public sealed class ScenarioSet
{
    public List<TestScenario> Scenarios { get; set; } = new();
}
