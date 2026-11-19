import Foundation
import Network

/// Serveur TCP : attend l'app PC (Wi-Fi, ou USB via le service Apple du PC) et lui envoie les images.
/// Un seul PC à la fois : une nouvelle connexion remplace l'ancienne.
final class StreamServer {
    private let queue = DispatchQueue(label: "stream.server")
    private var listener: NWListener?
    private var connection: NWConnection?   // utilisé uniquement sur `queue`

    private let lock = NSLock()
    private var connected = false
    private var frameInFlight = false
    private var framesSent = 0

    /// Commande JSON reçue du PC (appelé sur le fil du serveur).
    var onCommand: (([String: Any]) -> Void)?
    /// Changement d'état : connecté ou non, et adresse du PC.
    var onConnectionChange: ((Bool, String) -> Void)?
    /// Contenu du message HELLO envoyé au PC à la connexion.
    var helloProvider: (() -> [String: Any])?
    /// Photo d'arrière-plan reçue du PC.
    var onBackgroundImage: ((Data) -> Void)?

    /// Vrai si un PC est connecté.
    var isConnected: Bool { lock.withLock { connected } }

    // MARK: - Démarrage

    func start() {
        queue.async { self.startListener() }
    }

    private func startListener() {
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        let parameters = NWParameters(tls: nil, tcp: tcp)
        parameters.allowLocalEndpointReuse = true

        guard let port = NWEndpoint.Port(rawValue: WireProtocol.port) else { return }
        let listener: NWListener
        do {
            listener = try NWListener(using: parameters, on: port)
        } catch {
            AppLog.write("Impossible de démarrer le serveur : \(error)")
            retryListener()
            return
        }
        // L'annonce Bonjour déclenche aussi la demande d'accès au réseau local d'iOS.
        listener.service = NWListener.Service(type: WireProtocol.bonjourType)
        listener.stateUpdateHandler = { [weak self] state in
            switch state {
            case .ready:
                AppLog.write("En attente du PC sur le port \(WireProtocol.port)")
            case .failed(let error):
                AppLog.write("Serveur arrêté : \(error)")
                listener.cancel()
                self?.retryListener()
            default:
                break
            }
        }
        listener.newConnectionHandler = { [weak self] newConnection in
            self?.accept(newConnection)
        }
        listener.start(queue: queue)
        self.listener = listener
    }

    private func retryListener() {
        listener = nil
        queue.asyncAfter(deadline: .now() + 2) { self.startListener() }
    }

    // MARK: - Connexion du PC

    private func accept(_ newConnection: NWConnection) {
        if let old = connection {
            AppLog.write("Nouveau PC : l'ancienne connexion est fermée")
            old.cancel()
        }
        connection = newConnection
        lock.withLock {
            connected = false
            frameInFlight = false
        }

        newConnection.stateUpdateHandler = { [weak self, weak newConnection] state in
            guard let self = self, let conn = newConnection else { return }
            switch state {
            case .ready:
                self.lock.withLock { self.connected = true }
                let peer = "\(conn.endpoint)"
                AppLog.write("PC connecté : \(peer)")
                self.onConnectionChange?(true, peer)
                if let hello = self.helloProvider?() {
                    conn.send(content: WireProtocol.json(.hello, hello), completion: .contentProcessed { _ in })
                }
                self.receiveHeader(on: conn)
            case .failed, .cancelled:
                guard self.connection === conn else { return }
                self.connection = nil
                self.lock.withLock {
                    self.connected = false
                    self.frameInFlight = false
                }
                AppLog.write("PC déconnecté")
                self.onConnectionChange?(false, "")
            default:
                break
            }
        }
        newConnection.start(queue: queue)
    }

    // MARK: - Réception

    private func receiveHeader(on conn: NWConnection) {
        conn.receive(minimumIncompleteLength: 5, maximumLength: 5) { [weak self] data, _, isComplete, error in
            guard let self = self else { return }
            if let error = error {
                AppLog.write("Réception : \(error)")
                conn.cancel()
                return
            }
            guard let data = data, data.count == 5 else {
                if isComplete { conn.cancel() }
                return
            }
            let bytes = [UInt8](data)
            let length = Int(bytes[0]) << 24 | Int(bytes[1]) << 16 | Int(bytes[2]) << 8 | Int(bytes[3])
            let type = bytes[4]
            guard length <= WireProtocol.maxPayload else {
                AppLog.write("Message trop grand (\(length) octets), connexion fermée")
                conn.cancel()
                return
            }
            if length == 0 {
                self.handle(type: type, payload: Data(), on: conn)
                self.receiveHeader(on: conn)
                return
            }
            conn.receive(minimumIncompleteLength: length, maximumLength: length) { payload, _, _, error in
                guard error == nil, let payload = payload, payload.count == length else {
                    conn.cancel()
                    return
                }
                self.handle(type: type, payload: payload, on: conn)
                self.receiveHeader(on: conn)
            }
        }
    }

    private func handle(type: UInt8, payload: Data, on conn: NWConnection) {
        switch MessageType(rawValue: type) {
        case .ping:
            conn.send(content: WireProtocol.message(.pong, payload), completion: .contentProcessed { _ in })
        case .command:
            if let object = try? JSONSerialization.jsonObject(with: payload) as? [String: Any] {
                onCommand?(object)
            }
        case .backgroundImage:
            onBackgroundImage?(payload)
        default:
            break
        }
    }

    // MARK: - Envoi (appelable depuis n'importe quel fil)

    /// Vrai si un PC est connecté et que l'image précédente est partie.
    func wantsFrame() -> Bool {
        lock.withLock { connected && !frameInFlight }
    }

    func sendFrame(_ jpeg: Data, timestamp: UInt64) {
        var payload = Data(capacity: 8 + jpeg.count)
        var ts = timestamp.bigEndian
        withUnsafeBytes(of: &ts) { payload.append(contentsOf: $0) }
        payload.append(jpeg)
        let message = WireProtocol.message(.frameJPEG, payload)

        setFrameInFlight(true)
        queue.async {
            guard let conn = self.connection else {
                self.setFrameInFlight(false)
                return
            }
            conn.send(content: message, completion: .contentProcessed { [weak self] error in
                guard let self = self else { return }
                self.lock.withLock {
                    self.frameInFlight = false
                    if error == nil { self.framesSent += 1 }
                }
            })
        }
    }

    func send(_ message: Data) {
        queue.async {
            self.connection?.send(content: message, completion: .contentProcessed { _ in })
        }
    }

    /// Nombre d'images envoyées depuis le dernier appel (pour les images/s).
    func takeFrameCount() -> Int {
        lock.withLock {
            let count = framesSent
            framesSent = 0
            return count
        }
    }

    private func setFrameInFlight(_ value: Bool) {
        lock.withLock { frameInFlight = value }
    }
}
