import Foundation
import Darwin

typealias IPCObject = [String: Any]

/// Newline-delimited JSON, never shells out. The child's stdin is kept open only for jobs.
final class CoreIPC {
    final class Job {
        private let process = Process()
        private let input = Pipe(), output = Pipe(), errors = Pipe()
        private let writer = DispatchQueue(label: "com.discarchive.mac.ipc.write")
        private let reader = DispatchQueue(label: "com.discarchive.mac.ipc.read")
        private var pending = Data()
        private var stderr = Data()
        private var outputClosed = false, terminated = false, exited = false
        private var exitCode: Int32 = -1
        private var event: ((IPCObject) -> Void)?
        private var exit: ((Int32, String) -> Void)?
        private let persistent: Bool
        init(command: String, payload: IPCObject, persistent: Bool, executableURL: URL = MacBundle.core, environment: [String: String] = MacBundle.environment, onEvent: @escaping (IPCObject) -> Void, onExit: @escaping (Int32, String) -> Void) throws {
            self.persistent = persistent; self.event = onEvent; self.exit = onExit
            process.executableURL = executableURL; process.arguments = [command]; process.environment = environment
            process.standardInput = input; process.standardOutput = output; process.standardError = errors
            // A child may close stdin between isRunning and write. Keep that race an EPIPE error,
            // rather than a SIGPIPE that would terminate the entire multi-drive workstation.
            guard fcntl(input.fileHandleForWriting.fileDescriptor, F_SETNOSIGPIPE, 1) != -1 else { throw CaptureFailure("无法建立安全的本地归档通信管道。") }
            let first = try JSONSerialization.data(withJSONObject: payload) + Data([10])
            output.fileHandleForReading.readabilityHandler = { [weak self] handle in
                let data = handle.availableData
                self?.reader.async { [weak self] in self?.receive(data) }
            }
            errors.fileHandleForReading.readabilityHandler = { [weak self] handle in
                let data = handle.availableData
                if data.isEmpty { handle.readabilityHandler = nil }
                self?.reader.async { [weak self] in
                    guard let self = self else { return }; if self.stderr.count < 65536 { self.stderr.append(data.prefix(65536-self.stderr.count)) }
                }
            }
            process.terminationHandler = { [weak self] p in
                self?.reader.async { [weak self] in
                    guard let self = self else { return }; self.exitCode = p.terminationStatus; self.terminated = true; self.finishIfReady()
                }
            }
            do { try process.run() }
            catch { output.fileHandleForReading.readabilityHandler = nil; errors.fileHandleForReading.readabilityHandler = nil; throw error }
            writer.async { [weak self] in
                guard let self = self else { return }
                do { try self.input.fileHandleForWriting.write(contentsOf: first); if !persistent { try self.input.fileHandleForWriting.close() } }
                catch { self.report(["event": "error", "text": "无法将指令发送给归档进程。"]); try? self.input.fileHandleForWriting.close() }
            }
        }
        func send(_ object: IPCObject) {
            guard persistent, let data = try? JSONSerialization.data(withJSONObject: object) else { return }
            writer.async { [weak self] in
                do { try self?.input.fileHandleForWriting.write(contentsOf: data + Data([10])) }
                catch { self?.report(["event": "error", "text": "归档进程已关闭输入，未发送后续操作。"] ) }
            }
        }
        private func report(_ object: IPCObject) { DispatchQueue.main.async { [weak self] in self?.event?(object) } }
        private func receive(_ data: Data) {
            if data.isEmpty {
                outputClosed = true; output.fileHandleForReading.readabilityHandler = nil
                if !pending.isEmpty { parse(pending); pending.removeAll() }; finishIfReady(); return
            }
            pending.append(data)
            // Large catalog responses are allowed, but malformed unbounded output cannot consume all memory.
            if pending.count > 32 * 1024 * 1024 { pending.removeAll(); report(["event": "error", "text": "归档进程输出过大，无法显示；请检查本地回执。"]); return }
            while let newline = pending.firstIndex(of: 10) {
                let line = pending.subdata(in: 0..<newline); pending.removeSubrange(0...newline); if !line.isEmpty { parse(line) }
            }
        }
        private func parse(_ data: Data) {
            guard let json = try? JSONSerialization.jsonObject(with: data) as? IPCObject else { report(["event": "error", "text": "归档进程返回了无法读取的响应。"]); return }; report(json)
        }
        private func finishIfReady() {
            guard outputClosed, terminated, !exited else { return }; exited = true
            errors.fileHandleForReading.readabilityHandler = nil; try? input.fileHandleForWriting.close()
            let text = String(data: stderr, encoding: .utf8) ?? ""
            let code = exitCode
            DispatchQueue.main.async { [weak self] in
                guard let self = self else { return }; let callback = self.exit; self.event = nil; self.exit = nil; callback?(code, text)
            }
        }
        deinit { output.fileHandleForReading.readabilityHandler = nil; errors.fileHandleForReading.readabilityHandler = nil }
    }
    private static var commands: [UUID: Job] = [:] // Used on main only; retains one-shot processes until EOF + exit.
    @discardableResult static func run(_ command: String, _ payload: IPCObject = [:], completion: @escaping (Result<IPCObject, Error>) -> Void) -> UUID {
        let id = UUID(); var response: IPCObject?
        do {
            let job = try Job(command: command, payload: payload, persistent: false, onEvent: { object in response = object }, onExit: { code, diagnostic in
                commands.removeValue(forKey: id)
                if let response = response, response["ok"] as? Bool == true, code == 0 { completion(.success(response)) }
                else { completion(.failure(CaptureFailure((response?["error"] as? String) ?? (response?["text"] as? String) ?? "本地归档命令失败（\(code)）。\(String(diagnostic.prefix(300)))"))) }
            })
            commands[id] = job
        } catch { completion(.failure(CaptureFailure("无法启动本地归档组件：\(error.localizedDescription)"))) }
        return id
    }
}
