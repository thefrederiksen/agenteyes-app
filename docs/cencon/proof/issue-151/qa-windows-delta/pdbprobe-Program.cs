using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
// usage: pdbprobe <dll> <name-substring>...   prints every compiled source document and every type
// whose path/name contains one of the substrings; exit 0.
var dll = args[0];
var pdb = Path.ChangeExtension(dll, ".pdb");
Console.WriteLine($"assembly: {dll}");
using (var pe = new PEReader(File.OpenRead(dll)))
{
    var md = pe.GetMetadataReader();
    var types = md.TypeDefinitions.Select(h => md.GetTypeDefinition(h)).Select(t => md.GetString(t.Namespace) + "." + md.GetString(t.Name)).ToList();
    Console.WriteLine($"typedefs: {types.Count}");
    foreach (var q in args.Skip(1))
        foreach (var t in types.Where(t => t.Split('.').Last() == q || t.Split('.').Last().StartsWith(q)))
            Console.WriteLine($"  TYPE  [{q}] {t}");
}
using var prov = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdb));
var r = prov.GetMetadataReader();
var docs = r.Documents.Select(h => r.GetString(r.GetDocument(h).Name)).ToList();
Console.WriteLine($"pdb documents: {docs.Count}");
foreach (var q in args.Skip(1))
{
    var hits = docs.Where(d => Path.GetFileNameWithoutExtension(d) == q).ToList();
    Console.WriteLine(hits.Count == 0 ? $"  DOC   [{q}] ABSENT" : string.Join("\n", hits.Select(h => $"  DOC   [{q}] {h}")));
}
