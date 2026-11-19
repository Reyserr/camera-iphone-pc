import AVFoundation
import UIKit

enum CameraKind: String, CaseIterable {
    case back, ultrawide, front

    var label: String {
        switch self {
        case .back: return "Arrière"
        case .ultrawide: return "Ultra grand-angle"
        case .front: return "Avant"
        }
    }
}

/// Caméra et micro : sélection de l'objectif, format (résolution + images/s) et réglages de l'appareil.
/// Les images et le son sortent par `onVideo` et `onAudio`, sur des fils de fond.
final class CaptureEngine: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate, AVCaptureAudioDataOutputSampleBufferDelegate {
    let session = AVCaptureSession()

    private let sessionQueue = DispatchQueue(label: "capture.session")
    private let videoQueue = DispatchQueue(label: "capture.video", qos: .userInteractive)
    private let audioQueue = DispatchQueue(label: "capture.audio", qos: .userInitiated)
    private let videoOutput = AVCaptureVideoDataOutput()
    private let audioOutput = AVCaptureAudioDataOutput()
    private var videoInput: AVCaptureDeviceInput?
    private var audioInput: AVCaptureDeviceInput?
    private var videoOutputReady = false
    private var audioOutputReady = false

    // Réglages (sessionQueue uniquement)
    private var currentKind: CameraKind = .back
    private var resolution = "720p"
    private var framesPerSecond = 30

    // Rotation : l'image envoyée reste droite, portrait ou paysage.
    private weak var previewLayer: AVCaptureVideoPreviewLayer?
    private var rotationCoordinator: AVCaptureDevice.RotationCoordinator?
    private var rotationObservations: [NSKeyValueObservation] = []

    /// Image de la caméra (fil vidéo).
    var onVideo: ((CMSampleBuffer) -> Void)?
    /// Son du micro (fil audio).
    var onAudio: ((CMSampleBuffer) -> Void)?
    /// Images/s réellement appliquées après un changement de format.
    var onFrameRateApplied: ((Int) -> Void)?

    // MARK: - Démarrage

    func requestAccessAndStart(completion: @escaping (Bool) -> Void) {
        AVCaptureDevice.requestAccess(for: .video) { granted in
            DispatchQueue.main.async { completion(granted) }
            guard granted else {
                AppLog.write("Accès à la caméra refusé")
                return
            }
            self.sessionQueue.async {
                self.configure(kind: self.currentKind)
                self.session.startRunning()
            }
        }
        let center = NotificationCenter.default
        center.addObserver(forName: AVCaptureSession.runtimeErrorNotification, object: session, queue: nil) { [weak self] _ in
            AppLog.write("Erreur caméra, redémarrage")
            self?.sessionQueue.async { self?.session.startRunning() }
        }
        center.addObserver(forName: AVCaptureSession.wasInterruptedNotification, object: session, queue: nil) { _ in
            AppLog.write("Caméra interrompue (app en arrière-plan ou autre app)")
        }
        center.addObserver(forName: AVCaptureSession.interruptionEndedNotification, object: session, queue: nil) { _ in
            AppLog.write("Caméra relancée")
        }
    }

    func attachPreview(_ layer: AVCaptureVideoPreviewLayer) {
        previewLayer = layer
        sessionQueue.async {
            guard let device = self.videoInput?.device else { return }
            DispatchQueue.main.async { self.makeRotationCoordinator(for: device) }
        }
    }

    // MARK: - Objectif et format

    func selectCamera(_ kind: CameraKind) {
        sessionQueue.async { self.configure(kind: kind) }
    }

    func setFormat(resolution: String, framesPerSecond: Int) {
        sessionQueue.async {
            self.resolution = resolution == "1080p" ? "1080p" : "720p"
            self.framesPerSecond = [15, 24, 30, 60].contains(framesPerSecond) ? framesPerSecond : 30
            guard self.videoInput != nil else { return }
            self.session.beginConfiguration()
            self.applyFormat()
            self.session.commitConfiguration()
        }
    }

    private static func device(for kind: CameraKind) -> AVCaptureDevice? {
        switch kind {
        case .back: return AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .back)
        case .ultrawide: return AVCaptureDevice.default(.builtInUltraWideCamera, for: .video, position: .back)
        case .front: return AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .front)
        }
    }

    private func configure(kind: CameraKind) {
        guard let device = Self.device(for: kind) else {
            AppLog.write("Caméra « \(kind.label) » introuvable")
            return
        }
        let newInput: AVCaptureDeviceInput
        do {
            newInput = try AVCaptureDeviceInput(device: device)
        } catch {
            AppLog.write("Erreur caméra : \(error.localizedDescription)")
            return
        }

        session.beginConfiguration()
        if let oldInput = videoInput { session.removeInput(oldInput) }
        if session.canAddInput(newInput) {
            session.addInput(newInput)
            videoInput = newInput
            currentKind = kind
        } else if let oldInput = videoInput {
            session.addInput(oldInput)
            AppLog.write("Impossible d'utiliser la caméra « \(kind.label) »")
        }
        if !videoOutputReady, session.canAddOutput(videoOutput) {
            videoOutput.alwaysDiscardsLateVideoFrames = true
            videoOutput.setSampleBufferDelegate(self, queue: videoQueue)
            session.addOutput(videoOutput)
            videoOutputReady = true
        }
        applyFormat()
        session.commitConfiguration()

        if let activeDevice = videoInput?.device {
            DispatchQueue.main.async { self.makeRotationCoordinator(for: activeDevice) }
        }
        AppLog.write("Caméra : \(currentKind.label)")
    }

    /// Choisit le format de l'appareil qui correspond à la résolution et aux images/s voulues.
    private func applyFormat() {
        guard let device = videoInput?.device else { return }
        let target: (Int32, Int32) = resolution == "1080p" ? (1920, 1080) : (1280, 720)
        let wanted = Double(framesPerSecond)

        func maxRate(_ format: AVCaptureDevice.Format) -> Double {
            format.videoSupportedFrameRateRanges.map(\.maxFrameRate).max() ?? 0
        }
        let sameSize = device.formats.filter { format in
            let size = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
            return size.width == target.0 && size.height == target.1
                && CMFormatDescriptionGetMediaSubType(format.formatDescription) == kCVPixelFormatType_420YpCbCr8BiPlanarFullRange
        }
        let chosen = sameSize.first { maxRate($0) >= wanted } ?? sameSize.max { maxRate($0) < maxRate($1) }

        guard let format = chosen else {
            let preset: AVCaptureSession.Preset = resolution == "1080p" ? .hd1920x1080 : .hd1280x720
            session.sessionPreset = session.canSetSessionPreset(preset) ? preset : .high
            AppLog.write("Format \(resolution) introuvable, réglage par défaut utilisé")
            return
        }
        do {
            try device.lockForConfiguration()
            device.activeFormat = format
            let rate = min(wanted, maxRate(format))
            let duration = CMTime(value: 1, timescale: CMTimeScale(rate.rounded()))
            device.activeVideoMinFrameDuration = duration
            device.activeVideoMaxFrameDuration = duration
            device.unlockForConfiguration()
            AppLog.write("Format : \(resolution) à \(Int(rate)) img/s")
            onFrameRateApplied?(Int(rate))
        } catch {
            AppLog.write("Format impossible : \(error.localizedDescription)")
        }
    }

    // MARK: - Réglages de l'appareil

    private func withDevice(_ name: String, _ body: @escaping (AVCaptureDevice) -> Void) {
        sessionQueue.async {
            guard let device = self.videoInput?.device else { return }
            do {
                try device.lockForConfiguration()
                body(device)
                device.unlockForConfiguration()
            } catch {
                AppLog.write("\(name) impossible : \(error.localizedDescription)")
            }
        }
    }

    func setZoom(_ factor: Double) {
        withDevice("Zoom") { device in
            let maxZoom = min(device.maxAvailableVideoZoomFactor, 10)
            device.videoZoomFactor = max(device.minAvailableVideoZoomFactor, min(CGFloat(factor), maxZoom))
        }
    }

    /// Compensation d'exposition en EV (environ -3 à +3).
    func setExposure(_ bias: Double) {
        withDevice("Exposition") { device in
            let value = max(device.minExposureTargetBias, min(Float(bias), device.maxExposureTargetBias))
            device.setExposureTargetBias(value, completionHandler: nil)
        }
    }

    /// Mise au point automatique, ou fixe (0 = près, 1 = loin).
    func setFocus(auto: Bool, position: Double) {
        withDevice("Mise au point") { device in
            if auto {
                if device.isFocusModeSupported(.continuousAutoFocus) { device.focusMode = .continuousAutoFocus }
            } else if device.isLockingFocusWithCustomLensPositionSupported {
                device.setFocusModeLocked(lensPosition: Float(max(0, min(position, 1))), completionHandler: nil)
            }
        }
    }

    /// Balance des blancs automatique, ou fixée à une température en kelvins.
    func setWhiteBalance(auto: Bool, kelvin: Double) {
        withDevice("Balance des blancs") { device in
            if auto {
                if device.isWhiteBalanceModeSupported(.continuousAutoWhiteBalance) {
                    device.whiteBalanceMode = .continuousAutoWhiteBalance
                }
            } else if device.isLockingWhiteBalanceWithCustomDeviceGainsSupported {
                let values = AVCaptureDevice.WhiteBalanceTemperatureAndTintValues(temperature: Float(kelvin), tint: 0)
                var gains = device.deviceWhiteBalanceGains(for: values)
                let maxGain = device.maxWhiteBalanceGain
                gains.redGain = max(1, min(gains.redGain, maxGain))
                gains.greenGain = max(1, min(gains.greenGain, maxGain))
                gains.blueGain = max(1, min(gains.blueGain, maxGain))
                device.setWhiteBalanceModeLocked(with: gains, completionHandler: nil)
            }
        }
    }

    /// Allume ou éteint la lampe. `completion` reçoit l'état réel.
    func setTorch(_ on: Bool, completion: @escaping (Bool) -> Void) {
        sessionQueue.async {
            guard let device = self.videoInput?.device, device.hasTorch, device.isTorchModeSupported(.on) else {
                if on { AppLog.write("Pas de lampe sur cette caméra") }
                DispatchQueue.main.async { completion(false) }
                return
            }
            do {
                try device.lockForConfiguration()
                device.torchMode = on ? .on : .off
                device.unlockForConfiguration()
                DispatchQueue.main.async { completion(on) }
            } catch {
                AppLog.write("Lampe impossible : \(error.localizedDescription)")
                DispatchQueue.main.async { completion(false) }
            }
        }
    }

    // MARK: - Micro

    /// Branche ou débranche le micro. `completion` reçoit l'état réel.
    func setMicrophone(_ on: Bool, completion: @escaping (Bool) -> Void) {
        guard on else {
            sessionQueue.async {
                if let input = self.audioInput {
                    self.session.beginConfiguration()
                    self.session.removeInput(input)
                    self.session.commitConfiguration()
                    self.audioInput = nil
                    AppLog.write("Micro coupé")
                }
                DispatchQueue.main.async { completion(false) }
            }
            return
        }
        AVCaptureDevice.requestAccess(for: .audio) { granted in
            self.sessionQueue.async {
                guard granted else {
                    AppLog.write("Accès au micro refusé (Réglages › LynxCam)")
                    DispatchQueue.main.async { completion(false) }
                    return
                }
                if self.audioInput == nil, let mic = AVCaptureDevice.default(for: .audio),
                   let input = try? AVCaptureDeviceInput(device: mic) {
                    self.session.beginConfiguration()
                    if self.session.canAddInput(input) {
                        self.session.addInput(input)
                        self.audioInput = input
                    }
                    if !self.audioOutputReady, self.session.canAddOutput(self.audioOutput) {
                        self.audioOutput.setSampleBufferDelegate(self, queue: self.audioQueue)
                        self.session.addOutput(self.audioOutput)
                        self.audioOutputReady = true
                    }
                    self.session.commitConfiguration()
                }
                let active = self.audioInput != nil
                AppLog.write(active ? "Micro activé" : "Micro indisponible")
                DispatchQueue.main.async { completion(active) }
            }
        }
    }

    // MARK: - Rotation (fil principal)

    private func makeRotationCoordinator(for device: AVCaptureDevice) {
        rotationObservations.removeAll()
        let coordinator = AVCaptureDevice.RotationCoordinator(device: device, previewLayer: previewLayer)
        rotationCoordinator = coordinator

        rotationObservations.append(coordinator.observe(\.videoRotationAngleForHorizonLevelPreview,
                                                        options: [.initial, .new]) { [weak self] coordinator, _ in
            let angle = coordinator.videoRotationAngleForHorizonLevelPreview
            DispatchQueue.main.async {
                guard let connection = self?.previewLayer?.connection,
                      connection.isVideoRotationAngleSupported(angle) else { return }
                connection.videoRotationAngle = angle
            }
        })

        rotationObservations.append(coordinator.observe(\.videoRotationAngleForHorizonLevelCapture,
                                                        options: [.initial, .new]) { [weak self] coordinator, _ in
            let angle = coordinator.videoRotationAngleForHorizonLevelCapture
            self?.sessionQueue.async {
                guard let connection = self?.videoOutput.connection(with: .video),
                      connection.isVideoRotationAngleSupported(angle) else { return }
                connection.videoRotationAngle = angle
            }
        })
    }

    // MARK: - Sorties

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer,
                       from connection: AVCaptureConnection) {
        if output === videoOutput {
            onVideo?(sampleBuffer)
        } else {
            onAudio?(sampleBuffer)
        }
    }
}
