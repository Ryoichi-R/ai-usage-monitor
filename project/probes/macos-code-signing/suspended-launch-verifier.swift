import Darwin
import Foundation
import Security

struct FileIdentity: Equatable {
    let device: Int64
    let inode: UInt64
    let size: Int64
    let modifiedSeconds: Int64
    let modifiedNanoseconds: Int64
    let changedSeconds: Int64
    let changedNanoseconds: Int64
}

func identity(for descriptor: Int32) throws -> FileIdentity {
    var metadata = stat()
    guard fstat(descriptor, &metadata) == 0 else {
        throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
    }
    return FileIdentity(
        device: Int64(metadata.st_dev),
        inode: UInt64(metadata.st_ino),
        size: Int64(metadata.st_size),
        modifiedSeconds: Int64(metadata.st_mtimespec.tv_sec),
        modifiedNanoseconds: Int64(metadata.st_mtimespec.tv_nsec),
        changedSeconds: Int64(metadata.st_ctimespec.tv_sec),
        changedNanoseconds: Int64(metadata.st_ctimespec.tv_nsec))
}

func fail(_ message: String, pid: pid_t? = nil, exitCode: Int32 = 1) -> Never {
    if let pid {
        _ = kill(pid, SIGKILL)
        var status: Int32 = 0
        _ = waitpid(pid, &status, 0)
    }
    fputs("FAIL \(message)\n", stderr)
    exit(exitCode)
}

func signingInformation(_ code: SecStaticCode) throws -> NSDictionary {
    var information: CFDictionary?
    let status = SecCodeCopySigningInformation(code, SecCSFlags(rawValue: 1 << 1), &information)
    guard status == errSecSuccess, let information else {
        throw NSError(domain: "Security.framework", code: Int(status))
    }
    return information as NSDictionary
}

func waitUntilStopped(_ pid: pid_t, timeoutMilliseconds: Int) -> Bool {
    let deadline = Date().addingTimeInterval(Double(timeoutMilliseconds) / 1000)
    while Date() < deadline {
        var status: Int32 = 0
        let waited = waitpid(pid, &status, WUNTRACED | WNOHANG)
        if waited == pid {
            return (status & 0x7f) == 0x7f && ((status >> 8) & 0xff) != Int32(SIGCONT)
        }
        if waited < 0 && errno != EINTR { return false }
        usleep(10_000)
    }
    return false
}

func waitForSuccessExit(_ pid: pid_t, timeoutMilliseconds: Int) -> Bool {
    let deadline = Date().addingTimeInterval(Double(timeoutMilliseconds) / 1000)
    while Date() < deadline {
        var status: Int32 = 0
        let waited = waitpid(pid, &status, WNOHANG)
        if waited == pid {
            return (status & 0x7f) == 0 && ((status >> 8) & 0xff) == 0
        }
        if waited < 0 && errno != EINTR { return false }
        usleep(10_000)
    }
    return false
}

let arguments = CommandLine.arguments
let replacementPath: String?
switch arguments.count {
case 4:
    replacementPath = nil
case 6 where arguments[4] == "--replace-after-spawn":
    replacementPath = arguments[5]
default:
    fputs("usage: suspended-launch-verifier <prevalidated-path> <launch-path> <new-marker-path> [--replace-after-spawn <replacement-path>]\n", stderr)
    exit(64)
}

let prevalidatedPath = arguments[1]
let launchPath = arguments[2]
let markerPath = arguments[3]
var paths = [prevalidatedPath, launchPath, markerPath]
if let replacementPath { paths.append(replacementPath) }
guard paths.allSatisfy({ $0.hasPrefix("/") }) else {
    fail("all paths must be absolute")
}
if let replacementPath {
    let safePrefix = "/private/tmp/ai-usage-suspended-launch-probe."
    let safeScratchPath: (String) -> Bool = { path in
        guard path.hasPrefix(safePrefix) else { return false }
        let components = path.split(separator: "/", omittingEmptySubsequences: false)
        return !components.dropFirst().contains(where: { $0.isEmpty || $0 == "." || $0 == ".." })
    }
    let launchParent = (launchPath as NSString).deletingLastPathComponent
    guard paths.allSatisfy(safeScratchPath),
          (prevalidatedPath as NSString).deletingLastPathComponent == launchParent,
          (markerPath as NSString).deletingLastPathComponent == launchParent,
          (replacementPath as NSString).deletingLastPathComponent == launchParent,
          replacementPath != launchPath else {
        fail("path replacement mode is limited to distinct files in /private/tmp/ai-usage-suspended-launch-probe.*")
    }
    var directoryMetadata = stat()
    guard lstat(launchParent, &directoryMetadata) == 0,
          (directoryMetadata.st_mode & S_IFMT) == S_IFDIR,
          directoryMetadata.st_uid == getuid(),
          (directoryMetadata.st_mode & 0o077) == 0 else {
        fail("path replacement scratch directory must be an owner-only directory owned by the current user")
    }
}
guard !FileManager.default.fileExists(atPath: markerPath) else {
    fail("marker path already exists")
}
if let replacementPath {
    guard FileManager.default.fileExists(atPath: replacementPath) else {
        fail("replacement path does not exist")
    }
}

// This probe uses a dedicated ad-hoc-signed fake identifier. Production must
// replace this with the reviewed Apple anchor, Claude identifier, chain, and Team ID requirement.
let requirementText = "identifier \"org.example.ai-usage-suspended-probe\""
var requirement: SecRequirement?
let parseStatus = SecRequirementCreateWithString(requirementText as CFString, [], &requirement)
guard parseStatus == errSecSuccess, let requirement else {
    fail("requirement parsing failed: \(parseStatus)")
}

let descriptor = open(prevalidatedPath, O_RDONLY | O_CLOEXEC)
guard descriptor >= 0 else { fail("cannot open prevalidated file: \(errno)") }
defer { close(descriptor) }
let initialDescriptorIdentity: FileIdentity
let initialPathIdentity: FileIdentity
do {
    initialDescriptorIdentity = try identity(for: descriptor)
    initialPathIdentity = try openPath(prevalidatedPath)
} catch {
    fail("cannot capture initial file identity")
}
guard initialDescriptorIdentity == initialPathIdentity else {
    fail("path and opened descriptor differ before validation")
}

let openedFDPath = "/dev/fd/\(descriptor)"
var prevalidatedCode: SecStaticCode?
let createStatus = SecStaticCodeCreateWithPath(URL(fileURLWithPath: openedFDPath) as CFURL, [], &prevalidatedCode)
guard createStatus == errSecSuccess, let prevalidatedCode else {
    fail("static code creation from open FD failed: \(createStatus)")
}
let strictFlags = SecCSFlags(rawValue: 1 << 4) // kSecCSStrictValidate
let validationStatus = SecStaticCodeCheckValidity(prevalidatedCode, strictFlags, requirement)
guard validationStatus == errSecSuccess else {
    fail("static signature requirement failed: \(validationStatus)")
}
let expectedCodeIdentity = try signingInformation(prevalidatedCode)[kSecCodeInfoUnique as String] as? Data
guard let expectedCodeIdentity else { fail("prevalidated code identity is missing") }
guard try identity(for: descriptor) == initialDescriptorIdentity,
      try openPath(prevalidatedPath) == initialPathIdentity else {
    fail("prevalidated file changed during static verification")
}

var spawnAttributes: posix_spawnattr_t? = nil
guard posix_spawnattr_init(&spawnAttributes) == 0 else { fail("posix_spawnattr_init failed") }
defer { posix_spawnattr_destroy(&spawnAttributes) }
// POSIX_SPAWN_START_SUSPENDED | POSIX_SPAWN_SETPGROUP from <spawn.h>.
guard posix_spawnattr_setflags(&spawnAttributes, Int16(0x0080 | 0x0002)) == 0,
      posix_spawnattr_setpgroup(&spawnAttributes, 0) == 0 else {
    fail("suspended process-group spawn setup failed")
}

var childPID: pid_t = 0
let spawnStatus: Int32 = launchPath.withCString { executable in
    markerPath.withCString { marker in
        guard let argument0 = strdup(launchPath), let argument1 = strdup(markerPath) else {
            return ENOMEM
        }
        defer { free(argument0); free(argument1) }
        var arguments: [UnsafeMutablePointer<CChar>?] = [argument0, argument1, nil]
        // Do not pass the caller's environment (which may contain credentials or DYLD overrides) to the fake.
        var emptyEnvironment: [UnsafeMutablePointer<CChar>?] = [nil]
        return posix_spawn(&childPID, executable, nil, &spawnAttributes, &arguments, &emptyEnvironment)
    }
}
guard spawnStatus == 0 else { fail("posix_spawn failed: \(spawnStatus)") }
guard waitUntilStopped(childPID, timeoutMilliseconds: 2_000) else {
    fail("POSIX_SPAWN_START_SUSPENDED did not stop the child", pid: childPID)
}
guard !FileManager.default.fileExists(atPath: markerPath) else {
    fail("fake target executed before validation", pid: childPID)
}
if let replacementPath {
    let replaceStatus = replacementPath.withCString { replacement in
        launchPath.withCString { launch in rename(replacement, launch) }
    }
    guard replaceStatus == 0 else {
        fail("scratch launch-path replacement failed: \(errno)", pid: childPID)
    }
}

var guestCode: SecCode?
let guestAttributes = [kSecGuestAttributePid as String: NSNumber(value: childPID)] as NSDictionary
let guestStatus = SecCodeCopyGuestWithAttributes(nil, guestAttributes as CFDictionary, [], &guestCode)
guard guestStatus == errSecSuccess, let guestCode else {
    fail("running code lookup failed: \(guestStatus)", pid: childPID)
}
let dynamicStatus = SecCodeCheckValidity(guestCode, strictFlags, requirement)
if dynamicStatus != errSecSuccess {
    guard replacementPath != nil else {
        fail("suspended process signature validation failed: \(dynamicStatus)", pid: childPID)
    }
    _ = kill(childPID, SIGKILL)
    var status: Int32 = 0
    guard waitpid(childPID, &status, 0) == childPID else { fail("could not reap path-replaced child") }
    guard !FileManager.default.fileExists(atPath: markerPath) else {
        fail("path-replaced code ran before rejection", exitCode: 2)
    }
    print("REJECTED post_spawn_path_replacement dynamic_status=\(dynamicStatus) marker_absent=1")
    exit(0)
}
var runningStaticCode: SecStaticCode?
let runningStaticStatus = SecCodeCopyStaticCode(guestCode, [], &runningStaticCode)
guard runningStaticStatus == errSecSuccess, let runningStaticCode else {
    fail("running process static-code lookup failed: \(runningStaticStatus)", pid: childPID)
}
let runningCodeIdentity: Data
do {
    guard let identity = try signingInformation(runningStaticCode)[kSecCodeInfoUnique as String] as? Data else {
        fail("running process code identity is missing", pid: childPID)
    }
    runningCodeIdentity = identity
} catch {
    fail("running process signing information could not be read", pid: childPID)
}
guard runningCodeIdentity == expectedCodeIdentity else {
    _ = kill(childPID, SIGKILL)
    var status: Int32 = 0
    guard waitpid(childPID, &status, 0) == childPID else { fail("could not reap mismatched child") }
    guard !FileManager.default.fileExists(atPath: markerPath) else {
        fail("mismatched code ran before rejection", exitCode: 2)
    }
    let rejection = replacementPath == nil ? "code_identity_mismatch" : "post_spawn_path_replacement_code_identity_mismatch"
    print("REJECTED \(rejection) before resume; dynamic_signature=valid marker_absent=1")
    exit(0)
}

guard kill(childPID, SIGCONT) == 0 else {
    fail("could not resume verified process: \(errno)", pid: childPID)
}
guard waitForSuccessExit(childPID, timeoutMilliseconds: 2_000) else {
    fail("verified fake did not exit successfully", pid: childPID)
}
guard FileManager.default.fileExists(atPath: markerPath) else {
    fail("verified fake did not create its marker")
}
let markerValue = try String(contentsOfFile: markerPath, encoding: .utf8)
print("PASS stopped_before_user_code=1 dynamic_signature=valid code_identity_match=1 resumed_after_validation=1 marker=\(markerValue)")

func openPath(_ path: String) throws -> FileIdentity {
    let pathDescriptor = open(path, O_RDONLY | O_CLOEXEC)
    guard pathDescriptor >= 0 else { throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno)) }
    defer { close(pathDescriptor) }
    return try identity(for: pathDescriptor)
}
