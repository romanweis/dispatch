using System.Text;
using System.Text.RegularExpressions;
using Dispatch.Api.Config;
using Dispatch.Api.Data;

namespace Dispatch.Api.Services;

/// <summary>Simple `{{placeholder}}` replacement per docs/project-config.md. Unknown placeholders render as empty strings.</summary>
public static partial class PromptRenderer
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\}\}")]
    private static partial Regex Placeholder();

    public static string Render(
        string template,
        Ticket ticket,
        ProjectConfig project,
        IReadOnlyList<Question>? answers = null,
        IReadOnlyList<Question>? questions = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ticket.id"] = ticket.Id.ToString(),
            ["ticket.title"] = ticket.Title,
            ["ticket.body"] = ticket.Body,
            ["ticket.spec"] = ticket.Spec ?? "",
            ["ticket.slug"] = ticket.Slug ?? "",
            ["project.name"] = project.Name,
            ["project.workspace"] = project.Workspace,
            ["project.org"] = project.Org,
            ["answers"] = FormatAnswers(answers ?? []),
            ["questions"] = FormatQuestions(questions ?? []),
        };

        return Render(template, values);
    }

    public static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(template, m => values.GetValueOrDefault(m.Groups[1].Value, ""));

    public static string FormatAnswers(IReadOnlyList<Question> answered)
    {
        var sb = new StringBuilder();
        foreach (var q in answered)
        {
            if (sb.Length > 0)
            {
                sb.Append("\n\n");
            }

            sb.Append("Q: ").Append(q.Text.Trim()).Append("\nA: ").Append((q.Answer ?? "").Trim());
        }

        return sb.ToString();
    }

    public static string FormatQuestions(IReadOnlyList<Question> questions) =>
        string.Join("\n", questions.Select(q => "- " + q.Text.Trim()));
}
