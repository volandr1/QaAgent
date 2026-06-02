namespace QaAgent.Generation;

/// <summary>
/// Витягує JSON-обʼєкт із сирої відповіді LLM (прибирає markdown-огорожі, префікси тощо).
/// </summary>
public static class JsonExtractor
{
    public static string Extract(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "{}";

        var text = raw.Trim();

        // Прибрати ```json ... ``` огорожі, якщо модель їх додала.
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            var fence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) text = text[..fence];
            text = text.Trim();
        }

        // Взяти від першої '{' до останньої '}'.
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
            return text[start..(end + 1)];

        return text;
    }
}
