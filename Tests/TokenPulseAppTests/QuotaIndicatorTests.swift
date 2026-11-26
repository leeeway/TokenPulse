import AppKit
import Testing
@testable import TokenPulseApp

@Test func quotaIndicatorUsesRemainingPercentageAndIgnoresExpiredOrInvalidValues() {
    #expect(QuotaIndicator(usedPercent: 0, expired: false) == .ample(100))
    #expect(QuotaIndicator(usedPercent: 49, expired: false) == .ample(51))
    #expect(QuotaIndicator(usedPercent: 50, expired: false) == .watch(50))
    #expect(QuotaIndicator(usedPercent: 79, expired: false) == .watch(21))
    #expect(QuotaIndicator(usedPercent: 79.99, expired: false).percentText == ">20%")
    #expect(QuotaIndicator(usedPercent: 80, expired: false) == .low(20))
    #expect(QuotaIndicator(usedPercent: 100, expired: false) == .exhausted)
    #expect(QuotaIndicator(usedPercent: 99.999, expired: false).percentText == "<0.1%")
    #expect(QuotaIndicator(usedPercent: 5, expired: true) == .awaitingUpdate)
    #expect(QuotaIndicator(usedPercent: nil, expired: false) == .unknown)
    #expect(QuotaIndicator(usedPercent: .nan, expired: false) == .unknown)
    #expect(QuotaIndicator(usedPercent: 101, expired: false) == .unknown)
}

@Test @MainActor func menuBarQuotaWarningSymbolRendersRed() throws {
    let image = try #require(QuotaIndicator.menuBarWarningImage)
    #expect(!image.isTemplate)
    let rendered = NSImage(size: image.size, flipped: false) { rect in
        image.draw(in: rect)
        return true
    }
    let data = try #require(rendered.tiffRepresentation)
    let bitmap = try #require(NSBitmapImageRep(data: data))
    let hasRed = (0..<bitmap.pixelsHigh).contains { y in
        (0..<bitmap.pixelsWide).contains { x in
            guard let color = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB) else { return false }
            return color.alphaComponent > 0.5
                && color.redComponent - color.greenComponent > 0.2
                && color.redComponent - color.blueComponent > 0.2
        }
    }
    #expect(hasRed)
}
