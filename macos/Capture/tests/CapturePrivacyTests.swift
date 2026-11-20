// Offline test executable; run on macOS 13+ (no camera, network, or Keychain access):
// swiftc -swift-version 5 -o /tmp/disc-capture-tests macos/Capture/CapturePane.swift macos/Capture/VisionPrivacy.swift macos/Capture/VisionPane.swift macos/Capture/tests/CapturePrivacyTests.swift
// /tmp/disc-capture-tests
import AppKit
import Vision

@main struct CapturePrivacyTests {
    static func main() throws {
        func check(_ value: @autoclosure () throws -> Bool, _ message: String) throws {
            if try !value() { throw CaptureFailure("测试失败：" + message) }
        }
        for invalid in ["http://example.com/v1", "ftp://localhost/v1", "https://u:p@example.com/v1", "https://example.com/v1?key=private", "https://example.com/v1#secret", "http://127.0.0.2/v1", "http://localhost.example.org/v1"] {
            do { _ = try VisionPrivacy.endpoint(invalid); throw CaptureFailure("测试失败：危险地址被接受") }
            catch let error as CaptureFailure { if error.message.hasPrefix("测试失败") { throw error } }
        }
        try check(try VisionPrivacy.endpoint("https://example.com/v1").absoluteString == "https://example.com/v1/chat/completions", "地址追加路径")
        try check(try VisionPrivacy.endpoint("http://localhost:8080/v1").host == "127.0.0.1", "本机回环固定地址")
        try check(try VisionPrivacy.endpoint("https://example.com/v1/chat/completions").path == "/v1/chat/completions", "完整地址不得重复追加")
        let alphabet = Array("ABCDEFGHJKLMNPQRSTUVWXYZ23456789")
        let expected = String((0..<8).map { _ in alphabet.randomElement()! })
        let original = try VisionPrivacy.synthetic(expected)
        try check(try VisionPrivacy.discRect(original) != nil, "合成盘面应可定位")
        let masked = try VisionPrivacy.outboundImage(original, allowFull: false)
        try check(masked.width < original.width, "外发图应裁掉背景")
        // Inspect decoded pixels for opaque white background and masked center.
        var bytes = [UInt8](repeating: 0, count: masked.width * masked.height * 4)
        try bytes.withUnsafeMutableBytes { raw in
            guard let c = CGContext(data: raw.baseAddress, width: masked.width, height: masked.height, bitsPerComponent: 8, bytesPerRow: masked.width*4, space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { throw CaptureFailure("像素检查失败") }
            c.draw(masked, in: CGRect(x: 0, y: 0, width: CGFloat(masked.width), height: CGFloat(masked.height)))
        }
        let center = ((masked.height/2)*masked.width + masked.width/2)*4
        try check(bytes[0] > 250 && bytes[1] > 250 && bytes[2] > 250, "背景必须被白色覆盖")
        try check(bytes[center] > 250 && bytes[center+1] > 250 && bytes[center+2] > 250, "中心孔必须被白色覆盖")
        let jpeg = try VisionPrivacy.jpeg(masked)
        try check(!jpeg.isEmpty, "JPEG 应有内容")
        let blank = NSImage(size: NSSize(width: 700, height: 700)); blank.lockFocus(); NSColor.white.setFill(); NSBezierPath(rect: NSRect(x: 0, y: 0, width: 700, height: 700)).fill(); blank.unlockFocus()
        var rect = NSRect(x: 0, y: 0, width: 700, height: 700)
        guard let blankPixels = blank.cgImage(forProposedRect: &rect, context: nil, hints: nil) else { throw CaptureFailure("空白图生成失败") }
        do { _ = try VisionPrivacy.outboundImage(blankPixels, allowFull: false); throw CaptureFailure("测试失败：未定位图不得外发") }
        catch let error as CaptureFailure { if error.message.hasPrefix("测试失败") { throw error } }
        _ = try VisionPrivacy.outboundImage(blankPixels, allowFull: true)
        let request = VNRecognizeTextRequest(); request.recognitionLevel = .accurate; request.usesLanguageCorrection = false; request.recognitionLanguages = ["en-US"]
        try VNImageRequestHandler(cgImage: masked).perform([request])
        let text = (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }.joined()
        let normalized = String(text.uppercased().filter { $0.isASCII && ($0.isLetter || $0.isNumber) })
        try check(normalized.contains(expected), "随机字符 OCR：预期 \(expected)，实际 \(text)")
        print("PASS: endpoint policy, synthetic disc localization, background+hole mask, fail-closed fallback, JPEG, randomized local OCR. No camera, external service or Keychain was accessed.")
    }
}
