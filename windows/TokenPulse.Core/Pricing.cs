namespace TokenPulse.Core;

/// <summary>Compatibility rates copied from the Mac implementation, not a live price feed.</summary>
public static class Pricing
{
    public static (double Cost, double Saved) Estimate(string provider, string model, long input, long write, long read, long output)
    {
        var name = model.ToLowerInvariant();
        (double Input, double Write, double Read, double Output) rate = provider == "ollama" ? (0, 0, 0, 0)
            : name.Contains("opus") ? (15, 18.75, 1.5, 75)
            : name.Contains("haiku") ? (.8, 1, .08, 4)
            : name.Contains("sonnet") || provider == Providers.Claude ? (3, 3.75, .3, 15)
            : name.Contains("o3") || name.Contains("gpt-5") || name.Contains("codex") || provider == Providers.Codex ? (2, 2, .5, 8)
            : name.Contains("flash") ? (.3, .3, .075, 2.5)
            : name.Contains("pro") || name.Contains("gemini") || provider == Providers.Antigravity ? (1.25, 1.25, .31, 10)
            : (2.5, 2.5, .5, 10);
        return ((input * rate.Input + write * rate.Write + read * rate.Read + output * rate.Output) / 1e6,
            Math.Max(0, read * (rate.Input - rate.Read) / 1e6));
    }
}
