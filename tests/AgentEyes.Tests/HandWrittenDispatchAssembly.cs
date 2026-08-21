using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace AgentEyes.Tests
{
    /// <summary>
    /// A tiny assembly EMITTED BY HAND (MetadataBuilder + ManagedPEBuilder, both in the .NET 8
    /// shared framework), carrying dispatch metadata Roslyn will not produce - so the walk-level
    /// regressions built on it exercise code paths of <see cref="CompiledCode"/> that no
    /// C#-compiled decoy can reach.
    ///
    /// Why it exists (issue #2, round 3). The round-2 gate's REJECT names the inherited interface
    /// declaration: IChild : IBase, a class implements IChild, the call goes through
    /// IBase::Configure, and a dispatch map that never traverses the interface inheritance graph
    /// has no edge. But ECMA-335 leaves it to the COMPILER whether a class's InterfaceImpl rows
    /// list inherited interfaces, and Roslyn FLATTENS: `class Impl : IChild` is emitted with rows
    /// for both IChild and IBase (verified empirically on this machine, .NET 8 SDK). So every
    /// C#-compiled decoy of the gate's construction carries the direct IBase row, the pre-fix map
    /// finds the edge through it, and the regression can never go red - it would pin nothing. The
    /// closure traversal exists for metadata the C# compiler does not emit but the format allows
    /// (ilasm, other compilers, IL rewriters), and only hand-written metadata can prove it works.
    ///
    /// What the assembly contains (all types in namespace Probe):
    ///
    /// * IBase - interface declaring abstract Configure().
    /// * IChild - interface with ONE InterfaceImpl row: IBase. Declares nothing.
    /// * Impl - class with ONE InterfaceImpl row: IChild. NOT flattened - no IBase row, which is
    ///   the whole point. Body: Configure() { ret }.
    /// * Handler - class with two static bodies:
    ///     Run()    { ldnull; callvirt IBase::Configure; ret }   - the gate's call shape
    ///     RunJmp() { jmp Impl::Configure }                      - the one other IL instruction
    ///       that transfers control to a method token (ECMA-335 III.3.37) and that C# never
    ///       emits, pinned here so the token-collection claim is complete over IL kinds.
    ///
    /// The IL is never executed - the assembly is written to disk and READ, exactly like every
    /// other assembly CompiledCode scans.
    /// </summary>
    internal static class HandWrittenDispatchAssembly
    {
        /// <summary>Emits the assembly to a fresh temp file and returns its path. Callers delete
        /// it when done.</summary>
        public static string Emit()
        {
            var md = new MetadataBuilder();
            var ilStream = new BlobBuilder();
            var bodies = new MethodBodyStreamEncoder(ilStream);

            md.AddModule(0, md.GetOrAddString("Probe.dll"), md.GetOrAddGuid(Guid.NewGuid()),
                default, default);
            md.AddAssembly(md.GetOrAddString("Probe"), new Version(1, 0, 0, 0),
                default, default, 0, AssemblyHashAlgorithm.None);

            var corlib = md.AddAssemblyReference(md.GetOrAddString("System.Runtime"),
                new Version(8, 0, 0, 0), default, default, 0, default);
            var systemObject = md.AddTypeReference(corlib,
                md.GetOrAddString("System"), md.GetOrAddString("Object"));

            // Signatures: instance void() and static void().
            var instanceVoid = new BlobBuilder();
            new BlobEncoder(instanceVoid).MethodSignature(isInstanceMethod: true)
                .Parameters(0, rt => rt.Void(), _ => { });
            var instanceVoidSig = md.GetOrAddBlob(instanceVoid);

            var staticVoid = new BlobBuilder();
            new BlobEncoder(staticVoid).MethodSignature()
                .Parameters(0, rt => rt.Void(), _ => { });
            var staticVoidSig = md.GetOrAddBlob(staticVoid);

            // Method rows are handed out sequentially, so the tokens bodies need are known
            // before the definitions exist. Row 1: IBase.Configure; row 2: Impl.Configure;
            // row 3: Handler.Run; row 4: Handler.RunJmp.
            var ibaseConfigure = MetadataTokens.MethodDefinitionHandle(1);
            var implConfigure = MetadataTokens.MethodDefinitionHandle(2);

            // Impl.Configure: { ret }
            var implCode = new InstructionEncoder(new BlobBuilder());
            implCode.OpCode(ILOpCode.Ret);
            int implBody = bodies.AddMethodBody(implCode);

            // Handler.Run: { ldnull; callvirt IBase::Configure; ret } - the gate's call shape.
            // The IL is decoded, never run, so the null receiver is irrelevant.
            var runCode = new InstructionEncoder(new BlobBuilder());
            runCode.OpCode(ILOpCode.Ldnull);
            runCode.OpCode(ILOpCode.Callvirt);
            runCode.Token(ibaseConfigure);
            runCode.OpCode(ILOpCode.Ret);
            int runBody = bodies.AddMethodBody(runCode);

            // Handler.RunJmp: { jmp Impl::Configure } - jmp ends the method itself.
            var jmpCode = new InstructionEncoder(new BlobBuilder());
            jmpCode.OpCode(ILOpCode.Jmp);
            jmpCode.Token(implConfigure);
            int jmpBody = bodies.AddMethodBody(jmpCode);

            // Row 1: IBase.Configure - abstract, no body.
            md.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual
                    | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                MethodImplAttributes.IL, md.GetOrAddString("Configure"),
                instanceVoidSig, -1, MetadataTokens.ParameterHandle(1));

            // Row 2: Impl.Configure.
            md.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final
                    | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                MethodImplAttributes.IL, md.GetOrAddString("Configure"),
                instanceVoidSig, implBody, MetadataTokens.ParameterHandle(1));

            // Rows 3 and 4: Handler.Run and Handler.RunJmp.
            md.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.Static,
                MethodImplAttributes.IL, md.GetOrAddString("Run"),
                staticVoidSig, runBody, MetadataTokens.ParameterHandle(1));
            md.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.Static,
                MethodImplAttributes.IL, md.GetOrAddString("RunJmp"),
                staticVoidSig, jmpBody, MetadataTokens.ParameterHandle(1));

            var probeNs = md.GetOrAddString("Probe");
            var noFields = MetadataTokens.FieldDefinitionHandle(1);

            // Type row 1: <Module>, required first.
            md.AddTypeDefinition(default, default, md.GetOrAddString("<Module>"),
                default, noFields, MetadataTokens.MethodDefinitionHandle(1));

            var ibase = md.AddTypeDefinition(
                TypeAttributes.Interface | TypeAttributes.Abstract | TypeAttributes.Public,
                probeNs, md.GetOrAddString("IBase"),
                default, noFields, MetadataTokens.MethodDefinitionHandle(1));

            var ichild = md.AddTypeDefinition(
                TypeAttributes.Interface | TypeAttributes.Abstract | TypeAttributes.Public,
                probeNs, md.GetOrAddString("IChild"),
                default, noFields, MetadataTokens.MethodDefinitionHandle(2));

            var impl = md.AddTypeDefinition(
                TypeAttributes.Public | TypeAttributes.BeforeFieldInit,
                probeNs, md.GetOrAddString("Impl"),
                systemObject, noFields, MetadataTokens.MethodDefinitionHandle(2));

            md.AddTypeDefinition(
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
                probeNs, md.GetOrAddString("Handler"),
                systemObject, noFields, MetadataTokens.MethodDefinitionHandle(3));

            // The InterfaceImpl rows - the point of the whole file. IChild : IBase, and Impl
            // lists ONLY IChild. No compiler-style flattening.
            md.AddInterfaceImplementation(ichild, ibase);
            md.AddInterfaceImplementation(impl, ichild);

            var pe = new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(),
                new MetadataRootBuilder(md), ilStream);
            var blob = new BlobBuilder();
            pe.Serialize(blob);

            string path = Path.Combine(Path.GetTempPath(),
                "agenteyes-dispatch-probe-" + Guid.NewGuid().ToString("N") + ".dll");
            File.WriteAllBytes(path, blob.ToArray());
            return path;
        }

        /// <summary>The interfaces a type's OWN InterfaceImpl rows name, so a test can prove the
        /// emitted fixture still poses the hazard it exists for: if Impl's rows ever came back
        /// flattened (IChild AND IBase), the regression would be passing through the direct row
        /// and certifying nothing about the inheritance traversal.</summary>
        public static IReadOnlyList<string> DirectInterfaceRowsOf(string assemblyPath, string typeName)
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();

            foreach (var typeHandle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(typeHandle);
                if (md.GetString(type.Name) != typeName) continue;

                var rows = new List<string>();
                foreach (var implHandle in type.GetInterfaceImplementations())
                {
                    var iface = md.GetInterfaceImplementation(implHandle).Interface;
                    rows.Add(iface.Kind == HandleKind.TypeDefinition
                        ? md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)iface).Name)
                        : iface.Kind.ToString());
                }
                return rows;
            }

            throw new InvalidOperationException(
                $"'{typeName}' is not in {Path.GetFileName(assemblyPath)} - the emitted fixture is broken.");
        }
    }
}
