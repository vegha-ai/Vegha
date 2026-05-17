#!/usr/bin/env bash
# ui-snap.sh — launch the Avalonia app, wait for first paint, screencapture its
# main window, save to an output PNG. Used by automation (and humans) to visually
# validate UI changes on macOS without screen-recording permission for the desktop:
# `screencapture -l<windowID>` works against an owned window.
#
# Usage:
#   eng/ui-snap.sh [output-png]
#     output-png defaults to /tmp/vegha-ui.png
#
# Env (optional):
#   STARTUP_DELAY=8   seconds to wait after launch (default 8). Bump on slow runners.
set -euo pipefail

OUT="${1:-/tmp/vegha-ui.png}"
DELAY="${STARTUP_DELAY:-8}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BIN="$ROOT/app/Vegha.App/bin/Debug/net10.0/Vegha.App"

if [[ ! -x "$BIN" ]]; then
    echo "Building app first..." >&2
    (cd "$ROOT" && dotnet build app/Vegha.App/Vegha.App.csproj --nologo >/dev/null)
fi

# Kill any prior instance so we get a fresh window with a known pid.
pkill -f "Vegha.App$" 2>/dev/null || true
sleep 1

cd "$(dirname "$BIN")"
nohup "$BIN" > /tmp/vegha-stdout.log 2> /tmp/vegha-stderr.log &
disown
sleep "$DELAY"

PID=$(pgrep -f "Vegha.App$" | head -1)
if [[ -z "$PID" ]]; then
    echo "app did not start; stderr:" >&2
    tail -20 /tmp/vegha-stderr.log >&2
    exit 1
fi

# Pick the largest window owned by the app — robust against helper popups and
# transient dialogs that appear during startup.
WID=$(swift - "$PID" <<'EOF'
import AppKit
let pid = Int32(CommandLine.arguments[1])!
let windows = CGWindowListCopyWindowInfo([.optionAll, .excludeDesktopElements], kCGNullWindowID) as! [[String: Any]]
var best: (id: Int, area: CGFloat) = (0, 0)
for w in windows {
    if (w[kCGWindowOwnerPID as String] as? Int32) == pid,
       let id = w[kCGWindowNumber as String] as? Int,
       let bounds = w[kCGWindowBounds as String] as? [String: CGFloat] {
        let area = (bounds["Height"] ?? 0) * (bounds["Width"] ?? 0)
        if area > best.area { best = (id, area) }
    }
}
print(best.id)
EOF
)

if [[ -z "$WID" || "$WID" == "0" ]]; then
    echo "could not locate window for pid $PID" >&2
    exit 2
fi

screencapture -l"$WID" -x "$OUT"
echo "$OUT"
