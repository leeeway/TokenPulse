import SwiftUI

enum QuotaIndicator: Equatable {
    case ample(Double), watch(Double), low(Double), exhausted, awaitingUpdate, unknown

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

    var dot: some View {
        Circle().fill(color).frame(width: 6, height: 6).accessibilityHidden(true)
    }
}
