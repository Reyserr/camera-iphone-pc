import AVFoundation
import CoreImage
import Photos

/// Enregistre la vidéo (avec effets, et le son si le micro est activé) et les photos dans la galerie de l'iPhone.
final class Recorder {
    private let lock = NSLock()
    private var writer: AVAssetWriter?
    private var videoInput: AVAssetWriterInput?
    private var audioInput: AVAssetWriterInput?
    private var adaptor: AVAssetWriterInputPixelBufferAdaptor?
    private var outputSize = CGSize.zero
    private var pendingStart = false
    private var pendingAudio = false
    private var sessionStarted = false
    private var audioFormat: CMFormatDescription?
    private var startDate: Date?

    /// Vrai entre start() et stop().
    var isRecording: Bool { lock.withLock { pendingStart || writer != nil } }

    /// Durée de l'enregistrement en cours, en secondes.
    var elapsedSeconds: Int {
        lock.withLock { startDate.map { Int(Date().timeIntervalSince($0)) } ?? 0 }
    }

    /// Démarre à la prochaine image (sa taille fixe celle de la vidéo).
    func start(withAudio: Bool) {
        lock.withLock {
            guard writer == nil, !pendingStart else { return }
            pendingStart = true
            pendingAudio = withAudio
            startDate = Date()
        }
        AppLog.write("Enregistrement démarré")
    }

    func stop() {
        let finished: (AVAssetWriter, URL)? = lock.withLock {
            pendingStart = false
            startDate = nil
            guard let writer = writer else { return nil }
            let url = writer.outputURL
            videoInput?.markAsFinished()
            audioInput?.markAsFinished()
            self.writer = nil
            videoInput = nil
            audioInput = nil
            adaptor = nil
            sessionStarted = false
            return (writer, url)
        }
        guard let result = finished else { return }
        let writer = result.0
        let url = result.1
        writer.finishWriting {
            if writer.status == .completed {
                Self.saveVideoToPhotos(url)
            } else {
                AppLog.write("Enregistrement raté : \(writer.error?.localizedDescription ?? "erreur inconnue")")
            }
        }
    }

    // MARK: - Images et son (fils vidéo et audio)

    func appendVideo(_ image: CIImage, at time: CMTime, context: CIContext, colorSpace: CGColorSpace) {
        lock.lock()
        defer { lock.unlock() }

        if pendingStart {
            pendingStart = false
            setUpWriter(size: image.extent.size)
        }
        guard let writer = writer, let videoInput = videoInput, let adaptor = adaptor else { return }
        if !sessionStarted {
            writer.startSession(atSourceTime: time)
            sessionStarted = true
        }
        guard videoInput.isReadyForMoreMediaData, let pool = adaptor.pixelBufferPool else { return }

        var buffer: CVPixelBuffer?
        CVPixelBufferPoolCreatePixelBuffer(nil, pool, &buffer)
        guard let pixelBuffer = buffer else { return }

        // Si l'iPhone a tourné pendant l'enregistrement, on adapte l'image au cadre de la vidéo.
        var frame = image
        if image.extent.size != outputSize {
            let scale = min(outputSize.width / image.extent.width, outputSize.height / image.extent.height)
            frame = image
                .transformed(by: CGAffineTransform(scaleX: scale, y: scale))
            frame = frame.transformed(by: CGAffineTransform(
                translationX: (outputSize.width - frame.extent.width) / 2 - frame.extent.minX,
                y: (outputSize.height - frame.extent.height) / 2 - frame.extent.minY))
            frame = frame.composited(over: CIImage(color: .black).cropped(to: CGRect(origin: .zero, size: outputSize)))
        }
        context.render(frame, to: pixelBuffer, bounds: CGRect(origin: .zero, size: outputSize), colorSpace: colorSpace)
        if !adaptor.append(pixelBuffer, withPresentationTime: time) {
            AppLog.write("Image non enregistrée : \(writer.error?.localizedDescription ?? "?")")
        }
    }

    func appendAudio(_ sampleBuffer: CMSampleBuffer) {
        lock.lock()
        defer { lock.unlock() }
        if audioFormat == nil { audioFormat = CMSampleBufferGetFormatDescription(sampleBuffer) }
        guard sessionStarted, let audioInput = audioInput, audioInput.isReadyForMoreMediaData else { return }
        audioInput.append(sampleBuffer)
    }

    /// À appeler sous `lock`.
    private func setUpWriter(size: CGSize) {
        let width = Int(size.width), height = Int(size.height)
        let name = "LynxCam-\(Int(Date().timeIntervalSince1970)).mov"
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(name)
        do {
            let writer = try AVAssetWriter(outputURL: url, fileType: .mov)
            let videoSettings: [String: Any] = [
                AVVideoCodecKey: AVVideoCodecType.h264,
                AVVideoWidthKey: width,
                AVVideoHeightKey: height,
                AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: width * height * 6],
            ]
            let video = AVAssetWriterInput(mediaType: .video, outputSettings: videoSettings)
            video.expectsMediaDataInRealTime = true
            let attributes: [String: Any] = [
                kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
                kCVPixelBufferWidthKey as String: width,
                kCVPixelBufferHeightKey as String: height,
            ]
            let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: video,
                                                               sourcePixelBufferAttributes: attributes)
            guard writer.canAdd(video) else { throw RecorderError.cannotAddInput }
            writer.add(video)

            var audio: AVAssetWriterInput?
            if pendingAudio, let format = audioFormat,
               let description = CMAudioFormatDescriptionGetStreamBasicDescription(format)?.pointee {
                let audioSettings: [String: Any] = [
                    AVFormatIDKey: kAudioFormatMPEG4AAC,
                    AVNumberOfChannelsKey: max(1, min(Int(description.mChannelsPerFrame), 2)),
                    AVSampleRateKey: description.mSampleRate,
                    AVEncoderBitRateKey: 128_000,
                ]
                let input = AVAssetWriterInput(mediaType: .audio, outputSettings: audioSettings, sourceFormatHint: format)
                input.expectsMediaDataInRealTime = true
                if writer.canAdd(input) {
                    writer.add(input)
                    audio = input
                }
            }

            guard writer.startWriting() else { throw writer.error ?? RecorderError.cannotStart }
            self.writer = writer
            self.videoInput = video
            self.audioInput = audio
            self.adaptor = adaptor
            self.outputSize = CGSize(width: width, height: height)
            self.sessionStarted = false
            AppLog.write("Vidéo \(width)×\(height)\(audio != nil ? " avec son" : " sans son")")
        } catch {
            startDate = nil
            AppLog.write("Impossible d'enregistrer : \(error.localizedDescription)")
        }
    }

    enum RecorderError: Error {
        case cannotAddInput, cannotStart
    }

    // MARK: - Galerie

    private static func saveVideoToPhotos(_ url: URL) {
        PHPhotoLibrary.requestAuthorization(for: .addOnly) { status in
            guard status == .authorized || status == .limited else {
                AppLog.write("Accès à la galerie refusé : vidéo non enregistrée")
                return
            }
            PHPhotoLibrary.shared().performChanges({
                PHAssetChangeRequest.creationRequestForAssetFromVideo(atFileURL: url)
            }) { success, error in
                AppLog.write(success ? "Vidéo enregistrée dans la galerie" : "Vidéo non enregistrée : \(error?.localizedDescription ?? "?")")
                try? FileManager.default.removeItem(at: url)
            }
        }
    }

    static func savePhotoToPhotos(_ jpeg: Data) {
        PHPhotoLibrary.requestAuthorization(for: .addOnly) { status in
            guard status == .authorized || status == .limited else {
                AppLog.write("Accès à la galerie refusé : photo non enregistrée")
                return
            }
            PHPhotoLibrary.shared().performChanges({
                PHAssetCreationRequest.forAsset().addResource(with: .photo, data: jpeg, options: nil)
            }) { success, error in
                AppLog.write(success ? "Photo enregistrée dans la galerie" : "Photo non enregistrée : \(error?.localizedDescription ?? "?")")
            }
        }
    }
}
