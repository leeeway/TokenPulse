import Testing
@testable import TokenPulseApp

@Test func quotaIndicatorUsesRemainingPercentageAndIgnoresExpiredOrInvalidValues() {
    #expect(QuotaIndicator(usedPercent: 0, expired: false) == .ample(100))
    #expect(QuotaIndicator(usedPercent: 49, expired: false) == .ample(51))
    #expect(QuotaIndicator(usedPercent: 50, expired: false) == .watch(50))
    #expect(QuotaIndicator(usedPercent: 79, expired: false) == .watch(21))
    #expect(QuotaIndicator(usedPercent: 80, expired: false) == .low(20))
    #expect(QuotaIndicator(usedPercent: 100, expired: false) == .exhausted)
    #expect(QuotaIndicator(usedPercent: 99.999, expired: false).percentText == "<0.1%")
    #expect(QuotaIndicator(usedPercent: 5, expired: true) == .awaitingUpdate)
    #expect(QuotaIndicator(usedPercent: nil, expired: false) == .unknown)
    #expect(QuotaIndicator(usedPercent: .nan, expired: false) == .unknown)
    #expect(QuotaIndicator(usedPercent: 101, expired: false) == .unknown)
}
