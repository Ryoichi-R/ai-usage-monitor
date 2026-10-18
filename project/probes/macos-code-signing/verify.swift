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

func identity(at path: String) throws -> FileIdentity {
    let descriptor = open(path, O_RDONLY | O_CLOEXEC)
    guard descriptor >= 0 else {
        throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
    }
    defer { close(descriptor) }
    return try identity(for: descriptor)
}

func fail(_ message: String, status: Int32 = 1) -> Never {
    fputs("FAIL \(message)\n", stderr)
    exit(status)
}

guard CommandLine.arguments.count == 3 else {
    fail("usage: verify <executable-path> <expected-team-id>", status: 64)
}

let executablePath = CommandLine.arguments[1]
let expectedTeamID = CommandLine.arguments[2]
let allowedTeamIDCharacters = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")
guard expectedTeamID.count == 10,
      expectedTeamID.unicodeScalars.allSatisfy({ allowedTeamIDCharacters.contains($0) }) else {
    fail("invalid expected Team ID", status: 64)
}

let descriptor = open(executablePath, O_RDONLY | O_CLOEXEC)
guard descriptor >= 0 else {
    fail("cannot open target", status: 66)
}
defer { close(descriptor) }

let initialDescriptorIdentity: FileIdentity
let initialPathIdentity: FileIdentity
do {
    initialDescriptorIdentity = try identity(for: descriptor)
    initialPathIdentity = try identity(at: executablePath)
} catch {
    fail("cannot capture initial file identity")
}
guard initialDescriptorIdentity == initialPathIdentity else {
    fail("path and opened file identity differ before verification")
}

let openedFilePath = "/dev/fd/\(descriptor)"
var staticCode: SecStaticCode?
let createStatus = SecStaticCodeCreateWithPath(URL(fileURLWithPath: openedFilePath) as CFURL, [], &staticCode)
guard createStatus == errSecSuccess, let staticCode else {
    fail("SecStaticCodeCreateWithPath failed: \(createStatus)")
}

let requirementText = "identifier \"com.anthropic.claude-code\" and anchor apple generic and certificate 1[field.1.2.840.113635.100.6.2.6] exists and certificate leaf[field.1.2.840.113635.100.6.1.13] exists and certificate leaf[subject.OU] = \"\(expectedTeamID)\""
var requirement: SecRequirement?
let parseStatus = SecRequirementCreateWithString(requirementText as CFString, [], &requirement)
guard parseStatus == errSecSuccess, let requirement else {
    fail("SecRequirementCreateWithString failed: \(parseStatus)")
}

let strictFlags = SecCSFlags(rawValue: 1 << 4) // kSecCSStrictValidate from SecStaticCode.h
let verificationStatus = SecStaticCodeCheckValidity(staticCode, strictFlags, requirement)
print("verification_source=opened_fd_path")
print("full_requirement_strict=\(verificationStatus)")
guard verificationStatus == errSecSuccess else {
    fail("full strict signing requirement did not validate")
}

var signingInformation: CFDictionary?
let signingInformationStatus = SecCodeCopySigningInformation(
    staticCode,
    SecCSFlags(rawValue: 1 << 1), // kSecCSSigningInformation from SecCode.h
    &signingInformation)
guard signingInformationStatus == errSecSuccess, let signingInformation else {
    fail("SecCodeCopySigningInformation failed: \(signingInformationStatus)")
}
let signingInfo = signingInformation as NSDictionary
let actualIdentifier = signingInfo[kSecCodeInfoIdentifier as String] as? String
let actualTeamID = signingInfo[kSecCodeInfoTeamIdentifier as String] as? String
print("signing_information_status=\(signingInformationStatus) identifier=\(actualIdentifier ?? "<missing>") team_id=\(actualTeamID ?? "<missing>")")
guard actualIdentifier == "com.anthropic.claude-code", actualTeamID == expectedTeamID else {
    fail("signing information did not match the expected identifier and Team ID")
}

var impossibleRequirement: SecRequirement?
let impossibleParseStatus = SecRequirementCreateWithString(
    "identifier \"com.example.invalid-signature-probe\" and anchor apple generic" as CFString,
    [],
    &impossibleRequirement)
guard impossibleParseStatus == errSecSuccess, let impossibleRequirement else {
    fail("negative-control requirement parsing failed: \(impossibleParseStatus)")
}
let impossibleStatus = SecStaticCodeCheckValidity(staticCode, strictFlags, impossibleRequirement)
print("negative_control_status=\(impossibleStatus)")
guard impossibleStatus != errSecSuccess else {
    fail("negative control unexpectedly accepted the executable")
}

let finalDescriptorIdentity: FileIdentity
let finalPathIdentity: FileIdentity
do {
    finalDescriptorIdentity = try identity(for: descriptor)
    finalPathIdentity = try identity(at: executablePath)
} catch {
    fail("cannot capture final file identity")
}
let identityStable = initialDescriptorIdentity == finalDescriptorIdentity
    && initialPathIdentity == finalPathIdentity
    && finalDescriptorIdentity == finalPathIdentity
print("file_identity=\(identityStable ? "stable" : "changed")")
guard identityStable else {
    fail("file identity changed during verification")
}

print("PASS static code requirement, signer metadata, negative control, and file identity")
