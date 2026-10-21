namespace TokenPulse.Core;

/// <summary>Compatibility rates copied from the Mac implementation, not a live price feed.</summary>
public static class Pricing
{
    public const string CatalogVersion = "compat-v1";
    public const string EstimateNotice = "内置兼容估算 compat-v1，非实时官方报价或实际账单。自定义记录可自带金额。";
    private static readonly HashSet<string> CatalogModels = new(StringComparer.OrdinalIgnoreCase)
    { "claude-opus", "claude-haiku", "claude-sonnet", "o3", "gpt-5", "gpt-5-codex", "gemini-flash", "gemini-pro" };
    public static string Basis(string provider, string model)
    {
        if (provider == Providers.Custom) return "自定义记录金额优先；未提供时使用兼容估算 · " + CatalogVersion;
        if (provider == "ollama") return "本地模型 API 费用按 0 估算，不包含硬件或电费";
        var lower = model.ToLowerInvariant();
        var matchingProvider = provider == Providers.Claude && lower.StartsWith("claude-")
            || provider == Providers.Codex && (lower.StartsWith("gpt-") || lower == "o3")
            || provider == Providers.Antigravity && lower.StartsWith("gemini-");
        var match = matchingProvider && CatalogModels.Contains(model) ? "内置模型条目" : "未精确匹配模型，使用兼容回退";
        var r = Rate(provider, model);
        return FormattableString.Invariant($"{model}：{match} · {CatalogVersion}\n每百万 Token（USD）：输入 {r.Input}，缓存写入 {r.Write}，缓存读取 {r.Read}，输出 {r.Output}\n非实时官方报价或实际账单");
    }

    private static (double Input, double Write, double Read, double Output) Rate(string provider, string model)
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
        return rate;
    }
    public static (double Cost, double Saved) Estimate(string provider, string model, long input, long write, long read, long output)
    {
        var rate = Rate(provider, model);
        return ((input * rate.Input + write * rate.Write + read * rate.Read + output * rate.Output) / 1e6,
            Math.Max(0, read * (rate.Input - rate.Read) / 1e6));
    }
}
