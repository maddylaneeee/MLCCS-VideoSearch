#!/usr/bin/env swift
import Foundation
import Security

guard CommandLine.arguments.count == 3, let secret = readLine(strippingNewline: true) else {
    FileHandle.standardError.write(Data("Usage: store_keychain_secret.swift <service> <account>\n".utf8))
    exit(2)
}
let service = CommandLine.arguments[1]
let account = CommandLine.arguments[2]
let identity: [String: Any] = [
    kSecClass as String: kSecClassGenericPassword,
    kSecAttrService as String: service,
    kSecAttrAccount as String: account
]
SecItemDelete(identity as CFDictionary)
var item = identity
item[kSecValueData as String] = Data(secret.utf8)
item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
let status = SecItemAdd(item as CFDictionary, nil)
guard status == errSecSuccess else {
    FileHandle.standardError.write(Data("Keychain write failed: \(status)\n".utf8))
    exit(1)
}
print("Production Manifest password stored in the macOS Keychain.")
