using System.Globalization;

namespace TokenPulse.Core;

/// <summary>A model summary export; never includes prompts, session IDs or local file paths.</summary>
public static class UsageCsv
{
    public static void Write(TextWriter writer, DashboardSnapshot snapshot, TimeWindow window)
    {
        writer.WriteLine("Period,GeneratedAt,Provider,Model,InputTokens,CacheWriteTokens,CacheReadTokens,OutputTokens,ReasoningTokens,TotalTokens,CacheHitRatio,EstimatedUSD,CacheSavedUSD,Sessions,Events");
        foreach (var model in snapshot.Providers.SelectMany(p => p.Models))
        {
            var t = model.Totals;
            string Number(IFormattable value) => value.ToString(null, CultureInfo.InvariantCulture);
            writer.WriteLine(string.Join(",", new[] { Cell(window.ToString()), Cell(snapshot.GeneratedAt.ToString("O")), Cell(Providers.Name(model.Provider)), Cell(model.Model),
                Number(t.Input), Number(t.CacheWrite), Number(t.CacheRead), Number(t.Output), Number(t.Reasoning), Number(t.Total),
                Number(t.CacheHitRatio), Number(t.Cost), Number(t.Saved), Number(t.Sessions), Number(t.Events) }));
        }
    }

    private static string Cell(string value)
    {
        // Quoting alone does not prevent spreadsheet formula execution. Model names
        // can come from custom logs; keep such names literal when opened in Excel.
        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@' || value.Any(c => c is '\t' or '\r' or '\n')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
