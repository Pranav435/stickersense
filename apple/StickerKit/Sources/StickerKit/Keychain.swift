import Foundation
import Security

/// Stores small secrets (the GIPHY API key) in the Keychain instead of preferences.
public enum Keychain {
    static let service = "com.pranavsavla.StickerSense"

    public static func string(for account: String, accessGroup: String? = nil) -> String? {
        var query = baseQuery(account, accessGroup)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess, let data = item as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    @discardableResult
    public static func set(_ value: String?, for account: String, accessGroup: String? = nil) -> Bool {
        let query = baseQuery(account, accessGroup)
        SecItemDelete(query as CFDictionary)
        guard let value, !value.isEmpty else { return true }
        var add = query
        add[kSecValueData as String] = Data(value.utf8)
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        return SecItemAdd(add as CFDictionary, nil) == errSecSuccess
    }

    static func baseQuery(_ account: String, _ accessGroup: String?) -> [String: Any] {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        if let accessGroup { query[kSecAttrAccessGroup as String] = accessGroup }
        return query
    }
}

public extension Keychain {
    static let giphyAccount = "giphy-api-key"
}
