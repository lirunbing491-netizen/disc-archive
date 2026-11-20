// Standalone macOS test target. Do NOT compile into the application.
// From repository root:
// swiftc -swift-version 5 macos/App/IPC.swift macos/App/tests/IPCIntegrationTests.swift -o /tmp/disc-ipc-tests
// /tmp/disc-ipc-tests
// This executable launches itself as a synthetic JSON-lines backend. No camera,
// optical device, account, real core, Python, network, or persistent user data is used.
import Foundation
import Darwin

// Minimal production-dependency replacements for this standalone test target only.
enum MacBundle {
    static let core = URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL
    static let environment = ProcessInfo.processInfo.environment
}
struct CaptureFailure: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

private struct TestFailure: Error, CustomStringConvertible {
    let description: String
    init(_ message: String) { description = message }
}
private func require(_ condition: @autoclosure () -> Bool, _ message: String) throws {
    guard condition() else { throw TestFailure(message) }
}
private func pump(until condition: () -> Bool, timeout: TimeInterval = 12) throws {
    let deadline = Date().addingTimeInterval(timeout)
    while !condition() && Date() < deadline {
        _ = RunLoop.current.run(mode: .default, before: Date().addingTimeInterval(0.01))
    }
    try require(condition(), "Timed out waiting for synthetic IPC callbacks")
}
private func drain(for seconds: TimeInterval = 0.12) {
    let deadline = Date().addingTimeInterval(seconds)
    while Date() < deadline { _ = RunLoop.current.run(mode: .default, before: Date().addingTimeInterval(0.01)) }
}

private final class Probe {
    let drive: String
    var events: [IPCObject] = []
    var exits = 0, code: Int32?, diagnostic = "", wrongThread = false
    var handle: CoreIPC.Job?
    init(_ drive: String) { self.drive = drive }
    func event(_ object: IPCObject) { wrongThread = wrongThread || !Thread.isMainThread; events.append(object) }
    func exited(_ value: Int32, _ text: String) { wrongThread = wrongThread || !Thread.isMainThread; exits += 1; code = value; diagnostic = text }
    func verify(expectedStatus: String) throws {
        try require(!wrongThread, "\(drive): callback did not arrive on main thread")
        try require(exits == 1 && code == 0, "\(drive): expected exactly one successful exit")
        try require(events.first?["event"] as? String == "registered", "\(drive): registration must precede result")
        try require(events.filter { $0["event"] as? String == "registered" }.count == 1, "\(drive): duplicated registration")
        let results = events.filter { $0["event"] as? String == "result" }
        try require(results.count == 1, "\(drive): missing/duplicate final result (including EOF tail)")
        try require((results.first?["entry"] as? IPCObject)?["Status"] as? String == expectedStatus, "\(drive): incorrect final state")
        for e in events where e["event"] as? String != "error" {
            try require(e["drive"] as? String == drive, "\(drive): received another task's event")
            if let entry = e["entry"] as? IPCObject { try require(entry["Id"] as? String == "disc-" + drive, "\(drive): result identity crossed streams") }
        }
    }
}

@main struct IPCIntegrationTests {
    static func main() {
        do {
            if CommandLine.arguments.count > 1 { try backend(CommandLine.arguments[1]); return }
            try shortCommands()
            try registeredAndResult()
            try eofTail()
            try cancelOneAndKeepOthers()
            try brokenInputDoesNotKillApp()
            print("PASS: short commands, stdin EOF, registration/result ordering, fragmented UTF-8/CRLF, final line without newline, per-job cancellation, EOF+exit, concurrent stream isolation, broken stdin, main-thread callbacks, exactly-once completion")
        } catch {
            let text = "FAIL: \(error)\n"; try? FileHandle.standardError.write(contentsOf: Data(text.utf8)); Darwin.exit(1)
        }
    }

    private static func shortCommands() throws {
        var done = 0, answer: Result<IPCObject, Error>?, threadWrong = false
        let echo = "中文盘面\n引号\"与反斜线\\"
        CoreIPC.run("short", ["echo": echo]) { result in threadWrong = !Thread.isMainThread; done += 1; answer = result }
        try pump { done > 0 }; drain()
        try require(done == 1 && !threadWrong, "short: completion must run once on main")
        guard case .success(let object)? = answer else { throw TestFailure("short: missing success") }
        try require(object["echo"] as? String == echo, "short: escaped Unicode payload changed")
        try require(object["sawEOF"] as? Bool == true, "short: stdin was not closed after initial JSON line")
        var failureDone = false, expectedFailure = false
        CoreIPC.run("short-error") { result in if case .failure(let error) = result { expectedFailure = error.localizedDescription == "synthetic rejection" }; failureDone = true }
        try pump { failureDone }; try require(expectedFailure, "short-error: backend ok:false must reach caller as failure")
        var exitDone = false, rejectedExit = false
        CoreIPC.run("short-exit") { result in if case .failure = result { rejectedExit = true }; exitDone = true }
        try pump { exitDone }; try require(rejectedExit, "short-exit: nonzero process exit must not be success")
    }
    private static func launch(_ probe: Probe, behavior: String, onRegistration: @escaping (Probe) -> Void) throws {
        probe.handle = try CoreIPC.Job(command: "job", payload: ["drive": probe.drive, "behavior": behavior], persistent: true, onEvent: { [weak probe] object in
            guard let probe = probe else { return }; probe.event(object)
            if object["event"] as? String == "registered" { onRegistration(probe) }
        }, onExit: { [weak probe] code, text in probe?.exited(code, text) })
    }
    private static func registeredAndResult() throws {
        let p = Probe("single")
        try launch(p, behavior: "normal") { $0.handle?.send(["action": "confirm", "marker": "single"]) }
        try pump { p.exits > 0 }; drain(); try p.verify(expectedStatus: "Complete")
        try require(p.events.contains { $0["text"] as? String == "盘面识别完成 ✓" }, "single: fragmented multi-byte output was lost")
        try require(!p.events.contains { $0["event"] as? String == "error" }, "single: unexpected protocol error")
    }
    private static func eofTail() throws {
        let p = Probe("eof")
        try launch(p, behavior: "eof") { _ in } // Child finishes without ever reading a second input line.
        try pump { p.exits > 0 }; drain(); try p.verify(expectedStatus: "Complete")
        let count = p.events.count
        p.handle?.send(["action": "confirm"]) // Late sends must not crash the parent or redeliver exit.
        drain(); try require(p.exits == 1 && p.events.count == count, "eof: callbacks escaped after terminal completion")
    }
    private static func cancelOneAndKeepOthers() throws {
        let a = Probe("A"), b = Probe("B"), c = Probe("C")
        try launch(a, behavior: "normal") { p in DispatchQueue.main.asyncAfter(deadline: .now()+0.18) { p.handle?.send(["action": "confirm", "marker": "A"]) } }
        try launch(b, behavior: "normal") { $0.handle?.send(["action": "cancel", "marker": "B"]) }
        try launch(c, behavior: "normal") { p in DispatchQueue.main.asyncAfter(deadline: .now()+0.10) { p.handle?.send(["action": "confirm", "marker": "C"]) } }
        try pump { a.exits > 0 && b.exits > 0 && c.exits > 0 }; drain()
        try a.verify(expectedStatus: "Complete"); try b.verify(expectedStatus: "Cancelled"); try c.verify(expectedStatus: "Complete")
        for p in [a,b,c] { try require(!p.events.contains { $0["event"] as? String == "error" }, "\(p.drive): commands crossed streams or child input closed prematurely") }
    }
    private static func brokenInputDoesNotKillApp() throws {
        let p = Probe("broken")
        try launch(p, behavior: "closed-input") { $0.handle?.send(["action": "confirm"]) }
        try pump { p.exits > 0 }; drain(); try p.verify(expectedStatus: "InputClosed")
        try require(p.events.contains { $0["event"] as? String == "error" }, "broken: EPIPE should report a local error without killing the app")
    }

    // MARK: Synthetic child mode (same binary, separate process and pipes per job)
    private static func write(_ object: IPCObject, newline: Bool = true, fragmented: Bool = false) throws {
        var data = try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])
        if newline { data.append(contentsOf: [13,10]) }
        if fragmented {
            // Deliberately split every byte, including multi-byte Chinese characters and CRLF.
            for byte in data { try FileHandle.standardOutput.write(contentsOf: Data([byte])) }
        } else { try FileHandle.standardOutput.write(contentsOf: data) }
    }
    private static func object(_ text: String) throws -> IPCObject {
        guard let data = text.data(using: .utf8), let o = try JSONSerialization.jsonObject(with: data) as? IPCObject else { throw TestFailure("fake backend: invalid JSON input") }; return o
    }
    private static func backend(_ command: String) throws {
        if command.hasPrefix("short") {
            // Blocks unless the one-shot parent closes its stdin after exactly one JSON line.
            let data = FileHandle.standardInput.readDataToEndOfFile()
            guard let text = String(data: data, encoding: .utf8) else { throw TestFailure("fake backend: invalid UTF-8") }
            let lines = text.split(separator: "\n"); try require(lines.count == 1, "fake backend: expected one input line")
            let input = try object(String(lines[0]))
            if command == "short-error" { try write(["ok": false, "error": "synthetic rejection"]); return }
            if command == "short-exit" { try write(["ok": true]); Darwin.exit(9) }
            try write(["ok": true, "echo": input["echo"] as? String ?? "", "sawEOF": true], newline: false, fragmented: true); return
        }
        try require(command == "job", "fake backend: unsupported command")
        guard let first = readLine() else { throw TestFailure("fake backend: missing job payload") }
        let input = try object(first); let drive = input["drive"] as? String ?? ""; let behavior = input["behavior"] as? String ?? ""
        if behavior == "closed-input" { try FileHandle.standardInput.close() }
        try write(["event": "registered", "id": "disc-" + drive, "photo": "synthetic.jpg", "drive": drive])
        if behavior == "closed-input" {
            Thread.sleep(forTimeInterval: 0.25)
            try write(["event": "result", "drive": drive, "entry": ["Id": "disc-" + drive, "Status": "InputClosed"]], newline: false); return
        }
        if behavior == "eof" {
            try write(["event": "result", "drive": drive, "entry": ["Id": "disc-" + drive, "Status": "Complete"]], newline: false)
            try FileHandle.standardOutput.close(); Thread.sleep(forTimeInterval: 0.1); return
        }
        guard let actionLine = readLine() else { throw TestFailure("fake backend: persistent job stdin closed before follow-up action") }
        let action = try object(actionLine)
        try require(action["marker"] as? String == drive, "fake backend: another task's input arrived")
        let cancelled = action["action"] as? String == "cancel"
        try require(cancelled || action["action"] as? String == "confirm", "fake backend: unexpected follow-up action")
        try write(["event": "status", "drive": drive, "text": "盘面识别完成 ✓"], fragmented: true)
        try write(["event": "result", "drive": drive, "entry": ["Id": "disc-" + drive, "Status": cancelled ? "Cancelled" : "Complete"]], newline: false)
    }
}
