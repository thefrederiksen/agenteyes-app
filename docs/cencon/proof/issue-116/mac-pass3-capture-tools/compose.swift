// Composite a transparent menu-bar capture onto a dark background and scale it, so a white icon is legible.
import AppKit
let a = CommandLine.arguments; let src = NSImage(contentsOfFile: a[1])!; let scale = CGFloat(Double(a[3])!)
let rep = src.representations[0]; let w = CGFloat(rep.pixelsWide) * scale, h = CGFloat(rep.pixelsHigh) * scale
let out = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(w), pixelsHigh: Int(h), bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: out)
NSColor(calibratedRed: 0.17, green: 0.18, blue: 0.21, alpha: 1).setFill(); NSRect(x: 0, y: 0, width: w, height: h).fill()
NSGraphicsContext.current!.imageInterpolation = .none
src.draw(in: NSRect(x: 0, y: 0, width: w, height: h)); NSGraphicsContext.restoreGraphicsState()
try! out.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: a[2]))
