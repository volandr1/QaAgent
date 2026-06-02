using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using QaAgent.Core;

namespace QaAgent.Llm;

/// <summary>
/// Тонка обгортка над Semantic Kernel для простих chat-запитів до Ollama.
/// </summary>
public sealed class LlmClient
{
    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chat;
    private readonly OllamaOptions _options;

    public LlmClient(OllamaOptions options)
    {
        _options = options;
        _kernel = KernelFactory.Create(options);
        _chat = _kernel.GetRequiredService<IChatCompletionService>();
    }

    /// <summary>
    /// Надсилає system+user промпт і повертає текст відповіді моделі.
    /// </summary>
    public async Task<string> AskAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var history = new ChatHistory();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(userPrompt);

        var settings = new OllamaPromptExecutionSettings
        {
            Temperature = _options.Temperature
        };

        var result = await _chat.GetChatMessageContentAsync(history, settings, _kernel, ct);
        return result.Content ?? string.Empty;
    }
}
