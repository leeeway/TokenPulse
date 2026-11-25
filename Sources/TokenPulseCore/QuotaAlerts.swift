import Foundation

/// A short and a weekly window can share the same reset time. The duration
/// distinguishes them, while the reset time identifies a new cycle.
public struct QuotaAlerts: Codable, Sendable {
    private var notified: [String: Date] = [:]

    public init() {}

    public mutating func claim(_ observations: [QuotaObservation], at now: Date) -> [QuotaObservation] {
        notified = notified.filter { $0.value > now.addingTimeInterval(-7 * 86400) }
        return observations.filter { observation in
            guard observation.isValid, observation.observedAt <= now,
                  !observation.isExpired(at: now), observation.isLow else { return false }
            let key = "\(observation.windowMinutes)/\(observation.resetsAt.timeIntervalSince1970)"
            guard notified[key] == nil else { return false }
            notified[key] = observation.resetsAt
            return true
        }
    }
}
