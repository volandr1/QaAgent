using System.Net;
using System.Net.Mail;
using QaAgent.Core;

namespace QaAgent.Reporting;

/// <summary>Записує Markdown + HTML звіти у теку (та latest.*).</summary>
public sealed class FileReporter
{
    private readonly string _dir;
    public FileReporter(string reportsDir) => _dir = reportsDir;

    public (string Markdown, string Html) Write(RunReport report)
    {
        Directory.CreateDirectory(_dir);
        var stamp = report.GeneratedAt.ToString("yyyyMMdd-HHmmss");

        var md = ReportRenderer.ToMarkdown(report);
        var html = ReportRenderer.ToHtml(report);

        var mdPath = Path.Combine(_dir, $"report-{stamp}.md");
        var htmlPath = Path.Combine(_dir, $"report-{stamp}.html");
        File.WriteAllText(mdPath, md);
        File.WriteAllText(htmlPath, html);
        File.WriteAllText(Path.Combine(_dir, "latest.md"), md);
        File.WriteAllText(Path.Combine(_dir, "latest.html"), html);

        // Структурований звіт для History/Compare/Detail.
        ReportStore.Save(_dir, report, stamp);

        return (mdPath, htmlPath);
    }
}

/// <summary>Надсилає короткий звіт у Telegram (Bot API). Конфіг — лише через env.</summary>
public sealed class TelegramReporter
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static bool IsConfigured =>
        Env("TELEGRAM_BOT_TOKEN") is not null && Env("TELEGRAM_CHAT_ID") is not null;

    public async Task<bool> SendAsync(string text, CancellationToken ct = default)
    {
        var token = Env("TELEGRAM_BOT_TOKEN");
        var chatId = Env("TELEGRAM_CHAT_ID");
        if (token is null || chatId is null) return false;

        var url = $"https://api.telegram.org/bot{token}/sendMessage";
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["chat_id"] = chatId,
            ["text"] = text
        });

        var resp = await Http.PostAsync(url, form, ct);
        return resp.IsSuccessStatusCode;
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);
}

/// <summary>Надсилає HTML-звіт поштою через SMTP. Конфіг — лише через env.</summary>
public sealed class EmailReporter
{
    public static bool IsConfigured =>
        Env("QA_SMTP_HOST") is not null && Env("QA_MAIL_TO") is not null;

    public async Task<bool> SendAsync(string subject, string htmlBody, CancellationToken ct = default)
    {
        var host = Env("QA_SMTP_HOST");
        var to = Env("QA_MAIL_TO");
        if (host is null || to is null) return false;

        var port = int.TryParse(Env("QA_SMTP_PORT"), out var p) ? p : 587;
        var user = Env("QA_SMTP_USER");
        var pass = Env("QA_SMTP_PASS");
        var from = Env("QA_MAIL_FROM") ?? user ?? "qa-agent@example.com";

        using var message = new MailMessage(from, to, subject, htmlBody) { IsBodyHtml = true };
        using var client = new SmtpClient(host, port) { EnableSsl = true };
        if (user is not null) client.Credentials = new NetworkCredential(user, pass);

        await client.SendMailAsync(message, ct);
        return true;
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);
}

/// <summary>Зчіплює всі канали: файл завжди, Telegram/Email — якщо налаштовані через env.</summary>
public sealed class ReportDispatcher
{
    private readonly FileReporter _file;

    public ReportDispatcher(string reportsDir) => _file = new FileReporter(reportsDir);

    public async Task<List<string>> DispatchAsync(RunReport report, CancellationToken ct = default)
    {
        var log = new List<string>();

        var (mdPath, _) = _file.Write(report);
        log.Add($"📄 Файл: {mdPath}");

        if (TelegramReporter.IsConfigured)
        {
            var ok = await new TelegramReporter().SendAsync(ReportRenderer.ToShortText(report), ct);
            log.Add(ok ? "📨 Telegram: надіслано" : "📨 Telegram: помилка надсилання");
        }
        else log.Add("📨 Telegram: пропущено (немає TELEGRAM_BOT_TOKEN/CHAT_ID)");

        if (EmailReporter.IsConfigured)
        {
            var subject = $"[QA-агент] {(report.Success ? "PASS" : "FAIL")} — {report.ApiTitle}";
            var ok = await new EmailReporter().SendAsync(subject, ReportRenderer.ToHtml(report), ct);
            log.Add(ok ? "✉️ Email: надіслано" : "✉️ Email: помилка надсилання");
        }
        else log.Add("✉️ Email: пропущено (немає QA_SMTP_HOST/QA_MAIL_TO)");

        return log;
    }
}
