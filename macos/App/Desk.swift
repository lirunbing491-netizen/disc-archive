import AppKit
import UserNotifications

private struct DeskDrive {
    var id: String, name: String, token: String, label: String
    var empty: Bool, ready: Bool
    init(_ o: IPCObject) { id = o["id"] as? String ?? ""; name = o["name"] as? String ?? id; token = o["token"] as? String ?? ""; label = o["label"] as? String ?? ""; empty = o["empty"] as? Bool ?? true; ready = o["ready"] as? Bool ?? false }
    // Native token starts with a per-process UUID. This suffix is ONLY a UI observation hint;
    // core must obtain and verify its own complete token, never call da_verify with this suffix.
    var mediaHint: String { let pieces = token.split(separator: ":", maxSplits: 1); return pieces.count == 2 ? String(pieces[1]) : "" }
}
private struct DeskSubmission {
    var drive: DeskDrive, target: String, targetId: String?, project: String, existingProject: String?, mode: String, view: String, resumeId: String?
    var payload: IPCObject {
        var o: IPCObject = ["drive": drive.id, "target": target, "project": project, "emptyAtShutter": drive.empty, "mode": mode, "view": view, "mediaHint": drive.mediaHint]
        if let id = targetId { o["targetId"] = id }; if let existing = existingProject { o["existingProject"] = existing }; if let resume = resumeId { o["resumeId"] = resume }; return o
    }
}
private final class DeskTask {
    var process: CoreIPC.Job?
    let submission: DeskSubmission
    var status = "正在登记…", log = "", entry: IPCObject?, pending: IPCObject?
    var expectedHint: String, disconnected = false, registered = false, gotResult = false, hadError = false
    init(_ submission: DeskSubmission) { self.submission = submission; expectedHint = submission.drive.mediaHint }
}
private final class PhotoDropView: NSView {
    var receive: ((URL) -> Void)?
    override init(frame: NSRect) { super.init(frame: frame); registerForDraggedTypes([.fileURL]); wantsLayer = true; layer?.borderWidth = 1; layer?.borderColor = NSColor.separatorColor.cgColor; layer?.cornerRadius = 6 }
    required init?(coder: NSCoder) { fatalError() }
    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation { imageURLs(sender).count == 1 ? .copy : [] }
    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool { let urls = imageURLs(sender); guard urls.count == 1 else { return false }; receive?(urls[0]); return true }
    private func imageURLs(_ sender: NSDraggingInfo) -> [URL] { ((sender.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL]) ?? []).filter { ["jpg", "jpeg", "png", "heic", "tif", "tiff"].contains($0.pathExtension.lowercased()) } }
}
private final class DeskMenuAction: NSObject {
    let handler: () -> Void
    init(_ handler: @escaping () -> Void) { self.handler = handler }
    @objc func changed() { handler() }
}

@MainActor final class Desk: NSObject, NSApplicationDelegate, NSWindowDelegate, NSTableViewDataSource, NSTableViewDelegate, NSTextFieldDelegate {
    private var window: NSWindow!
    private let driveMenu = NSPopUpButton(), targetsMenu = NSPopUpButton(), projectMenu = NSPopUpButton(), viewMenu = NSPopUpButton()
    private let targetField = NSTextField(string: ""), projectField = NSTextField(string: "")
    private let manualCheck = NSButton(checkboxWithTitle: "已人工核对：当前所选光驱的实体盘与这张照片一致", target: nil, action: nil)
    private let overall = NSTextField(wrappingLabelWithString: "正在读取本机光驱…")
    private let selectedStatus = NSTextField(wrappingLabelWithString: "请选择光驱和归档目录。")
    private let photoLabel = NSTextField(wrappingLabelWithString: "照片来自本机：选一张照片、拖入照片，或手动打开摄像头。")
    private let logView = NSTextView(), preview = NSImageView()
    private let table = NSTableView(), search = NSSearchField(), editTitle = NSTextField(string: ""), editProject = NSTextField(string: "")
    private let recordNote = NSTextField(wrappingLabelWithString: "选择登记后可改标签、续导或查看回执。")
    private let watchNote = NSTextField(wrappingLabelWithString: "未启用照片文件夹接收。每次只接收一张，固定绑定启用时的光驱。")
    private let tabs = NSTabView()
    private var capture: CapturePane!, vision: VisionPane!
    private var drives: [DeskDrive] = [], targets: [IPCObject] = [], jobs: [String: DeskTask] = [:]
    private var selectedDrive = "", targetId: String?, entries: [IPCObject] = [], filtered: [IPCObject] = []
    private var shots: [String: DeskSubmission] = [:]
    private var timer: Timer?, polling = false, catalogLoading = false, closing = false
    private var notificationRequested = false
    private var recovered = false
    private var sheetOpen = false
    private var watchFolder: URL?, watchDrive: String?, watchSeen: Set<String> = [], watchCandidate: (String, UInt64, Date)?

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildWindow(); capture.setCaptureEnabled(false); overall.stringValue = "正在核对上次中断记录，完成前不允许登记。"
        CoreIPC.run("recover") { [weak self] result in
            guard let self = self else { return }
            switch result {
            case .success:
                self.recovered = true; self.refreshDevices(); self.refreshCatalog()
                self.timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in Task { @MainActor in self?.refreshDevices(); self?.pollPhotoFolder() } }
            case .failure(let error): self.overall.stringValue = "上次中断记录核对失败，登记已暂停：\(error.localizedDescription)"; self.notice("请修复本地归档组件后重新启动。未开始任何导盘任务。\n\(error.localizedDescription)")
            }
        }
        NSApp.activate(ignoringOtherApps: true)
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard jobs.values.contains(where: { $0.process != nil }) else { timer?.invalidate(); capture.stopPreview(); vision.cancel(); return .terminateNow }
        guard !closing else { return .terminateLater }
        let alert = NSAlert(); alert.messageText = "仍有光驱在工作"
        alert.informativeText = "选择“停止并等待退出”会通知所有光驱安全停止，等每个进程结束后退出；已登记和已导出的内容保留。"
        alert.addButton(withTitle: "继续工作"); alert.addButton(withTitle: "停止并等待退出")
        alert.beginSheetModal(for: window) { [weak self] response in
            guard let self = self else { return }
            if response == .alertSecondButtonReturn {
                self.closing = true; self.capture.stopPreview(); self.vision.cancel(); self.disableWatch()
                for job in self.jobs.values where job.process != nil { job.process?.send(["action": "cancel"]) }
                self.overall.stringValue = "已请求所有光驱停止，等待它们保存回执并结束后退出。"
                self.maybeQuit()
            } else { NSApp.reply(toApplicationShouldTerminate: false) }
        }
        return .terminateLater
    }
    func windowShouldClose(_ sender: NSWindow) -> Bool { NSApp.terminate(nil); return false }
    private func maybeQuit() { if closing && !jobs.values.contains(where: { $0.process != nil }) { timer?.invalidate(); NSApp.reply(toApplicationShouldTerminate: true) } }

    private func button(_ title: String, _ action: Selector) -> NSButton { NSButton(title: title, target: self, action: action) }
    private func row(_ views: [NSView]) -> NSStackView { let s = NSStackView(views: views); s.orientation = .horizontal; s.spacing = 8; s.alignment = .centerY; return s }
    private func column(_ views: [NSView]) -> NSStackView { let s = NSStackView(views: views); s.orientation = .vertical; s.spacing = 10; s.alignment = .leading; return s }
    private func label(_ text: String) -> NSTextField { NSTextField(labelWithString: text) }
    private func stretch(_ view: NSView, width: CGFloat) { view.widthAnchor.constraint(greaterThanOrEqualToConstant: width).isActive = true }
    private func addTab(_ title: String, _ view: NSView) { let item = NSTabViewItem(identifier: title); item.label = title; item.view = view; tabs.addTabViewItem(item) }
    private func buildWindow() {
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 830), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "光盘归档工作台"; window.minSize = NSSize(width: 950, height: 750); window.delegate = self; window.center()
        driveMenu.target = self; driveMenu.action = #selector(selectDrive); stretch(driveMenu, width: 300)
        targetsMenu.target = self; targetsMenu.action = #selector(selectSuggestedTarget); stretch(targetsMenu, width: 160)
        projectMenu.target = self; projectMenu.action = #selector(selectProject); stretch(projectMenu, width: 200)
        projectField.placeholderString = "填写项目名，或从已有项目选择"; stretch(projectField, width: 280)
        targetField.isEditable = false; stretch(targetField, width: 500)
        viewMenu.addItems(withTitles: ["自动读取", "Mac视图", "UDF视图", "ISO视图"])
        capture = CapturePane(beforeCapture: { [weak self] in
            guard let self = self else { throw CaptureFailure("工作台已关闭。") }
            let submission = try self.snapshot(); self.shots[submission.drive.id] = submission; return submission.drive.id
        }, onPhoto: { [weak self] url, drive in
            guard let self = self, let submission = self.shots.removeValue(forKey: drive) else { return }
            self.start(photo: url, submission: submission, ownTemporary: true)
        })
        vision = VisionPane()
        let drop = PhotoDropView(frame: .zero); drop.heightAnchor.constraint(equalToConstant: 65).isActive = true
        let dropText = label("将一张盘面照片拖到这里（按此刻所选光驱登记）")
        dropText.translatesAutoresizingMaskIntoConstraints = false; drop.addSubview(dropText)
        NSLayoutConstraint.activate([dropText.centerXAnchor.constraint(equalTo: drop.centerXAnchor), dropText.centerYAnchor.constraint(equalTo: drop.centerYAnchor)])
        drop.receive = { [weak self] url in Task { @MainActor in self?.importPhoto(url) } }
        let targetRow = row([label("归档目录"), targetField, button("选择目录…", #selector(chooseTarget))])
        let importPanel = column([targetRow, row([label("直连卷快捷入口"), targetsMenu]), row([label("项目"), projectField, projectMenu, button("刷新项目", #selector(refreshProjects))]), row([label("读取格式"), viewMenu, button("查看当前盘信息", #selector(inspectDisc))]), manualCheck, drop, row([button("选择盘面照片并登记…", #selector(choosePhoto)), button("停止所选光驱", #selector(stopSelected)), button("已核对所选盘，继续", #selector(confirmSelected))]), photoLabel, selectedStatus, row([button("启用照片文件夹接收…", #selector(chooseWatchFolder)), button("停止接收", #selector(stopWatch))]), watchNote])
        drop.widthAnchor.constraint(equalTo: importPanel.widthAnchor).isActive = true
        let logScroll = NSScrollView(); logScroll.hasVerticalScroller = true; logScroll.documentView = logView; logView.isEditable = false; logView.font = .monospacedSystemFont(ofSize: 12, weight: .regular); logView.isVerticallyResizable = true; logView.isHorizontallyResizable = false; logView.autoresizingMask = [.width]; logView.textContainer?.widthTracksTextView = true; logView.maxSize = NSSize(width: .greatestFiniteMagnitude, height: .greatestFiniteMagnitude); logScroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 150).isActive = true
        importPanel.addArrangedSubview(logScroll); logScroll.widthAnchor.constraint(equalTo: importPanel.widthAnchor).isActive = true
        addTab("导盘", importPanel); addTab("登记册", buildCatalog()); addTab("摄像头", column([label("摄像头使用顶部同一个光驱选择；快门时固定光驱、目录、项目及识图方式。"), capture])); addTab("识图设置", column([vision]))
        let top = row([label("当前光驱"), driveMenu, button("刷新设备", #selector(refreshAction))])
        let root = column([top, overall, tabs]); root.translatesAutoresizingMaskIntoConstraints = false
        window.contentView!.addSubview(root)
        NSLayoutConstraint.activate([root.leadingAnchor.constraint(equalTo: window.contentView!.leadingAnchor, constant: 16), root.trailingAnchor.constraint(equalTo: window.contentView!.trailingAnchor, constant: -16), root.topAnchor.constraint(equalTo: window.contentView!.topAnchor, constant: 14), root.bottomAnchor.constraint(equalTo: window.contentView!.bottomAnchor, constant: -14), tabs.widthAnchor.constraint(equalTo: root.widthAnchor)])
        window.makeKeyAndOrderFront(nil)
        let menu = NSMenu(); let application = NSMenuItem(); menu.addItem(application); let submenu = NSMenu(); submenu.addItem(withTitle: "退出光盘归档", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"); application.submenu = submenu
        let edit = NSMenuItem(); menu.addItem(edit); let edits = NSMenu(title: "编辑"); edit.submenu = edits
        edits.addItem(withTitle: "剪切", action: #selector(NSText.cut(_:)), keyEquivalent: "x"); edits.addItem(withTitle: "复制", action: #selector(NSText.copy(_:)), keyEquivalent: "c"); edits.addItem(withTitle: "粘贴", action: #selector(NSText.paste(_:)), keyEquivalent: "v"); edits.addItem(withTitle: "全选", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a"); NSApp.mainMenu = menu
    }
    private func buildCatalog() -> NSView {
        search.placeholderString = "搜索盘号、标题、项目、状态"; search.delegate = self; stretch(search, width: 300)
        for (name, title, width) in [("Id", "盘号", 130.0), ("Title", "标题", 240.0), ("Project", "项目", 160.0), ("Status", "状态", 140.0)] { let c = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(name)); c.title = title; c.width = width; table.addTableColumn(c) }
        table.delegate = self; table.dataSource = self; table.allowsMultipleSelection = false
        let scroll = NSScrollView(); scroll.hasVerticalScroller = true; scroll.documentView = table; scroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 250).isActive = true
        preview.imageScaling = .scaleProportionallyUpOrDown; preview.widthAnchor.constraint(equalToConstant: 140).isActive = true; preview.heightAnchor.constraint(equalToConstant: 100).isActive = true
        stretch(editTitle, width: 350); stretch(editProject, width: 350)
        let editor = row([preview, column([row([label("标题"), editTitle]), row([label("项目标签"), editProject]), recordNote])])
        let panel = column([row([search, button("刷新", #selector(refreshCatalog)), button("导出本机CSV…", #selector(exportLocal)), button("导出此归档目录总表…", #selector(exportTarget))]), scroll, editor, row([button("保存标签", #selector(saveLabels)), button("原盘号核对/续导", #selector(resumeSelected)), button("打开项目", #selector(openProject)), button("打开回执", #selector(openReceipt)), button("查看归类依据", #selector(showEvidence))])])
        scroll.widthAnchor.constraint(equalTo: panel.widthAnchor).isActive = true; return panel
    }

    @objc private func refreshAction() { refreshDevices() }
    private func refreshDevices() {
        guard recovered, !polling, !closing else { return }; polling = true
        CoreIPC.run("devices") { [weak self] result in
            guard let self = self else { return }; self.polling = false
            switch result {
            case .failure(let error): self.overall.stringValue = error.localizedDescription
            case .success(let o):
                let old = self.drives.first(where: { $0.id == self.selectedDrive })
                self.drives = (o["drives"] as? [IPCObject] ?? []).map(DeskDrive.init); self.targets = o["targets"] as? [IPCObject] ?? []
                let current = self.drives.first(where: { $0.id == self.selectedDrive })
                if old?.mediaHint != current?.mediaHint || old?.empty != current?.empty { self.manualCheck.state = .off }
                self.rebuildDriveMenu()
                let targetTitles = ["选择快捷卷…"] + self.targets.map { ($0["name"] as? String ?? "卷") + " · " + ($0["path"] as? String ?? "") }
                if self.targetsMenu.itemTitles != targetTitles { self.targetsMenu.removeAllItems(); self.targetsMenu.addItems(withTitles: targetTitles) }
                for (id, task) in self.jobs where task.process != nil && !task.disconnected {
                    guard let drive = self.drives.first(where: { $0.id == id }) else { self.disconnect(task, "光驱已断连，已请求停止，禁止转接到其他驱动。"); continue }
                    if !drive.empty && !drive.mediaHint.isEmpty {
                        if task.expectedHint.isEmpty { task.expectedHint = drive.mediaHint }
                        else if task.expectedHint != drive.mediaHint { self.disconnect(task, "光盘身份已变化，已请求停止，请核对实体盘后重新登记。") }
                    } else if !task.expectedHint.isEmpty && drive.empty { self.disconnect(task, "原盘已移出，已请求停止。") }
                }
                self.overall.stringValue = "发现 \(self.drives.count) 个光驱，\(self.jobs.values.filter { $0.process != nil }.count) 个任务正在运行。切换选择不会停止其他光驱。"
                self.showSelectedTask()
            }
        }
    }
    private func disconnect(_ task: DeskTask, _ reason: String) { task.disconnected = true; task.status = reason; task.log += reason + "\n"; task.process?.send(["action": "cancel"]) }
    private func rebuildDriveMenu() {
        var ids = drives.map { $0.id }; for id in jobs.keys.sorted() where !ids.contains(id) { ids.append(id) }
        let titles = ids.map { id -> String in let d = drives.first { $0.id == id }; let task = jobs[id]; let state = task?.process != nil ? "工作中" : (task != nil ? String(task!.status.prefix(28)) : (d == nil ? "已断连" : (d!.empty ? "空仓" : (d!.ready ? "有盘" : "未就绪")))); return "\(d?.name ?? id) · \(state) · \(d?.label ?? "")" }
        if driveMenu.itemTitles != titles || driveMenu.itemArray.compactMap({ $0.representedObject as? String }) != ids { driveMenu.removeAllItems(); for (i,id) in ids.enumerated() { driveMenu.addItem(withTitle: titles[i]); driveMenu.lastItem?.representedObject = id } }
        if selectedDrive.isEmpty { selectedDrive = ids.first ?? "" }
        if let index = ids.firstIndex(of: selectedDrive) { driveMenu.selectItem(at: index) } else { selectedDrive = ids.first ?? "" }
    }
    @objc private func selectDrive() { selectedDrive = driveMenu.selectedItem?.representedObject as? String ?? ""; manualCheck.state = .off; showSelectedTask() }
    private func showSelectedTask() {
        if let task = jobs[selectedDrive] { selectedStatus.stringValue = task.status; logView.string = task.log; logView.scrollToEndOfDocument(nil) }
        else { selectedStatus.stringValue = "所选光驱尚无任务；拍照/导入会固定绑定这个光驱。"; logView.string = "" }
        capture?.setCaptureEnabled(!closing && !selectedDrive.isEmpty && jobs[selectedDrive]?.process == nil)
        showNextInteraction()
    }
    @objc private func chooseTarget() { let p = NSOpenPanel(); p.title = "选择直连卷中的任意可写归档目录"; p.canChooseDirectories = true; p.canChooseFiles = false; p.canCreateDirectories = true; p.beginSheetModal(for: window) { [weak self] r in if r == .OK, let url = p.url { self?.acceptTarget(url) } } }
    private func acceptTarget(_ url: URL) {
        let url = url.resolvingSymlinksInPath().standardizedFileURL
        do { let values = try url.resourceValues(forKeys: [.volumeIsLocalKey, .isDirectoryKey]); guard values.volumeIsLocal == true, values.isDirectory == true, FileManager.default.isWritableFile(atPath: url.path) else { throw CaptureFailure("请选择本机直连卷中已有的可写目录；网络共享不用于导盘。") }
            targetField.stringValue = url.path; targetId = matchingTarget(url.path); refreshProjects()
        } catch { notice(error.localizedDescription) }
    }
    private func matchingTarget(_ path: String) -> String? { targets.sorted { ($0["path"] as? String ?? "").count > ($1["path"] as? String ?? "").count }.first { o in guard let root = o["path"] as? String, !root.isEmpty else { return false }; return path == root || path.hasPrefix(root.hasSuffix("/") ? root : root + "/") }?["id"] as? String }
    @objc private func selectSuggestedTarget() { let index = targetsMenu.indexOfSelectedItem-1; if targets.indices.contains(index), let path = targets[index]["path"] as? String { acceptTarget(URL(fileURLWithPath: path)) } }
    @objc private func refreshProjects() {
        let target = targetField.stringValue; guard !target.isEmpty else { return }
        CoreIPC.run("projects", ["target": target]) { [weak self] result in guard let self = self, self.targetField.stringValue == target else { return }; if case .success(let o) = result { self.projectMenu.removeAllItems(); self.projectMenu.addItem(withTitle: "填写新项目 / 自动归类"); self.projectMenu.addItems(withTitles: o["projects"] as? [String] ?? []) } }
    }
    @objc private func selectProject() { if projectMenu.indexOfSelectedItem > 0 { projectField.stringValue = projectMenu.titleOfSelectedItem ?? "" } }
    private func snapshot(resume: String? = nil) throws -> DeskSubmission {
        guard recovered else { throw CaptureFailure("尚未完成上次中断记录核对，不能登记新任务。") }
        guard !closing else { throw CaptureFailure("正在安全退出，不能再开始任务。") }
        guard let drive = drives.first(where: { $0.id == selectedDrive }) else { throw CaptureFailure("所选光驱不在线，请刷新设备。") }
        guard drive.empty || drive.ready else { throw CaptureFailure("光驱仍在识盘或状态未知，请等设备就绪；不会把未知状态当成空仓。") }
        guard jobs[drive.id]?.process == nil else { throw CaptureFailure("此光驱尚在工作。可切换另一光驱继续。") }
        guard !targetField.stringValue.isEmpty else { throw CaptureFailure("请先选择归档目录。") }
        if !drive.empty && manualCheck.state != .on { throw CaptureFailure("光驱中已有盘。请核对实体盘、当前光驱与照片一致，再勾选人工核对。") }
        let project = projectField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        let existing = projectMenu.indexOfSelectedItem > 0 && projectMenu.titleOfSelectedItem == project ? project : nil
        return DeskSubmission(drive: drive, target: targetField.stringValue, targetId: targetId, project: project, existingProject: existing, mode: vision.mode, view: ["auto", "mac", "udf", "iso"][max(0,viewMenu.indexOfSelectedItem)], resumeId: resume)
    }
    @objc private func choosePhoto() { let p = NSOpenPanel(); p.title = "选择一张盘面照片并登记"; p.canChooseDirectories = false; p.allowsMultipleSelection = false; p.allowedFileTypes = ["jpg", "jpeg", "png", "heic", "tif", "tiff"]; p.beginSheetModal(for: window) { [weak self] r in if r == .OK, let u = p.url { self?.importPhoto(u) } } }
    private func importPhoto(_ url: URL) { do { let submission = try snapshot(); start(photo: url, submission: submission, ownTemporary: false) } catch { notice(error.localizedDescription) } }
    private func start(photo: URL, submission: DeskSubmission, ownTemporary: Bool) {
        guard jobs[submission.drive.id]?.process == nil, let current = drives.first(where: { $0.id == submission.drive.id }), (submission.drive.empty || current.mediaHint == submission.drive.mediaHint) else { notice("快门后驱动状态已变化或正在工作；未登记，照片已保留：\(photo.path)"); return }
        let task = DeskTask(submission); jobs[submission.drive.id] = task
        var payload = submission.payload; payload["photo"] = photo.path
        do {
            task.process = try CoreIPC.Job(command: "job", payload: payload, persistent: true, onEvent: { [weak self, weak task] o in guard let self = self, let task = task else { return }; self.handle(o, task: task, sourcePhoto: photo, ownTemporary: ownTemporary) }, onExit: { [weak self, weak task] code, error in guard let self = self, let task = task else { return }; self.ended(task, code: code, diagnostic: error) })
            photoLabel.stringValue = "已绑定 \(submission.drive.name)：\(photo.lastPathComponent)"; manualCheck.state = .off
            if !notificationRequested { notificationRequested = true; UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { _,_ in } }
            vision.setEditable(false); showSelectedTask(); rebuildDriveMenu()
        } catch { jobs.removeValue(forKey: submission.drive.id); notice("任务无法启动，原照片保留：\(error.localizedDescription)") }
    }
    private func handle(_ o: IPCObject, task: DeskTask, sourcePhoto: URL, ownTemporary: Bool) {
        switch o["event"] as? String ?? "" {
        case "registered":
            if let drive = o["drive"] as? String, drive != task.submission.drive.id { disconnect(task, "登记回执的光驱不符，已请求停止，临时照片保留。"); break }
            task.registered = true; task.status = "已登记盘号 \(o["id"] as? String ?? "")，等待核对或导盘。"
            // Delete only this application's newly-created temp JPEG, after backend confirms registration.
            let root = FileManager.default.temporaryDirectory.appendingPathComponent("DiscArchivePhotos", isDirectory: true).standardizedFileURL.path + "/"
            if ownTemporary, sourcePhoto.standardizedFileURL.path.hasPrefix(root), sourcePhoto.pathExtension.lowercased() == "jpg" { do { try FileManager.default.removeItem(at: sourcePhoto) } catch { task.log += "登记成功，但临时照片清理失败；已保留：\(sourcePhoto.path)\n" } }
            refreshCatalog()
        case "status": task.status = o["text"] as? String ?? "处理中…"
        case "recognize":
            guard let path = o["photo"] as? String else { task.process?.send(["action": "recognition", "text": "", "error": "未提供照片路径"]); break }
            // VisionPane is a single cancellable operation. Serialize OCR/model requests across drives.
            recognitionQueue.append((task, URL(fileURLWithPath: path))); runNextRecognition()
        case "match", "project": task.pending = o; task.status = "等待人工选择（其他光驱继续运行）。"; showNextInteraction()
        case "result": task.entry = o["entry"] as? IPCObject; task.gotResult = true; task.status = "任务结束：\(task.entry?["Status"] as? String ?? "请核对回执")"; refreshCatalog()
        case "error": task.hadError = true; task.status = o["text"] as? String ?? "任务遇到错误，照片和回执保留。"
        default: task.status = "收到未识别的任务事件，请查看回执。"
        }
        task.log += task.status + "\n"; if task.log.count > 100000 { task.log = String(task.log.suffix(80000)) }; if selectedDrive == task.submission.drive.id { showSelectedTask() }
    }
    private var recognitionQueue: [(DeskTask, URL)] = []
    private var recognizing: DeskTask?
    private func runNextRecognition() {
        guard recognizing == nil else { return }
        recognitionQueue.removeAll { $0.0.process == nil || $0.0.disconnected }
        guard !recognitionQueue.isEmpty else { vision.setEditable(!jobs.values.contains { $0.process != nil }); return }
        let (task, photo) = recognitionQueue.removeFirst(); recognizing = task; vision.setEditable(false)
        // A job's mode was frozen at shutter. Avoid silently applying a changed mode to an older job.
        guard vision.mode == task.submission.mode else { task.process?.send(["action": "recognition", "text": "", "error": "识图设置已改变，本盘跳过识图，保留照片。"]); recognizing = nil; runNextRecognition(); return }
        vision.read(photo: photo) { [weak self, weak task] result in
            guard let self = self else { return }; if let task = task, task.process != nil {
                switch result { case .success(let text): task.process?.send(["action": "recognition", "text": text]); case .failure(let error): task.process?.send(["action": "recognition", "text": "", "error": error.localizedDescription]) }
            }; self.recognizing = nil; self.runNextRecognition()
        }
    }
    private func ended(_ task: DeskTask, code: Int32, diagnostic: String) {
        task.process = nil; task.pending = nil
        if !task.gotResult { task.status = "进程已结束（\(code)），\(task.registered ? "请查看已登记盘的回执" : "未确认登记成功，原照片保留")。"; if !diagnostic.isEmpty { task.log += String(diagnostic.prefix(2000)) + "\n" } }
        task.log += task.status + "\n"
        if recognizing === task { vision.cancel() }
        let content = UNMutableNotificationContent(); content.title = "\(task.submission.drive.name) 任务结束"; content.body = task.status; content.sound = .default
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
        overall.stringValue = "\(task.submission.drive.name)：\(task.status)"; vision.setEditable(!jobs.values.contains { $0.process != nil }); refreshCatalog(); rebuildDriveMenu(); showSelectedTask(); maybeQuit()
    }
    @objc private func stopSelected() { guard let task = jobs[selectedDrive], task.process != nil else { return }; task.process?.send(["action": "cancel"]); task.status = "已请求停止此光驱，等待保存回执；其他光驱继续。"; if recognizing === task { vision.cancel() }; showSelectedTask() }
    @objc private func confirmSelected() { guard let task = jobs[selectedDrive], task.process != nil, !task.disconnected else { return }; task.process?.send(["action": "confirm"]); task.status = "已确认所选实体盘，等待导盘进程继续。"; showSelectedTask() }
    @objc private func inspectDisc() { guard !selectedDrive.isEmpty else { return }; let drive = selectedDrive; CoreIPC.run("inspect", ["drive": drive]) { [weak self] r in switch r { case .success(let o): self?.notice(o["text"] as? String ?? "无盘片信息"); case .failure(let e): self?.notice(e.localizedDescription) } } }

    private func showNextInteraction() {
        guard !sheetOpen, !closing, let task = jobs[selectedDrive], let event = task.pending else { return }
        sheetOpen = true
        if event["event"] as? String == "match" {
            let options = event["options"] as? [IPCObject] ?? []; let menu = NSPopUpButton(); menu.addItem(withTitle: "作为新盘登记")
            menu.addItems(withTitles: options.map { "\($0["id"] as? String ?? "") · \($0["status"] as? String ?? "") · \($0["project"] as? String ?? "") · \($0["reason"] as? String ?? "")" })
            let newPhoto = NSImageView(), oldPhoto = NSImageView(); newPhoto.imageScaling = .scaleProportionallyUpOrDown; oldPhoto.imageScaling = .scaleProportionallyUpOrDown
            for image in [newPhoto, oldPhoto] { image.widthAnchor.constraint(equalToConstant: 180).isActive = true; image.heightAnchor.constraint(equalToConstant: 140).isActive = true }
            newPhoto.image = (event["photo"] as? String).flatMap { NSImage(contentsOfFile: $0) }
            let chooser = DeskMenuAction { let i = menu.indexOfSelectedItem-1; oldPhoto.image = options.indices.contains(i) ? (options[i]["photo"] as? String).flatMap { NSImage(contentsOfFile: $0) } : nil }
            menu.target = chooser; menu.action = #selector(DeskMenuAction.changed)
            let alert = NSAlert(); alert.messageText = "发现可能相同的盘，请人工核对"; alert.informativeText = "左为本次照片，右为选中的旧盘照片。选择旧盘沿用原盘号；不确定时作为新盘。其他光驱继续运行。"; alert.accessoryView = column([menu, row([newPhoto, oldPhoto])])
            alert.addButton(withTitle: "使用此选择"); alert.addButton(withTitle: "停止此任务")
            alert.beginSheetModal(for: window) { [weak self, weak task, chooser] r in
                _ = chooser // Retain popup target until this sheet completes.
                guard let self = self else { return }; self.sheetOpen = false; guard let task = task else { self.showNextInteraction(); return }; task.pending = nil
                if r == .alertFirstButtonReturn { let index = menu.indexOfSelectedItem-1; let value = options.indices.contains(index) ? options[index]["id"] as? String ?? "" : ""; task.process?.send(["action": "match", "value": value]) } else { task.process?.send(["action": "cancel"]) }; self.showNextInteraction()
            }
        } else {
            let proposed = event["proposed"] as? String ?? ""; let options = event["options"] as? [String] ?? []; let field = NSComboBox(); field.addItems(withObjectValues: options); field.stringValue = proposed; field.frame = NSRect(x: 0, y: 0, width: 440, height: 26)
            let alert = NSAlert(); alert.messageText = "确认项目目录/别名"; alert.informativeText = "输入新项目或选择已有项目。此选择只影响当前光驱；其他光驱继续。"; alert.accessoryView = field; alert.addButton(withTitle: "确认项目"); alert.addButton(withTitle: "停止此任务")
            alert.beginSheetModal(for: window) { [weak self, weak task] r in guard let self = self else { return }; self.sheetOpen = false; task?.pending = nil; if r == .alertFirstButtonReturn { task?.process?.send(["action": "project", "value": field.stringValue]) } else { task?.process?.send(["action": "cancel"]) }; self.showNextInteraction() }
        }
    }

    @objc private func refreshCatalog() {
        guard !catalogLoading else { return }; catalogLoading = true
        CoreIPC.run("list") { [weak self] r in guard let self = self else { return }; self.catalogLoading = false; switch r { case .success(let o): self.entries = o["entries"] as? [IPCObject] ?? []; self.filterCatalog(); case .failure(let e): self.recordNote.stringValue = e.localizedDescription } }
    }
    func controlTextDidChange(_ obj: Notification) { if obj.object as? NSSearchField === search { filterCatalog() } }
    private func filterCatalog() { let term = search.stringValue.lowercased(); filtered = entries.filter { e in term.isEmpty || ["Id", "Title", "Project", "Status"].contains { (e[$0] as? String ?? "").lowercased().contains(term) } }; table.reloadData() }
    func numberOfRows(in tableView: NSTableView) -> Int { filtered.count }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? { guard filtered.indices.contains(row) else { return nil }; return label(filtered[row][tableColumn?.identifier.rawValue ?? ""] as? String ?? "") }
    private var selectedEntry: IPCObject? { filtered.indices.contains(table.selectedRow) ? filtered[table.selectedRow] : nil }
    func tableViewSelectionDidChange(_ notification: Notification) { guard let e = selectedEntry else { return }; editTitle.stringValue = e["Title"] as? String ?? ""; editProject.stringValue = e["Project"] as? String ?? ""; recordNote.stringValue = "\(e["Id"] as? String ?? "") · \(e["Status"] as? String ?? "")"; preview.image = (e["Photo"] as? String).flatMap { NSImage(contentsOfFile: $0) } }
    @objc private func saveLabels() { guard let e = selectedEntry, let id = e["Id"] as? String else { return }; CoreIPC.run("edit", ["id": id, "title": editTitle.stringValue, "project": editProject.stringValue]) { [weak self] r in switch r { case .success: self?.refreshCatalog(); case .failure(let error): self?.notice(error.localizedDescription) } } }
    @objc private func exportLocal() { export(command: "export") }
    @objc private func exportTarget() { guard !targetField.stringValue.isEmpty else { notice("请先在导盘页选择归档目录。"); return }; export(command: "exportTarget") }
    private func export(command: String) { let p = NSSavePanel(); p.title = "导出CSV登记表"; p.nameFieldStringValue = "光盘登记册.csv"; let target = targetField.stringValue; p.beginSheetModal(for: window) { [weak self] r in guard r == .OK, let url = p.url else { return }; CoreIPC.run(command, ["target": target, "path": url.path]) { result in switch result { case .success: self?.notice("登记表已导出：\(url.path)"); case .failure(let error): self?.notice(error.localizedDescription) } } } }
    @objc private func resumeSelected() {
        guard let entry = selectedEntry, let id = entry["Id"] as? String, let photo = entry["Photo"] as? String else { return }
        let alert = NSAlert(); alert.messageText = "以原盘号核对/续导 \(id)"; alert.informativeText = "先选择原目标目录和当前光驱，放入原盘。继续前须人工核对实体盘与登记照片一致。不会自动转移到其他盘号或驱动。"; alert.addButton(withTitle: "已核对原盘，续导"); alert.addButton(withTitle: "取消")
        alert.beginSheetModal(for: window) { [weak self] r in guard let self = self, r == .alertFirstButtonReturn else { return }; do { self.manualCheck.state = .on; let s = try self.snapshot(resume: id); self.start(photo: URL(fileURLWithPath: photo), submission: s, ownTemporary: false) } catch { self.notice(error.localizedDescription) } }
    }
    @objc private func openProject() { guard let e = selectedEntry else { return }; let root = e["TargetRoot"] as? String ?? ""; let folders = e["ArchiveFolders"] as? [String] ?? []; let candidates = folders.map { $0.hasPrefix("/") ? $0 : URL(fileURLWithPath: root).appendingPathComponent($0).path }; openPaths(candidates.isEmpty ? [e["JobDir"] as? String ?? ""] : candidates) }
    @objc private func openReceipt() { guard let e = selectedEntry else { return }; openPaths([e["Report"] as? String ?? e["JobDir"] as? String ?? ""]) }
    private func openPaths(_ paths: [String]) { let paths = paths.filter { !$0.isEmpty && FileManager.default.fileExists(atPath: $0) }; guard !paths.isEmpty else { notice("目录或回执不在线，请接回原归档卷。"); return }; if paths.count == 1 { NSWorkspace.shared.open(URL(fileURLWithPath: paths[0])); return }; let alert = NSAlert(); let menu = NSPopUpButton(); menu.addItems(withTitles: paths); alert.messageText = "选择要打开的项目目录"; alert.accessoryView = menu; alert.addButton(withTitle: "打开"); alert.addButton(withTitle: "取消"); alert.beginSheetModal(for: window) { r in if r == .alertFirstButtonReturn, let path = menu.titleOfSelectedItem { NSWorkspace.shared.open(URL(fileURLWithPath: path)) } } }
    @objc private func showEvidence() { guard let e = selectedEntry else { return }; let text = ["DiscLabel", "PhotoText", "SourceFormat", "VisionNote", "NamingReason", "CanonicalId", "IdentityEvidence", "CloseWarning"].map { "\($0)：\(e[$0] ?? "")" }.joined(separator: "\n\n"); notice(text) }

    @objc private func chooseWatchFolder() {
        guard !selectedDrive.isEmpty else { notice("先选择需要固定绑定的光驱。"); return }
        let bound = selectedDrive; let p = NSOpenPanel(); p.title = "选择照片接收文件夹（只接收启用后新增的一张照片）"; p.canChooseDirectories = true; p.canChooseFiles = false
        p.beginSheetModal(for: window) { [weak self] r in guard let self = self, r == .OK, let url = p.url else { return }; self.watchFolder = url; self.watchDrive = bound; self.watchCandidate = nil; self.watchSeen = Set(self.folderPhotos(url).map { $0.path }); self.watchNote.stringValue = "接收中：\(url.path) → 固定光驱 \(bound)。其他光驱不会接收这里的照片。" }
    }
    private func folderPhotos(_ folder: URL) -> [URL] { ((try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.fileSizeKey, .contentModificationDateKey, .isRegularFileKey], options: [.skipsHiddenFiles])) ?? []).filter { ["jpg", "jpeg", "png", "heic"].contains($0.pathExtension.lowercased()) } }
    private func disableWatch() { watchFolder = nil; watchDrive = nil; watchSeen.removeAll(); watchCandidate = nil; watchNote.stringValue = "照片文件夹接收已停止。" }
    @objc private func stopWatch() { disableWatch() }
    private func pollPhotoFolder() {
        guard let folder = watchFolder, let bound = watchDrive, !closing else { return }
        let new = folderPhotos(folder).filter { !watchSeen.contains($0.path) }
        guard !new.isEmpty else { return }
        if new.count > 1 { watchNote.stringValue = "新增多张照片，已暂停接收以避免盘片串配。请逐张选择照片登记。"; watchFolder = nil; return }
        let url = new[0]; guard let values = try? url.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey, .isRegularFileKey]), values.isRegularFile == true, let size = values.fileSize, size > 0, let changed = values.contentModificationDate else { return }
        guard let candidate = watchCandidate, candidate.0 == url.path, candidate.1 == UInt64(size), Date().timeIntervalSince(candidate.2) > 2, Date().timeIntervalSince(changed) > 2 else { watchCandidate = (url.path, UInt64(size), Date()); return }
        // Never use the currently selected replacement drive. A sheet pins the original binding for human review.
        watchSeen.insert(url.path); watchCandidate = nil; watchFolder = nil
        watchNote.stringValue = "收到一张照片，接收已暂停；核对并登记后可重新启用。"
        let alert = NSAlert(); alert.messageText = "收到照片：\(url.lastPathComponent)"; alert.informativeText = "固定绑定光驱 \(bound)。请核对照片与该光驱实体盘。继续将切回此驱动，不影响其他任务。"; alert.addButton(withTitle: "已核对，登记这张"); alert.addButton(withTitle: "保留照片，不登记")
        alert.beginSheetModal(for: window) { [weak self] r in guard let self = self, r == .alertFirstButtonReturn else { return }; self.selectedDrive = bound; self.rebuildDriveMenu(); self.manualCheck.state = .on; self.importPhoto(url); self.showSelectedTask() }
    }
    private func notice(_ text: String) { let alert = NSAlert(); alert.messageText = "光盘归档"; alert.informativeText = String(text.prefix(8000)); alert.addButton(withTitle: "知道了"); alert.beginSheetModal(for: window) }
}
