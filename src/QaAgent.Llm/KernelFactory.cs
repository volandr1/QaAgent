using Microsoft.SemanticKernel;
using QaAgent.Core;

namespace QaAgent.Llm;

/// <summary>
/// Створює налаштований Semantic Kernel із підключенням до локальної Ollama.
/// </summary>
public static class KernelFactory
{
    public static Kernel Create(OllamaOptions options)
    {
        var builder = Kernel.CreateBuilder();

        builder.AddOllamaChatCompletion(
            modelId: options.Model,
            endpoint: new Uri(options.Endpoint));

        return builder.Build();
    }
}
