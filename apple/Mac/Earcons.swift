import AVFoundation
import Foundation

/// Short, quiet sounds so the user knows StickerSense heard the shortcut and is working,
/// without adding more speech. Generated in code so there are no audio files to ship.
@MainActor
final class Earcons {
    enum Sound {
        /// Soft upward blip: "working on it".
        case working
        /// Gentle two-note chime: "here it comes" (played just before speech).
        case ready
        /// Low double tone: "that didn't work".
        case failed
        /// Very short tick: something was added (picker paste, library save).
        case tick
    }

    var isEnabled = true
    var volume: Float = 0.35
    private var players: [AVAudioPlayer] = []
    private lazy var sounds: [String: Data] = [
        "working": Self.wav(notes: [(660, 0.05), (880, 0.06)]),
        "ready": Self.wav(notes: [(988, 0.05), (1319, 0.09)]),
        "failed": Self.wav(notes: [(330, 0.09), (0, 0.04), (262, 0.12)]),
        "tick": Self.wav(notes: [(1760, 0.025)]),
    ]

    func play(_ sound: Sound) {
        guard isEnabled else { return }
        let key: String = switch sound {
        case .working: "working"
        case .ready: "ready"
        case .failed: "failed"
        case .tick: "tick"
        }
        guard let data = sounds[key], let player = try? AVAudioPlayer(data: data) else { return }
        player.volume = volume
        player.play()
        players.removeAll { !$0.isPlaying }
        players.append(player)
    }

    /// Sine notes with short fades, 16-bit mono WAV. A frequency of 0 is silence.
    static func wav(notes: [(frequency: Double, seconds: Double)], sampleRate: Double = 44_100) -> Data {
        var samples: [Int16] = []
        for note in notes {
            let count = Int(note.seconds * sampleRate)
            let fade = min(Int(0.008 * sampleRate), count / 2)
            for i in 0..<count {
                guard note.frequency > 0 else { samples.append(0); continue }
                var envelope = 1.0
                if i < fade { envelope = Double(i) / Double(fade) }
                if i > count - fade { envelope = Double(count - i) / Double(fade) }
                let value = sin(2 * .pi * note.frequency * Double(i) / sampleRate) * envelope * 0.6
                samples.append(Int16(value * Double(Int16.max)))
            }
        }
        var data = Data()
        func append<T: FixedWidthInteger>(_ value: T) { withUnsafeBytes(of: value.littleEndian) { data.append(contentsOf: $0) } }
        let byteCount = samples.count * 2
        data.append(contentsOf: Array("RIFF".utf8)); append(UInt32(36 + byteCount))
        data.append(contentsOf: Array("WAVE".utf8))
        data.append(contentsOf: Array("fmt ".utf8)); append(UInt32(16)); append(UInt16(1)); append(UInt16(1))
        append(UInt32(sampleRate)); append(UInt32(sampleRate * 2)); append(UInt16(2)); append(UInt16(16))
        data.append(contentsOf: Array("data".utf8)); append(UInt32(byteCount))
        for s in samples { append(s) }
        return data
    }
}
