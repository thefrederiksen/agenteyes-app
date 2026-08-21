using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Reads what the product's compiled code ACTUALLY DOES, from the IL of the built assemblies -
    /// the counterpart to <see cref="RepoSource"/>, which reads what the source SAYS.
    ///
    /// Why this exists (issue #155, criterion 4). A guard built on scanning source TEXT cannot hold:
    /// the same write can be spelled with an alias, split across two statements, hidden behind a
    /// one-line helper in a file that never names the target, reached through a const, a different
    /// serializer, or a delegate. Two independent reviewers defeated the text-based guard that way.
    /// IL has none of those degrees of freedom: a call to <c>File.WriteAllText</c> is one instruction
    /// carrying one metadata token, no matter how the C# was written, which file it lives in, or how
    /// many locals the path passed through on the way. So the question this class answers is
    /// "which METHOD contains a call to which API", and the answer is spelling-independent.
    ///
    /// No new dependency: <c>System.Reflection.Metadata</c> ships in the .NET 8 shared framework, and
    /// <see cref="PEReader"/> reads a PE file off disk WITHOUT loading it into the runtime - which is
    /// what makes it possible to inspect AgentEyesApp (a WPF WinExe) at all.
    ///
    /// The IL walker is deliberately FAIL-CLOSED. An opcode it does not know, an operand that runs
    /// past the end of the body, or a walk that does not land exactly on the last byte all THROW.
    /// A decoder that silently fell out of step would report no calls at all, and "no calls found"
    /// would then certify a scan that never happened.
    /// </summary>
    internal static class CompiledCode
    {
        /// <summary>One call instruction: the method that contains it and the method it targets.</summary>
        internal sealed record CallSite(string Assembly, string Method, string Callee);

        // ---- the assemblies under guard --------------------------------------

        /// <summary>AgentEyes.Core (agenteyes.dll), taken from the copy the test run itself loaded,
        /// so it is by construction the build under test.</summary>
        public static string CoreAssembly => typeof(Manifest).Assembly.Location;

        /// <summary>AgentEyes.Setup.Engine.dll - the assembly that carries the update channel - taken
        /// from the copy the test run itself loaded, so it is by construction the build under test.</summary>
        public static string EngineAssembly => typeof(AgentEyes.Setup.Engine.ReleaseSource).Assembly.Location;

        /// <summary>AgentEyesApp.dll. The test project has a ProjectReference to AgentEyes.App purely
        /// so MSBuild builds it first and copies it here - the WPF app is never loaded or started,
        /// only read as bytes. That reference is what makes this a FRESH binary rather than whatever
        /// happens to be sitting in some bin directory.</summary>
        public static string AppAssembly => Path.Combine(AppContext.BaseDirectory, "AgentEyesApp.dll");

        /// <summary>The test assembly - scanned only to prove the guard sees the bypass shapes that
        /// are compiled into it as negative controls.</summary>
        public static string TestAssembly => typeof(CompiledCode).Assembly.Location;

        public static IReadOnlyList<string> ProductAssemblies() => new[] { CoreAssembly, AppAssembly };

        // ---- the write APIs ---------------------------------------------------

        /// <summary>
        /// The write APIs this scan knows, in two groups.
        ///
        /// GROUP 1 - every System.IO entry point that can create, overwrite, rename, copy or remove a
        /// file. Read-only APIs are deliberately absent; <c>File.Open</c> is present even though it
        /// can be opened for reading, because deciding that needs the argument and this errs toward
        /// reporting too much rather than too little.
        ///
        /// GROUP 2 - a NAMED, NON-EXHAUSTIVE set of framework APIs that take a path and write it
        /// themselves, without going through System.IO in the caller's own IL. A reviewer defeated
        /// round 2 of this guard with exactly that shape (<c>XmlDocument.Save(manifestPath)</c>), so
        /// the entry points of that kind that a manifest could plausibly be written through are
        /// listed. This group is ENUMERATED, not complete: the framework has an open-ended number of
        /// path-taking writers, and one that is not on this list is not seen. That limit is stated in
        /// <c>ManifestWriterIlTests</c> rather than papered over.
        /// </summary>
        public static readonly IReadOnlyList<string> FileWriteApis = new[]
        {
            "System.IO.File::WriteAllText",     "System.IO.File::WriteAllTextAsync",
            "System.IO.File::WriteAllBytes",    "System.IO.File::WriteAllBytesAsync",
            "System.IO.File::WriteAllLines",    "System.IO.File::WriteAllLinesAsync",
            "System.IO.File::AppendAllText",    "System.IO.File::AppendAllTextAsync",
            "System.IO.File::AppendAllLines",   "System.IO.File::AppendAllLinesAsync",
            "System.IO.File::Create",           "System.IO.File::CreateText",
            "System.IO.File::AppendText",       "System.IO.File::OpenWrite",
            "System.IO.File::Open",             "System.IO.File::OpenHandle",
            "System.IO.File::Move",             "System.IO.File::Copy",
            "System.IO.File::Replace",          "System.IO.File::Delete",
            "System.IO.FileInfo::Create",       "System.IO.FileInfo::CreateText",
            "System.IO.FileInfo::AppendText",   "System.IO.FileInfo::OpenWrite",
            "System.IO.FileInfo::Open",         "System.IO.FileInfo::MoveTo",
            "System.IO.FileInfo::CopyTo",       "System.IO.FileInfo::Replace",
            "System.IO.FileInfo::Delete",
            "System.IO.FileStream::.ctor",      "System.IO.StreamWriter::.ctor",
            "System.IO.RandomAccess::Write",    "System.IO.RandomAccess::WriteAsync",
            "Microsoft.Win32.SafeHandles.SafeFileHandle::.ctor",

            // Group 2: path-taking framework writers (enumerated, not exhaustive).
            "System.Xml.XmlDocument::Save",         "System.Xml.XmlWriter::Create",
            "System.Xml.XmlTextWriter::.ctor",      "System.Xml.Linq.XDocument::Save",
            "System.Xml.Linq.XElement::Save",       "System.Xml.Serialization.XmlSerializer::Serialize",
            "System.Drawing.Image::Save",           "System.Drawing.Bitmap::Save",
            "System.IO.Compression.ZipFile::CreateFromDirectory",
            "System.IO.Compression.ZipFile::ExtractToDirectory",
            "System.IO.Compression.ZipFileExtensions::ExtractToFile",
            "System.IO.Compression.ZipFileExtensions::ExtractToDirectory",
        };

        private static readonly HashSet<string> WriteApiSet = new(FileWriteApis, StringComparer.Ordinal);

        public static bool IsFileWriteApi(string callee) => WriteApiSet.Contains(callee);

        // ---- the scans --------------------------------------------------------

        /// <summary>Every call site in <paramref name="assemblyPath"/> whose target matches
        /// <paramref name="wanted"/>, with the containing method normalized to the method a human
        /// wrote (lambdas, local functions, async and iterator state machines all fold back into
        /// their declaring method).</summary>
        public static IReadOnlyList<CallSite> CallSites(string assemblyPath, Func<string, bool> wanted)
        {
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException(
                    "The assembly to scan was not built. This scan cannot be allowed to pass by finding nothing.",
                    assemblyPath);

            var sites = new List<CallSite>();
            string assembly = Path.GetFileName(assemblyPath);

            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();

            // A scan that walked nothing would satisfy every "no offenders" assertion built on it.
            if (md.MethodDefinitions.Count == 0)
                throw new InvalidOperationException($"{assembly} contains no methods - the scanner is looking at the wrong file.");

            foreach (var handle in md.MethodDefinitions)
            {
                var method = md.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress == 0) continue;   // abstract, interface, or P/Invoke

                string where = MethodName(md, handle);
                byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()
                            ?? throw new InvalidOperationException($"No IL for {where} in {assembly}.");

                foreach (int token in ReferencedMethodTokens(il, $"{assembly}!{where}"))
                {
                    string? callee = Callee(md, token);
                    if (callee != null && wanted(callee)) sites.Add(new CallSite(assembly, where, callee));
                }
            }

            return sites;
        }

        /// <summary>
        /// Every method call inside ONE method, IN IL ORDER - the counterpart to
        /// <see cref="CallSites"/>, which answers "does this method call X" but never "does it call
        /// X BEFORE Y".
        ///
        /// Order is a real correctness property, not a style question. Issue #154's capture guard is
        /// only complete while <c>RecordingService::BeginSession</c> takes the capture claim BEFORE
        /// it bumps the capture epoch: announcing first leaves an instant in which the epoch already
        /// counts a capture that has not claimed anything yet, and a repair pass reading the epoch
        /// there sees no capture at all. A presence-only assertion cannot see that, and neither can a
        /// behavioral test - the test writes the interleaving itself, so it can only prove what the
        /// guard does with a given order, never which order the product actually uses.
        ///
        /// Fail-closed like everything else here: <paramref name="method"/> must match EXACTLY ONE
        /// method definition. No match is a renamed method silently certifying nothing; several
        /// matches means the compiler split the body (lambdas, a local function and an async state
        /// machine all fold back onto their declaring method here), and a concatenated "order" across
        /// those pieces would be meaningless. Both throw.
        /// </summary>
        /// <param name="method">Substring of the normalized name, e.g. "RecordingService::BeginSession".</param>
        public static IReadOnlyList<string> CallsIn(string assemblyPath, string method)
        {
            if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("a method to read is required", nameof(method));
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException(
                    "The assembly to scan was not built. This scan cannot be allowed to pass by finding nothing.",
                    assemblyPath);

            string assembly = Path.GetFileName(assemblyPath);
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();

            var found = new List<(string Where, List<string> Calls)>();
            foreach (var handle in md.MethodDefinitions)
            {
                var definition = md.GetMethodDefinition(handle);
                if (definition.RelativeVirtualAddress == 0) continue;

                string where = MethodName(md, handle);
                if (!where.Contains(method, StringComparison.Ordinal)) continue;

                byte[] il = pe.GetMethodBody(definition.RelativeVirtualAddress).GetILBytes()
                            ?? throw new InvalidOperationException($"No IL for {where} in {assembly}.");

                var calls = new List<string>();
                foreach (int token in ReferencedMethodTokens(il, $"{assembly}!{where}"))
                {
                    string? callee = Callee(md, token);
                    if (callee != null) calls.Add(callee);
                }
                found.Add((where, calls));
            }

            if (found.Count == 0)
                throw new InvalidOperationException(
                    $"No method matching '{method}' exists in {assembly} - an ordering assertion must not pass by reading nothing.");
            if (found.Count > 1)
                throw new InvalidOperationException(
                    $"'{method}' matches {found.Count} method definitions in {assembly} "
                    + $"({string.Join(", ", found.Select(f => f.Where))}) - ordering across separate bodies has no meaning. "
                    + "Name one method.");

            return found[0].Calls;
        }

        /// <summary>
        /// Every method of <paramref name="assemblyPath"/> REACHABLE from <paramref name="seeds"/>,
        /// following calls that stay inside this assembly - the seeds included.
        ///
        /// Why (issue #178, round-2 review). A scan that inventories an enumerated LIST of methods
        /// only ever answers for those methods. The reviewer defeated the date guard with exactly
        /// that: <c>RecentItem.From</c> calls a new <c>LibraryDateFallback.For(dir)</c>, the helper
        /// calls <c>Directory.GetCreationTime</c>, and because the helper's name was not on the list,
        /// both guards stayed green over a live filesystem-date fallback. Following the call graph is
        /// the answer: the offending helper is reachable from the seed, so it is scanned.
        ///
        /// The closure stops at the assembly boundary, because that is where the IL of a callee stops
        /// being visible here. That is a real limit, and the guards built on this state it.
        ///
        /// VIRTUAL AND INTERFACE DISPATCH is followed conservatively (issue #2, fix pass - the
        /// round-1 gate's finding). A call through an in-assembly interface or virtual method names
        /// only the DECLARATION in its IL token; the body that actually runs belongs to whatever
        /// implementation the object happens to be, and a walk that only follows calls into bodies
        /// never reaches it - which let a handler group the Library through
        /// <c>IConfigurer.Configure(view)</c> with every guard green. So whenever a reached method
        /// calls a method DECLARED in this assembly (interface, abstract or virtual), EVERY
        /// in-assembly implementation and override of it is treated as reachable, whether or not
        /// that concrete type can flow to the call site. That over-reports rather than
        /// under-reports: fail closed. See <see cref="DispatchEdges"/> for how the implementations
        /// are found.
        ///
        /// Fail-closed: every seed must exist as a method definition in the assembly. A renamed seed
        /// would otherwise silently shrink the closure to nothing, and a scan over nothing passes.
        /// </summary>
        public static IReadOnlyList<string> Reachable(string assemblyPath, IEnumerable<string> seeds)
        {
            var wanted = seeds.ToList();
            if (wanted.Count == 0) throw new ArgumentException("at least one seed is required", nameof(seeds));

            var graph = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var site in CallSites(assemblyPath, _ => true))
            {
                if (!graph.TryGetValue(site.Method, out var callees))
                    graph[site.Method] = callees = new HashSet<string>(StringComparer.Ordinal);
                callees.Add(NormalizeCallee(site.Callee));
            }

            // A method with no calls at all still exists; it just has no edges. Both matter: the
            // seed check below must accept it, and the closure must be able to contain it.
            foreach (string method in MethodNames(assemblyPath))
                if (!graph.ContainsKey(method)) graph[method] = new HashSet<string>(StringComparer.Ordinal);

            foreach (string seed in wanted)
                if (!graph.ContainsKey(seed))
                    throw new InvalidOperationException(
                        $"'{seed}' is not a method in {Path.GetFileName(assemblyPath)}, so the reachability "
                        + "scan seeded with it would cover nothing and pass by finding nothing.");

            var dispatch = DispatchEdges(assemblyPath);

            var reached = new HashSet<string>(wanted, StringComparer.Ordinal);
            var queue = new Queue<string>(wanted);
            while (queue.Count > 0)
            {
                foreach (string callee in graph[queue.Dequeue()])
                {
                    // The callee's own body, when it has one in this assembly.
                    if (graph.ContainsKey(callee) && reached.Add(callee))
                        queue.Enqueue(callee);

                    // And every in-assembly implementation/override the call could dispatch to. The
                    // declaration itself (an interface or abstract method) has no body and is not in
                    // the graph - which is exactly why this edge set exists.
                    if (dispatch.TryGetValue(callee, out var implementations))
                        foreach (string implementation in implementations)
                            if (graph.ContainsKey(implementation) && reached.Add(implementation))
                                queue.Enqueue(implementation);
                }
            }

            return reached.OrderBy(m => m, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Every dispatch edge the assembly's own metadata declares: for each method DECLARED in
        /// this assembly as an interface method, an abstract method or a virtual method, the
        /// in-assembly methods that can actually RUN when it is called. Three sources, all read
        /// from metadata tables rather than guessed from names alone:
        ///
        /// 1. The MethodImpl table - explicit interface implementations and explicit overrides.
        ///    Each row IS a dispatch edge, exact by construction. Rows whose declaration lives in
        ///    another assembly are dropped, to keep this map to what this assembly declares.
        /// 2. InterfaceImpl rows - implicit interface implementations: a type that implements an
        ///    in-assembly interface (generic instantiations of one included) implements its methods
        ///    by NAME, so each same-named method of the type with a body gets an edge from the
        ///    interface's declaration.
        /// 3. The base-type chain - virtual overrides: for every virtual method of every in-assembly
        ///    ancestor, a same-named method of the derived type gets an edge from the ancestor's
        ///    declaration, because a call through the ancestor can run the override.
        ///
        /// Matching by name inside those relationships (not by full signature) is deliberate: an
        /// overload that is NOT the implementation still gets an edge, which over-reports. This map
        /// exists so a reachability guard fails closed; a false extra edge is a false alarm, a
        /// missing edge is a silent pass.
        ///
        /// Its honest limit is the assembly boundary, same as the rest of this class: a declaration
        /// living OUTSIDE the assembly (a BCL or WPF interface or base class) is not a key here, so
        /// an in-assembly implementation invoked only through such an external declaration is not an
        /// edge. The guards built on <see cref="Reachable"/> state that.
        /// </summary>
        private static Dictionary<string, HashSet<string>> DispatchEdges(string assemblyPath)
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();

            var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            void Add(string declaration, string implementation)
            {
                if (declaration == implementation) return;
                if (!edges.TryGetValue(declaration, out var set))
                    edges[declaration] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(implementation);
            }

            foreach (var typeHandle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(typeHandle);

                // 1. Explicit implementations and overrides: each MethodImpl row is an edge.
                foreach (var implHandle in type.GetMethodImplementations())
                {
                    var row = md.GetMethodImplementation(implHandle);
                    string? declaration = InAssemblyDeclaration(md, row.MethodDeclaration);
                    string? body = InAssemblyDeclaration(md, row.MethodBody);
                    if (declaration != null && body != null)
                        Add(NormalizeCallee(declaration), NormalizeCallee(body));
                }

                // This type's methods WITH a body, folded the same way call sites are, so the edge
                // targets match the reachability graph's keys.
                var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var methodHandle in type.GetMethods())
                {
                    var method = md.GetMethodDefinition(methodHandle);
                    if (method.RelativeVirtualAddress == 0) continue;
                    bodies[md.GetString(method.Name)] = MethodName(md, methodHandle);
                }
                if (bodies.Count == 0) continue;

                // 2. Implicit implementations of in-assembly interfaces, by name.
                foreach (var interfaceHandle in type.GetInterfaceImplementations())
                {
                    var declared = DefinedType(md, md.GetInterfaceImplementation(interfaceHandle).Interface);
                    if (declared is not TypeDefinitionHandle interfaceType) continue;   // declared elsewhere
                    string interfaceName = TypeName(md, interfaceType);
                    foreach (var methodHandle in md.GetTypeDefinition(interfaceType).GetMethods())
                    {
                        string name = md.GetString(md.GetMethodDefinition(methodHandle).Name);
                        if (bodies.TryGetValue(name, out string? body))
                            Add(NormalizeCallee($"{interfaceName}::{name}"), body);
                    }
                }

                // 3. Virtual overrides, up the in-assembly base chain.
                var baseHandle = type.BaseType;
                while (!baseHandle.IsNil && DefinedType(md, baseHandle) is TypeDefinitionHandle ancestorType)
                {
                    var ancestor = md.GetTypeDefinition(ancestorType);
                    string ancestorName = TypeName(md, ancestorType);
                    foreach (var methodHandle in ancestor.GetMethods())
                    {
                        var method = md.GetMethodDefinition(methodHandle);
                        if ((method.Attributes & MethodAttributes.Virtual) == 0) continue;
                        string name = md.GetString(method.Name);
                        if (bodies.TryGetValue(name, out string? body))
                            Add(NormalizeCallee($"{ancestorName}::{name}"), body);
                    }
                    baseHandle = ancestor.BaseType;
                }
            }

            return edges;
        }

        /// <summary>A MethodImpl row's method as <c>Type::Method</c> when the type is defined in
        /// THIS assembly (directly or as a generic instantiation of an in-assembly type), else null.</summary>
        private static string? InAssemblyDeclaration(MetadataReader md, EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.MethodDefinition:
                    var def = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                    return $"{TypeName(md, def.GetDeclaringType())}::{md.GetString(def.Name)}";

                case HandleKind.MemberReference:
                    var member = md.GetMemberReference((MemberReferenceHandle)handle);
                    if (member.GetKind() != MemberReferenceKind.Method) return null;
                    return DefinedType(md, member.Parent) is TypeDefinitionHandle parent
                        ? $"{TypeName(md, parent)}::{md.GetString(member.Name)}"
                        : null;

                default:
                    return null;
            }
        }

        /// <summary>The in-assembly type definition a handle names: a TypeDefinition directly, or
        /// the OPEN generic type behind a TypeSpecification's generic instantiation when that type
        /// is defined here. A TypeReference - a type from another assembly - is null: the walk's
        /// assembly boundary.</summary>
        private static TypeDefinitionHandle? DefinedType(MetadataReader md, EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    return (TypeDefinitionHandle)handle;

                case HandleKind.TypeSpecification:
                    var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                    if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
                    if (blob.ReadSignatureTypeCode() != SignatureTypeCode.TypeHandle) return null;
                    var generic = blob.ReadTypeHandle();
                    return generic.Kind == HandleKind.TypeDefinition ? (TypeDefinitionHandle)generic : null;

                default:
                    return null;
            }
        }

        /// <summary>Every method defined in the assembly, normalized the same way call sites are.</summary>
        public static IReadOnlyList<string> MethodNames(string assemblyPath)
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();

            return md.MethodDefinitions
                .Where(h => md.GetMethodDefinition(h).RelativeVirtualAddress != 0)
                .Select(h => MethodName(md, h))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>One field access: the method that contains it and the field it names.</summary>
        internal sealed record FieldSite(string Assembly, string Method, string Field);

        /// <summary>
        /// Every field the assembly's methods read or write whose name matches
        /// <paramref name="wanted"/> - the counterpart to <see cref="CallSites"/> for the question
        /// "which methods TOUCH this thing", which no call scan can answer because a field access is
        /// not a call.
        ///
        /// Issue #178 uses it to scope a guard to the library instead of to the whole app: a method
        /// that groups a collection view is only a Library defect if that method is handling the
        /// Library's rows (<c>_recent</c>) or its list control (<c>RecentList</c>). Banning grouping
        /// everywhere would fail a legitimate grouped view built for some other feature later, and a
        /// guard that punishes unrelated work is a guard someone eventually deletes.
        /// </summary>
        public static IReadOnlyList<FieldSite> FieldAccesses(string assemblyPath, Func<string, bool> wanted)
        {
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException(
                    "The assembly to scan was not built. This scan cannot be allowed to pass by finding nothing.",
                    assemblyPath);

            var sites = new List<FieldSite>();
            string assembly = Path.GetFileName(assemblyPath);

            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();

            if (md.MethodDefinitions.Count == 0)
                throw new InvalidOperationException($"{assembly} contains no methods - the scanner is looking at the wrong file.");

            foreach (var handle in md.MethodDefinitions)
            {
                var method = md.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress == 0) continue;

                string where = MethodName(md, handle);
                byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()
                            ?? throw new InvalidOperationException($"No IL for {where} in {assembly}.");

                foreach (int token in ReferencedFieldTokens(il, $"{assembly}!{where}"))
                {
                    string? field = FieldOf(md, token);
                    if (field != null && wanted(field)) sites.Add(new FieldSite(assembly, where, field));
                }
            }

            return sites;
        }

        /// <summary>One string literal: the method that loads it and the text it loads.</summary>
        internal sealed record StringSite(string Assembly, string Method, string Value);

        /// <summary>
        /// Every string CONSTANT the assembly's compiled code loads (`ldstr`), with the method that
        /// loads it - the compiled-artifact counterpart to "which URL does this product talk to".
        ///
        /// Why over source (issue #184, round-2 gate finding). A retired update channel can be
        /// reintroduced on a path no behavioral test walks - the gate did it by selecting the old URL
        /// only when the default HttpClient was used, and every channel test passed because every one
        /// of them injected a client. A literal cannot hide that way: whatever branch selects it, the
        /// string has to BE in the assembly, in some method, as an `ldstr` operand, and a const,
        /// an interpolation, an alias or a helper does not change that.
        ///
        /// Its LIMIT is equally concrete and is stated by the tests that use it: a literal ASSEMBLED
        /// at run time from fragments no single one of which contains the searched text is not seen.
        /// It answers "does this compiled product carry this string", not "can this product ever
        /// produce this string".
        /// </summary>
        public static IReadOnlyList<StringSite> StringLiterals(string assemblyPath, Func<string, bool> wanted)
        {
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException(
                    "The assembly to scan was not built. This scan cannot be allowed to pass by finding nothing.",
                    assemblyPath);

            var sites = new List<StringSite>();
            string assembly = Path.GetFileName(assemblyPath);

            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();

            if (md.MethodDefinitions.Count == 0)
                throw new InvalidOperationException($"{assembly} contains no methods - the scanner is looking at the wrong file.");

            foreach (var handle in md.MethodDefinitions)
            {
                var method = md.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress == 0) continue;

                string where = MethodName(md, handle);
                byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()
                            ?? throw new InvalidOperationException($"No IL for {where} in {assembly}.");

                var tokens = new List<int>();
                Walk(il, $"{assembly}!{where}", (opcode, twoByte, operandAt) =>
                {
                    if (!twoByte && opcode == LdStr) tokens.Add(BitConverter.ToInt32(il, operandAt));
                });

                foreach (int token in tokens)
                {
                    string value = md.GetUserString(MetadataTokens.UserStringHandle(token));
                    if (wanted(value)) sites.Add(new StringSite(assembly, where, value));
                }
            }

            return sites;
        }

        /// <summary>How many string literals an assembly loads in total - the instrument check for
        /// <see cref="StringLiterals"/>, so "no offending literal" can never be the answer of a scan
        /// that read nothing.</summary>
        public static int StringLiteralCount(string assemblyPath) => StringLiterals(assemblyPath, _ => true).Count;

        /// <summary>Every file-write call site in the given assemblies.</summary>
        public static IReadOnlyList<CallSite> FileWrites(IEnumerable<string> assemblies) =>
            assemblies.SelectMany(a => CallSites(a, IsFileWriteApi)).ToList();

        /// <summary>Call sites rendered as stable, sorted, human-readable lines:
        /// <c>assembly!Type::Method -> Callee xN</c>. Comparing the whole block as text is what makes
        /// a failure say exactly which method gained (or lost) which call.</summary>
        public static string Describe(IEnumerable<CallSite> sites) =>
            string.Join(Environment.NewLine, sites
                .GroupBy(s => $"{s.Assembly}!{s.Method} -> {s.Callee}", StringComparer.Ordinal)
                .Select(g => $"{g.Key} x{g.Count()}")
                .OrderBy(line => line, StringComparer.Ordinal));

        /// <summary>Number of <c>calli</c> (indirect call) instructions. An indirect call carries a
        /// signature, not a method token, so its target cannot be named by any static scan - the
        /// guard's claim is only honest while the product contains none.</summary>
        public static int IndirectCalls(string assemblyPath)
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();

            int count = 0;
            foreach (var handle in md.MethodDefinitions)
            {
                var method = md.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress == 0) continue;
                byte[] il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                count += CountCalli(il, $"{Path.GetFileName(assemblyPath)}!{MethodName(md, handle)}");
            }
            return count;
        }

        /// <summary>Every native import in the assembly as <c>module!EntryPoint</c>. A P/Invoke to
        /// CreateFileW/WriteFile would be a file writer that no IL call-site scan of System.IO can
        /// see, so the imports are enumerated and pinned rather than assumed absent.</summary>
        public static IReadOnlyList<string> NativeImports(string assemblyPath)
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();

            var imports = new List<string>();
            foreach (var handle in md.MethodDefinitions)
            {
                var method = md.GetMethodDefinition(handle);
                if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0) continue;

                var import = method.GetImport();
                if (import.Module.IsNil) continue;

                string module = md.GetString(md.GetModuleReference(import.Module).Name);
                string entry = import.Name.IsNil ? md.GetString(method.Name) : md.GetString(import.Name);
                imports.Add($"{module}!{entry}");
            }
            return imports.Distinct(StringComparer.OrdinalIgnoreCase)
                          .OrderBy(i => i, StringComparer.OrdinalIgnoreCase)
                          .ToList();
        }

        /// <summary>The simple names of every assembly <paramref name="assemblyPath"/> references.
        /// Used to show that an assembly OUTSIDE the guarded set cannot reach the manifest type at
        /// all, which is what makes the guard's scope statement a fact rather than an assumption.</summary>
        public static IReadOnlyList<string> AssemblyReferences(string assemblyPath)
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();

            return md.AssemblyReferences
                .Select(h => md.GetString(md.GetAssemblyReference(h).Name))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ---- metadata naming --------------------------------------------------

        /// <summary>
        /// The method a human wrote. The C# compiler moves lambdas onto <c>&lt;&gt;c</c> /
        /// <c>&lt;&gt;c__DisplayClassNN_M</c>, local functions onto
        /// <c>&lt;Method&gt;g__Name|NN_M</c>, and async/iterator bodies onto
        /// <c>&lt;Method&gt;d__NN::MoveNext</c>. Those generated names carry ORDINALS that shift when
        /// an unrelated lambda is added earlier in the file, so pinning them raw would make the guard
        /// fail on edits it does not care about. Folding them back to the declaring method keeps the
        /// pin stable while still counting the call.
        /// </summary>
        private static string MethodName(MetadataReader md, MethodDefinitionHandle handle)
        {
            var method = md.GetMethodDefinition(handle);
            return Fold(TypeName(md, method.GetDeclaringType()), md.GetString(method.Name));
        }

        /// <summary>The same folding applied to a CALLEE name ("Namespace.Type::Member"), so a call
        /// that targets a lambda or an async body can be matched against the declaring method the
        /// method-definition scan reports. Without it a call graph would break at every lambda.</summary>
        private static string NormalizeCallee(string callee)
        {
            int at = callee.LastIndexOf("::", StringComparison.Ordinal);
            return at < 0 ? callee : Fold(callee.Substring(0, at), callee.Substring(at + 2));
        }

        private static string Fold(string declaring, string name)
        {
            // A generated nested type names the method it came from: Package/<Finalize>d__12.
            string? origin = null;
            var kept = new List<string>();
            foreach (string part in declaring.Split('/'))
            {
                var m = Regex.Match(part, @"^<([^>]+)>");
                if (m.Success) origin ??= m.Groups[1].Value;
                else if (!part.StartsWith("<", StringComparison.Ordinal)) kept.Add(part);
            }

            // A generated method names it too: <Finalize>b__0, <Finalize>g__Write|3_0.
            var fromName = Regex.Match(name, @"^<([^>]+)>[a-z]__");
            if (fromName.Success) origin ??= fromName.Groups[1].Value;

            string type = kept.Count > 0 ? string.Join("/", kept) : declaring;
            return $"{type}::{origin ?? name}";
        }

        private static string TypeName(MetadataReader md, TypeDefinitionHandle handle)
        {
            var type = md.GetTypeDefinition(handle);
            string name = md.GetString(type.Name);
            if (type.IsNested) return $"{TypeName(md, type.GetDeclaringType())}/{name}";
            string ns = md.GetString(type.Namespace);
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }

        private static string TypeName(MetadataReader md, TypeReferenceHandle handle)
        {
            var type = md.GetTypeReference(handle);
            string name = md.GetString(type.Name);
            if (type.ResolutionScope.Kind == HandleKind.TypeReference)
                return $"{TypeName(md, (TypeReferenceHandle)type.ResolutionScope)}/{name}";
            string ns = md.GetString(type.Namespace);
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }

        /// <summary>The target of a call/newobj/ldftn/ldtoken token as <c>Namespace.Type::Method</c>,
        /// or null when the token does not name a method (a type token, or a generic instantiation
        /// whose parent is a TypeSpec).</summary>
        private static string? Callee(MetadataReader md, int token)
        {
            var handle = MetadataTokens.EntityHandle(token);
            switch (handle.Kind)
            {
                case HandleKind.MemberReference:
                    var member = md.GetMemberReference((MemberReferenceHandle)handle);
                    if (member.GetKind() != MemberReferenceKind.Method) return null;
                    string? parent = member.Parent.Kind switch
                    {
                        HandleKind.TypeReference => TypeName(md, (TypeReferenceHandle)member.Parent),
                        HandleKind.TypeDefinition => TypeName(md, (TypeDefinitionHandle)member.Parent),
                        // A generic instantiation of an IN-ASSEMBLY type folds back onto its open
                        // type, so a call through IConfigure<T> or Helper<T> is an edge the
                        // reachability walk can follow. External instantiations stay null.
                        HandleKind.TypeSpecification when DefinedType(md, member.Parent) is TypeDefinitionHandle generic
                            => TypeName(md, generic),
                        _ => null,   // external TypeSpec, ModuleRef, MethodDef (vararg)
                    };
                    return parent == null ? null : $"{parent}::{md.GetString(member.Name)}";

                case HandleKind.MethodDefinition:
                    var def = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                    return $"{TypeName(md, def.GetDeclaringType())}::{md.GetString(def.Name)}";

                case HandleKind.MethodSpecification:
                    var spec = md.GetMethodSpecification((MethodSpecificationHandle)handle);
                    return Callee(md, MetadataTokens.GetToken(spec.Method));

                default:
                    return null;
            }
        }

        /// <summary>The target of a field token as <c>Namespace.Type::Field</c>, or null when the
        /// token does not name a field (a generic instantiation whose parent is a TypeSpec).</summary>
        private static string? FieldOf(MetadataReader md, int token)
        {
            var handle = MetadataTokens.EntityHandle(token);
            switch (handle.Kind)
            {
                case HandleKind.FieldDefinition:
                    var field = md.GetFieldDefinition((FieldDefinitionHandle)handle);
                    return $"{TypeName(md, field.GetDeclaringType())}::{md.GetString(field.Name)}";

                case HandleKind.MemberReference:
                    var member = md.GetMemberReference((MemberReferenceHandle)handle);
                    if (member.GetKind() != MemberReferenceKind.Field) return null;
                    string? parent = member.Parent.Kind switch
                    {
                        HandleKind.TypeReference => TypeName(md, (TypeReferenceHandle)member.Parent),
                        HandleKind.TypeDefinition => TypeName(md, (TypeDefinitionHandle)member.Parent),
                        _ => null,
                    };
                    return parent == null ? null : $"{parent}::{md.GetString(member.Name)}";

                default:
                    return null;
            }
        }

        // ---- the IL walker ----------------------------------------------------

        private const int Invalid = -1;
        private const int SwitchOperand = -2;
        private const int TwoBytePrefix = -3;

        private const int Call = 0x28;
        private const int Calli = 0x29;
        private const int CallVirt = 0x6F;
        private const int NewObj = 0x73;
        private const int LdToken = 0xD0;
        private const int LdStr = 0x72;
        private const int LdFld = 0x7B;         // ldfld, ldflda, stfld, ldsfld, ldsflda, stsfld
        private const int StSFld = 0x80;        // ...are 0x7B..0x80, contiguous
        private const int Prefix = 0xFE;
        private const int LdFtn = 0x06;         // 0xFE 0x06
        private const int LdVirtFtn = 0x07;     // 0xFE 0x07

        private static readonly int[] OneByteOperand = BuildOneByteTable();
        private static readonly int[] TwoByteOperand = BuildTwoByteTable();

        /// <summary>Operand size per single-byte opcode (ECMA-335 Partition VI). Everything not
        /// listed stays <see cref="Invalid"/>, so an unassigned byte stops the walk instead of
        /// silently desynchronizing it.</summary>
        private static int[] BuildOneByteTable()
        {
            var table = Filled(256);
            Fill(table, 0, (0x00, 0x0D), (0x14, 0x1E), (0x25, 0x26), (0x2A, 0x2A), (0x46, 0x6E),
                          (0x76, 0x76), (0x7A, 0x7A), (0x82, 0x8B), (0x8E, 0x8E), (0x90, 0xA2),
                          (0xB3, 0xBA), (0xC3, 0xC3), (0xD1, 0xDC), (0xDF, 0xE0));
            Fill(table, 1, (0x0E, 0x13), (0x1F, 0x1F), (0x2B, 0x37), (0xDE, 0xDE));
            Fill(table, 4, (0x20, 0x20), (0x22, 0x22), (0x27, 0x29), (0x38, 0x44), (0x6F, 0x75),
                          (0x79, 0x79), (0x7B, 0x81), (0x8C, 0x8D), (0x8F, 0x8F), (0xA3, 0xA5),
                          (0xC2, 0xC2), (0xC6, 0xC6), (0xD0, 0xD0), (0xDD, 0xDD));
            Fill(table, 8, (0x21, 0x21), (0x23, 0x23));
            table[0x45] = SwitchOperand;      // switch: 4-byte count, then that many 4-byte targets
            table[Prefix] = TwoBytePrefix;
            return table;
        }

        /// <summary>Operand size per 0xFE-prefixed opcode.</summary>
        private static int[] BuildTwoByteTable()
        {
            var table = Filled(256);
            Fill(table, 0, (0x00, 0x05), (0x0F, 0x0F), (0x11, 0x11), (0x13, 0x14), (0x17, 0x18),
                          (0x1A, 0x1A), (0x1D, 0x1E));
            Fill(table, 1, (0x12, 0x12), (0x19, 0x19));
            Fill(table, 2, (0x09, 0x0E));
            Fill(table, 4, (0x06, 0x07), (0x15, 0x16), (0x1C, 0x1C));
            return table;
        }

        private static int[] Filled(int size)
        {
            var table = new int[size];
            for (int i = 0; i < size; i++) table[i] = Invalid;
            return table;
        }

        private static void Fill(int[] table, int operandSize, params (int From, int To)[] ranges)
        {
            foreach (var (from, to) in ranges)
                for (int op = from; op <= to; op++) table[op] = operandSize;
        }

        /// <summary>
        /// Every metadata token in <paramref name="il"/> that names a method: <c>call</c>,
        /// <c>callvirt</c>, <c>newobj</c>, plus <c>ldftn</c> / <c>ldvirtftn</c> (a delegate built over
        /// an API is still a use of that API) and <c>ldtoken</c> (the reflection handle shape a scan
        /// can see). <c>calli</c> is NOT here - it carries a signature rather than a target - which is
        /// why <see cref="IndirectCalls"/> exists to prove the product has none.
        /// </summary>
        private static IEnumerable<int> ReferencedMethodTokens(byte[] il, string where)
        {
            var tokens = new List<int>();
            Walk(il, where, (opcode, twoByte, operandAt) =>
            {
                bool namesAMethod = twoByte
                    ? opcode == LdFtn || opcode == LdVirtFtn
                    : opcode == Call || opcode == CallVirt || opcode == NewObj || opcode == LdToken;
                if (namesAMethod) tokens.Add(BitConverter.ToInt32(il, operandAt));
            });
            return tokens;
        }

        /// <summary>Every metadata token in <paramref name="il"/> that names a FIELD: the four
        /// instance forms and the two static ones, loads and stores alike. Reading a field is as much
        /// a use of it as writing it - a method that reads <c>_recent</c> to group it is handling the
        /// Library just as surely as one that assigns to it.</summary>
        private static IEnumerable<int> ReferencedFieldTokens(byte[] il, string where)
        {
            var tokens = new List<int>();
            Walk(il, where, (opcode, twoByte, operandAt) =>
            {
                if (!twoByte && opcode >= LdFld && opcode <= StSFld)
                    tokens.Add(BitConverter.ToInt32(il, operandAt));
            });
            return tokens;
        }

        private static int CountCalli(byte[] il, string where)
        {
            int count = 0;
            Walk(il, where, (opcode, twoByte, _) => { if (!twoByte && opcode == Calli) count++; });
            return count;
        }

        /// <summary>
        /// Walks the instruction stream exactly, calling <paramref name="onInstruction"/> with the
        /// opcode and the offset of its operand. Every failure mode throws rather than returning a
        /// short list: an unknown opcode, an operand that runs past the end, or a walk that does not
        /// finish exactly on the last byte all mean the scanner lost the instruction boundary, and a
        /// desynchronized scan reports too FEW calls - the direction that would let a bypass through.
        /// </summary>
        private static void Walk(byte[] il, string where, Action<int, bool, int> onInstruction)
        {
            int i = 0;
            while (i < il.Length)
            {
                int start = i;
                int opcode = il[i++];
                bool twoByte = opcode == Prefix;
                int operandSize;

                if (twoByte)
                {
                    if (i >= il.Length)
                        throw new InvalidOperationException($"IL of {where} ends inside a two-byte opcode at {start}.");
                    opcode = il[i++];
                    operandSize = TwoByteOperand[opcode];
                    if (operandSize == Invalid)
                        throw new InvalidOperationException($"Unknown IL opcode 0xFE{opcode:X2} at {start} in {where}.");
                }
                else
                {
                    operandSize = OneByteOperand[opcode];
                    if (operandSize == Invalid)
                        throw new InvalidOperationException($"Unknown IL opcode 0x{opcode:X2} at {start} in {where}.");
                }

                if (operandSize == SwitchOperand)
                {
                    if (i + 4 > il.Length)
                        throw new InvalidOperationException($"switch at {start} in {where} has no count.");
                    long targets = BitConverter.ToUInt32(il, i);
                    i += 4;
                    long end = i + 4 * targets;
                    if (end > il.Length)
                        throw new InvalidOperationException($"switch at {start} in {where} runs past the end of the body.");
                    i = (int)end;
                    continue;
                }

                if (i + operandSize > il.Length)
                    throw new InvalidOperationException($"Operand of the opcode at {start} in {where} runs past the end of the body.");

                onInstruction(opcode, twoByte, i);
                i += operandSize;
            }

            if (i != il.Length)
                throw new InvalidOperationException($"The IL walk of {where} finished at {i} of {il.Length} - the scanner lost the instruction boundary.");
        }
    }
}
