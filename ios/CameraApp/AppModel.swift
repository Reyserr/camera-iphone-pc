import AVFoundation
import Darwin
import SwiftUI
import UIKit

/// Relie la caméra, les effets, l'enregistrement, le serveur et l'écran.
final class AppModel: ObservableObject {
    @Published var ipAddress = "—"
    @Published var isConnected = false
    @Published var peer = ""
    @Published var fps = 0
    @Published var cameraKind: CameraKind = .back
    @Published var torchOn = false
    @Published var micOn = false
    @Published var isRecording = false
    @Published var recordSeconds = 0
    @Published var lastLog = ""
    @Published var blackScreen = false
    @Published var cameraAuthorized = true

    let camera = CaptureEngine()
    let server = StreamServer()
    let processor = FrameProcessor()
    let recorder = Recorder()

    // Réglages courants (fil principal)
    private var zoom = 1.0
    private var resolution = "720p"
    private var targetFps = 30
    private var appliedFps = 30
    private var quality = 0.7
    private var exposure = 0.0
    private var focusAuto = true
    private var focusPosition = 0.5
    private var whiteBalanceAuto = true
    private var kelvin = 5500.0

    // Partagés avec le fil vidéo
    private let frameLock = NSLock()
    private var jpegQuality = 0.7
    private var photoRequested = false

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

        camera.onVideo = { [weak self] sampleBuffer in self?.handleVideo(sampleBuffer) }
        camera.onAudio = { [weak self] sampleBuffer in self?.handleAudio(sampleBuffer) }
        camera.onFrameRateApplied = { [weak self] rate in
            DispatchQueue.main.async { self?.appliedFps = rate }
        }

        let name = deviceName
        let version = systemVersion
        server.helloProvider = {
            ["name": name, "system": "iOS \(version)", "protocol": WireProtocol.version, "app": "LynxCam",
             "version": Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "2.0"]
        }
        server.onCommand = { [weak self] command in
            DispatchQueue.main.async { self?.handle(command) }
        }
        server.onBackgroundImage = { [weak self] data in
            let ok = self?.processor.setBackgroundImage(data) ?? false
            AppLog.write(ok ? "Photo d'arrière-plan reçue" : "Photo d'arrière-plan illisible")
        }
        server.onConnectionChange = { [weak self] connected, peer in
            DispatchQueue.main.async {
                guard let self = self else { return }
                self.isConnected = connected
                self.peer = peer
                if connected { self.sendStatus() }
            }
        }
        NotificationCenter.default.addObserver(forName: UIApplication.didEnterBackgroundNotification,
                                               object: nil, queue: .main) { [weak self] _ in
            guard let self = self, self.recorder.isRecording else { return }
            AppLog.write("App en arrière-plan : enregistrement arrêté")
            self.setRecording(false)
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
        isRecording = recorder.isRecording
        recordSeconds = recorder.elapsedSeconds
        if isConnected { sendStatus() }
    }

    // MARK: - Images et son (fils de fond)

    private func handleVideo(_ sampleBuffer: CMSampleBuffer) {
        let streaming = server.wantsFrame()
        let recording = recorder.isRecording
        let (photo, quality) = frameLock.withLock { () -> (Bool, Double) in
            let requested = photoRequested
            photoRequested = false
            return (requested, jpegQuality)
        }
        guard streaming || recording || photo,
              let buffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }

        let image = processor.process(buffer)
        let time = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)

        if recording {
            recorder.appendVideo(image, at: time, context: processor.context, colorSpace: processor.colorSpace)
        }
        if photo, let data = processor.jpeg(image, quality: 0.95) {
            Recorder.savePhotoToPhotos(data)
            server.send(WireProtocol.message(.photo, data))
        }
        if streaming, let jpeg = processor.jpeg(image, quality: quality) {
            let seconds = CMTimeGetSeconds(time)
            let timestamp = seconds.isFinite && seconds > 0 ? UInt64(seconds * 1000) : 0
            server.sendFrame(jpeg, timestamp: timestamp)
        }
    }

    private func handleAudio(_ sampleBuffer: CMSampleBuffer) {
        recorder.appendAudio(sampleBuffer)
        if server.isConnected, let message = WireProtocol.audioMessage(from: sampleBuffer) {
            server.send(message)
        }
    }

    // MARK: - Actions (écran ou PC)

    func selectCamera(_ kind: CameraKind) {
        cameraKind = kind
        torchOn = false
        zoom = 1.0
        camera.selectCamera(kind)
        // Le nouvel objectif repart en automatique : on réapplique les réglages choisis.
        camera.setExposure(exposure)
        camera.setFocus(auto: focusAuto, position: focusPosition)
        camera.setWhiteBalance(auto: whiteBalanceAuto, kelvin: kelvin)
        sendStatus()
    }

    func setTorch(_ on: Bool) {
        camera.setTorch(on) { [weak self] actual in
            self?.torchOn = actual
            self?.sendStatus()
        }
    }

    func setMicrophone(_ on: Bool) {
        camera.setMicrophone(on) { [weak self] actual in
            self?.micOn = actual
            self?.sendStatus()
        }
    }

    func setRecording(_ on: Bool) {
        if on {
            recorder.start(withAudio: micOn)
        } else {
            recorder.stop()
        }
        isRecording = recorder.isRecording
        recordSeconds = recorder.elapsedSeconds
        sendStatus()
    }

    func takePhoto() {
        frameLock.withLock { photoRequested = true }
    }

    private func setQuality(_ value: Double) {
        quality = min(max(value, 0.1), 1.0)
        let q = quality
        frameLock.withLock { jpegQuality = q }
    }

    private func applyFormat() {
        camera.setFormat(resolution: resolution, framesPerSecond: targetFps)
        let fast = targetFps >= 60
        processor.update { $0.fastMask = fast }
    }

    private func handle(_ command: [String: Any]) {
        guard let name = command["cmd"] as? String else { return }
        let value = command["value"]
        let number = (value as? NSNumber)?.doubleValue
        let flag = value as? Bool
        let text = value as? String

        switch name {
        case "camera":
            if let raw = text, let kind = CameraKind(rawValue: raw) { selectCamera(kind) }
        case "torch":
            if let on = flag { setTorch(on) }
        case "zoom":
            if let factor = number {
                zoom = factor
                camera.setZoom(factor)
            }
        case "resolution":
            if let raw = text {
                resolution = raw == "1080p" ? "1080p" : "720p"
                applyFormat()
            }
        case "fps":
            if let rate = number {
                targetFps = Int(rate)
                applyFormat()
            }
        case "quality":
            if let q = number { setQuality(q) }
        case "background":
            if let raw = text, let mode = BackgroundMode(rawValue: raw) { processor.update { $0.background = mode } }
        case "blur":
            if let amount = number { processor.update { $0.blur = amount } }
        case "theme":
            if let theme = text { processor.update { $0.theme = theme } }
        case "mirror":
            if let on = flag { processor.update { $0.mirror = on } }
        case "brightness":
            if let v = number { processor.update { $0.brightness = v } }
        case "contrast":
            if let v = number { processor.update { $0.contrast = v } }
        case "saturation":
            if let v = number { processor.update { $0.saturation = v } }
        case "exposure":
            if let ev = number {
                exposure = ev
                camera.setExposure(ev)
            }
        case "focus":
            focusAuto = text == "auto" || number == nil
            if let position = number { focusPosition = position }
            camera.setFocus(auto: focusAuto, position: focusPosition)
        case "whitebalance":
            whiteBalanceAuto = text == "auto" || number == nil
            if let k = number { kelvin = k }
            camera.setWhiteBalance(auto: whiteBalanceAuto, kelvin: kelvin)
        case "mic":
            if let on = flag { setMicrophone(on) }
        case "record":
            if let on = flag { setRecording(on) }
        case "photo":
            takePhoto()
        default:
            AppLog.write("Commande inconnue : \(name)")
        }
        sendStatus()
    }

    func sendStatus() {
        let device = UIDevice.current
        let effects = processor.current
        let status: [String: Any] = [
            "camera": cameraKind.rawValue,
            "torch": torchOn,
            "zoom": zoom,
            "resolution": resolution,
            "fps": fps,
            "fpsTarget": targetFps,
            "fpsApplied": appliedFps,
            "quality": quality,
            "mic": micOn,
            "recording": isRecording,
            "recordSeconds": recordSeconds,
            "background": effects.background.rawValue,
            "battery": device.batteryLevel >= 0 ? Double(device.batteryLevel) : -1.0,
            "charging": device.batteryState == .charging || device.batteryState == .full,
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
