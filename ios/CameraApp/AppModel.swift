import Darwin
import SwiftUI
import UIKit

/// Relie la caméra, le serveur et l'écran.
final class AppModel: ObservableObject {
    @Published var ipAddress = "—"
    @Published var isConnected = false
    @Published var peer = ""
    @Published var fps = 0
    @Published var cameraKind: CameraKind = .back
    @Published var torchOn = false
    @Published var zoom = 1.0
    @Published var resolution = "720p"
    @Published var quality = 0.7
    @Published var lastLog = ""
    @Published var blackScreen = false
    @Published var cameraAuthorized = true

    let camera = CameraManager()
    let server = StreamServer()

    private var started = false
    private var timer: Timer?
    private let deviceName = UIDevice.current.name
    private let systemVersion = UIDevice.current.systemVersion

    init() {
        let server = self.server
        AppLog.sink = { [weak self] text in
            server.send(WireProtocol.message(.log, Data(text.utf8)))
            DispatchQueue.main.async { self?.lastLog = text }
        }
        camera.shouldEncode = { server.wantsFrame() }
        camera.onFrame = { jpeg, timestamp in server.sendFrame(jpeg, timestamp: timestamp) }

        let name = deviceName
        let version = systemVersion
        server.helloProvider = {
            ["name": name, "system": "iOS \(version)", "protocol": WireProtocol.version,
             "app": Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "1.0"]
        }
        server.onCommand = { [weak self] command in
            DispatchQueue.main.async { self?.handle(command) }
        }
        server.onConnectionChange = { [weak self] connected, peer in
            DispatchQueue.main.async {
                guard let self = self else { return }
                self.isConnected = connected
                self.peer = peer
                if connected { self.sendStatus() }
            }
        }
    }

    func start() {
        guard !started else { return }
        started = true
        UIApplication.shared.isIdleTimerDisabled = true
        UIDevice.current.isBatteryMonitoringEnabled = true
        ipAddress = NetworkInfo.wifiAddress() ?? "Pas de Wi-Fi"
        camera.requestAccessAndStart { [weak self] granted in
            self?.cameraAuthorized = granted
        }
        server.start()
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            self?.tick()
        }
    }

    private func tick() {
        fps = server.takeFrameCount()
        ipAddress = NetworkInfo.wifiAddress() ?? "Pas de Wi-Fi"
        if isConnected { sendStatus() }
    }

    // MARK: - Actions (écran ou PC)

    func selectCamera(_ kind: CameraKind) {
        cameraKind = kind
        torchOn = false
        zoom = 1.0
        camera.selectCamera(kind)
        sendStatus()
    }

    func setTorch(_ on: Bool) {
        camera.setTorch(on) { [weak self] actual in
            self?.torchOn = actual
            self?.sendStatus()
        }
    }

    func setZoom(_ factor: Double) {
        zoom = factor
        camera.setZoom(factor)
    }

    func setResolution(_ value: String) {
        resolution = value == "1080p" ? "1080p" : "720p"
        camera.setResolution(resolution)
    }

    func setQuality(_ value: Double) {
        quality = min(max(value, 0.1), 1.0)
        camera.setQuality(quality)
    }

    private func handle(_ command: [String: Any]) {
        guard let name = command["cmd"] as? String else { return }
        let value = command["value"]
        switch name {
        case "camera":
            if let raw = value as? String, let kind = CameraKind(rawValue: raw) { selectCamera(kind) }
        case "torch":
            if let on = value as? Bool { setTorch(on) }
        case "zoom":
            if let factor = value as? Double { setZoom(factor) }
        case "resolution":
            if let raw = value as? String { setResolution(raw) }
        case "quality":
            if let q = value as? Double { setQuality(q) }
        default:
            AppLog.write("Commande inconnue : \(name)")
        }
        sendStatus()
    }

    func sendStatus() {
        let size = camera.frameSize
        let device = UIDevice.current
        let status: [String: Any] = [
            "camera": cameraKind.rawValue,
            "torch": torchOn,
            "zoom": zoom,
            "resolution": resolution,
            "quality": quality,
            "fps": fps,
            "width": Int(size.width),
            "height": Int(size.height),
            "battery": device.batteryLevel >= 0 ? Double(device.batteryLevel) : -1.0,
            "charging": device.batteryState == .charging || device.batteryState == .full
        ]
        server.send(WireProtocol.json(.status, status))
    }
}

/// Adresse IP Wi-Fi de l'iPhone (interface en0).
enum NetworkInfo {
    static func wifiAddress() -> String? {
        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0, let first = ifaddr else { return nil }
        defer { freeifaddrs(ifaddr) }

        var address: String?
        for pointer in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let interface = pointer.pointee
            guard let addr = interface.ifa_addr,
                  addr.pointee.sa_family == UInt8(AF_INET),
                  String(cString: interface.ifa_name) == "en0" else { continue }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            let result = getnameinfo(addr, socklen_t(addr.pointee.sa_len), &host, socklen_t(host.count),
                                     nil, 0, NI_NUMERICHOST)
            if result == 0 {
                address = host.withUnsafeBufferPointer { String(cString: $0.baseAddress!) }
            }
        }
        return address
    }
}
