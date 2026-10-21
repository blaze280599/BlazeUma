using System.Text;
using BlazeUma.Advisor;

namespace BlazeUma.Cli;

/// <summary>
/// Renders advice for a terminal: status header, the recommendation, a ranked list with reasons for the top
/// pick only (all of them in verbose mode), and labelled notes. Colors are used only on an interactive console.
/// </summary>
internal sealed class AdviceFormatter(bool verbose, bool color, int width)
{
    private const int MaxOptions = 8;

    public static AdviceFormatter ForConsole(bool verbose)
    {
        bool interactive = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        int width = 100;
        try { if (!Console.IsOutputRedirected) width = Math.Clamp(Console.WindowWidth - 1, 60, 160); } catch (IOException) { }
        return new AdviceFormatter(verbose, interactive, width);
    }

    private string Paint(string text, string code) => color ? $"\u001b[{code}m{text}\u001b[0m" : text;
    private string Bold(string t) => Paint(t, "1");
    private string Dim(string t) => Paint(t, "2");
    private string Green(string t) => Paint(t, "1;32");
    private string Cyan(string t) => Paint(t, "36");
    private string Yellow(string t) => Paint(t, "33");

    private string Fit(string text, int indent) => text.Length + indent <= width ? text : text[..Math.Max(0, width - indent - 1)] + "…";

    public string Footer(string text) => Dim(" " + Fit(text, 1));

    /// <summary>A status panel for when there is no advice: game not running, no career, unreadable state.</summary>
    public string Status(string title, string detail, bool problem = false, string? hint = null)
    {
        string rule = new('─', Math.Min(width, 90));
        string heading = " ● " + title;
        var lines = new List<string>
        {
            Dim(rule),
            problem ? Paint(heading, "1;31") : Paint(heading, "1;33"),
            "   " + Fit(detail, 3),
            Dim(rule),
        };
        if (hint is not null) lines.Add(Dim($" {DateTime.Now:HH:mm:ss}  {hint}"));
        return string.Join(Environment.NewLine, lines);
    }

    public string Format(Advice advice, DateTimeOffset readAt)
    {
        var sb = new StringBuilder();
        string rule = new('─', Math.Min(width, 90));
        sb.AppendLine(Dim(rule));
        sb.AppendLine(" " + Cyan(Fit(advice.Header, 1)) + Dim($"   {readAt:HH:mm:ss}"));
        sb.AppendLine(" " + (advice.Situation == Situation.NoCareer ? Yellow("● " + advice.Summary) : Green("▶ " + advice.Summary)));
        sb.AppendLine(Dim(rule));

        int labelWidth = advice.Options.Take(MaxOptions).Select(o => o.Label.Length).DefaultIfEmpty(10).Max() + 2;
        int shown = 0;
        foreach (var option in advice.Options.Take(MaxOptions))
        {
            bool top = shown++ == 0;
            string line = $" {shown,2}. {option.Label.PadRight(labelWidth)}{option.Score,7:0.0}";
            sb.AppendLine(top ? Bold(line) : line);
            if (top || verbose)
            {
                foreach (string reason in option.Reasons)
                    sb.AppendLine(Dim("       • ") + Fit(reason, 9));
            }
        }
        if (advice.Options.Count > MaxOptions)
            sb.AppendLine(Dim($"     … {advice.Options.Count - MaxOptions} more"));

        if (advice.Notes.Count > 0)
        {
            if (advice.Options.Count > 0) sb.AppendLine();
            int noteWidth = advice.Notes.Max(n => n.Label.Length) + 2;
            foreach (var note in advice.Notes)
            {
                string label = note.Label.PadRight(noteWidth);
                sb.AppendLine(" " + (note.Label is "Read error" or "Warning" ? Yellow(label) : Bold(label)) + Fit(note.Text, noteWidth + 1));
            }
        }
        if (verbose)
            foreach (string detail in advice.Details)
                sb.AppendLine(Dim(" " + detail));
        return sb.ToString().TrimEnd();
    }
}
