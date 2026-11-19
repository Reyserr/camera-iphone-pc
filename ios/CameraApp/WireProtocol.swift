import AVFoundation
import Foundation

/// Types de messages échangés avec l'app PC (voir docs/protocole.md).
enum MessageType: UInt8 {
    case hello = 0x01
    case frameJPEG = 0x02
    case frameH264 = 0x03
    case audioPCM = 0x04
    case command = 0x10
    case status = 0x11
    case log = 0x12
    case backgroundImage = 0x13
    case photo = 0x14
    case ping = 0x20
    case pong = 0x21
}

/// Format d'un message : [taille des données : 4 octets gros-boutiste][type : 1 octet][données]
enum WireProtocol {
    static let version = 2
    static let port: UInt16 = 5050
    static let bonjourType = "_camiphone._tcp"
    static let maxPayload = 8_000_000

    static func message(_ type: MessageType, _ payload: Data) -> Data {
        var data = Data(capacity: 5 + payload.count)
        var length = UInt32(payload.count).bigEndian
        withUnsafeBytes(of: &length) { data.append(contentsOf: $0) }
        data.append(type.rawValue)
        data.append(payload)
        return data
    }

    static func json(_ type: MessageType, _ object: [String: Any]) -> Data {
        let payload = (try? JSONSerialization.data(withJSONObject: object)) ?? Data()
        return message(type, payload)
    }

    /// Son du micro : [fréquence : 4 octets][voies : 1][bits : 1][options : 1][0][échantillons PCM]
    /// Options : bit 0 = nombres à virgule (float).
    static func audioMessage(from sampleBuffer: CMSampleBuffer) -> Data? {
        guard let format = CMSampleBufferGetFormatDescription(sampleBuffer),
              let description = CMAudioFormatDescriptionGetStreamBasicDescription(format)?.pointee,
              description.mFormatID == kAudioFormatLinearPCM,
              let block = CMSampleBufferGetDataBuffer(sampleBuffer) else { return nil }

        let length = CMBlockBufferGetDataLength(block)
        guard length > 0 else { return nil }
        var samples = Data(count: length)
        let status = samples.withUnsafeMutableBytes { raw -> OSStatus in
            guard let base = raw.baseAddress else { return -1 }
            return CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: length, destination: base)
        }
        guard status == noErr else { return nil }

        var channels = Int(description.mChannelsPerFrame)
        let nonInterleaved = description.mFormatFlags & kAudioFormatFlagIsNonInterleaved != 0
        if nonInterleaved && channels > 1 {
            // Voies séparées : on garde seulement la première.
            samples = samples.prefix(length / channels)
            channels = 1
        }
        let isFloat = description.mFormatFlags & kAudioFormatFlagIsFloat != 0

        var header = Data(capacity: 8)
        var rate = UInt32(description.mSampleRate).bigEndian
        withUnsafeBytes(of: &rate) { header.append(contentsOf: $0) }
        header.append(UInt8(max(1, min(channels, 8))))
        header.append(UInt8(description.mBitsPerChannel))
        header.append(isFloat ? 1 : 0)
        header.append(0)
        return message(.audioPCM, header + samples)
    }
}

/// Journal de l'app : affiché sur l'iPhone et envoyé au PC quand il est connecté.
enum AppLog {
    static var sink: ((String) -> Void)?

    static func write(_ text: String) {
        print(text)
        sink?(text)
    }
}
