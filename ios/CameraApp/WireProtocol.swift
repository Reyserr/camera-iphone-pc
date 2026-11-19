import Foundation

/// Types de messages échangés avec l'app PC (voir docs/protocole.md).
enum MessageType: UInt8 {
    case hello = 0x01
    case frameJPEG = 0x02
    case frameH264 = 0x03
    case command = 0x10
    case status = 0x11
    case log = 0x12
    case ping = 0x20
    case pong = 0x21
}

/// Format d'un message : [taille des données : 4 octets gros-boutiste][type : 1 octet][données]
enum WireProtocol {
    static let version = 1
    static let port: UInt16 = 5050
    static let bonjourType = "_camiphone._tcp"
    static let maxPayload = 1_000_000

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
}

/// Journal de l'app : affiché sur l'iPhone et envoyé au PC quand il est connecté.
enum AppLog {
    static var sink: ((String) -> Void)?

    static func write(_ text: String) {
        print(text)
        sink?(text)
    }
}
