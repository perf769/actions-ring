using System.Reflection;
using System.Runtime.InteropServices;

namespace ActionsRing.App.Services;

/// <summary>Builds an editable browser form; never sends an issue or attaches local data.</summary>
public static class BugReportService
{
    private const string IssueFormUrl = "https://github.com/perf769/actions-ring/issues/new";

    public static Uri CreateIssueUri() => CreateIssueUri(
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "Неизвестна",
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture);

    internal static Uri CreateIssueUri(string applicationVersion, string operatingSystem, Architecture architecture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatingSystem);
        var body = $"""
            ## Что произошло


            ## Как повторить

            1. Опишите шаги.

            ## Ожидаемое поведение


            ## Скриншоты

            При необходимости приложите скриншот, скрыв личные данные.

            ## Версия и система

            - Actions Ring: {SingleLine(applicationVersion, 128)}
            - ОС: {SingleLine(operatingSystem, 256)}
            - Архитектура: {architecture}
            """;
        return new Uri($"{IssueFormUrl}?template=bug_report.md&body={Uri.EscapeDataString(body)}");
    }

    private static string SingleLine(string value, int maximumLength) =>
        new(value.Trim().Take(maximumLength).Select(character => char.IsControl(character) ? ' ' : character).ToArray());
}
