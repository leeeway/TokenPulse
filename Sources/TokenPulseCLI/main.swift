import Foundation
import TokenPulseCore

struct CLIOptions {
    var window: TimeWindow = .thirtyDays
    var jsonOutput: Bool = false
    var statusBarLine: Bool = false

    static func parse(args: [String]) -> CLIOptions {
        var opts = CLIOptions()
        var i = 1
        while i < args.count {
            let arg = args[i]
            switch arg {
            case "--json", "-j":
                opts.jsonOutput = true
            case "--status-bar", "-s":
                opts.statusBarLine = true
            case "--window", "-w":
                if i + 1 < args.count, let w = TimeWindow(rawValue: args[i + 1]) {
                    opts.window = w
                    i += 1
                }
            case "--today":
                opts.window = .today
            case "--7d":
                opts.window = .sevenDays
            case "--30d":
                opts.window = .thirtyDays
            case "--all":
                opts.window = .allTime
            default:
                break
            }
            i += 1
        }
        return opts
    }
}

let options = CLIOptions.parse(args: CommandLine.arguments)
let collector = UsageCollector()
let snapshot = collector.collectSnapshot(window: options.window)

if options.jsonOutput {
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
    encoder.dateEncodingStrategy = .iso8601
    if let data = try? encoder.encode(snapshot),
       let text = String(data: data, encoding: .utf8) {
        print(text)
    }
    exit(0)
}

if options.statusBarLine {
    let total = TokenFormatters.compactTokens(snapshot.grandTotalTokens)
    let up = TokenFormatters.compactTokens(snapshot.totalUpstreamTokens)
    let down = TokenFormatters.compactTokens(snapshot.totalDownstreamTokens)
    let cost = TokenFormatters.usd(snapshot.totalCostUSD)
    print("⚡ \(total) (↑\(up) ↓\(down)) · \(cost)")
    exit(0)
}

// Rich ANSI terminal dashboard
let bold = "\u{001B}[1m"
let dim = "\u{001B}[2m"
let cyan = "\u{001B}[36m"
let green = "\u{001B}[32m"
let yellow = "\u{001B}[33m"
let magenta = "\u{001B}[35m"
let reset = "\u{001B}[0m"

print("\(bold)\(cyan)╭──────────────────────────────────────────────────────────────────────────╮\(reset)")
print("\(bold)\(cyan)│\(reset)  ⚡ \(bold)TokenPulse\(reset) — macOS Local AI Token & Quota Monitor (\(snapshot.window.title))          \(bold)\(cyan)│\(reset)")
print("\(bold)\(cyan)╰──────────────────────────────────────────────────────────────────────────╯\(reset)")
print("")
print("  \(bold)总 Token 消耗:\(reset) \(bold)\(TokenFormatters.compactTokens(snapshot.grandTotalTokens))\(reset)   " +
      "\(cyan)↑ 上行 (Prompt+Cache): \(TokenFormatters.compactTokens(snapshot.totalUpstreamTokens))\(reset)   " +
      "\(magenta)↓ 下行 (Output): \(TokenFormatters.compactTokens(snapshot.totalDownstreamTokens))\(reset)")
print("  \(bold)缓存命中率:\(reset)   \(green)\(TokenFormatters.percent(snapshot.cacheHitRatio))\(reset) " +
      "\(dim)(Cache Read: \(TokenFormatters.compactTokens(snapshot.totalCacheReadTokens)) / 节省约 \(TokenFormatters.usd(snapshot.totalSavedByCacheUSD)))\(reset)")
print("  \(bold)折算 API 价值:\(reset) \(yellow)\(TokenFormatters.usd(snapshot.totalCostUSD))\(reset) " +
      "\(dim)(共 \(snapshot.totalSessions) 个会话, \(snapshot.totalEvents) 次调用)\(reset)")
print("")
print("\(bold)  AI 工具明细 (Provider Breakdown)\(reset)")
print("  ──────────────────────────────────────────────────────────────────────────")
let header = String(
    format: "  %-16@ %10@ %10@ %10@ %9@ %10@",
    "Provider" as NSString,
    "↑Upstream" as NSString,
    "↓Output" as NSString,
    "Total" as NSString,
    "CacheHit" as NSString,
    "API Value" as NSString
)
print("\(dim)\(header)\(reset)")

for p in snapshot.providers {
    let row = String(
        format: "  %-16@ %10@ %10@ %10@ %9@ %10@",
        p.provider.displayName as NSString,
        TokenFormatters.compactTokens(p.upstreamTokens) as NSString,
        TokenFormatters.compactTokens(p.downstreamTokens) as NSString,
        TokenFormatters.compactTokens(p.totalTokens) as NSString,
        TokenFormatters.percent(p.cacheHitRatio) as NSString,
        TokenFormatters.usd(p.costUSD) as NSString
    )
    print(row)
    if let rl = p.rateLimit {
        let secInfo = rl.secondaryUsedPercent != nil ? String(format: " | 7d Quota: %.0f%%", rl.secondaryUsedPercent!) : ""
        print("    \(dim)↳ Rate Limit (5h Window): \(String(format: "%.0f%%", rl.primaryUsedPercent))\(secInfo) · Plan: \(rl.planType ?? "pro")\(reset)")
    }
}
print("  ──────────────────────────────────────────────────────────────────────────")
print("  \(dim)提示: 使用 `tokenpulse --today` / `--7d` / `--30d` / `--all` 切换窗口，或 `--json` 导出 JSON\(reset)")
