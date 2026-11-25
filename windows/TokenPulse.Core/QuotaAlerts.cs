using System.Globalization;

namespace TokenPulse.Core;

/// <summary>One notification per window duration and reset, including across restarts.</summary>
public sealed class QuotaAlerts(Dictionary<string, DateTimeOffset>? notified = null)
{
    private readonly Dictionary<string, DateTimeOffset> cycles = notified ?? [];
    public IReadOnlyDictionary<string, DateTimeOffset> NotifiedCycles => cycles;

    public IReadOnlyList<QuotaObservation> Claim(IEnumerable<QuotaObservation> observations, DateTimeOffset now)
    {
        foreach (var key in cycles.Where(pair => pair.Value <= now.AddDays(-7)).Select(pair => pair.Key).ToArray())
            cycles.Remove(key);

        var alerts = new List<QuotaObservation>();
        foreach (var observation in observations)
        {
            if (!observation.IsValid || observation.RecordedAtUtc > now || observation.IsExpired(now) || !observation.IsLow)
                continue;
            var key = observation.WindowMinutes.ToString(CultureInfo.InvariantCulture)
                + "/" + observation.ResetsAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture);
            if (cycles.TryAdd(key, observation.ResetsAtUtc)) alerts.Add(observation);
        }
        return alerts;
    }
}
