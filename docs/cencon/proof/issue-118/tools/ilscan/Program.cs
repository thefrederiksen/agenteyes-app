// Lists every method in the given assemblies whose IL calls Avalonia.Application::get_Name (or, as the
// positive control, X11PlatformOptions::get_WmClass). Prints [EMPTY] when nothing matched.
using System; using System.IO; using System.Linq; using System.Reflection.Metadata; using System.Reflection.Metadata.Ecma335; using System.Reflection.PortableExecutable;
var targets = args[1..]; int hits = 0;
foreach (var path in targets) {
  using var pe = new PEReader(File.OpenRead(path)); var md = pe.GetMetadataReader();
  string Owner(EntityHandle h) => h.Kind switch {
    HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)h).Namespace) + "." + md.GetString(md.GetTypeReference((TypeReferenceHandle)h).Name),
    HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)h).Namespace) + "." + md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)h).Name),
    _ => "?" };
  bool Wanted(int token, out string what) {
    what = null; var h = MetadataTokens.EntityHandle(token);
    if (h.Kind == HandleKind.MemberReference) { var m = md.GetMemberReference((MemberReferenceHandle)h); what = Owner(m.Parent) + "::" + md.GetString(m.Name); }
    else if (h.Kind == HandleKind.MethodDefinition) { var m = md.GetMethodDefinition((MethodDefinitionHandle)h); what = Owner(m.GetDeclaringType()) + "::" + md.GetString(m.Name); }
    return what == args[0];
  }
  foreach (var th in md.TypeDefinitions) { var t = md.GetTypeDefinition(th);
    foreach (var mh in t.GetMethods()) { var m = md.GetMethodDefinition(mh); if (m.RelativeVirtualAddress == 0) continue;
      var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILReader();
      while (il.RemainingBytes > 0) { var op = il.ReadByte(); int code = op == 0xFE ? 0xFE00 | il.ReadByte() : op;
        if (code == 0x28 || code == 0x6F) { int tok = il.ReadInt32(); if (Wanted(tok, out var w)) { hits++; Console.WriteLine($"{Path.GetFileName(path)}: {md.GetString(t.Namespace)}.{md.GetString(t.Name)}::{md.GetString(m.Name)} calls {w}"); } continue; }
        int skip = code switch { 0x20 or 0x22 or 0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 or 0x79 or 0x7B or 0x7C or 0x7D or 0x7E or 0x7F or 0x80 or 0x81 or 0x8C or 0x8D or 0x8F or 0xA3 or 0xA4 or 0xA5 or 0xC2 or 0xC6 or 0xD0 or 0xDD or 0x27 or 0x29 or 0x38 or 0x39 or 0x3A or 0x3B or 0x3C or 0x3D or 0x3E or 0x3F or 0x40 or 0x41 or 0x42 or 0x43 or 0x44 or 0xFE06 or 0xFE07 or 0xFE15 or 0xFE16 or 0xFE1C => 4,
          0x21 or 0x23 => 8, 0x0E or 0x0F or 0x10 or 0x11 or 0x12 or 0x13 or 0x1F or 0x2B or 0x2C or 0x2D or 0x2E or 0x2F or 0x30 or 0x31 or 0x32 or 0x33 or 0x34 or 0x35 or 0x36 or 0x37 or 0xDE or 0xFE12 => 1,
          0xFE09 or 0xFE0A or 0xFE0B or 0xFE0C or 0xFE0D or 0xFE0E => 2, 0x45 => -1, _ => 0 };
        if (skip == -1) { int n = il.ReadInt32(); il.Offset += 4 * n; } else il.Offset += skip; } } } }
if (hits == 0) Console.WriteLine("[EMPTY] no call matched " + args[0]);
