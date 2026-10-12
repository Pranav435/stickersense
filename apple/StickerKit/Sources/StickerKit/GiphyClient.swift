import Foundation

public struct GiphyItem: Codable, Sendable, Hashable, Identifiable {
    public enum Kind: String, Codable, Sendable { case gif, sticker }

    public var id: String
    public var kind: Kind
    /// GIPHY's own title / alt text, e.g. "Happy Birthday GIF by Disney". Often vague.
    public var title: String
    public var altText: String
    /// Small animated GIF for previews and describing.
    public var previewURL: URL
    /// Full-size animated GIF for sending.
    public var originalURL: URL
    /// Smallest animated rendition (~100 px wide), used for describing: quicker to download.
    public var smallURL: URL?

    /// Best text GIPHY gives us, cleaned of the "GIF by …" boilerplate.
    public var cleanTitle: String {
        let base = altText.isEmpty ? title : altText
        var t = base
        for marker in [" GIF by ", " Sticker by ", " GIF", " Sticker"] {
            if let r = t.range(of: marker) { t = String(t[..<r.lowerBound]) }
        }
        t = t.trimmingCharacters(in: .whitespacesAndNewlines)
        return t.isEmpty ? (kind == .sticker ? "Sticker" : "GIF") : t
    }
}

public struct GiphyPage: Sendable {
    public var items: [GiphyItem]
    public var nextOffset: Int
    public var totalCount: Int
    public var hasMore: Bool { nextOffset < totalCount && !items.isEmpty }
}

public enum GiphyError: Error, LocalizedError, Sendable {
    case missingKey
    case badResponse(Int)
    case invalidKey

    public var errorDescription: String? {
        switch self {
        case .missingKey: "Add your GIPHY API key in Settings to search GIFs."
        case .invalidKey: "GIPHY didn't accept the API key. Check it in Settings."
        case .badResponse(let code): "GIPHY search failed (error \(code))."
        }
    }
}

/// Minimal GIPHY search client (https://developers.giphy.com/docs/api/endpoint#search).
public struct GiphyClient: Sendable {
    public var apiKey: String
    public var rating: String
    public var session: URLSession

    public init(apiKey: String, rating: String = "pg-13", session: URLSession = .shared) {
        self.apiKey = apiKey
        self.rating = rating
        self.session = session
    }

    /// One page of results. GIPHY allows up to 50 per request; `offset` pages through the rest.
    public func search(_ query: String, kind: GiphyItem.Kind, limit: Int = 25, offset: Int = 0) async throws -> GiphyPage {
        guard !apiKey.trimmingCharacters(in: .whitespaces).isEmpty else { throw GiphyError.missingKey }
        let endpoint = kind == .gif ? "gifs" : "stickers"
        var components = URLComponents(string: "https://api.giphy.com/v1/\(endpoint)/search")!
        components.queryItems = [
            URLQueryItem(name: "api_key", value: apiKey),
            URLQueryItem(name: "q", value: query),
            URLQueryItem(name: "limit", value: String(min(limit, 50))),
            URLQueryItem(name: "offset", value: String(offset)),
            URLQueryItem(name: "rating", value: rating),
            URLQueryItem(name: "lang", value: Locale.current.language.languageCode?.identifier ?? "en"),
        ]
        let (data, response) = try await session.data(from: components.url!)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if status == 401 || status == 403 { throw GiphyError.invalidKey }
        guard status == 200 else { throw GiphyError.badResponse(status) }
        return try Self.parsePage(data, kind: kind, offset: offset)
    }

    static func parsePage(_ data: Data, kind: GiphyItem.Kind, offset: Int) throws -> GiphyPage {
        struct Envelope: Decodable {
            struct Pagination: Decodable { let total_count: Int?; let count: Int? }
            let pagination: Pagination?
        }
        let items = try parse(data, kind: kind)
        let pagination = (try? JSONDecoder().decode(Envelope.self, from: data))?.pagination
        let received = pagination?.count ?? items.count
        let total = pagination?.total_count ?? (offset + received)
        return GiphyPage(items: items, nextOffset: offset + received, totalCount: total)
    }

    static func parse(_ data: Data, kind: GiphyItem.Kind) throws -> [GiphyItem] {
        struct Response: Decodable { let data: [Entry] }
        struct Entry: Decodable {
            let id: String
            let title: String?
            let alt_text: String?
            let images: Images
        }
        struct Images: Decodable {
            let fixed_width: Rendition?
            let fixed_width_small: Rendition?
            let fixed_width_downsampled: Rendition?
            let downsized: Rendition?
            let original: Rendition?
        }
        struct Rendition: Decodable { let url: String? }

        let decoded = try JSONDecoder().decode(Response.self, from: data)
        return decoded.data.compactMap { entry in
            let preview = entry.images.fixed_width?.url ?? entry.images.fixed_width_small?.url ?? entry.images.downsized?.url
            let original = entry.images.downsized?.url ?? entry.images.original?.url ?? preview
            guard let preview, let previewURL = URL(string: preview), let original, let originalURL = URL(string: original) else { return nil }
            let small = (entry.images.fixed_width_downsampled?.url ?? entry.images.fixed_width_small?.url).flatMap(URL.init(string:))
            return GiphyItem(
                id: entry.id, kind: kind, title: entry.title ?? "", altText: entry.alt_text ?? "",
                previewURL: previewURL, originalURL: originalURL, smallURL: small
            )
        }
    }
}
