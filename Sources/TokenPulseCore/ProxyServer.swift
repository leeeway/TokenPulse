import Foundation
import Network

/// Lightweight local HTTP server on `127.0.0.1:11435` for real-time telemetry ingestion (`/v1/ingest`)
/// and live status querying (`/v1/stats`, `/v1/health`).
public final class LocalGatewayServer: @unchecked Sendable {
    public let port: UInt16
    private let collector: UsageCollector
    private let onEventIngested: (@Sendable () -> Void)?
    private var listener: NWListener?
    private let queue = DispatchQueue(label: "io.tokenpulse.gateway", qos: .utility)
    private(set) public var isRunning: Bool = false

    public init(
        port: UInt16 = 11435,
        collector: UsageCollector,
        onEventIngested: (@Sendable () -> Void)? = nil
    ) {
        self.port = port
        self.collector = collector
        self.onEventIngested = onEventIngested
    }

    public func start() {
        guard listener == nil else { return }
        guard let nwPort = NWEndpoint.Port(rawValue: port) else { return }

        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        params.requiredLocalEndpoint = NWEndpoint.hostPort(host: .ipv4(.loopback), port: nwPort)

        guard let newListener = try? NWListener(using: params) else { return }
        newListener.stateUpdateHandler = { [weak self] state in
            switch state {
            case .ready:
                self?.isRunning = true
            case .failed, .cancelled:
                self?.isRunning = false
            default:
                break
            }
        }
        newListener.newConnectionHandler = { [weak self] conn in
            self?.handleConnection(conn)
        }
        newListener.start(queue: queue)
        self.listener = newListener
    }

    public func stop() {
        listener?.cancel()
        listener = nil
        isRunning = false
    }

    private func handleConnection(_ connection: NWConnection) {
        connection.start(queue: queue)
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, _, _ in
            guard let self, let data, let rawRequest = String(data: data, encoding: .utf8) else {
                connection.cancel()
                return
            }
            let responseData = self.routeHTTP(rawRequest: rawRequest)
            connection.send(content: responseData, completion: .contentProcessed { _ in
                connection.cancel()
            })
        }
    }

    private func routeHTTP(rawRequest: String) -> Data {
        let parts = rawRequest.components(separatedBy: "\r\n\r\n")
        let headerPart = parts.first ?? ""
        let bodyPart = parts.dropFirst().joined(separator: "\r\n\r\n")
        let firstLine = headerPart.components(separatedBy: "\r\n").first ?? ""
        let tokens = firstLine.split(separator: " ")
        guard tokens.count >= 2 else {
            return Self.httpResponse(status: "400 Bad Request", json: "{\"error\":\"bad_request\"}")
        }

        let method = String(tokens[0]).uppercased()
        let path = String(tokens[1])

        // Block cross-origin browser requests (CSRF / cross-site local port probing protection)
        let lowerHeaders = headerPart.lowercased()
        if lowerHeaders.contains("\r\norigin:") || lowerHeaders.contains("sec-fetch-site: cross-site") {
            return Self.httpResponse(status: "403 Forbidden", json: "{\"error\":\"cross_origin_forbidden\"}")
        }

        if method == "GET" && path.hasPrefix("/v1/health") {
            return Self.httpResponse(
                status: "200 OK",
                json: "{\"status\":\"ok\",\"service\":\"TokenPulse\",\"port\":\(port)}"
            )
        }

        if method == "GET" && path.hasPrefix("/v1/stats") {
            let snapshot = collector.snapshotFromCache(window: .today)
            let encoder = JSONEncoder()
            encoder.dateEncodingStrategy = .iso8601
            if let data = try? encoder.encode(snapshot),
               let jsonStr = String(data: data, encoding: .utf8) {
                return Self.httpResponse(status: "200 OK", json: jsonStr)
            }
            return Self.httpResponse(status: "500 Internal Server Error", json: "{\"error\":\"encode_failed\"}")
        }

        if method == "POST" && path.hasPrefix("/v1/ingest") {
            guard let bodyData = bodyPart.data(using: .utf8),
                  let json = try? JSONSerialization.jsonObject(with: bodyData) as? [String: Any] else {
                return Self.httpResponse(status: "400 Bad Request", json: "{\"error\":\"invalid_json\"}")
            }

            let providerRaw = Self.sanitizeIdentifier((json["provider"] as? String) ?? "proxy_gateway", maxLength: 64)
            let provider = AIProvider(rawValue: providerRaw) ?? .proxyGateway
            let model = Self.sanitizeIdentifier((json["model"] as? String) ?? "custom-model", maxLength: 128)
            let sessionId = Self.sanitizeIdentifier((json["session_id"] as? String) ?? "gateway", maxLength: 128)
            let maxAllowedTokens = 100_000_000
            let input = min(max(0, (json["input_tokens"] as? Int) ?? 0), maxAllowedTokens)
            let cacheWrite = min(max(0, (json["cache_write_tokens"] as? Int) ?? 0), maxAllowedTokens)
            let cacheRead = min(max(0, (json["cache_read_tokens"] as? Int) ?? 0), maxAllowedTokens)
            let output = min(max(0, (json["output_tokens"] as? Int) ?? 0), maxAllowedTokens)
            let reasoning = min(max(0, (json["reasoning_tokens"] as? Int) ?? 0), maxAllowedTokens)

            let event = TokenUsageEvent(
                id: "gw-\(UUID().uuidString)",
                provider: provider,
                model: model,
                timestamp: Date(),
                sessionId: sessionId,
                inputTokens: input,
                cacheWriteTokens: cacheWrite,
                cacheReadTokens: cacheRead,
                outputTokens: output,
                reasoningTokens: reasoning
            )
            collector.recordCustomEvent(event)
            onEventIngested?()

            return Self.httpResponse(
                status: "200 OK",
                json: "{\"status\":\"recorded\",\"id\":\"\(event.id)\",\"total_tokens\":\(event.totalTokens)}"
            )
        }

        return Self.httpResponse(status: "404 Not Found", json: "{\"error\":\"not_found\"}")
    }

    private static func sanitizeIdentifier(_ raw: String, maxLength: Int) -> String {
        let filtered = raw.unicodeScalars.filter { scalar in
            scalar.value >= 0x20 && scalar.value != 0x7F
        }
        let clean = String(String.UnicodeScalarView(filtered)).trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty else { return "unknown" }
        return String(clean.prefix(maxLength))
    }

    private static func httpResponse(status: String, json: String) -> Data {
        let utf8 = json.data(using: .utf8) ?? Data()
        let headers = [
            "HTTP/1.1 \(status)",
            "Content-Type: application/json; charset=utf-8",
            "Content-Length: \(utf8.count)",
            "X-Content-Type-Options: nosniff",
            "Connection: close",
            "",
            ""
        ].joined(separator: "\r\n")
        var data = headers.data(using: .utf8) ?? Data()
        data.append(utf8)
        return data
    }
}
