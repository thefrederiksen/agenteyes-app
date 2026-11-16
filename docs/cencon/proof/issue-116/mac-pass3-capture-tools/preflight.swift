// Side-effect-free TCC read: CGPreflightScreenCaptureAccess never prompts and never adds a TCC entry.
import CoreGraphics
import Foundation
print("preflight=\(CGPreflightScreenCaptureAccess()) pid=\(getpid()) ppid=\(getppid())")
