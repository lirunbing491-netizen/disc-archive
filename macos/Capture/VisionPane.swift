import AppKit
import Vision

final class VisionPane: NSView {
    private let modes = NSPopUpButton()
    private let endpointField = NSTextField(string: "")
    private let modelField = NSTextField(string: "")
    private let keyField = NSSecureTextField(string: "")
    private let fullPhoto = NSButton(checkboxWithTitle: "定位失败时允许发送整图（可能包含背景及私人信息）", target: nil, action: nil)
    private let saveButton = NSButton(title: "保存服务设置", target: nil, action: nil)
    private let testButton = NSButton(title: "随机字符图片自测（会调用模型）", target: nil, action: nil)
    private let cancelButton = NSButton(title: "取消识图", target: nil, action: nil)
    private let status = NSTextField(wrappingLabelWithString: "默认仅用卷标，不识图。离线 OCR 不联网。模型识图会把处理后的盘面发送给所填服务，可能收费；结果仅作线索。")
    private let defaults = UserDefaults.standard
    private var savedEndpoint = "", savedModel = "", savedKey = ""
    private var saved = false
    private var operation = UUID()
    private var transport: VisionTransport?
    private var request: VNRecognizeTextRequest?
    private var completion: ((Result<String, Error>) -> Void)?
    private var timeout: DispatchWorkItem?
    private var editable = true
    var mode: String { ["metadata", "ocr", "model"][max(0, min(2, modes.indexOfSelectedItem))] }

    init() {
        super.init(frame: .zero)
        modes.addItems(withTitles: ["仅卷标（默认）", "本机离线 OCR", "可选外部识图模型"])
        modes.target = self; modes.action = #selector(modeChanged)
        endpointField.placeholderString = "https://服务地址/v1 或完整 /chat/completions"
        modelField.placeholderString = "支持图片的模型名称"
        keyField.placeholderString = "API Key（只存于本机钥匙串；本机无密钥服务可留空）"
        saveButton.target = self; saveButton.action = #selector(saveSettings)
        testButton.target = self; testButton.action = #selector(testModel)
        cancelButton.target = self; cancelButton.action = #selector(cancel)
        let buttons = NSStackView(views: [saveButton, testButton, cancelButton]); buttons.orientation = .horizontal
        let stack = NSStackView(views: [modes, endpointField, modelField, keyField, fullPhoto, buttons, status])
        stack.orientation = .vertical; stack.alignment = .leading; stack.spacing = 7; stack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(stack)
        NSLayoutConstraint.activate([stack.leadingAnchor.constraint(equalTo: leadingAnchor), stack.trailingAnchor.constraint(equalTo: trailingAnchor), stack.topAnchor.constraint(equalTo: topAnchor), stack.bottomAnchor.constraint(equalTo: bottomAnchor), endpointField.widthAnchor.constraint(equalTo: stack.widthAnchor), modelField.widthAnchor.constraint(equalTo: stack.widthAnchor), keyField.widthAnchor.constraint(equalTo: stack.widthAnchor)])
        endpointField.stringValue = defaults.string(forKey: "discVision.endpoint") ?? ""
        modelField.stringValue = defaults.string(forKey: "discVision.model") ?? ""
        // Mode and full-photo consent deliberately reset each launch; saved service alone never starts network activity.
        if defaults.bool(forKey: "discVision.saved") {
            do {
                _ = try VisionPrivacy.endpoint(endpointField.stringValue)
                savedKey = try VisionKeychain.read(); keyField.stringValue = savedKey
                savedEndpoint = endpointField.stringValue; savedModel = modelField.stringValue
                saved = !savedModel.isEmpty
            } catch { status.stringValue = error.localizedDescription }
        }
        updateControls()
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func viewDidMoveToWindow() { super.viewDidMoveToWindow(); if window == nil { cancel() } }
    deinit { timeout?.cancel(); request?.cancel(); transport?.cancel() }

    func setEditable(_ value: Bool) { editable = value; updateControls() }
    private func updateControls() {
        let idle = completion == nil, model = mode == "model"
        modes.isEnabled = editable && idle
        endpointField.isEnabled = editable && idle && model; modelField.isEnabled = editable && idle && model; keyField.isEnabled = editable && idle && model
        fullPhoto.isEnabled = editable && idle && model
        saveButton.isEnabled = editable && idle && model; testButton.isEnabled = editable && idle && model
        cancelButton.isEnabled = !idle
    }
    @objc private func modeChanged() { updateControls() }
    @objc private func saveSettings() {
        do {
            _ = try VisionPrivacy.endpoint(endpointField.stringValue)
            let model = modelField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !model.isEmpty, model.count <= 200, keyField.stringValue.count <= 8192, !keyField.stringValue.contains("\n"), !keyField.stringValue.contains("\r") else { throw CaptureFailure("请填写模型名称并检查密钥格式。") }
            try VisionKeychain.save(keyField.stringValue)
            savedEndpoint = endpointField.stringValue; savedModel = model; savedKey = keyField.stringValue; saved = true
            defaults.set(savedEndpoint, forKey: "discVision.endpoint"); defaults.set(savedModel, forKey: "discVision.model"); defaults.set(true, forKey: "discVision.saved")
            status.stringValue = "服务设置已保存，密钥存于钥匙串。模型识图和自测会调用该服务，可能收费；尚未发送任何照片。"
        } catch { status.stringValue = error.localizedDescription }
    }
    private func checkedSettings() throws -> (URL, String, String) {
        guard saved, endpointField.stringValue == savedEndpoint, modelField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines) == savedModel, keyField.stringValue == savedKey else { throw CaptureFailure("请先主动保存当前服务地址、模型和密钥，再调用模型。") }
        return (try VisionPrivacy.endpoint(savedEndpoint), savedModel, savedKey)
    }
    private func begin(_ callback: @escaping (Result<String, Error>) -> Void) -> UUID {
        cancel(); operation = UUID(); completion = callback; updateControls()
        let token = operation
        let work = DispatchWorkItem { [weak self] in
            guard let self = self, self.operation == token else { return }
            self.request?.cancel(); self.transport?.cancel()
            self.finish(.failure(CaptureFailure("识图已超时，照片归档可继续。")), token: token)
        }
        timeout = work; DispatchQueue.main.asyncAfter(deadline: .now() + 22, execute: work)
        return token
    }
    private func finish(_ result: Result<String, Error>, token: UUID) {
        guard operation == token, let callback = completion else { return }
        timeout?.cancel(); timeout = nil; completion = nil; request = nil; transport = nil; operation = UUID()
        switch result {
        case .success: status.stringValue = "识图结束，结果仅作归类线索；请结合卷标核对。"
        case .failure(let error): status.stringValue = error.localizedDescription
        }
        updateControls(); callback(result)
    }
    @objc func cancel() {
        guard completion != nil else { return }
        let token = operation; request?.cancel(); transport?.cancel()
        finish(.failure(CaptureFailure("识图已取消，照片归档可继续。")), token: token)
    }
    /// Call on main; callback is delivered on main exactly once, including cancellation/failure.
    func read(photo: URL, completion: @escaping (Result<String, Error>) -> Void) {
        guard mode != "metadata" else { cancel(); completion(.success("")); return }
        let selected = mode, allowFull = fullPhoto.state == .on
        let settings: (URL, String, String)?
        do { settings = selected == "model" ? try checkedSettings() : nil }
        catch { completion(.failure(error)); return }
        let token = begin(completion)
        status.stringValue = selected == "ocr" ? "本机 OCR 中（不联网）…" : "正在本机定位并遮罩盘面，随后调用已保存服务（可能收费）…"
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            do {
                let image = try VisionPrivacy.load(photo)
                if selected == "ocr" {
                    let request = VNRecognizeTextRequest(); request.recognitionLevel = .accurate; request.usesLanguageCorrection = false
                    request.recognitionLanguages = ["zh-Hans", "en-US"]
                    DispatchQueue.main.async {
                        guard let self = self, self.operation == token else { return }; self.request = request
                        DispatchQueue.global(qos: .userInitiated).async {
                            do {
                                try VNImageRequestHandler(cgImage: image).perform([request])
                                let text = (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }.joined(separator: "\n")
                                DispatchQueue.main.async { self.finish(.success(String(text.prefix(12000))), token: token) }
                            } catch { DispatchQueue.main.async { self.finish(.failure(CaptureFailure("本机 OCR 失败，照片归档可继续。")), token: token) } }
                        }
                    }
                } else if let settings = settings {
                    let pixels = try VisionPrivacy.outboundImage(image, allowFull: allowFull)
                    let data = try VisionPrivacy.jpeg(pixels)
                    DispatchQueue.main.async {
                        guard let self = self, self.operation == token else { return }
                        self.send(data: data, settings: settings, token: token)
                    }
                }
            } catch { DispatchQueue.main.async { self?.finish(.failure(error), token: token) } }
        }
    }
    private func send(data: Data, settings: (URL, String, String), token: UUID) {
        let sender = VisionTransport(); transport = sender
        do { try sender.send(endpoint: settings.0, model: settings.1, key: settings.2, image: data) { [weak self] result in self?.finish(result, token: token) } }
        catch { finish(.failure(CaptureFailure("无法构建模型请求，照片归档可继续。")), token: token) }
    }
    @objc private func testModel() {
        do {
            let settings = try checkedSettings()
            let alphabet = Array("ABCDEFGHJKLMNPQRSTUVWXYZ23456789")
            let expected = String((0..<8).map { _ in alphabet.randomElement()! })
            // Generated characters are in image pixels only: never put the answer in the prompt.
            let image = try VisionPrivacy.outboundImage(VisionPrivacy.synthetic(expected), allowFull: false)
            let data = try VisionPrivacy.jpeg(image)
            let token = begin { [weak self] result in
                switch result {
                case .success(let text):
                    let normalized = String(text.uppercased().filter { $0.isASCII && ($0.isLetter || $0.isNumber) })
                    self?.status.stringValue = normalized.contains(expected) ? "随机图自测通过：模型读出了图片中的 \(expected)。不代表真实盘面必定准确。" : "随机图自测未通过：预期 \(expected)，模型返回 \(String(text.prefix(160)))。"
                case .failure: break
                }
            }
            status.stringValue = "正在发送随机字符合成盘图自测（可能收费；不包含用户照片）…"
            send(data: data, settings: settings, token: token)
        } catch { status.stringValue = error.localizedDescription }
    }
}
