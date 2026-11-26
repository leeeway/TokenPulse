import AppKit
import SwiftUI
import TokenPulseCore

enum QuotaIndicator: Equatable {
    case ample(Double), watch(Double), low(Double), exhausted, awaitingUpdate, unknown

    @MainActor static let menuBarWarningImage: NSImage? = {
        let configuration = NSImage.SymbolConfiguration(pointSize: 11, weight: .medium)
            .applying(.init(paletteColors: [.systemRed]))
        let image = NSImage(systemSymbolName: "exclamationmark.circle.fill", accessibilityDescription: nil)?
            .withSymbolConfiguration(configuration)
        image?.isTemplate = false
        return image
    }()

    init(usedPercent: Double?, expired: Bool) {
        if expired { self = .awaitingUpdate; return }
        guard let usedPercent, (0...100).contains(usedPercent) else { self = .unknown; return }
        let remaining = 100 - usedPercent
        switch remaining {
        case 0: self = .exhausted
        case ...20: self = .low(remaining)
        case ...50: self = .watch(remaining)
        default: self = .ample(remaining)
        }
    }

    var remainingPercent: Double? {
        switch self {
        case let .ample(value), let .watch(value), let .low(value): value
        case .exhausted: 0
        case .awaitingUpdate, .unknown: nil
        }
    }

    var percentText: String? {
        remainingPercent.map { value in
            if value > 0 && value < 0.1 { return "<0.1%" }
            if value > 20 && value <= 20.05 { return ">20%" }
            return (value / 100).formatted(.percent.precision(.fractionLength(0...1)))
        }
    }

    var status: String {
        switch self {
        case .ample: "充足"
        case .watch: "需留意"
        case .low: "余量偏低"
        case .exhausted: "已用尽"
        case .awaitingUpdate: "待更新"
        case .unknown: "余量未知"
        }
    }

    var color: Color {
        switch self {
        case .ample: .green
        case .watch: .orange
        case .low, .exhausted: .red
        case .awaitingUpdate, .unknown: .gray
        }
    }

    static func summary(of observation: QuotaObservation, at now: Date) -> String {
        let indicator = Self(usedPercent: observation.usedPercent, expired: observation.isExpired(at: now))
        let remaining = indicator.percentText.map { "剩余 \($0) · \(indicator.status)" } ?? indicator.status
        return "Codex \(TokenFormatters.quotaWindow(observation.windowMinutes)) \(remaining) · "
            + TokenFormatters.quotaReset(observation.resetsAt, now: now)
    }

    var dot: some View {
        Circle().fill(color).frame(width: 6, height: 6).accessibilityHidden(true)
    }
}
