// QA, issue #116 - the window probe used for criterion 2 on macOS.
// Build:  swiftc -O -o winlist mac-window-probe.swift
// Run:    ./winlist <pid>     (no pid = every window on the system)
// Asks the macOS WINDOW SERVER what windows a process owns, instead of believing the app's
// own log. Owner name, pid, layer, bounds and on-screen state come back WITHOUT the Screen
// Recording grant; only the window NAME is withheld without it. Exit 2 and an [EMPTY] line
// when nothing matched, so an empty answer reads as a broken instrument, never a clean run.
import CoreGraphics
import Foundation

// Enumerate the window server's window list. Owner name, pid, layer and bounds are returned
// WITHOUT the Screen Recording grant; the window NAME is withheld without it.
let pidFilter = CommandLine.arguments.count > 1 ? Int(CommandLine.arguments[1]) : nil
guard let list = CGWindowListCopyWindowInfo([.optionAll], kCGNullWindowID) as? [[String: Any]] else {
    print("[FAIL] CGWindowListCopyWindowInfo returned nothing"); exit(1)
}
var shown = 0
for w in list {
    let pid = w[kCGWindowOwnerPID as String] as? Int ?? -1
    if let f = pidFilter, pid != f { continue }
    let owner = w[kCGWindowOwnerName as String] as? String ?? "?"
    let layer = w[kCGWindowLayer as String] as? Int ?? -999
    let num = w[kCGWindowNumber as String] as? Int ?? -1
    let onScreen = (w[kCGWindowIsOnscreen as String] as? Bool) ?? false
    let alpha = w[kCGWindowAlpha as String] as? Double ?? -1
    let name = w[kCGWindowName as String] as? String ?? "(name withheld: no Screen Recording grant)"
    var bounds = "?"
    if let b = w[kCGWindowBounds as String] as? [String: Any],
       let x = b["X"] as? Double, let y = b["Y"] as? Double,
       let ww = b["Width"] as? Double, let hh = b["Height"] as? Double {
        bounds = "\(Int(ww))x\(Int(hh)) at \(Int(x)),\(Int(y))"
    }
    print("pid=\(pid) owner=\"\(owner)\" window=\(num) layer=\(layer) onscreen=\(onScreen) alpha=\(alpha) bounds=\(bounds) name=\(name)")
    shown += 1
}
if shown == 0 { print("[EMPTY] no window matched - an empty result is a broken instrument, not a clean run"); exit(2) }
