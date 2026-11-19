import AVFoundation
import SwiftUI

struct ContentView: View {
    @ObservedObject var model: AppModel

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            CameraPreview(session: model.camera.session) { layer in
                model.camera.attachPreview(layer)
            }
            .ignoresSafeArea()

            VStack {
                Spacer()
                if !model.cameraAuthorized {
                    Text("Accès à la caméra refusé.\nAutorise-le dans Réglages › Caméra PC.")
                        .multilineTextAlignment(.center)
                        .padding()
                        .background(Color.red.opacity(0.8), in: RoundedRectangle(cornerRadius: 12))
                        .foregroundColor(.white)
                }
                controlPanel
            }
            .padding()

            if model.blackScreen {
                Color.black
                    .ignoresSafeArea()
                    .overlay(
                        Text("Diffusion en cours\nTouche l'écran pour revenir")
                            .multilineTextAlignment(.center)
                            .font(.footnote)
                            .foregroundColor(Color(white: 0.25))
                    )
                    .onTapGesture { model.blackScreen = false }
            }
        }
        .statusBarHidden(model.blackScreen)
        .onAppear { model.start() }
    }

    private var controlPanel: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Circle()
                    .fill(model.isConnected ? Color.green : Color.orange)
                    .frame(width: 10, height: 10)
                Text(model.isConnected ? "Connecté au PC" : "En attente du PC…")
                    .font(.headline)
                Spacer()
                if model.isConnected {
                    Text("\(model.fps) img/s")
                        .font(.subheadline.monospacedDigit())
                }
            }

            VStack(alignment: .leading, spacing: 2) {
                Text("Adresse à saisir sur le PC")
                    .font(.caption)
                    .foregroundColor(.secondary)
                Text("\(model.ipAddress)   port \(String(WireProtocol.port))")
                    .font(.title3.monospacedDigit().bold())
                    .textSelection(.enabled)
            }

            HStack(spacing: 10) {
                Menu {
                    ForEach(CameraKind.allCases, id: \.self) { kind in
                        Button(kind.label) { model.selectCamera(kind) }
                    }
                } label: {
                    Label(model.cameraKind.label, systemImage: "camera.rotate")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(.bordered)

                Button {
                    model.setTorch(!model.torchOn)
                } label: {
                    Image(systemName: model.torchOn ? "flashlight.on.fill" : "flashlight.off.fill")
                }
                .buttonStyle(.bordered)
                .disabled(model.cameraKind == .front)

                Button {
                    model.blackScreen = true
                } label: {
                    Image(systemName: "moon.fill")
                }
                .buttonStyle(.bordered)
            }

            if !model.lastLog.isEmpty {
                Text(model.lastLog)
                    .font(.caption2)
                    .foregroundColor(.secondary)
                    .lineLimit(2)
            }
        }
        .padding()
        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 16))
    }
}

/// Aperçu de la caméra (couche AVCaptureVideoPreviewLayer dans SwiftUI).
struct CameraPreview: UIViewRepresentable {
    let session: AVCaptureSession
    let onLayer: (AVCaptureVideoPreviewLayer) -> Void

    final class PreviewView: UIView {
        override class var layerClass: AnyClass { AVCaptureVideoPreviewLayer.self }
        var previewLayer: AVCaptureVideoPreviewLayer { layer as! AVCaptureVideoPreviewLayer }
    }

    func makeUIView(context: Context) -> PreviewView {
        let view = PreviewView()
        view.backgroundColor = .black
        view.previewLayer.session = session
        view.previewLayer.videoGravity = .resizeAspect
        onLayer(view.previewLayer)
        return view
    }

    func updateUIView(_ uiView: PreviewView, context: Context) {}
}
