import AppKit
import AVFoundation
import CoreImage

/// Camera is opened only by the explicit Preview action. All public calls are on main.
final class CapturePane: NSView, AVCaptureVideoDataOutputSampleBufferDelegate {
    private let beforeCapture: () throws -> String
    private let onPhoto: (URL, String) -> Void
    private let cameras = NSPopUpButton()
    private let previewButton = NSButton(title: "开始预览", target: nil, action: nil)
    private let shutter = NSButton(title: "拍摄盘面", target: nil, action: nil)
    private let stopButton = NSButton(title: "关闭摄像头", target: nil, action: nil)
    private let status = NSTextField(wrappingLabelWithString: "选择内置或 USB 摄像头，再手动开始预览。拍摄保留完整视频帧分辨率。")
    private let surface = NSView()
    private let session = AVCaptureSession()
    private let queue = DispatchQueue(label: "com.discarchive.mac.camera")
    private var previewLayer: AVCaptureVideoPreviewLayer?
    private var devices: [AVCaptureDevice] = []
    private var latestPixelBuffer: CVPixelBuffer?
    private var latestSampleAt = Date.distantPast
    private var enabled = true
    private var running = false
    private var capturing = false
    private var generation = UUID()
    private var cameraPageActive = false
    private var previewReadyAt = Date.distantPast
    private var frameGeneration = UUID() // Accessed only on the camera queue.
    private var shortcutGate = CaptureShortcutGate()
    private var keyMonitor: Any?
    private var menuObservers: [NSObjectProtocol] = []
    private var menuTrackingDepth = 0

    init(beforeCapture: @escaping () throws -> String, onPhoto: @escaping (URL, String) -> Void) {
        self.beforeCapture = beforeCapture; self.onPhoto = onPhoto
        super.init(frame: .zero)
        surface.wantsLayer = true; surface.layer?.backgroundColor = NSColor.black.cgColor
        surface.heightAnchor.constraint(equalToConstant: 220).isActive = true
        previewButton.target = self; previewButton.action = #selector(startPreview)
        shutter.target = self; shutter.action = #selector(takePhoto)
        stopButton.target = self; stopButton.action = #selector(stopAction)
        cameras.target = self; cameras.action = #selector(cameraChanged)
        let refresh = NSButton(title: "刷新设备", target: self, action: #selector(refreshDevices))
        let row = NSStackView(views: [cameras, refresh, previewButton, shutter, stopButton])
        row.orientation = .horizontal
        let stack = NSStackView(views: [row, surface, status]); stack.orientation = .vertical
        stack.alignment = .leading; stack.spacing = 8; stack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(stack)
        NSLayoutConstraint.activate([stack.leadingAnchor.constraint(equalTo: leadingAnchor), stack.trailingAnchor.constraint(equalTo: trailingAnchor), stack.topAnchor.constraint(equalTo: topAnchor), stack.bottomAnchor.constraint(equalTo: bottomAnchor), surface.widthAnchor.constraint(equalTo: stack.widthAnchor)])
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .keyUp]) { [weak self] event in
            guard let self = self else { return event }
            return self.handleShortcut(event)
        }
        menuObservers = [NSMenu.didBeginTrackingNotification, NSMenu.didEndTrackingNotification].map { name in
            NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                guard let self = self else { return }
                self.menuTrackingDepth = max(0, self.menuTrackingDepth + (name == NSMenu.didBeginTrackingNotification ? 1 : -1))
            }
        }
        refreshDevices(); updateButtons()
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func layout() { super.layout(); previewLayer?.frame = surface.bounds }
    override func viewDidMoveToWindow() { super.viewDidMoveToWindow(); if window == nil { stopPreview() } }
    deinit {
        if let monitor = keyMonitor { NSEvent.removeMonitor(monitor) }
        menuObservers.forEach { NotificationCenter.default.removeObserver($0) }
        let s = session; queue.async { s.stopRunning(); s.inputs.forEach { s.removeInput($0) }; s.outputs.forEach { s.removeOutput($0) } }
    }

    func setCameraPageActive(_ active: Bool) { cameraPageActive = active; shortcutGate.reset() }
    private func handleShortcut(_ event: NSEvent) -> NSEvent? {
        guard CaptureShortcutGate.keys.contains(event.keyCode) else { return event }
        if event.type == .keyUp { shortcutGate.release(event.keyCode); return event }
        let blockedModifiers = event.modifierFlags.intersection([.command, .control, .option, .shift])
        let focusedControl = window?.firstResponder as? NSControl
        let editing = window?.firstResponder is NSTextView || (focusedControl != nil && focusedControl !== shutter)
        let ready = cameraPageActive && !isHiddenOrHasHiddenAncestor && window?.isKeyWindow == true &&
            NSApp.isActive && NSApp.modalWindow == nil && window?.attachedSheet == nil &&
            menuTrackingDepth == 0 && !editing && blockedModifiers.isEmpty &&
            shutter.isEnabled && Date().timeIntervalSince(previewReadyAt) < 1.5
        if shortcutGate.press(event.keyCode, isRepeat: event.isARepeat, eligible: ready) {
            // Mouse and keyboard always resolve the slot and capture through one action.
            shutter.performClick(nil); return nil
        }
        // Suppress repeat/default-button activation while this shortcut owns the key.
        return shortcutGate.owns(event.keyCode) ? nil : event
    }

    func setCaptureEnabled(_ enabled: Bool) { self.enabled = enabled; updateButtons() }
    private func updateButtons() {
        shutter.isEnabled = enabled && running && previewReadyAt != .distantPast && !capturing
        cameras.isEnabled = !running && !capturing
        previewButton.isEnabled = !running && !devices.isEmpty && !capturing
        stopButton.isEnabled = running || capturing
    }
    @objc private func refreshDevices() {
        guard !running else { return }
        let previous = cameras.indexOfSelectedItem
        devices = AVCaptureDevice.DiscoverySession(deviceTypes: [.builtInWideAngleCamera, .externalUnknown], mediaType: .video, position: .unspecified).devices
        cameras.removeAllItems(); cameras.addItems(withTitles: devices.map { $0.localizedName })
        if devices.indices.contains(previous) { cameras.selectItem(at: previous) }
        status.stringValue = devices.isEmpty ? "未发现摄像头。接入 USB 摄像头后点击刷新设备。" : "选择摄像头后点击开始预览；不会自动打开设备。"
        updateButtons()
    }
    @objc private func cameraChanged() { stopPreview() }
    @objc private func stopAction() { stopPreview() }
    func stopPreview() {
        generation = UUID(); running = false; capturing = false; previewReadyAt = .distantPast
        previewLayer?.removeFromSuperlayer(); previewLayer = nil
        queue.async { [weak self] in
            guard let self = self else { return }
            self.session.stopRunning()
            self.session.beginConfiguration()
            self.session.inputs.forEach { self.session.removeInput($0) }
            self.session.outputs.forEach { self.session.removeOutput($0) }
            self.session.commitConfiguration(); self.latestPixelBuffer = nil
        }
        status.stringValue = "摄像头已关闭。"; updateButtons()
    }
    @objc private func startPreview() {
        guard devices.indices.contains(cameras.indexOfSelectedItem), !running else { return }
        let device = devices[cameras.indexOfSelectedItem], token = UUID(); generation = token
        previewButton.isEnabled = false
        let begin: (Bool) -> Void = { [weak self] granted in
            DispatchQueue.main.async {
                guard let self = self, self.generation == token else { return }
                guard granted else { self.status.stringValue = "摄像头权限未开启。请在系统设置 → 隐私与安全性 → 摄像头中允许本应用。"; self.updateButtons(); return }
                self.configure(device: device, token: token)
            }
        }
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized: begin(true)
        case .notDetermined: AVCaptureDevice.requestAccess(for: .video, completionHandler: begin)
        default: begin(false)
        }
    }
    private func configure(device: AVCaptureDevice, token: UUID) {
        queue.async { [weak self] in
            guard let self = self else { return }
            do {
                let input = try AVCaptureDeviceInput(device: device)
                let output = AVCaptureVideoDataOutput()
                output.alwaysDiscardsLateVideoFrames = true
                output.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA]
                output.setSampleBufferDelegate(self, queue: self.queue)
                self.session.beginConfiguration()
                self.session.inputs.forEach { self.session.removeInput($0) }
                self.session.outputs.forEach { self.session.removeOutput($0) }
                guard self.session.canAddInput(input), self.session.canAddOutput(output) else {
                    self.session.commitConfiguration(); throw CaptureFailure("此摄像头不支持视频预览。")
                }
                self.session.addInput(input); self.session.addOutput(output)
                if self.session.canSetSessionPreset(.high) { self.session.sessionPreset = .high }
                self.session.commitConfiguration(); self.frameGeneration = token; self.session.startRunning()
                DispatchQueue.main.async {
                    guard self.generation == token else { return }
                    self.running = self.session.isRunning
                    let layer = AVCaptureVideoPreviewLayer(session: self.session); layer.videoGravity = .resizeAspect
                    layer.frame = self.surface.bounds; self.surface.layer?.addSublayer(layer); self.previewLayer = layer
                    self.status.stringValue = self.running ? "预览中。按 Enter 或空格拍摄；输入文字、弹窗或菜单打开时不触发。拍摄绑定当前盘槽，保存完整画面。" : "摄像头未能启动，请重新选择设备。"
                    self.updateButtons()
                }
            } catch {
                DispatchQueue.main.async { if self.generation == token { self.status.stringValue = "摄像头启动失败：\(error.localizedDescription)"; self.updateButtons() } }
            }
        }
    }
    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        latestPixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer); latestSampleAt = Date()
        let token = frameGeneration, sampledAt = latestSampleAt
        DispatchQueue.main.async { [weak self] in
            guard let self = self, self.generation == token, self.running else { return }
            self.previewReadyAt = sampledAt; self.updateButtons()
        }
    }
    @objc private func takePhoto() {
        guard enabled, running, !capturing else { return }
        // Resolve slot BEFORE queuing image work. Never look up a slot again in the callback.
        let slot: String
        do { slot = try beforeCapture(); guard !slot.isEmpty else { throw CaptureFailure("请先选择盘槽。") } }
        catch { status.stringValue = error.localizedDescription; return }
        capturing = true; updateButtons(); let token = generation
        queue.async { [weak self] in
            guard let self = self else { return }
            do {
                guard let pixels = self.latestPixelBuffer, Date().timeIntervalSince(self.latestSampleAt) < 1.5 else { throw CaptureFailure("未收到新画面，请检查摄像头后重拍。") }
                let image = CIImage(cvPixelBuffer: pixels)
                guard let fullFrame = CIContext().createCGImage(image, from: image.extent) else { throw CaptureFailure("无法生成完整画面。") }
                let data = try VisionPrivacy.jpeg(fullFrame)
                let dir = FileManager.default.temporaryDirectory.appendingPathComponent("DiscArchivePhotos", isDirectory: true)
                try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                let url = dir.appendingPathComponent(UUID().uuidString).appendingPathExtension("jpg")
                try data.write(to: url, options: .atomic)
                DispatchQueue.main.async {
                    guard self.generation == token else { try? FileManager.default.removeItem(at: url); return }
                    self.capturing = false; self.updateButtons()
                    self.status.stringValue = "已拍摄完整画面 \(CVPixelBufferGetWidth(pixels)) × \(CVPixelBufferGetHeight(pixels))。"
                    self.onPhoto(url, slot)
                }
            } catch {
                DispatchQueue.main.async { if self.generation == token { self.capturing = false; self.updateButtons(); self.status.stringValue = error.localizedDescription } }
            }
        }
    }
}

/// Pure key state, so held-key and eligibility behavior can be tested without a camera.
struct CaptureShortcutGate {
    static let keys: Set<UInt16> = [36, 49, 76] // Return, Space, keypad Enter.
    private var held: Set<UInt16> = []
    private var owned: Set<UInt16> = []
    mutating func press(_ key: UInt16, isRepeat: Bool, eligible: Bool) -> Bool {
        guard Self.keys.contains(key), held.insert(key).inserted, !isRepeat, eligible else { return false }
        owned.insert(key); return true
    }
    func owns(_ key: UInt16) -> Bool { owned.contains(key) }
    mutating func release(_ key: UInt16) { held.remove(key); owned.remove(key) }
    mutating func reset() { held.removeAll(); owned.removeAll() }
}

struct CaptureFailure: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}
