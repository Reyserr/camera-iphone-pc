import CoreImage
import CoreImage.CIFilterBuiltins
import ImageIO
import Vision

enum BackgroundMode: String {
    case none, blur, theme, image
}

/// Réglages des effets, modifiés depuis le PC et lus à chaque image.
struct EffectSettings {
    var background: BackgroundMode = .none
    var blur = 50.0          // 0 à 100
    var theme = "nuit"
    var mirror = false
    var brightness = 0.0     // -100 à 100
    var contrast = 0.0       // -100 à 100
    var saturation = 0.0     // -100 à 100
    var fastMask = false     // détourage plus rapide (60 img/s)
}

/// Détoure la personne (puce de l'iPhone) et applique flou, thème ou photo en arrière-plan,
/// puis les réglages de couleur et le miroir.
final class FrameProcessor {
    let context = CIContext(options: [.cacheIntermediates: false])
    let colorSpace = CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB()

    private let lock = NSLock()
    private var settings = EffectSettings()
    private var backgroundImage: CIImage?

    // Utilisés uniquement sur le fil vidéo
    private let sequenceHandler = VNSequenceRequestHandler()
    private let segmentation = VNGeneratePersonSegmentationRequest()

    init() {
        segmentation.qualityLevel = .balanced
        segmentation.outputPixelFormat = kCVPixelFormatType_OneComponent8
    }

    var current: EffectSettings { lock.withLock { settings } }

    func update(_ change: (inout EffectSettings) -> Void) {
        lock.withLock { change(&settings) }
    }

    /// Photo d'arrière-plan envoyée par le PC (JPEG ou PNG).
    func setBackgroundImage(_ data: Data) -> Bool {
        guard let image = CIImage(data: data, options: [.applyOrientationProperty: true]) else { return false }
        lock.withLock { backgroundImage = image }
        return true
    }

    // MARK: - Traitement d'une image (fil vidéo)

    func process(_ buffer: CVPixelBuffer) -> CIImage {
        let s = current
        let photo = lock.withLock { backgroundImage }
        var image = CIImage(cvPixelBuffer: buffer)
        let extent = image.extent

        if s.background != .none, let mask = personMask(buffer, extent: extent, fast: s.fastMask) {
            let backdrop: CIImage
            switch s.background {
            case .blur:
                backdrop = Self.blurred(image, amount: s.blur)
            case .image:
                backdrop = photo.map { Self.aspectFill($0, in: extent) } ?? Themes.image(s.theme, size: extent.size)
            case .theme, .none:
                backdrop = Themes.image(s.theme, size: extent.size)
            }
            let blend = CIFilter.blendWithMask()
            blend.inputImage = image
            blend.backgroundImage = backdrop
            blend.maskImage = mask
            image = blend.outputImage?.cropped(to: extent) ?? image
        }

        if s.brightness != 0 || s.contrast != 0 || s.saturation != 0 {
            let color = CIFilter.colorControls()
            color.inputImage = image
            color.brightness = Float(s.brightness / 250)
            color.contrast = Float(1 + s.contrast / 200)
            color.saturation = Float(1 + s.saturation / 100)
            image = color.outputImage?.cropped(to: extent) ?? image
        }

        if s.mirror {
            image = image.transformed(by: CGAffineTransform(scaleX: -1, y: 1).translatedBy(x: -extent.width, y: 0))
        }
        return image
    }

    private func personMask(_ buffer: CVPixelBuffer, extent: CGRect, fast: Bool) -> CIImage? {
        segmentation.qualityLevel = fast ? .fast : .balanced
        do {
            try sequenceHandler.perform([segmentation], on: buffer, orientation: .up)
        } catch {
            return nil
        }
        guard let maskBuffer = segmentation.results?.first?.pixelBuffer else { return nil }
        let mask = CIImage(cvPixelBuffer: maskBuffer)
        let scaleX = extent.width / mask.extent.width
        let scaleY = extent.height / mask.extent.height
        return mask.transformed(by: CGAffineTransform(scaleX: scaleX, y: scaleY))
    }

    /// Flou rapide : calculé sur une image 4 fois plus petite puis agrandie.
    private static func blurred(_ image: CIImage, amount: Double) -> CIImage {
        let extent = image.extent
        let sigma = 1 + max(0, min(amount, 100)) / 100 * 11
        let small = image.transformed(by: CGAffineTransform(scaleX: 0.25, y: 0.25))
        return small.clampedToExtent()
            .applyingGaussianBlur(sigma: sigma)
            .transformed(by: CGAffineTransform(scaleX: 4, y: 4))
            .cropped(to: extent)
    }

    /// Remplit tout le cadre avec l'image, sans la déformer (les bords dépassent).
    static func aspectFill(_ image: CIImage, in extent: CGRect) -> CIImage {
        let width = image.extent.width, height = image.extent.height
        guard width > 0, height > 0 else { return image }
        let scale = max(extent.width / width, extent.height / height)
        var output = image
            .transformed(by: CGAffineTransform(translationX: -image.extent.minX, y: -image.extent.minY))
            .transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        let dx = (output.extent.width - extent.width) / 2
        let dy = (output.extent.height - extent.height) / 2
        output = output.transformed(by: CGAffineTransform(translationX: -dx, y: -dy))
        return output.cropped(to: extent)
    }

    // MARK: - Sorties

    func jpeg(_ image: CIImage, quality: Double) -> Data? {
        let options = [kCGImageDestinationLossyCompressionQuality as CIImageRepresentationOption: quality]
        return context.jpegRepresentation(of: image, colorSpace: colorSpace, options: options)
    }
}

/// Arrière-plans intégrés (mêmes noms et couleurs que dans l'app PC).
enum Themes {
    private static let gradients: [String: (UInt32, UInt32)] = [
        "nuit": (0x141E30, 0x243B55),
        "coucher": (0xFF7E5F, 0xFEB47B),
        "ocean": (0x2193B0, 0x6DD5ED),
        "neon": (0xFC00FF, 0x00DBDE),
    ]

    static func image(_ name: String, size: CGSize) -> CIImage {
        let rect = CGRect(origin: .zero, size: size)
        switch name {
        case "vert":
            return CIImage(color: CIColor(red: 0, green: 1, blue: 0)).cropped(to: rect)
        case "studio":
            let radial = CIFilter.radialGradient()
            radial.center = CGPoint(x: size.width / 2, y: size.height * 0.55)
            radial.radius0 = Float(min(size.width, size.height) * 0.1)
            radial.radius1 = Float(max(size.width, size.height) * 0.75)
            radial.color0 = color(0x5A5A5A)
            radial.color1 = color(0x1E1E1E)
            return (radial.outputImage ?? CIImage(color: color(0x2A2A2A))).cropped(to: rect)
        default:
            let pair = gradients[name] ?? gradients["nuit"]!
            let linear = CIFilter.linearGradient()
            linear.point0 = CGPoint(x: 0, y: size.height)
            linear.point1 = CGPoint(x: size.width, y: 0)
            linear.color0 = color(pair.0)
            linear.color1 = color(pair.1)
            return (linear.outputImage ?? CIImage(color: color(pair.0))).cropped(to: rect)
        }
    }

    private static func color(_ hex: UInt32) -> CIColor {
        CIColor(red: CGFloat((hex >> 16) & 0xFF) / 255,
                green: CGFloat((hex >> 8) & 0xFF) / 255,
                blue: CGFloat(hex & 0xFF) / 255)
    }
}
