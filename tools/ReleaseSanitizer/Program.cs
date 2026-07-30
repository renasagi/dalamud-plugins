using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: ReleaseSanitizer <input.zip> <output.zip> <repository-url>");
    return 2;
}

try
{
    var inputPath = Path.GetFullPath(args[0]);
    var outputPath = Path.GetFullPath(args[1]);
    var repositoryUrl = args[2].TrimEnd('/');
    var forbiddenTerms = (Environment.GetEnvironmentVariable("SANITIZER_FORBIDDEN_TERMS") ?? string.Empty)
        .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (string.Equals(inputPath, outputPath, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Input and output paths must differ.");

    ArtifactPackage.Audit(inputPath, repositoryUrl, forbiddenTerms, false);
    ArtifactPackage.Repack(inputPath, outputPath);
    ArtifactPackage.Audit(outputPath, repositoryUrl, forbiddenTerms, true);

    var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(outputPath)));
    Console.WriteLine($"Sanitized package: {outputPath}");
    Console.WriteLine($"SHA256: {hash}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Release rejected: {exception.Message}");
    return 1;
}

internal static partial class ArtifactPackage
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void Audit(
        string archivePath,
        string repositoryUrl,
        IReadOnlyList<string> forbiddenTerms,
        bool requireNormalizedArchive)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count == 0)
            throw new InvalidOperationException("The package is empty.");

        var manifests = new List<JsonDocument>();
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName != entry.Name || entry.Name.Length == 0)
                throw new InvalidOperationException($"Nested or directory entry is not allowed: {entry.FullName}");

            var extension = Path.GetExtension(entry.Name);
            if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unexpected package file: {entry.Name}");
            }

            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();

            ScanStrings(entry.Name, bytes, forbiddenTerms);

            if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                AuditAssembly(entry.Name, bytes, forbiddenTerms);

            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                !entry.Name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
            {
                manifests.Add(JsonDocument.Parse(bytes));
            }

            if (requireNormalizedArchive && entry.LastWriteTime != FixedTimestamp)
                throw new InvalidOperationException($"Non-normalized timestamp on {entry.Name}.");
        }

        if (manifests.Count != 1)
            throw new InvalidOperationException("The package must contain exactly one plugin manifest.");

        using var manifest = manifests[0];
        var root = manifest.RootElement;
        RequireString(root, "InternalName");
        RequireString(root, "AssemblyVersion");
        RequireString(root, "Author");
        var manifestRepo = RequireString(root, "RepoUrl").TrimEnd('/');
        if (!string.Equals(manifestRepo, repositoryUrl, StringComparison.Ordinal))
            throw new InvalidOperationException("The manifest repository URL does not match the public feed.");

        if (requireNormalizedArchive)
            AuditZipHeaders(archivePath);
    }

    public static void Repack(string inputPath, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using (var input = ZipFile.OpenRead(inputPath))
        using (var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var output = new ZipArchive(outputStream, ZipArchiveMode.Create))
        {
            foreach (var source in input.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
            {
                var target = output.CreateEntry(source.FullName, CompressionLevel.SmallestSize);
                target.LastWriteTime = FixedTimestamp;
                target.ExternalAttributes = 0;
                using var sourceStream = source.Open();
                using var targetStream = target.Open();
                sourceStream.CopyTo(targetStream);
            }
        }

        NormalizeZipHeaders(outputPath);
    }

    private static void AuditAssembly(
        string name,
        byte[] bytes,
        IReadOnlyList<string> forbiddenTerms)
    {
        using var stream = new MemoryStream(bytes, false);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
            throw new InvalidOperationException($"{name} is not a managed assembly.");

        var certificate = peReader.PEHeaders.PEHeader?.CertificateTableDirectory;
        if (certificate is { Size: > 0 })
            throw new InvalidOperationException($"{name} contains an Authenticode certificate.");

        foreach (var entry in peReader.ReadDebugDirectory())
        {
            if (entry.Type != DebugDirectoryEntryType.Reproducible)
                throw new InvalidOperationException($"{name} contains {entry.Type} debug metadata.");
        }

        var reader = peReader.GetMetadataReader();
        var assembly = reader.GetAssemblyDefinition();
        var assemblyName = reader.GetString(assembly.Name);

        foreach (var attributeHandle in assembly.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(attributeHandle);
            var typeName = GetAttributeTypeName(reader, attribute.Constructor);
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.RemainingBytes < 2 || blob.ReadUInt16() != 1)
                continue;

            switch (typeName)
            {
                case "System.Reflection.AssemblyMetadataAttribute":
                    {
                        var key = blob.ReadSerializedString() ?? string.Empty;
                        var value = blob.ReadSerializedString() ?? string.Empty;
                        ScanText($"{name} assembly metadata", key, forbiddenTerms);
                        ScanText($"{name} assembly metadata", value, forbiddenTerms);
                        if (key.Equals("RepositoryUrl", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"{name} contains a repository assembly attribute.");
                        break;
                    }
                case "System.Reflection.AssemblyInformationalVersionAttribute":
                    {
                        var value = blob.ReadSerializedString() ?? string.Empty;
                        ScanText($"{name} informational version", value, forbiddenTerms);
                        if (RevisionSuffixRegex().IsMatch(value))
                            throw new InvalidOperationException($"{name} contains a source revision in its version.");
                        break;
                    }
                case "System.Reflection.AssemblyCompanyAttribute":
                    {
                        var value = blob.ReadSerializedString() ?? string.Empty;
                        ScanText($"{name} company", value, forbiddenTerms);
                        if (value.Length > 0 && !value.Equals(assemblyName, StringComparison.Ordinal))
                            throw new InvalidOperationException($"{name} contains a non-project company value.");
                        break;
                    }
                case "System.Reflection.AssemblyCopyrightAttribute":
                    {
                        var value = blob.ReadSerializedString() ?? string.Empty;
                        if (value.Length > 0)
                            throw new InvalidOperationException($"{name} contains copyright identity metadata.");
                        break;
                    }
            }
        }

        foreach (var text in ExtractStrings(bytes))
        {
            if (text.Contains("RepositoryUrl", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("SourceLink", StringComparison.OrdinalIgnoreCase) ||
                text.Contains(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{name} contains build provenance text.");
            }
        }
    }

    private static string GetAttributeTypeName(MetadataReader reader, EntityHandle constructor)
    {
        EntityHandle typeHandle = constructor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default,
        };

        return typeHandle.Kind switch
        {
            HandleKind.TypeReference => JoinTypeName(reader, reader.GetTypeReference((TypeReferenceHandle)typeHandle)),
            HandleKind.TypeDefinition => JoinTypeName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)typeHandle)),
            _ => string.Empty,
        };
    }

    private static string JoinTypeName(MetadataReader reader, TypeReference type) =>
        $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";

    private static string JoinTypeName(MetadataReader reader, TypeDefinition type) =>
        $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";

    private static void ScanStrings(string source, byte[] bytes, IReadOnlyList<string> forbiddenTerms)
    {
        foreach (var text in ExtractStrings(bytes))
            ScanText(source, text, forbiddenTerms);
    }

    private static IEnumerable<string> ExtractStrings(byte[] bytes)
    {
        foreach (var text in ExtractAsciiStrings(bytes))
            yield return text;
        foreach (var text in ExtractUtf16Strings(bytes))
            yield return text;
    }

    private static IEnumerable<string> ExtractAsciiStrings(byte[] bytes)
    {
        var current = new StringBuilder();
        foreach (var value in bytes)
        {
            if (value is >= 0x20 and <= 0x7E)
            {
                current.Append((char)value);
                continue;
            }

            if (current.Length >= 4)
                yield return current.ToString();
            current.Clear();
        }

        if (current.Length >= 4)
            yield return current.ToString();
    }

    private static IEnumerable<string> ExtractUtf16Strings(byte[] bytes)
    {
        for (var start = 0; start < 2; start++)
        {
            var current = new StringBuilder();
            for (var index = start; index + 1 < bytes.Length; index += 2)
            {
                var value = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(index, 2));
                if (value is >= 0x20 and <= 0x7E)
                {
                    current.Append((char)value);
                    continue;
                }

                if (current.Length >= 4)
                    yield return current.ToString();
                current.Clear();
            }

            if (current.Length >= 4)
                yield return current.ToString();
        }
    }

    private static void ScanText(string source, string text, IReadOnlyList<string> forbiddenTerms)
    {
        foreach (var term in forbiddenTerms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{source} contains a forbidden identifier.");
        }

        if (UnixPathRegex().IsMatch(text) || WindowsPathRegex().IsMatch(text))
            throw new InvalidOperationException($"{source} contains an absolute filesystem path.");
        if (EmailRegex().IsMatch(text))
            throw new InvalidOperationException($"{source} contains an email address.");
        if (SecretRegex().IsMatch(text))
            throw new InvalidOperationException($"{source} contains text resembling a secret.");
    }

    private static string RequireString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidOperationException($"The manifest is missing {propertyName}.");
        }

        return property.GetString()!;
    }

    private static void NormalizeZipHeaders(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var end = FindEndOfCentralDirectory(bytes);
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(end + 20, 2)) != 0)
            throw new InvalidOperationException("Archive comments are not allowed.");

        var entries = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(end + 10, 2));
        var position = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16, 4)));
        for (var index = 0; index < entries; index++)
        {
            RequireSignature(bytes, position, 0x02014B50);
            bytes[position + 5] = 0;
            bytes.AsSpan(position + 38, 4).Clear();

            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 28, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 30, 2));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 32, 2));
            if (extraLength != 0 || commentLength != 0)
                throw new InvalidOperationException("ZIP entry metadata could not be normalized.");

            var localOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 42, 4)));
            RequireSignature(bytes, localOffset, 0x04034B50);
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(localOffset + 28, 2)) != 0)
                throw new InvalidOperationException("Local ZIP extra fields are not allowed.");

            position += 46 + nameLength;
        }

        File.WriteAllBytes(path, bytes);
    }

    private static void AuditZipHeaders(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var end = FindEndOfCentralDirectory(bytes);
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(end + 20, 2)) != 0)
            throw new InvalidOperationException("The sanitized archive has a comment.");

        var entries = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(end + 10, 2));
        var position = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16, 4)));
        for (var index = 0; index < entries; index++)
        {
            RequireSignature(bytes, position, 0x02014B50);
            if (bytes[position + 5] != 0)
                throw new InvalidOperationException("The sanitized archive exposes its creator OS.");
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 38, 4)) != 0)
                throw new InvalidOperationException("The sanitized archive exposes file attributes.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 30, 2)) != 0 ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 32, 2)) != 0)
            {
                throw new InvalidOperationException("The sanitized archive contains entry metadata.");
            }

            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 28, 2));
            position += 46 + nameLength;
        }
    }

    private static int FindEndOfCentralDirectory(byte[] bytes)
    {
        var minimum = Math.Max(0, bytes.Length - 65_557);
        for (var index = bytes.Length - 22; index >= minimum; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) == 0x06054B50)
                return index;
        }

        throw new InvalidOperationException("ZIP central directory was not found.");
    }

    private static void RequireSignature(byte[] bytes, int offset, uint expected)
    {
        if (offset < 0 || offset + 4 > bytes.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)) != expected)
        {
            throw new InvalidOperationException("Malformed ZIP structure.");
        }
    }

    [GeneratedRegex(@"(?i)(?:^|[^a-z0-9])/(?:home|root|users|tmp|var/folders|mnt|workspace|work)/[^\s\0\""']+")]
    private static partial Regex UnixPathRegex();

    [GeneratedRegex(@"(?i)\b[a-z]:\\(?:users|documents and settings|home)\\[^\s\0\""']+")]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"(?i)\b[a-z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+\b")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?i)(?:github_pat_|ghp_|glpat-|AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----)")]
    private static partial Regex SecretRegex();

    [GeneratedRegex(@"\+[0-9a-fA-F]{7,64}$")]
    private static partial Regex RevisionSuffixRegex();
}
