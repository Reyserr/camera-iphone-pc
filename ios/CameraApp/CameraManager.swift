import AVFoundation
import CoreImage
import ImageIO
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

/// Capture la caméra et compresse chaque image en JPEG.
final class CameraManager: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate {
    let session = AVCaptureSession()

    private let sessionQueue = DispatchQueue(label: "camera.session")
    private let videoQueue = DispatchQueue(label: "camera.video", qos: .userInteractive)
    private let output = AVCaptureVideoDataOutput()
    private var input: AVCaptureDeviceInput?
    private var outputReady = false

    // Rotation : l'image envoyée reste droite, que l'iPhone soit tenu en portrait ou en paysage.
    private weak var previewLayer: AVCaptureVideoPreviewLayer?
    private var rotationCoordinator: AVCaptureDevice.RotationCoordinator?
    private var rotationObservations: [NSKeyValueObservation] = []

    // Compression JPEG (utilisé uniquement sur videoQueue)
    private let ciContext = CIContext(options: [.cacheIntermediates: false])
    private let colorSpace = CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB()
    private var quality: Double = 0.7

    // Réglages (utilisés uniquement sur sessionQueue)
    private var currentKind: CameraKind = .back
    private var resolution = "720p"

    // Taille de la dernière image (lue depuis plusieurs fils)
    private let sizeLock = NSLock()
    private var lastSize = CGSize.zero
    var frameSize: CGSize { sizeLock.withLock { lastSize } }

    /// Demande si le PC attend une image (évite de compresser pour rien).
    var shouldEncode: (() -> Bool)?
    /// Image JPEG prête, avec son horodatage en millisecondes.
    var onFrame: ((Data, UInt64) -> Void)?

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
        NotificationCenter.default.addObserver(forName: AVCaptureSession.runtimeErrorNotification,
                                               object: session, queue: nil) { [weak self] note in
            AppLog.write("Erreur caméra, redémarrage : \(String(describing: note.userInfo?[AVCaptureSessionErrorKey]))")
            self?.sessionQueue.async { self?.session.startRunning() }
        }
        NotificationCenter.default.addObserver(forName: AVCaptureSession.wasInterruptedNotification,
                                               object: session, queue: nil) { _ in
            AppLog.write("Caméra interrompue (app en arrière-plan ou autre app)")
        }
        NotificationCenter.default.addObserver(forName: AVCaptureSession.interruptionEndedNotification,
                                               object: session, queue: nil) { _ in
            AppLog.write("Caméra relancée")
        }
    }

    /// Appelé par l'aperçu SwiftUI quand sa couche d'affichage existe.
    func attachPreview(_ layer: AVCaptureVideoPreviewLayer) {
        previewLayer = layer
        sessionQueue.async {
            guard let device = self.input?.device else { return }
            DispatchQueue.main.async { self.makeRotationCoordinator(for: device) }
        }
    }

    // MARK: - Réglages demandés par le PC ou l'écran

    func selectCamera(_ kind: CameraKind) {
        sessionQueue.async { self.configure(kind: kind) }
    }

    func setResolution(_ value: String) {
        sessionQueue.async {
            self.resolution = value == "1080p" ? "1080p" : "720p"
            self.session.beginConfiguration()
            self.applyPreset()
            self.session.commitConfiguration()
            AppLog.write("Résolution : \(self.resolution)")
        }
    }

    func setQuality(_ value: Double) {
        videoQueue.async { self.quality = min(max(value, 0.1), 1.0) }
    }

    func setZoom(_ factor: Double) {
        sessionQueue.async {
            guard let device = self.input?.device else { return }
            do {
                try device.lockForConfiguration()
                let maxZoom = min(device.maxAvailableVideoZoomFactor, 10)
                device.videoZoomFactor = max(device.minAvailableVideoZoomFactor, min(CGFloat(factor), maxZoom))
                device.unlockForConfiguration()
            } catch {
                AppLog.write("Zoom impossible : \(error.localizedDescription)")
            }
        }
    }

    /// Allume ou éteint la lampe. `completion` reçoit l'état réel de la lampe.
    func setTorch(_ on: Bool, completion: @escaping (Bool) -> Void) {
        sessionQueue.async {
            guard let device = self.input?.device, device.hasTorch, device.isTorchModeSupported(.on) else {
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

    // MARK: - Configuration (sessionQueue)

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
        if let oldInput = input { session.removeInput(oldInput) }
        if session.canAddInput(newInput) {
            session.addInput(newInput)
            input = newInput
            currentKind = kind
        } else if let oldInput = input {
            session.addInput(oldInput)
            AppLog.write("Impossible d'utiliser la caméra « \(kind.label) »")
        }
        if !outputReady, session.canAddOutput(output) {
            output.alwaysDiscardsLateVideoFrames = true
            output.setSampleBufferDelegate(self, queue: videoQueue)
            session.addOutput(output)
            outputReady = true
        }
        applyPreset()
        session.commitConfiguration()

        if let activeDevice = input?.device {
            DispatchQueue.main.async { self.makeRotationCoordinator(for: activeDevice) }
        }
        AppLog.write("Caméra : \(currentKind.label)")
    }

    private func applyPreset() {
        let preset: AVCaptureSession.Preset = resolution == "1080p" ? .hd1920x1080 : .hd1280x720
        session.sessionPreset = session.canSetSessionPreset(preset) ? preset : .high
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
                guard let connection = self?.output.connection(with: .video),
                      connection.isVideoRotationAngleSupported(angle) else { return }
                connection.videoRotationAngle = angle
            }
        })
    }

    // MARK: - Images (videoQueue)

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer,
                       from connection: AVCaptureConnection) {
        guard shouldEncode?() == true,
              let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }

        let size = CGSize(width: CVPixelBufferGetWidth(pixelBuffer), height: CVPixelBufferGetHeight(pixelBuffer))
        sizeLock.withLock { lastSize = size }

        let image = CIImage(cvPixelBuffer: pixelBuffer)
        let options = [kCGImageDestinationLossyCompressionQuality as CIImageRepresentationOption: quality]
        guard let jpeg = ciContext.jpegRepresentation(of: image, colorSpace: colorSpace, options: options) else {
            return
        }
        let seconds = CMTimeGetSeconds(CMSampleBufferGetPresentationTimeStamp(sampleBuffer))
        let timestamp = seconds.isFinite && seconds > 0 ? UInt64(seconds * 1000) : 0
        onFrame?(jpeg, timestamp)
    }
}
