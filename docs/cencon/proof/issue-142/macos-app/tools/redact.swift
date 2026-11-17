// QA #142: paint opaque boxes over identity (user name in paths, the owner's name) in a capture.
// usage: redact <in.png> <out.png> x,y,w,h [x,y,w,h ...]   (pixels, origin top-left)
import AppKit
let a = CommandLine.arguments
let src = NSBitmapImageRep(data: try! Data(contentsOf: URL(fileURLWithPath: a[1])))!
let W = src.pixelsWide, H = src.pixelsHigh
let out = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: W, pixelsHigh: H, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: out)
src.draw(in: NSRect(x: 0, y: 0, width: W, height: H))
for spec in a.dropFirst(3) {
    let v = spec.split(separator: ",").map { CGFloat(Double($0)!) }
    let r = NSRect(x: v[0], y: CGFloat(H) - v[1] - v[3], width: v[2], height: v[3])
    NSColor(calibratedRed: 0.45, green: 0.45, blue: 0.48, alpha: 1).setFill(); r.fill()
    let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 11), .foregroundColor: NSColor.white]
    ("[redacted]" as NSString).draw(at: NSPoint(x: r.minX + 4, y: r.minY + (r.height - 13) / 2), withAttributes: attrs)
}
NSGraphicsContext.restoreGraphicsState()
try! out.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: a[2]))
