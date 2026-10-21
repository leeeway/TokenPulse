// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "TokenPulse",
    platforms: [
        .macOS(.v14)
    ],
    products: [
        .library(name: "TokenPulseCore", targets: ["TokenPulseCore"]),
        .executable(name: "tokenpulse", targets: ["TokenPulseCLI"]),
        .executable(name: "TokenPulseApp", targets: ["TokenPulseApp"])
    ],
    targets: [
        .target(
            name: "TokenPulseCore",
            path: "Sources/TokenPulseCore"
        ),
        .executableTarget(
            name: "TokenPulseCLI",
            dependencies: ["TokenPulseCore"],
            path: "Sources/TokenPulseCLI"
        ),
        .executableTarget(
            name: "TokenPulseApp",
            dependencies: ["TokenPulseCore"],
            path: "Sources/TokenPulseApp"
        ),
        .testTarget(
            name: "TokenPulseCoreTests",
            dependencies: ["TokenPulseCore"],
            path: "Tests/TokenPulseCoreTests"
        ),
        .testTarget(
            name: "TokenPulseAppTests",
            dependencies: ["TokenPulseApp", "TokenPulseCore"],
            path: "Tests/TokenPulseAppTests"
        )
    ]
)
