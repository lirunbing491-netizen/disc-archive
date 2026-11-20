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
    static let helpers = contents.appendingPathComponent("Helpers/" + arch, isDirectory: true)
    static let core = helpers.appendingPathComponent("discarchive-core")
    static let state = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("DiscArchive/Mac", isDirectory: true)
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
            withExtendedLifetime(desk) { app.run() }
        } catch {
            let alert=NSAlert();alert.messageText="导盘助手无法启动";alert.informativeText=error.localizedDescription;alert.runModal()
        }
    }
}
