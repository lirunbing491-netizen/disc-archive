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
    var drive: DeskDrive, target: String, targetDeviceId: String?, project: String, existingProject: String?, mode: String, view: String, resumeId: String?
    // Archive marker GUID survives renaming/remounting. This is separate from the current OS device identity.
    var archiveTargetId: String? = nil
    // Set only after the explicit original-disc sheet and a matching fresh device observation.
    // The worker must independently bind its full token; ordinary mediaHint never authorizes reading.
    var resumeConfirmedHint: String? = nil
    var payload: IPCObject {
        var o: IPCObject = ["drive": drive.id, "target": target, "project": project, "emptyAtShutter": drive.empty, "mode": mode, "view": view, "mediaHint": drive.mediaHint]
        if let id = targetDeviceId { o["targetDeviceId"] = id }; if let id = archiveTargetId { o["targetId"] = id }; if let hint = resumeConfirmedHint, !hint.isEmpty { o["resumeConfirmedHint"] = hint }; if let existing = existingProject { o["existingProject"] = existing }; if let resume = resumeId { o["resumeId"] = resume }; return o
    }
}
private final class DeskTask {
    let identity = UUID().uuidString
    var process: CoreIPC.Job?
    let submission: DeskSubmission
    var status = "正在登记…", log = "", entry: IPCObject?, pending: IPCObject?
    var expectedHint: String, disconnected = false, registered = false, gotResult = false, hadError = false
    var ejectNote = "", registeredID = ""
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

@MainActor final class Desk: NSObject, NSApplicationDelegate, NSWindowDelegate, NSTableViewDataSource, NSTableViewDelegate, NSSearchFieldDelegate, NSTabViewDelegate, UNUserNotificationCenterDelegate {
    private static let retryNotificationAction = "discarchive.retry-original"
    private static let retryNotificationCategory = "discarchive.retryable-result"
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
    private let importedNote = NSTextField(labelWithString: "本机已导 0 张")
    private let showHidden = NSButton(checkboxWithTitle: "查看已移除", target: nil, action: nil)
    private let retryButton = NSButton(title: "重试所选光驱原盘（无需重拍）", target: nil, action: nil)
    private let tabs = NSTabView()
    private var capture: CapturePane!, vision: VisionPane!
    private var drives: [DeskDrive] = [], targets: [IPCObject] = [], jobs: [String: DeskTask] = [:]
    private var selectedDrive = "", targetDeviceId: String?, entries: [IPCObject] = [], filtered: [IPCObject] = []
    private var shots: [String: DeskSubmission] = [:]
    private var timer: Timer?, polling = false, catalogLoading = false, closing = false
    private var notificationRequested = false
    private var recovered = false
    private var sheetOpen = false
    private var retryChecking = false
    private var requestedCatalogSelection: String?

    func applicationDidFinishLaunching(_ notification: Notification) {
        UNUserNotificationCenter.current().delegate = self
        let retry = UNNotificationAction(identifier: Self.retryNotificationAction, title: "重试这张盘", options: [.foreground])
        UNUserNotificationCenter.current().setNotificationCategories([UNNotificationCategory(identifier: Self.retryNotificationCategory, actions: [retry], intentIdentifiers: [], options: [])])
        buildWindow(); capture.setCaptureEnabled(false); overall.stringValue = "正在核对上次中断记录，完成前不允许登记。"
        CoreIPC.run("recover") { [weak self] result in
            guard let self = self else { return }
            switch result {
            case .success:
                self.recovered = true; self.refreshDevices(); self.refreshCatalog()
                self.timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in Task { @MainActor in self?.refreshDevices() } }
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
                self.closing = true; self.capture.stopPreview(); self.vision.cancel()
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
        retryButton.target = self; retryButton.action = #selector(retrySelectedTask)
        let importPanel = column([targetRow, row([label("直连卷快捷入口"), targetsMenu]), row([label("项目"), projectField, projectMenu, button("刷新项目", #selector(refreshProjects))]), row([label("读取格式"), viewMenu, button("查看当前盘信息", #selector(inspectDisc))]), manualCheck, drop, row([button("选择盘面照片并登记…", #selector(choosePhoto)), button("停止所选光驱", #selector(stopSelected)), button("已核对所选盘，继续", #selector(confirmSelected))]), photoLabel, selectedStatus, retryButton])
        drop.widthAnchor.constraint(equalTo: importPanel.widthAnchor).isActive = true
        let logScroll = NSScrollView(); logScroll.hasVerticalScroller = true; logScroll.documentView = logView; logView.isEditable = false; logView.font = .monospacedSystemFont(ofSize: 12, weight: .regular); logView.isVerticallyResizable = true; logView.isHorizontallyResizable = false; logView.autoresizingMask = [.width]; logView.textContainer?.widthTracksTextView = true; logView.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude); logScroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 150).isActive = true
        importPanel.addArrangedSubview(logScroll); logScroll.widthAnchor.constraint(equalTo: importPanel.widthAnchor).isActive = true
        tabs.delegate = self; capture.setCameraPageActive(false)
        addTab("导盘", importPanel); addTab("登记册", buildCatalog()); addTab("摄像头", column([label("摄像头使用顶部同一个光驱选择；快门时固定光驱、目录、项目及识图方式。"), capture])); addTab("识图设置", column([vision]))
        let top = row([label("当前光驱"), driveMenu, button("刷新设备", #selector(refreshAction)), button("使用说明", #selector(openHandbook))])
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
        for (name, title, width) in [("Primary", "项目 / 标题", 260.0), ("Alternate", "补充标题", 200.0), ("Status", "状态", 120.0), ("TargetName", "归档位置", 160.0)] { let c = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(name)); c.title = title; c.width = width; table.addTableColumn(c) }
        table.delegate = self; table.dataSource = self; table.allowsMultipleSelection = false
        let scroll = NSScrollView(); scroll.hasVerticalScroller = true; scroll.documentView = table; scroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 250).isActive = true
        preview.imageScaling = .scaleProportionallyUpOrDown; preview.widthAnchor.constraint(equalToConstant: 140).isActive = true; preview.heightAnchor.constraint(equalToConstant: 100).isActive = true
        stretch(editTitle, width: 350); stretch(editProject, width: 350)
        let editor = row([preview, column([row([label("标题"), editTitle]), row([label("项目标签"), editProject]), recordNote])])
        showHidden.target = self; showHidden.action = #selector(toggleHiddenFilter)
        let panel = column([row([importedNote, search, button("刷新", #selector(refreshCatalog)), button("导出本机CSV…", #selector(exportLocal)), button("导出此归档目录总表…", #selector(exportTarget))]), scroll, editor, row([button("保存标签", #selector(saveLabels)), button("手工补录…", #selector(addManual)), button("从本机移除", #selector(hideSelected)), button("恢复到本机列表", #selector(restoreSelected)), showHidden]), row([ button("原盘号核对/续导", #selector(resumeSelected)), button("打开项目", #selector(openProject)), button("打开回执", #selector(openReceipt)), button("查看归类依据", #selector(showEvidence))])])
        scroll.widthAnchor.constraint(equalTo: panel.widthAnchor).isActive = true; return panel
    }

    @objc private func refreshAction() { refreshDevices() }
    @objc private func openHandbook() {
        let url = Bundle.main.bundleURL.appendingPathComponent("Contents/Resources/使用说明.txt")
        guard FileManager.default.fileExists(atPath: url.path), NSWorkspace.shared.open(url) else { notice("未找到随应用打包的使用说明，请重新下载完整安装包。"); return }
    }
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
        let targetChanged = targetDeviceId.map { matchingTarget(targetField.stringValue) != $0 } ?? false
        if targetChanged { selectedStatus.stringValue = "归档目标卷已断连或重新连接，身份已变化。开始下一张前请重新选择归档目录。\n" + selectedStatus.stringValue }
        retryButton.isEnabled = !closing && !retryChecking && jobs[selectedDrive]?.process == nil && isRetryable(jobs[selectedDrive]?.entry)
        capture?.setCaptureEnabled(!retryChecking && !closing && !targetChanged && !selectedDrive.isEmpty && jobs[selectedDrive]?.process == nil)
        showNextInteraction()
    }
    @objc private func chooseTarget() { let p = NSOpenPanel(); p.title = "选择直连卷中的任意可写归档目录"; p.canChooseDirectories = true; p.canChooseFiles = false; p.canCreateDirectories = true; p.beginSheetModal(for: window) { [weak self] r in if r == .OK, let url = p.url { self?.acceptTarget(url) } } }
    private func acceptTarget(_ url: URL) {
        let url = url.resolvingSymlinksInPath().standardizedFileURL
        do { let values = try url.resourceValues(forKeys: [.volumeIsLocalKey, .isDirectoryKey]); guard values.volumeIsLocal == true, values.isDirectory == true, FileManager.default.isWritableFile(atPath: url.path) else { throw CaptureFailure("请选择本机直连卷中已有的可写目录；网络共享不用于导盘。") }
            // Freeze the current OS device identity until the user explicitly selects again.
            // It is not the archive marker GUID, which is created/verified only by core.
            targetField.stringValue = url.path; targetDeviceId = matchingTarget(url.path); refreshProjects(); showSelectedTask()
        } catch { notice(error.localizedDescription) }
    }
    private func matchingTarget(_ path: String) -> String? { let id = targets.sorted { ($0["path"] as? String ?? "").count > ($1["path"] as? String ?? "").count }.first { o in guard let root = o["path"] as? String, !root.isEmpty else { return false }; return path == root || path.hasPrefix(root.hasSuffix("/") ? root : root + "/") }?["id"] as? String; return id?.isEmpty == false ? id : nil }
    @objc private func selectSuggestedTarget() { let index = targetsMenu.indexOfSelectedItem-1; if targets.indices.contains(index), let path = targets[index]["path"] as? String { acceptTarget(URL(fileURLWithPath: path)) } }
    @objc private func refreshProjects() {
        let target = targetField.stringValue; guard !target.isEmpty else { return }
        CoreIPC.run("projects", ["target": target]) { [weak self] result in guard let self = self, self.targetField.stringValue == target else { return }; if case .success(let o) = result { self.projectMenu.removeAllItems(); self.projectMenu.addItem(withTitle: "填写新项目 / 自动归类"); self.projectMenu.addItems(withTitles: o["projects"] as? [String] ?? []) } }
    }
    @objc private func selectProject() { if projectMenu.indexOfSelectedItem > 0 { projectField.stringValue = projectMenu.titleOfSelectedItem ?? "" } }
    private func snapshot(resume: String? = nil) throws -> DeskSubmission {
        guard recovered else { throw CaptureFailure("尚未完成上次中断记录核对，不能登记新任务。") }
        guard !closing && !retryChecking else { throw CaptureFailure("正在安全退出，不能再开始任务。") }
        guard let drive = drives.first(where: { $0.id == selectedDrive }) else { throw CaptureFailure("所选光驱不在线，请刷新设备。") }
        guard drive.empty || drive.ready else { throw CaptureFailure("光驱仍在识盘或状态未知，请等设备就绪；不会把未知状态当成空仓。") }
        guard jobs[drive.id]?.process == nil else { throw CaptureFailure("此光驱尚在工作。可切换另一光驱继续。") }
        guard !targetField.stringValue.isEmpty else { throw CaptureFailure("请先选择归档目录。") }
        if let expected = targetDeviceId, matchingTarget(targetField.stringValue) != expected { throw CaptureFailure("归档目标卷已断连或身份已变化，请重新选择归档目录后再开始；不会自动沿用重插后的卷。") }
        if !drive.empty && manualCheck.state != .on { throw CaptureFailure("光驱中已有盘。请核对实体盘、当前光驱与照片一致，再勾选人工核对。") }
        let project = projectField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        let existing = projectMenu.indexOfSelectedItem > 0 && projectMenu.titleOfSelectedItem == project ? project : nil
        return DeskSubmission(drive: drive, target: targetField.stringValue, targetDeviceId: targetDeviceId, project: project, existingProject: existing, mode: vision.mode, view: ["auto", "mac", "udf", "iso"][max(0,viewMenu.indexOfSelectedItem)], resumeId: resume)
    }
    @objc private func choosePhoto() { let p = NSOpenPanel(); p.title = "选择一张盘面照片并登记"; p.canChooseDirectories = false; p.allowsMultipleSelection = false; p.allowedFileTypes = ["jpg", "jpeg", "png", "heic", "tif", "tiff"]; p.beginSheetModal(for: window) { [weak self] r in if r == .OK, let u = p.url { self?.importPhoto(u) } } }
    private func importPhoto(_ url: URL) { do { let submission = try snapshot(); start(photo: url, submission: submission, ownTemporary: false) } catch { notice(error.localizedDescription) } }
    private func start(photo: URL, submission: DeskSubmission, ownTemporary: Bool) {
        if let id = submission.resumeId, jobs.values.contains(where: { $0.process != nil && ($0.registeredID == id || $0.submission.resumeId == id || ($0.entry?["Id"] as? String) == id) }) { notice("此原盘号已在其他光驱工作，不能同时续导；原照片保留。"); return }
        if let expected = submission.targetDeviceId, matchingTarget(submission.target) != expected { notice("快门后归档目标卷已断连或身份已变化。请重新选择归档目录后再开始，照片已保留：\(photo.path)"); return }
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
            task.registeredID = o["id"] as? String ?? ""
            task.registered = true; task.status = "已登记盘号 \(o["id"] as? String ?? "")，等待核对或导盘。"
            // Delete only this application's newly-created temp JPEG, after backend confirms registration.
            let root = FileManager.default.temporaryDirectory.appendingPathComponent("DiscArchivePhotos", isDirectory: true).standardizedFileURL.path + "/"
            if ownTemporary, sourcePhoto.standardizedFileURL.path.hasPrefix(root), sourcePhoto.pathExtension.lowercased() == "jpg" { do { try FileManager.default.removeItem(at: sourcePhoto) } catch { task.log += "登记成功，但临时照片清理失败；已保留：\(sourcePhoto.path)\n" } }
            refreshCatalog()
        case "status":
            task.status = o["text"] as? String ?? "处理中…"
            if task.submission.resumeId != nil && task.status.contains("核对") && task.status.contains("确认") { task.status += "\n请在主窗口再次核对原盘，然后点“已核对所选盘，继续”；此前尚未开始读取。" }
        case "recognize":
            guard let path = o["photo"] as? String else { task.process?.send(["action": "recognition", "text": "", "error": "未提供照片路径"]); break }
            // VisionPane is a single cancellable operation. Serialize OCR/model requests across drives.
            recognitionQueue.append((task, URL(fileURLWithPath: path))); runNextRecognition()
        case "match", "project": task.pending = o; task.status = "等待人工选择（其他光驱继续运行）。"; showNextInteraction()
        case "result":
            task.entry = o["entry"] as? IPCObject; task.gotResult = true
            task.ejectNote = o["ejectNote"] as? String ?? ""
            task.status = "任务结束：\(task.entry?["Status"] as? String ?? "请核对回执")"
            if !task.ejectNote.isEmpty { task.status += "\n" + task.ejectNote }
            else if o["ejectAccepted"] as? Bool == false { task.status += "\n未确认弹盘成功，请核对托盘状态。" }
            if isRetryable(task.entry) { task.status += "\n放回原盘后可点“重试所选光驱原盘（无需重拍）”。" }
            refreshCatalog()
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
        let content = UNMutableNotificationContent(); content.title = "\(task.submission.drive.name) 任务结束"; content.body = task.status; content.sound = .default; content.userInfo = ["drive": task.submission.drive.id, "task": task.identity, "entry": task.entry?["Id"] as? String ?? ""]
        content.categoryIdentifier = notificationCategory(for: task)
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
            let proposed = event["proposed"] as? String ?? ""
            let options = event["options"] as? [String] ?? []
            var choices: [String] = []
            for name in [proposed] + options where !name.isEmpty && !choices.contains(name) { choices.append(name) }
            let menu = NSPopUpButton(frame: NSRect(x: 0, y: 0, width: 440, height: 26))
            for name in choices {
                menu.addItem(withTitle: (name == proposed ? "采用建议目录：" : "采用已有目录：") + name)
                menu.lastItem?.representedObject = name
            }
            if choices.isEmpty { menu.addItem(withTitle: "未收到可采用的项目目录"); menu.isEnabled = false }
            let alert = NSAlert(); alert.messageText = "确认项目目录/别名"
            alert.informativeText = "请选择建议目录或可采用的已有目录。需要指定其他项目时，请停止本任务，在开工前的项目输入框填写后重新登记。此选择只影响当前光驱，其他光驱继续。"
            alert.accessoryView = menu; alert.addButton(withTitle: "确认项目"); alert.addButton(withTitle: "停止此任务"); alert.buttons.first?.isEnabled = !choices.isEmpty
            alert.beginSheetModal(for: window) { [weak self, weak task] r in
                guard let self = self else { return }; self.sheetOpen = false; task?.pending = nil
                if r == .alertFirstButtonReturn, let value = menu.selectedItem?.representedObject as? String, choices.contains(value) {
                    task?.process?.send(["action": "project", "value": value])
                } else { task?.process?.send(["action": "cancel"]) }
                self.showNextInteraction()
            }
        }
    }

    @objc private func refreshCatalog() {
        guard !catalogLoading else { return }; catalogLoading = true
        CoreIPC.run("list") { [weak self] r in guard let self = self else { return }; self.catalogLoading = false; switch r { case .success(let o): self.applyCatalog(o); case .failure(let e): self.recordNote.stringValue = e.localizedDescription } }
    }
    private func applyCatalog(_ o: IPCObject) { entries = o["entries"] as? [IPCObject] ?? []; importedNote.stringValue = "本机已导 \(o["importedCount"] as? Int ?? 0) 张"; filterCatalog(); showSelectedTask() }
    func controlTextDidChange(_ obj: Notification) { if obj.object as? NSSearchField === search { filterCatalog() } }
    @objc private func toggleHiddenFilter() { filterCatalog() }
    private func catalogText(_ e: IPCObject, _ key: String) -> String {
        let title = e["Title"] as? String ?? "", project = e["Project"] as? String ?? "", id = e["Id"] as? String ?? ""
        let usefulTitle = title == id ? "" : title
        let primary = project.isEmpty ? usefulTitle : project
        if key == "Primary" { return primary }
        if key == "Alternate" { return usefulTitle.caseInsensitiveCompare(primary) == .orderedSame ? "" : usefulTitle }
        if e["ManualOnly"] as? Bool == true { if key == "Status" { return "手工补录" }; if key == "TargetName" { return "仅本机登记" } }
        return e[key] as? String ?? ""
    }
    private func filterCatalog() {
        let oldSelection = selectedEntry?["Id"] as? String
        let keep = requestedCatalogSelection ?? oldSelection, term = search.stringValue.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        requestedCatalogSelection = nil
        let typedTitle = editTitle.stringValue, typedProject = editProject.stringValue
        filtered = entries.filter { e in
            (e["HiddenFromCatalog"] as? Bool ?? false) == (showHidden.state == .on) &&
            (term.isEmpty || ["Id", "Title", "Project", "Status", "TargetName", "TitleState"].contains { catalogText(e, $0).lowercased().contains(term) })
        }
        table.reloadData()
        if let i = filtered.firstIndex(where: { ($0["Id"] as? String) == keep }) {
            table.selectRowIndexes(IndexSet(integer: i), byExtendingSelection: false)
            // Other drives may finish while labels are being edited. Keep the user's text.
            if keep == oldSelection { editTitle.stringValue = typedTitle; editProject.stringValue = typedProject }
            else { tableViewSelectionDidChange(Notification(name: NSTableView.selectionDidChangeNotification, object: table)) }
        }
        else { table.deselectAll(nil); editTitle.stringValue = ""; editProject.stringValue = ""; preview.image = nil; recordNote.stringValue = "选择登记后可改标签、续导或查看回执。" }
    }
    func numberOfRows(in tableView: NSTableView) -> Int { filtered.count }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? { guard filtered.indices.contains(row) else { return nil }; return label(catalogText(filtered[row], tableColumn?.identifier.rawValue ?? "")) }
    private var selectedEntry: IPCObject? { filtered.indices.contains(table.selectedRow) ? filtered[table.selectedRow] : nil }
    func tableViewSelectionDidChange(_ notification: Notification) {
        guard let e = selectedEntry else { return }
        editTitle.stringValue = e["Title"] as? String ?? ""; editProject.stringValue = e["Project"] as? String ?? ""
        let hidden = e["HiddenFromCatalog"] as? Bool ?? false
        editTitle.isEditable = !hidden; editProject.isEditable = !hidden
        recordNote.stringValue = "盘号：\(e["Id"] as? String ?? "") · \(e["Status"] as? String ?? "")\n" + (hidden ? "已从本机列表移除；照片、素材和回执保留，可恢复。" : e["ManualOnly"] as? Bool == true ? "仅本机补录，无归档文件。" : "修改标签不重命名归档目录。")
        preview.image = (e["Photo"] as? String).flatMap { NSImage(contentsOfFile: $0) }
    }
    @objc private func saveLabels() { guard let e = selectedEntry, e["HiddenFromCatalog"] as? Bool != true, let id = e["Id"] as? String else { return }; CoreIPC.run("edit", ["id": id, "title": editTitle.stringValue, "project": editProject.stringValue]) { [weak self] r in switch r { case .success: self?.refreshCatalog(); case .failure(let error): self?.notice(error.localizedDescription) } } }
    @objc private func addManual() {
        let title = NSTextField(string: ""), project = NSTextField(string: ""); stretch(title, width: 360); stretch(project, width: 360)
        let alert = NSAlert(); alert.messageText = "手工补录"; alert.informativeText = "只在本机登记，不启动导盘、不创建归档文件。标题和项目至少填写一项。"; alert.accessoryView = column([label("标题"), title, label("项目"), project]); alert.addButton(withTitle: "保存补录"); alert.addButton(withTitle: "取消")
        alert.beginSheetModal(for: window) { [weak self] r in
            guard let self = self, r == .alertFirstButtonReturn else { return }
            guard !title.stringValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || !project.stringValue.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { self.notice("标题和项目至少填写一项。"); return }
            CoreIPC.run("addManual", ["title": title.stringValue, "project": project.stringValue]) { [weak self] r in switch r { case .success(let o): self?.requestedCatalogSelection = (o["entry"] as? IPCObject)?["Id"] as? String; self?.showHidden.state = .off; self?.search.stringValue = ""; self?.refreshCatalog(); case .failure(let e): self?.notice(e.localizedDescription) } }
        }
    }
    @objc private func hideSelected() { setSelectedHidden(true) }
    @objc private func restoreSelected() { setSelectedHidden(false) }
    private func setSelectedHidden(_ hidden: Bool) {
        guard let entry = selectedEntry, let id = entry["Id"] as? String, (entry["HiddenFromCatalog"] as? Bool ?? false) != hidden else { return }
        guard !jobs.values.contains(where: { $0.process != nil && (($0.entry?["Id"] as? String) == id || $0.registeredID == id || $0.submission.resumeId == id) }) else { notice("此盘仍在工作，请结束后再移除或恢复。"); return }
        let alert = NSAlert(); alert.messageText = hidden ? "从本机列表移除 \(id)？" : "恢复到本机列表 \(id)？"; alert.informativeText = "只改变本机列表显示；照片、已导素材与硬盘回执均保留。"; alert.addButton(withTitle: hidden ? "从本机移除" : "恢复到本机列表"); alert.addButton(withTitle: "取消")
        alert.beginSheetModal(for: window) { [weak self] r in guard r == .alertFirstButtonReturn else { return }; CoreIPC.run("setHidden", ["id": id, "hidden": hidden]) { result in switch result { case .success: self?.refreshCatalog(); case .failure(let e): self?.notice(e.localizedDescription) } } }
    }
    @objc private func exportLocal() { export(command: "export") }
    @objc private func exportTarget() { guard !targetField.stringValue.isEmpty else { notice("请先在导盘页选择归档目录。"); return }; export(command: "exportTarget") }
    private func export(command: String) { let p = NSSavePanel(); p.title = "导出CSV登记表"; p.nameFieldStringValue = "光盘登记册.csv"; let target = targetField.stringValue; p.beginSheetModal(for: window) { [weak self] r in guard r == .OK, let url = p.url else { return }; CoreIPC.run(command, ["target": target, "path": url.path]) { result in switch result { case .success: self?.notice("登记表已导出：\(url.path)"); case .failure(let error): self?.notice(error.localizedDescription) } } } }
    private func isRetryable(_ entry: IPCObject?) -> Bool {
        guard let e = entry, e["ManualOnly"] as? Bool != true, e["HiddenFromCatalog"] as? Bool != true else { return false }
        if let current = entries.first(where: { ($0["Id"] as? String) == (e["Id"] as? String) }), current["HiddenFromCatalog"] as? Bool == true { return false }
        return ["部分拷出", "待复查"].contains(e["Status"] as? String ?? "")
    }
    @objc private func resumeSelected() {
        guard let entry = selectedEntry, entry["ManualOnly"] as? Bool != true, entry["HiddenFromCatalog"] as? Bool != true else { notice("手工补录或已移除的登记不能导盘。请先恢复已移除项。"); return }
        confirmResume(entry, expectedTask: nil)
    }
    @objc private func retrySelectedTask() {
        guard let task = jobs[selectedDrive], task.process == nil, isRetryable(task.entry), let entry = task.entry else { return }
        confirmResume(entry, expectedTask: task)
    }
    private static func resumeTarget(_ submission: DeskSubmission, entry: IPCObject) throws -> DeskSubmission {
        var frozen = submission
        let marker = (entry["TargetId"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        if !marker.isEmpty {
            // Host.TargetMarker verifies the GUID at the newly selected path; ResumeKnown
            // verifies that this archive is the original one. Never send the OS device ID here.
            frozen.archiveTargetId = marker
        } else {
            let original = entry["TargetRoot"] as? String ?? ""
            guard !original.isEmpty, URL(fileURLWithPath: original).standardizedFileURL.path == URL(fileURLWithPath: frozen.target).standardizedFileURL.path else { throw CaptureFailure("这条旧登记没有归档身份标记，请选择原归档目录：\(original)。不会仅凭新路径认定为原备份位置。") }
        }
        return frozen
    }
    private func confirmResume(_ entry: IPCObject, expectedTask: DeskTask?) {
        guard !retryChecking, !closing, let id = entry["Id"] as? String, let photo = entry["Photo"] as? String, !photo.isEmpty else { notice("没有可复用的原照片，请查看回执。"); return }
        if let current = entries.first(where: { $0["Id"] as? String == id }), current["HiddenFromCatalog"] as? Bool == true || current["ManualOnly"] as? Bool == true { notice("此登记已移除或为手工项，不能导盘。"); return }
        guard !jobs.values.contains(where: { $0.process != nil && (($0.entry?["Id"] as? String) == id || $0.registeredID == id || $0.submission.resumeId == id) }) else { notice("原盘号已在其他光驱工作，不能同时续导。"); return }
        let driveID = selectedDrive, expectedIdentity = jobs[selectedDrive]?.identity
        if let task = expectedTask { guard jobs[driveID] === task else { return } }
        guard !driveID.isEmpty, jobs[driveID]?.process == nil, let drive = drives.first(where: { $0.id == driveID }), drive.ready && !drive.empty else { notice("请把原盘 \(id) 放入当前所选光驱，等待识盘后重试；无需重新拍照。"); return }
        // Freeze the chosen drive and original target before displaying the human confirmation.
        // A catalog refresh, drive switch, or replacement task cannot redirect this callback.
        let previousCheck = manualCheck.state; manualCheck.state = .on
        let frozen: DeskSubmission
        do { frozen = try Self.resumeTarget(snapshot(resume: id), entry: entry) } catch { manualCheck.state = previousCheck; notice(error.localizedDescription); return }
        manualCheck.state = previousCheck
        retryChecking = true; showSelectedTask()
        let alert = NSAlert(); alert.messageText = "以原盘号核对/续导 \(id)"
        alert.informativeText = "将保留当前所选光驱 \(drive.name)。请确认其中实物确实为原盘 \(id)，与原照片一致。沿用原登记与回执，检查完整文件并重新读取未完成项，无需重拍。如果已经换盘，请取消并重新登记。\n归档组件会独立核对盘片，原盘一致便开始读取；若介质变化或证据不足，主窗口会提示再次核对并点“已核对所选盘，继续”。"; alert.addButton(withTitle: "已核对原盘，续导"); alert.addButton(withTitle: "取消")
        alert.beginSheetModal(for: window) { [weak self] r in
            guard let self = self else { return }; self.retryChecking = false; self.showSelectedTask()
            guard r == .alertFirstButtonReturn, !self.closing, self.selectedDrive == driveID, self.jobs[driveID]?.process == nil else { return }
            guard self.jobs[driveID]?.identity == expectedIdentity else { return }
            // Query devices once more: a tray may change between timer ticks while the sheet is open.
            CoreIPC.run("devices") { [weak self] result in
                guard let self = self, self.selectedDrive == driveID, self.jobs[driveID]?.process == nil, !self.closing else { return }
                guard self.jobs[driveID]?.identity == expectedIdentity else { return }
                guard case .success(let o) = result,
                      let current = (o["drives"] as? [IPCObject] ?? []).map(DeskDrive.init).first(where: { $0.id == driveID }), current.ready, !current.empty, current.mediaHint == frozen.drive.mediaHint else { self.notice("确认期间光驱或盘片状态变化，请重新核对后再试；原照片保留。"); return }
                self.drives = (o["drives"] as? [IPCObject] ?? []).map(DeskDrive.init); self.targets = o["targets"] as? [IPCObject] ?? []
                // This branch is reachable only after the original-disc sheet was accepted
                // and a second device query still observed the same medium. Never infer it in snapshot/payload.
                var confirmed = frozen
                if !current.mediaHint.isEmpty { confirmed.resumeConfirmedHint = current.mediaHint }
                self.tabs.selectTabViewItem(at: 0); self.start(photo: URL(fileURLWithPath: photo), submission: confirmed, ownTemporary: false)
            }
        }
    }
    func tabView(_ tabView: NSTabView, didSelect tabViewItem: NSTabViewItem?) { capture?.setCameraPageActive(tabViewItem?.label == "摄像头") }
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse, withCompletionHandler completionHandler: @escaping () -> Void) {
        let info = response.notification.request.content.userInfo
        let drive = info["drive"] as? String ?? "", identity = info["task"] as? String ?? "", entry = info["entry"] as? String ?? ""
        let action = response.actionIdentifier
        Task { @MainActor [weak self] in
            guard let self = self else { completionHandler(); return }
            guard action == UNNotificationDefaultActionIdentifier || action == Self.retryNotificationAction else { completionHandler(); return }
            self.window.deminiaturize(nil); self.window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true); self.tabs.selectTabViewItem(at: 0)
            if let task = self.notificationTask(drive: drive, identity: identity, entry: entry, action: action) {
                // Default clicks only display results; explicit retry still requires the original-disc confirmation.
                self.selectedDrive = drive; self.manualCheck.state = .off; self.rebuildDriveMenu(); self.showSelectedTask()
                if action == Self.retryNotificationAction, let original = task.entry { self.confirmResume(original, expectedTask: task) }
            } else { self.notice("这条通知对应的任务已被替换或当前不可重试，请在登记册按盘号 \(entry) 查阅；不会操作当前任务。") }
            completionHandler()
        }
    }
    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification, withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) { completionHandler([.banner, .sound]) }
    private func notificationCategory(for task: DeskTask) -> String {
        task.gotResult && task.process == nil && isRetryable(task.entry) ? Self.retryNotificationCategory : ""
    }
    private func notificationTask(drive: String, identity: String, entry: String, action: String = UNNotificationDefaultActionIdentifier) -> DeskTask? {
        guard action == UNNotificationDefaultActionIdentifier || action == Self.retryNotificationAction,
              let task = jobs[drive], task.identity == identity, (task.entry?["Id"] as? String ?? "") == entry else { return nil }
        if action == Self.retryNotificationAction { guard !closing, !retryChecking, !notificationCategory(for: task).isEmpty else { return nil } }
        return task
    }

    // Run only from the packaged --check-workbench entry point. This uses the isolated
    // real catalog and real AppKit controls; synthetic tasks never invoke a drive job.
    func checkWorkbenchFeatures(completion: @escaping (Result<String, Error>) -> Void) {
        guard MacBundle.checkingWorkbench,
              MacBundle.state.lastPathComponent.hasPrefix("DiscArchive-check-"),
              MacBundle.state.path.hasPrefix(FileManager.default.temporaryDirectory.resolvingSymlinksInPath().path) else { completion(.failure(CaptureFailure("UI 自查只允许临时登记目录。"))); return }
        timer?.invalidate()
        Task { @MainActor in
            func command(_ name: String, _ payload: IPCObject = [:]) async throws -> IPCObject {
                try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<IPCObject, Error>) in CoreIPC.run(name, payload) { continuation.resume(with: $0) } }
            }
            func require(_ condition: Bool, _ text: String) throws { if !condition { throw CaptureFailure(text) } }
            do {
                let initial = try await command("list"); applyCatalog(initial)
                try require(entries.isEmpty && initial["importedCount"] as? Int == 0 && importedNote.stringValue == "本机已导 0 张", "临时登记目录不是空目录或计数回执缺失。")
                let added = try await command("addManual", ["title": "UI核验项目", "project": "UI核验项目"])
                guard let entry = added["entry"] as? IPCObject, let id = entry["Id"] as? String else { throw CaptureFailure("补录未返回盘号。") }
                requestedCatalogSelection = id; applyCatalog(try await command("list"))
                try require(filtered.count == 1 && table.numberOfRows == 1 && selectedEntry?["Id"] as? String == id, "补录未出现在登记册或未选中。")
                try require(importedNote.stringValue == "本机已导 0 张" && !isRetryable(entry), "手工项被当作已导或可重试项。")
                try require(catalogText(entry, "Primary") == "UI核验项目" && catalogText(entry, "Alternate").isEmpty, "相同项目和标题未折叠。")
                try require(!table.tableColumns.contains(where: { ["Id", "Drive"].contains($0.identifier.rawValue) }), "盘号或历史光驱仍在列表列中。")
                _ = try await command("edit", ["id": id, "title": "修改后的标题", "project": "修改后的项目"])
                applyCatalog(try await command("list"))
                try require(entries.first?["Title"] as? String == "修改后的标题" && entries.first?["Project"] as? String == "修改后的项目", "补录标签编辑未保存。")
                search.stringValue = id; filterCatalog(); try require(filtered.count == 1, "移到详情的盘号不能搜索。")
                _ = try await command("setHidden", ["id": id, "hidden": true]); applyCatalog(try await command("list"))
                try require(filtered.isEmpty && entries.count == 1 && importedNote.stringValue == "本机已导 0 张", "移除未隐藏登记，或错误改变本机已导数。")
                showHidden.state = .on; filterCatalog(); try require(filtered.count == 1, "查看已移除未显示登记。")
                _ = try await command("setHidden", ["id": id, "hidden": false]); applyCatalog(try await command("list")); try require(filtered.isEmpty, "恢复项仍在已移除列表。")
                showHidden.state = .off; search.stringValue = ""; filterCatalog(); try require(filtered.count == 1, "恢复项未回到正常列表。")
                let fakeDrive = DeskDrive(["id": "ui-check-drive", "name": "虚拟自查光驱"])
                let submission = DeskSubmission(drive: fakeDrive, target: MacBundle.state.path, targetDeviceId: nil, project: "", existingProject: nil, mode: "metadata", view: "auto", resumeId: nil)
                try require(submission.payload["resumeConfirmedHint"] == nil, "普通照片错误产生一次性原盘确认。")
                var unconfirmedResume = submission; unconfirmedResume.resumeId = "ui-check-original"
                try require(unconfirmedResume.payload["resumeConfirmedHint"] == nil, "未经确认的续导错误产生一次性原盘确认。")
                unconfirmedResume.drive = DeskDrive(["id": fakeDrive.id, "token": "observation-session:ordinary-media-hint"])
                try require(unconfirmedResume.payload["mediaHint"] as? String == "ordinary-media-hint" && unconfirmedResume.payload["resumeConfirmedHint"] == nil, "普通介质观察被错误当作确认授权。")
                var moved = submission; moved.target = "/Volumes/RenamedBackup/Archive"; moved.targetDeviceId = "os-device-not-an-archive-guid"
                let marker = "0123456789abcdef0123456789abcdef"
                let movedResume = try Self.resumeTarget(moved, entry: ["TargetRoot": "/Volumes/OldBackup/Archive", "TargetId": marker])
                try require(movedResume.target == moved.target && movedResume.payload["targetId"] as? String == marker && movedResume.payload["targetDeviceId"] as? String == moved.targetDeviceId, "改卷名续导丢失归档GUID或混用了设备身份。")
                let legacy = try Self.resumeTarget(submission, entry: ["TargetRoot": submission.target])
                try require(legacy.payload["targetId"] == nil, "无GUID旧记录错误采用了设备身份。")
                var rejectedMovedLegacy = false
                do { _ = try Self.resumeTarget(moved, entry: ["TargetRoot": "/Volumes/OldBackup/Archive"]) } catch { rejectedMovedLegacy = true }
                try require(rejectedMovedLegacy, "无GUID旧记录被允许静默改归档路径。")
                let old = DeskTask(submission), replacement = DeskTask(submission); old.entry = entry; replacement.entry = entry
                jobs[fakeDrive.id] = replacement
                defer { jobs.removeValue(forKey: fakeDrive.id) }
                let previousDrive = selectedDrive
                try require(notificationTask(drive: fakeDrive.id, identity: old.identity, entry: id) == nil, "过期通知命中了替换后的任务。")
                try require(notificationTask(drive: "another-drive", identity: replacement.identity, entry: id) == nil, "通知跨驱命中任务。")
                try require(notificationTask(drive: fakeDrive.id, identity: replacement.identity, entry: "wrong-entry") == nil, "通知未验证原盘号。")
                try require(notificationTask(drive: fakeDrive.id, identity: replacement.identity, entry: id) === replacement && selectedDrive == previousDrive, "通知路由改变了当前光驱或没有命中正确任务。")
                try require(notificationCategory(for: replacement).isEmpty && notificationTask(drive: fakeDrive.id, identity: replacement.identity, entry: id, action: Self.retryNotificationAction) == nil, "手工项通知错误提供重试。")
                var retryEntry: IPCObject = ["Id": "ui-check-original", "Status": "部分拷出", "ManualOnly": false, "HiddenFromCatalog": false]
                replacement.entry = retryEntry; replacement.gotResult = true
                for status in ["部分拷出", "待复查"] {
                    retryEntry["Status"] = status; replacement.entry = retryEntry
                    try require(notificationCategory(for: replacement) == Self.retryNotificationCategory, "可重试结果未提供通知类别。")
                    for action in [UNNotificationDefaultActionIdentifier, Self.retryNotificationAction] {
                        try require(notificationTask(drive: fakeDrive.id, identity: replacement.identity, entry: "ui-check-original", action: action) === replacement, "有效通知动作被拒绝。")
                        try require(notificationTask(drive: fakeDrive.id, identity: old.identity, entry: "ui-check-original", action: action) == nil, "过期通知动作命中了替换任务。")
                        try require(notificationTask(drive: "another-drive", identity: replacement.identity, entry: "ui-check-original", action: action) == nil, "通知动作跨驱命中。")
                        try require(notificationTask(drive: fakeDrive.id, identity: replacement.identity, entry: "wrong-entry", action: action) == nil, "通知动作未验证盘号。")
                    }
                }
                retryEntry["Status"] = "已拷出"; replacement.entry = retryEntry
                try require(notificationCategory(for: replacement).isEmpty && notificationTask(drive: fakeDrive.id, identity: replacement.identity, entry: "ui-check-original", action: Self.retryNotificationAction) == nil, "已完成任务错误接受通知重试。")
                try require(notificationTask(drive: fakeDrive.id, identity: replacement.identity, entry: "ui-check-original") === replacement, "普通结果默认点击无法查看。")
                retryEntry["Status"] = "部分拷出"; retryEntry["HiddenFromCatalog"] = true; replacement.entry = retryEntry
                try require(notificationCategory(for: replacement).isEmpty, "已移除任务错误提供通知重试。")
                try require(notificationTask(drive: fakeDrive.id, identity: replacement.identity, entry: "ui-check-original", action: "unknown-action") == nil && selectedDrive == previousDrive, "未知动作被接受或通知门禁改变当前驱动。")
                completion(.success("PASS packaged AppKit catalog: manual add, count, duplicate title folding, label edit, ID search, local hide/restore; resume archive GUID versus OS device identity and legacy path policy; notification default/retry category and stale-task/drive/entry/action guards (synthetic routing only)"))
            } catch { completion(.failure(error)) }
        }
    }
    @objc private func openProject() { guard let e = selectedEntry, e["ManualOnly"] as? Bool != true, e["HiddenFromCatalog"] as? Bool != true else { return }; let root = e["TargetRoot"] as? String ?? ""; let folders = e["ArchiveFolders"] as? [String] ?? []; let candidates = folders.map { $0.hasPrefix("/") ? $0 : URL(fileURLWithPath: root).appendingPathComponent($0).path }; openPaths(candidates.isEmpty ? [e["JobDir"] as? String ?? ""] : candidates) }
    @objc private func openReceipt() { guard let e = selectedEntry, e["ManualOnly"] as? Bool != true, e["HiddenFromCatalog"] as? Bool != true else { return }; openPaths([e["Report"] as? String ?? e["JobDir"] as? String ?? ""]) }
    private func openPaths(_ paths: [String]) { let paths = paths.filter { !$0.isEmpty && FileManager.default.fileExists(atPath: $0) }; guard !paths.isEmpty else { notice("目录或回执不在线，请接回原归档卷。"); return }; if paths.count == 1 { NSWorkspace.shared.open(URL(fileURLWithPath: paths[0])); return }; let alert = NSAlert(); let menu = NSPopUpButton(); menu.addItems(withTitles: paths); alert.messageText = "选择要打开的项目目录"; alert.accessoryView = menu; alert.addButton(withTitle: "打开"); alert.addButton(withTitle: "取消"); alert.beginSheetModal(for: window) { r in if r == .alertFirstButtonReturn, let path = menu.titleOfSelectedItem { NSWorkspace.shared.open(URL(fileURLWithPath: path)) } } }
    @objc private func showEvidence() { guard let e = selectedEntry else { return }; let text = ["DiscLabel", "PhotoText", "SourceFormat", "VisionNote", "NamingReason", "CanonicalId", "IdentityEvidence", "CloseWarning"].map { "\($0)：\(e[$0] ?? "")" }.joined(separator: "\n\n"); notice(text) }

    private func notice(_ text: String) { let alert = NSAlert(); alert.messageText = "光盘归档"; alert.informativeText = String(text.prefix(8000)); alert.addButton(withTitle: "知道了"); alert.beginSheetModal(for: window) }
}
