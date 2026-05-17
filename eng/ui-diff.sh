#!/usr/bin/env bash
# ui-diff.sh — compare two PNG screenshots and report a pixel-difference summary.
# Useful for visual-regression smoke tests after a UI change: take a fresh snapshot
# with ui-snap.sh, compare against a checked-in baseline, decide if the diff is
# expected or a bug.
#
# Usage:
#   eng/ui-diff.sh <baseline.png> <candidate.png> [diff-output.png]
#     - Exits 0 if images are identical OR pixel-diff ratio <= threshold.
#     - Exits 1 if diff > threshold, or images can't be compared (size mismatch etc).
#     - Writes a red-highlighted delta image to diff-output.png when set.
#
# Env (optional):
#   UI_DIFF_THRESHOLD=0.005   max allowed differing pixels as a fraction (default 0.5%)
#
# Implementation uses a Swift one-liner against Core Graphics — no ImageMagick or
# other heavy deps. Each pixel is compared with a small per-channel tolerance to
# absorb anti-aliasing jitter between runs.
set -euo pipefail

if [[ $# -lt 2 ]]; then
    echo "usage: $0 <baseline.png> <candidate.png> [diff-output.png]" >&2
    exit 64
fi

BASELINE="$1"
CANDIDATE="$2"
DIFF_OUT="${3:-}"
THRESHOLD="${UI_DIFF_THRESHOLD:-0.005}"

if [[ ! -f "$BASELINE" ]]; then
    echo "baseline missing: $BASELINE" >&2
    exit 2
fi
if [[ ! -f "$CANDIDATE" ]]; then
    echo "candidate missing: $CANDIDATE" >&2
    exit 2
fi

swift - "$BASELINE" "$CANDIDATE" "$DIFF_OUT" "$THRESHOLD" <<'EOF'
import AppKit
import CoreGraphics

let args = CommandLine.arguments
let baselinePath = args[1]
let candidatePath = args[2]
let diffOutPath = args[3]
let threshold = Double(args[4]) ?? 0.005

func loadCG(_ path: String) -> CGImage? {
    guard let provider = CGDataProvider(filename: path) else { return nil }
    return CGImage(pngDataProviderSource: provider,
                   decode: nil, shouldInterpolate: false,
                   intent: .defaultIntent)
}

guard let a = loadCG(baselinePath), let b = loadCG(candidatePath) else {
    FileHandle.standardError.write("failed to decode one of the PNGs\n".data(using: .utf8)!)
    exit(2)
}

if a.width != b.width || a.height != b.height {
    print("size-mismatch baseline=\(a.width)x\(a.height) candidate=\(b.width)x\(b.height)")
    exit(1)
}

let w = a.width
let h = a.height
let bytesPerRow = w * 4

func rgbaData(_ img: CGImage) -> [UInt8]? {
    var data = [UInt8](repeating: 0, count: w * h * 4)
    let space = CGColorSpaceCreateDeviceRGB()
    let info: UInt32 = CGImageAlphaInfo.premultipliedLast.rawValue
    guard let ctx = CGContext(data: &data, width: w, height: h,
                              bitsPerComponent: 8, bytesPerRow: bytesPerRow,
                              space: space, bitmapInfo: info) else { return nil }
    ctx.draw(img, in: CGRect(x: 0, y: 0, width: w, height: h))
    return data
}

guard let pa = rgbaData(a), let pb = rgbaData(b) else {
    FileHandle.standardError.write("failed to rasterize PNGs\n".data(using: .utf8)!)
    exit(2)
}

// Per-channel tolerance absorbs anti-aliasing jitter — pixels within ±3 levels on
// every channel are treated as identical so subpixel snapping doesn't trip the diff.
let tol: Int = 3
var diff: [UInt8]? = diffOutPath.isEmpty ? nil : [UInt8](repeating: 0, count: w * h * 4)
var diffCount = 0
for i in stride(from: 0, to: w * h * 4, by: 4) {
    let dR = abs(Int(pa[i]) - Int(pb[i]))
    let dG = abs(Int(pa[i+1]) - Int(pb[i+1]))
    let dB = abs(Int(pa[i+2]) - Int(pb[i+2]))
    let differs = dR > tol || dG > tol || dB > tol
    if differs {
        diffCount += 1
        if diff != nil {
            diff![i] = 255   // R
            diff![i+1] = 0   // G
            diff![i+2] = 0   // B
            diff![i+3] = 255 // A
        }
    } else if diff != nil {
        // Preserve baseline pixel at half opacity so the diff highlights stand out
        // against a faded version of the reference image.
        diff![i] = pa[i]
        diff![i+1] = pa[i+1]
        diff![i+2] = pa[i+2]
        diff![i+3] = 96
    }
}

let total = w * h
let ratio = Double(diffCount) / Double(total)
print(String(format: "diff=%d/%d (%.4f%%) threshold=%.4f%%",
             diffCount, total, ratio * 100, threshold * 100))

if let diffBytes = diff {
    // Write the diff image to the requested path so a human can eyeball where the
    // regression sits. Red highlights = changed pixels.
    let space = CGColorSpaceCreateDeviceRGB()
    let info: UInt32 = CGImageAlphaInfo.premultipliedLast.rawValue
    let mutableBytes = UnsafeMutablePointer<UInt8>.allocate(capacity: diffBytes.count)
    mutableBytes.update(from: diffBytes, count: diffBytes.count)
    defer { mutableBytes.deallocate() }
    let ctx = CGContext(data: mutableBytes, width: w, height: h,
                        bitsPerComponent: 8, bytesPerRow: bytesPerRow,
                        space: space, bitmapInfo: info)!
    let img = ctx.makeImage()!
    let rep = NSBitmapImageRep(cgImage: img)
    if let data = rep.representation(using: .png, properties: [:]) {
        try? data.write(to: URL(fileURLWithPath: diffOutPath))
    }
}

exit(ratio > threshold ? 1 : 0)
EOF
