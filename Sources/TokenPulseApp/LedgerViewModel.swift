import AppKit
import Combine
import Foundation
import TokenPulseCore

private actor LedgerCoordinator {
    private var store: CodexLedgerStore?
    private let directory: URL?
    private var input = CodexLedgerInput.empty()
    private var sequence = 0

    init(directory: URL?) { self.directory = directory }
    private func storage() -> CodexLedgerStore {
        if let store { return store }
        let created = CodexLedgerStore(directory: directory)
        store = created
        return created
    }
    func update(_ input: CodexLedgerInput, sequence: Int, now: Date) -> LedgerViewState? {
        guard sequence >= self.sequence else { return nil }
        self.sequence = sequence
        self.input = input
        return storage().update(input: input, now: now, timeZone: .current)
    }
    func clear(sequence: Int, now: Date) -> LedgerViewState? {
        guard sequence >= self.sequence else { return nil }
        self.sequence = sequence
        return storage().clearHistory(now: now)
    }
    func rebuild(sequence: Int, now: Date) -> LedgerViewState? {
        guard sequence >= self.sequence else { return nil }
        self.sequence = sequence
        return storage().rebuild(input: input, now: now, timeZone: .current)
    }
    func custom(start: Date, end: Date, now: Date) throws -> UsageReportSnapshot {
        try CodexLedgerEngine.customReport(input: input, start: start, end: end, now: now, timeZone: .current)
    }
}

@MainActor
final class LedgerViewModel: ObservableObject {
    @Published private(set) var state = LedgerViewState()
    @Published private(set) var customReport: UsageReportSnapshot?
    @Published private(set) var isLoading = true
    @Published private(set) var isWorking = false
    @Published private(set) var isExporting = false
    @Published var message: String?
    private let coordinator: LedgerCoordinator
    private let clock: @Sendable () -> Date
    private var revision = 0
    private var customRevision = 0

    init(directory: URL? = nil, clock: @escaping @Sendable () -> Date = { Date() }) {
        coordinator = LedgerCoordinator(directory: directory)
        self.clock = clock
    }

    func receive(_ input: CodexLedgerInput) {
        revision += 1
        let expected = revision
        let now = clock()
        Task {
            let updated = await coordinator.update(input, sequence: expected, now: now)
            guard expected == revision, let updated else { return }
            state = updated
            isLoading = false
        }
    }

    func clearHistory() { mutate(rebuilding: false) }
    func rebuild() { mutate(rebuilding: true) }
    private func mutate(rebuilding: Bool) {
        guard !isWorking else { return }
        revision += 1
        let expected = revision
        let now = clock()
        isWorking = true
        message = nil
        Task {
            let updated = rebuilding
                ? await coordinator.rebuild(sequence: expected, now: now)
                : await coordinator.clear(sequence: expected, now: now)
            if expected == revision, let updated {
                state = updated
                message = updated.warning == nil
                    ? (rebuilding ? "已从现存原生日志重建账本。" : "已清空历史归档，保留当前周期和原始日志。")
                    : nil
            }
            isWorking = false
        }
    }

    func generateCustom(start: Date, end: Date) {
        customRevision += 1
        let expected = customRevision
        let now = clock()
        message = nil
        Task {
            do {
                let report = try await coordinator.custom(start: start, end: end, now: now)
                guard expected == customRevision else { return }
                customReport = report
            } catch {
                guard expected == customRevision else { return }
                customReport = nil
                message = error.localizedDescription
            }
        }
    }

    func export(_ report: UsageReportSnapshot) {
        guard !isExporting else { return }
        let panel = NSOpenPanel()
        panel.title = "选择周报保存位置"
        panel.message = "将在所选目录中新建报告文件夹，包含离线 HTML 和 CSV，不覆盖已有报告。"
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let directory = panel.url else { return }
        isExporting = true
        message = nil
        Task {
            do {
                let folder = try await Task.detached(priority: .utility) {
                    try ReportExporter.export(report: report, to: directory)
                }.value
                message = "已导出冻结报告。可用浏览器打开 report.html 并打印为 PDF。"
                NSWorkspace.shared.activateFileViewerSelecting([folder])
            } catch { message = "导出失败：" + error.localizedDescription }
            isExporting = false
        }
    }
}
