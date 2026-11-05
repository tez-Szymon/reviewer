// Draws the app icon, the tray icon's check mark on a rounded blue square, at the sizes macOS and Windows need.
// Run from the repository root; eng/icons/make-icons.sh turns the PNGs into Assets/app.icns and Assets/app.ico.
//
//   swift eng/icons/make-icons.swift <output-dir>
import AppKit

let out = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "."

func draw(size: Int, inset: Bool) -> Data {
    let s = CGFloat(size)
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let ctx = NSGraphicsContext.current!.cgContext

    // macOS icons sit inside a 10% margin on the canvas (Apple's icon grid); Windows icons fill it.
    let margin = inset ? s * 0.1 : 0
    let tile = CGRect(x: margin, y: margin, width: s - 2 * margin, height: s - 2 * margin)
    let radius = tile.width * 0.225
    let path = CGPath(roundedRect: tile, cornerWidth: radius, cornerHeight: radius, transform: nil)

    ctx.addPath(path)
    ctx.clip()
    let colors = [CGColor(srgbRed: 0.31, green: 0.55, blue: 1.0, alpha: 1),
                  CGColor(srgbRed: 0.15, green: 0.39, blue: 0.92, alpha: 1)] as CFArray
    let gradient = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB), colors: colors, locations: [0, 1])!
    ctx.drawLinearGradient(gradient, start: CGPoint(x: tile.midX, y: tile.maxY),
                           end: CGPoint(x: tile.midX, y: tile.minY), options: [])

    // The check, in the tile's own coordinates (origin bottom left).
    let w = tile.width
    let check = CGMutablePath()
    check.move(to: CGPoint(x: tile.minX + w * 0.27, y: tile.minY + w * 0.50))
    check.addLine(to: CGPoint(x: tile.minX + w * 0.43, y: tile.minY + w * 0.34))
    check.addLine(to: CGPoint(x: tile.minX + w * 0.74, y: tile.minY + w * 0.66))
    ctx.addPath(check)
    ctx.setStrokeColor(CGColor(srgbRed: 1, green: 1, blue: 1, alpha: 1))
    ctx.setLineWidth(w * 0.11)
    ctx.setLineCap(.round)
    ctx.setLineJoin(.round)
    ctx.strokePath()

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

let fm = FileManager.default
let iconset = "\(out)/app.iconset"
try fm.createDirectory(atPath: iconset, withIntermediateDirectories: true)
for (name, size) in [("16x16", 16), ("16x16@2x", 32), ("32x32", 32), ("32x32@2x", 64), ("128x128", 128),
                     ("128x128@2x", 256), ("256x256", 256), ("256x256@2x", 512), ("512x512", 512),
                     ("512x512@2x", 1024)] {
    try draw(size: size, inset: true).write(to: URL(fileURLWithPath: "\(iconset)/icon_\(name).png"))
}
let ico = "\(out)/ico"
try fm.createDirectory(atPath: ico, withIntermediateDirectories: true)
for size in [16, 24, 32, 48, 64, 128, 256] {
    try draw(size: size, inset: false).write(to: URL(fileURLWithPath: "\(ico)/\(size).png"))
}
try draw(size: 256, inset: true).write(to: URL(fileURLWithPath: "\(out)/app.png"))
