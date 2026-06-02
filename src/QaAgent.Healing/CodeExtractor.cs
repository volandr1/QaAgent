namespace QaAgent.Healing;

/// <summary>Прибирає markdown-огорожі з відповіді LLM, лишаючи чистий C#-код.</summary>
public static class CodeExtractor
{
    public static string Extract(string raw)
    {
        var text = raw.Trim();

        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            var fence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) text = text[..fence];
        }

        return text.Trim() + "\n";
    }
}
