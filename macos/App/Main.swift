import AppKit
import Darwin
import ImageIO
import UniformTypeIdentifiers

enum MacBundle {
    static let contents = Bundle.main.bundleURL.appendingPathComponent("Contents", isDirectory: true)
    #if arch(arm64)
    static let arch = "arm64"
    #else
    static let arch = "x64"
    #endif
    static let helpers = contents.appendingPathComponent("Resources/Runtime/" + arch, isDirectory: true)
    static let core = helpers.appendingPathComponent("discarchive-core")
    static let checkingWorkbench = CommandLine.arguments == [CommandLine.arguments[0], "--check-workbench"]
    static let state: URL = {
        if checkingWorkbench {
            return FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
                .appendingPathComponent("DiscArchive-check-" + UUID().uuidString, isDirectory: true)
        }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("DiscArchive/Mac", isDirectory: true)
    }()
    static var environment: [String: String] {
        var values = ProcessInfo.processInfo.environment
        values["DISCARC_MEDIA_HELPER"] = Bundle.main.executableURL?.path
        values["DISCARC_DEVICE_LIB"] = contents.appendingPathComponent("Frameworks/libDiscDevices.dylib").path
        values["DISCARCHIVE_NATIVE_DIR"] = helpers.appendingPathComponent("native").path
        values["DISCARC_STATE_DIR"] = state.path
        values["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
        return values
    }
    static func thumbnail(_ input: String, _ output: String) throws {
        guard let image = CGImageSourceCreateWithURL(URL(fileURLWithPath: input) as CFURL, nil),
              let thumbnail = CGImageSourceCreateThumbnailAtIndex(image, 0, [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceThumbnailMaxPixelSize: 1200
              ] as CFDictionary),
              let writer = CGImageDestinationCreateWithURL(URL(fileURLWithPath: output) as CFURL, UTType.jpeg.identifier as CFString, 1, nil)
        else { throw CocoaError(.fileReadCorruptFile) }
        CGImageDestinationAddImage(writer, thumbnail, [kCGImageDestinationLossyCompressionQuality: 0.87] as CFDictionary)
        guard CGImageDestinationFinalize(writer) else { throw CocoaError(.fileWriteUnknown) }
    }
}

@main @MainActor
struct ApplicationEntry {
    // Exercise the packaged window, native hardware inventory and bundled core together.
    @MainActor private static func checkWorkbench(_ app: NSApplication, deadline: Date) {
        func views(_ view: NSView) -> [NSView] { [view] + view.subviews.flatMap(views) }
        if let window = app.windows.first(where: { $0.isVisible && $0.title == "光盘归档工作台" }),
           let root = window.contentView {
            let all = views(root)
            let labels = all.compactMap { $0 as? NSTabView }.first?.tabViewItems.map { $0.label }
            let ready = all.compactMap { $0 as? NSTextField }.contains { $0.stringValue.hasPrefix("发现 ") }
            if labels == ["导盘", "登记册", "摄像头", "识图设置"] && ready {
                print("PASS packaged AppKit window, four tabs, startup recovery and native device inventory")
                guard let desk = app.delegate as? Desk else { exit(5) }
                DispatchQueue.main.asyncAfter(deadline: .now() + 45) {
                    fputs("Packaged catalog checks timed out.\n", stderr); exit(5)
                }
                desk.checkWorkbenchFeatures { result in
                    switch result {
                    case .success(let evidence): print("PASS " + evidence); app.terminate(nil)
                    case .failure(let error): fputs("Packaged catalog checks failed: \(error.localizedDescription)\n", stderr); exit(5)
                    }
                }
                return
            }
        }
        if Date() >= deadline { fputs("Packaged workbench did not become ready.\n", stderr); exit(5) }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { checkWorkbench(app, deadline: deadline) }
    }
    static func main() {
        let args = CommandLine.arguments
        if args.count == 4 && args[1] == "thumbnail" {
            do { try MacBundle.thumbnail(args[2], args[3]); exit(0) }
            catch { fputs("无法生成照片缩略图。\n", stderr); exit(2) }
        }
        if args.count == 2 && args[1] == "--check-package" {
            let required = [MacBundle.core.path, MacBundle.environment["DISCARC_DEVICE_LIB"] ?? "",
                            MacBundle.helpers.appendingPathComponent("native/7z.so").path,
                            MacBundle.helpers.appendingPathComponent("native/libdiscarchive7z.dylib").path]
            guard FileManager.default.isExecutableFile(atPath: required[0]), required.allSatisfy({FileManager.default.fileExists(atPath: $0)}) else { exit(3) }
            print("bundle \(MacBundle.arch) OK"); exit(0)
        }
        let app = NSApplication.shared
        do {
            try FileManager.default.createDirectory(at: MacBundle.state, withIntermediateDirectories: true)
            let fd = Darwin.open(MacBundle.state.appendingPathComponent("application.lock").path, O_RDWR | O_CREAT | O_NOFOLLOW, 0o600)
            guard fd >= 0 else { throw CocoaError(.fileWriteNoPermission) }
            guard flock(fd, LOCK_EX | LOCK_NB) == 0 else {
                close(fd)
                if let id=Bundle.main.bundleIdentifier {
                    NSRunningApplication.runningApplications(withBundleIdentifier:id)
                        .first(where: {$0.processIdentifier != ProcessInfo.processInfo.processIdentifier})?
                        .activate(options: [.activateIgnoringOtherApps])
                }
                return
            }
            _ = fcntl(fd, F_SETFD, FD_CLOEXEC)
            defer { close(fd) }
            let desk=Desk()
            app.setActivationPolicy(.regular); app.delegate=desk
            if MacBundle.checkingWorkbench {
                let deadline = Date().addingTimeInterval(35)
                DispatchQueue.main.async { checkWorkbench(app, deadline: deadline) }
            }
            withExtendedLifetime(desk) { app.run() }
        } catch {
            let alert=NSAlert();alert.messageText="导盘助手无法启动";alert.informativeText=error.localizedDescription;alert.runModal()
        }
    }
}
