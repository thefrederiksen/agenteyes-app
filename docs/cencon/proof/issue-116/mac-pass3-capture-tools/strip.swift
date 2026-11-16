// Rebuild the menu bar as a person would see it: the Menubar window, then each status-item window at its x.
import AppKit
let a = CommandLine.arguments; let outPath = a[1]; let W = 1920, H = 30
let out = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: W, pixelsHigh: H, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: out)
NSColor(calibratedRed: 0.17, green: 0.18, blue: 0.21, alpha: 1).setFill(); NSRect(x: 0, y: 0, width: W, height: H).fill()
for spec in a.dropFirst(2) {   // path@x
    let p = spec.split(separator: "@"); let img = NSImage(contentsOfFile: String(p[0]))!; let r = img.representations[0]
    img.draw(in: NSRect(x: Int(p[1])!, y: 0, width: r.pixelsWide, height: r.pixelsHigh))
}
NSGraphicsContext.restoreGraphicsState(); try! out.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: outPath))
