using System.Globalization;

namespace TokenPulse.Windows;

internal readonly record struct QuotaIndicator(double? RemainingPercent, string Status, Brush Color)
{
    private static readonly Brush Orange = Ui.Brush("#C87016"), Red = Ui.Brush("#CB3746");

    public static QuotaIndicator FromUsage(double? usedPercent, bool expired = false)
    {
        if (expired) return new(null, "待更新", Ui.Muted);
        if (usedPercent is not { } used || !double.IsFinite(used) || used is < 0 or > 100)
            return new(null, "余量未知", Ui.Muted);

        var remaining = 100 - used;
        return remaining switch
        {
            0 => new(0, "已用尽", Red),
            <= 20 => new(remaining, "余量偏低", Red),
            <= 50 => new(remaining, "需留意", Orange),
            _ => new(remaining, "充足", Ui.Green)
        };
    }

    public string? PercentText => RemainingPercent switch
    {
        > 0 and < 0.1 => "<0.1%",
        { } number => number.ToString("0.#", CultureInfo.CurrentCulture) + "%",
        null => null
    };

    public StackPanel StatusLabel(string text)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new Border { Name = "QuotaStatusDot", Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
            Background = Color, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(Ui.Text(text, 10, Ui.Ink, FontWeights.Medium));
        return label;
    }
}
