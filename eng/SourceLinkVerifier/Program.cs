using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Net;
using System.Text.Json;

const string SourceLinkGuid = "CC110556-A091-4D38-9FEC-25AB9A351A6A";

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: dotnet run --project eng/SourceLinkVerifier -- [--self-test] <pdb-or-directory> [...]");
    return 2;
}

if (args.Length == 1 && args[0].Equals("--self-test", StringComparison.OrdinalIgnoreCase))
    return SelfTest();

var files = args.SelectMany(Expand).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
if (files.Length == 0)
{
    Console.Error.WriteLine("No PDB files found.");
    return 2;
}

var failed = false;
var passed = 0;
foreach (var file in files)
{
    var result = Verify(file);
    Console.WriteLine($"{result.Status}: {file} — {result.Message}");
    failed |= result.Status == "FAIL";
    passed += result.Status == "PASS" ? 1 : 0;
}
if (passed == 0)
{
    Console.Error.WriteLine("No managed portable PDB with verifiable SourceLink data was found.");
    failed = true;
}
return failed ? 1 : 0;

static IEnumerable<string> Expand(string input)
{
    if (File.Exists(input))
        return Path.GetExtension(input).Equals(".pdb", StringComparison.OrdinalIgnoreCase) ? [Path.GetFullPath(input)] : [];
    if (Directory.Exists(input))
        return Directory.EnumerateFiles(input, "*.pdb", SearchOption.AllDirectories);
    Console.Error.WriteLine($"Missing input: {input}");
    return [];
}

static (string Status, string Message) Verify(string pdbPath)
{
    byte[] bytes;
    try { bytes = File.ReadAllBytes(pdbPath); }
    catch (Exception ex) { return ("FAIL", $"cannot read ({ex.Message})"); }

    // Portable PDB metadata starts with the ECMA-335 metadata signature. A Windows
    // native/AOT PDB has a different format and cannot be inspected by this verifier.
    if (bytes.Length < 4 || BitConverter.ToUInt32(bytes, 0) != 0x424A5342)
    {
        var directory = Path.GetDirectoryName(pdbPath)!;
        var stem = Path.GetFileNameWithoutExtension(pdbPath);
        var managed = HasManagedMetadata(Path.Combine(directory, stem + ".dll")) ||
                      HasManagedMetadata(Path.Combine(directory, stem + ".exe"));
        return (managed ? "FAIL" : "SKIP", managed
            ? "managed assembly has a non-portable PDB; SourceLink cannot be verified"
            : "native/AOT Windows PDB (not portable; not inspected)");
    }

    try
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        var module = MetadataTokens.EntityHandle(TableIndex.Module, 1);
        foreach (var handle in reader.GetCustomDebugInformation(module))
        {
            var cdi = reader.GetCustomDebugInformation(handle);
            if (reader.GetGuid(cdi.Kind).ToString().Equals(SourceLinkGuid, StringComparison.OrdinalIgnoreCase))
            {
                var json = reader.GetBlobBytes(cdi.Value);
                return ValidateSourceLink(json);
            }
        }
        return ("FAIL", "portable PDB has no SourceLink custom debug information");
    }
    catch (Exception ex) { return ("FAIL", $"invalid portable PDB ({ex.Message})"); }
}

static (string Status, string Message) ValidateSourceLink(ReadOnlySpan<byte> json)
{
    try
    {
        using var document = JsonDocument.Parse(json.ToArray());
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("documents", out var documents) ||
            documents.ValueKind != JsonValueKind.Object || documents.EnumerateObject().Count() == 0)
            return ("FAIL", "SourceLink JSON must contain a non-empty documents object");
        foreach (var mapping in documents.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(mapping.Name) || mapping.Value.ValueKind != JsonValueKind.String)
                return ("FAIL", "SourceLink mappings must have non-empty keys and string values");
            var value = mapping.Value.GetString();
            if (!mapping.Name.StartsWith("/_/", StringComparison.Ordinal) ||
                mapping.Name.Contains('\\', StringComparison.Ordinal) ||
                !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
                uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                (IPAddress.TryParse(uri.Host, out var address) && IsPrivate(address)) ||
                mapping.Name.Contains("..", StringComparison.Ordinal))
                return ("FAIL", "SourceLink mappings must use canonical /_/ document keys and privacy-safe HTTPS URLs");
        }
        return ("PASS", $"valid SourceLink JSON with {documents.EnumerateObject().Count()} mapping(s)");
    }
    catch (JsonException ex) { return ("FAIL", $"invalid SourceLink JSON ({ex.Message})"); }
}

static bool IsPrivate(IPAddress address)
{
    var bytes = address.GetAddressBytes();
    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) || bytes[0] == 169 && bytes[1] == 254;
    return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.ToString().StartsWith("fc", StringComparison.OrdinalIgnoreCase) ||
           address.ToString().StartsWith("fd", StringComparison.OrdinalIgnoreCase);
}

static bool HasManagedMetadata(string assemblyPath)
{
    if (!File.Exists(assemblyPath)) return false;
    try
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        return pe.HasMetadata && pe.GetMetadataReader().IsAssembly;
    }
    catch { return false; }
}

static int SelfTest()
{
    var good = "{\"documents\":{\"/_/*\":\"https://github.com/example/repo/*\"}}"u8;
    var bad = "{\"documents\":{\"src/*\":\"file:///secret/*\"}}"u8;
    var goodResult = ValidateSourceLink(good);
    var badResult = ValidateSourceLink(bad);
    if (goodResult.Status != "PASS" || badResult.Status != "FAIL")
    {
        Console.Error.WriteLine("SourceLink verifier self-test failed.");
        return 1;
    }
    Console.WriteLine("SourceLink verifier self-test passed.");
    return 0;
}
