// Render the icon macOS (NSWorkspace / Launch Services) resolves for a bundle - what Finder shows for it.
import AppKit
let path = CommandLine.arguments[1], out = CommandLine.arguments[2]
let img = NSWorkspace.shared.icon(forFile: path)
img.size = NSSize(width: 256, height: 256)
guard let tiff = img.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff),
      let png = rep.representation(using: .png, properties: [:]) else { print("[FAIL] no image"); exit(1) }
try! png.write(to: URL(fileURLWithPath: out))
let bundle = Bundle(path: path)
print("bundle=\(path) id=\(bundle?.bundleIdentifier ?? "nil") name=\(bundle?.object(forInfoDictionaryKey: "CFBundleName") ?? "nil") iconFile=\(bundle?.object(forInfoDictionaryKey: "CFBundleIconFile") ?? "nil") reps=\(img.representations.count) wrote=\(out)")
