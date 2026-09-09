// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "ActionsRing",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "ActionsRingKit", targets: ["ActionsRingKit"]),
        .executable(name: "ActionsRing", targets: ["ActionsRing"]),
    ],
    targets: [
        .target(name: "ActionsRingKit"),
        .executableTarget(name: "ActionsRing", dependencies: ["ActionsRingKit"]),
        .testTarget(name: "ActionsRingKitTests", dependencies: ["ActionsRingKit"]),
    ]
)
