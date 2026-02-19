using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var basicAuthUsername = Environment.GetEnvironmentVariable("FILESHARE_USERNAME") ?? "scoob";
var basicAuthPassword = Environment.GetEnvironmentVariable("FILESHARE_PASSWORD") ?? "choom";
var fileRoot = Environment.GetEnvironmentVariable("FILESHARE_ROOT") ?? "/data/files";
var fileRootFullPath = Path.GetFullPath(fileRoot);
var uploadTempRoot = Path.GetFullPath(Path.Combine(fileRootFullPath, ".uploads"));
var visibilityMetadataPath = Path.GetFullPath(Path.Combine(fileRootFullPath, ".fileshare-visibility.json"));
var visibilityStore = new FileVisibilityStore(fileRootFullPath, visibilityMetadataPath);

app.Use(async (context, next) =>
{
    if (CanBypassAuthForPublicDownload(context.Request, fileRootFullPath, visibilityStore))
    {
        await next();
        return;
    }

    if (!TryValidateBasicAuth(context.Request.Headers.Authorization, basicAuthUsername, basicAuthPassword))
    {
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"fileshare\"";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Unauthorized");
        return;
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/files", () =>
{
    if (!Directory.Exists(fileRootFullPath))
    {
        return Results.Ok(Array.Empty<FileEntry>());
    }

    var rootDirectory = new DirectoryInfo(fileRootFullPath);
    var files = EnumerateVisibleFiles(rootDirectory)
        .Select(file =>
        {
            var relativePath = Path.GetRelativePath(fileRootFullPath, file.FullName).Replace('\\', '/');
            return new FileEntry(
                file.Name,
                file.Length,
                file.LastWriteTimeUtc,
                relativePath,
                visibilityStore.IsPublic(relativePath));
        })
        .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    return Results.Ok(files);
});

app.MapGet("/api/files/download/{**path}", (string path) =>
{
    if (!TryNormalizeFileRelativePath(path, out _, out var segments, out var pathError))
    {
        return Results.BadRequest(pathError);
    }

    if (!TryResolveVisibleFile(fileRootFullPath, segments, out var fileInfo))
    {
        return Results.NotFound();
    }

    return Results.File(fileInfo.FullName, "application/octet-stream", fileInfo.Name, enableRangeProcessing: true);
});

app.MapPost("/api/files/visibility", async (HttpRequest request) =>
{
    VisibilityRequest? payload;
    try
    {
        payload = await request.ReadFromJsonAsync<VisibilityRequest>(request.HttpContext.RequestAborted);
    }
    catch (JsonException)
    {
        return Results.BadRequest("Invalid JSON payload.");
    }

    if (payload is null)
    {
        return Results.BadRequest("Body is required.");
    }

    if (!TryNormalizeFileRelativePath(payload.RelativePath, out _, out var segments, out var pathError))
    {
        return Results.BadRequest(pathError);
    }

    if (!TryResolveVisibleFile(fileRootFullPath, segments, out var fileInfo))
    {
        return Results.NotFound();
    }

    var relativePath = Path.GetRelativePath(fileRootFullPath, fileInfo.FullName).Replace('\\', '/');
    visibilityStore.SetVisibility(relativePath, payload.IsPublic);

    return Results.Ok(new VisibilityResponse(relativePath, payload.IsPublic));
});

app.MapPost("/api/files/upload", async (HttpRequest request) =>
{
    var contentType = request.ContentType ?? string.Empty;
    if (!request.HasFormContentType ||
        !contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest("Content type must be multipart/form-data.");
    }

    IFormCollection form;
    try
    {
        form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
    }
    catch (InvalidDataException)
    {
        return Results.BadRequest("Invalid form payload.");
    }

    var uploadedFile = form.Files.GetFile("file");
    if (uploadedFile is null)
    {
        return Results.BadRequest("Field 'file' is required.");
    }

    if (!TryNormalizeUploadFileName(uploadedFile.FileName, out var fileName, out var fileNameError))
    {
        return Results.BadRequest(fileNameError);
    }

    var requestedPath = form["path"].ToString();
    if (!TryNormalizeRelativePath(requestedPath, out var pathSegments, out var pathError))
    {
        return Results.BadRequest(pathError);
    }

    var relativeSegments = pathSegments.Append(fileName).ToArray();
    var destinationPath = Path.GetFullPath(Path.Combine(fileRootFullPath, Path.Combine(relativeSegments)));
    if (!IsSubPathOf(fileRootFullPath, destinationPath))
    {
        return Results.BadRequest("Invalid path.");
    }

    if (File.Exists(destinationPath))
    {
        return Results.Conflict("File already exists.");
    }

    var destinationDirectory = Path.GetDirectoryName(destinationPath) ?? fileRootFullPath;
    if (!IsSubPathOf(fileRootFullPath, destinationDirectory))
    {
        return Results.BadRequest("Invalid path.");
    }

    Directory.CreateDirectory(destinationDirectory);

    try
    {
        await using var outputStream = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        await using var inputStream = uploadedFile.OpenReadStream();
        await inputStream.CopyToAsync(outputStream, request.HttpContext.RequestAborted);
    }
    catch (IOException) when (File.Exists(destinationPath))
    {
        return Results.Conflict("File already exists.");
    }

    var fileInfo = new FileInfo(destinationPath);
    var relativePath = Path.GetRelativePath(fileRootFullPath, fileInfo.FullName).Replace('\\', '/');
    visibilityStore.SetVisibility(relativePath, isPublic: false);

    return Results.Ok(new FileEntry(fileInfo.Name, fileInfo.Length, fileInfo.LastWriteTimeUtc, relativePath, false));
});

app.MapPost("/api/files/upload/chunk", async (HttpRequest request) =>
{
    var contentType = request.ContentType ?? string.Empty;
    if (!request.HasFormContentType ||
        !contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest("Content type must be multipart/form-data.");
    }

    IFormCollection form;
    try
    {
        form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
    }
    catch (InvalidDataException)
    {
        return Results.BadRequest("Invalid form payload.");
    }
    catch (BadHttpRequestException)
    {
        return Results.BadRequest("Upload request was interrupted. Please retry.");
    }

    var chunkFile = form.Files.GetFile("chunk");
    if (chunkFile is null)
    {
        return Results.BadRequest("Field 'chunk' is required.");
    }

    var uploadIdRaw = form["uploadId"].ToString();
    if (!TryNormalizeUploadId(uploadIdRaw, out var uploadId, out var uploadIdError))
    {
        return Results.BadRequest(uploadIdError);
    }

    if (!TryNormalizeUploadFileName(form["fileName"].ToString(), out _, out var fileNameError))
    {
        return Results.BadRequest(fileNameError);
    }

    if (!TryNormalizeRelativePath(form["path"].ToString(), out _, out var pathError))
    {
        return Results.BadRequest(pathError);
    }

    if (!int.TryParse(form["chunkIndex"], out var chunkIndex) || chunkIndex < 0)
    {
        return Results.BadRequest("Field 'chunkIndex' must be a non-negative integer.");
    }

    if (!int.TryParse(form["totalChunks"], out var totalChunks) || totalChunks <= 0)
    {
        return Results.BadRequest("Field 'totalChunks' must be a positive integer.");
    }

    if (chunkIndex >= totalChunks)
    {
        return Results.BadRequest("Field 'chunkIndex' must be less than 'totalChunks'.");
    }

    var uploadDirectoryPath = Path.GetFullPath(Path.Combine(uploadTempRoot, uploadId));
    if (!IsSubPathOf(uploadTempRoot, uploadDirectoryPath))
    {
        return Results.BadRequest("Invalid upload id.");
    }

    Directory.CreateDirectory(uploadDirectoryPath);

    var chunkPath = Path.GetFullPath(Path.Combine(uploadDirectoryPath, GetChunkFileName(chunkIndex)));
    if (!IsSubPathOf(uploadDirectoryPath, chunkPath))
    {
        return Results.BadRequest("Invalid chunk path.");
    }

    await using (var outputStream = new FileStream(
                     chunkPath,
                     FileMode.Create,
                     FileAccess.Write,
                     FileShare.None,
                     bufferSize: 81920,
                     useAsync: true))
    await using (var inputStream = chunkFile.OpenReadStream())
    {
        await inputStream.CopyToAsync(outputStream, request.HttpContext.RequestAborted);
    }

    return Results.Ok(new { ok = true, chunkIndex });
});

app.MapPost("/api/files/upload/complete", async (HttpRequest request) =>
{
    CompleteUploadRequest? payload;
    try
    {
        payload = await request.ReadFromJsonAsync<CompleteUploadRequest>(request.HttpContext.RequestAborted);
    }
    catch (JsonException)
    {
        return Results.BadRequest("Invalid JSON payload.");
    }

    if (payload is null)
    {
        return Results.BadRequest("Body is required.");
    }

    if (!TryNormalizeUploadId(payload.UploadId, out var uploadId, out var uploadIdError))
    {
        return Results.BadRequest(uploadIdError);
    }

    if (!TryNormalizeUploadFileName(payload.FileName, out var fileName, out var fileNameError))
    {
        return Results.BadRequest(fileNameError);
    }

    if (!TryNormalizeRelativePath(payload.Path, out var pathSegments, out var pathError))
    {
        return Results.BadRequest(pathError);
    }

    if (payload.TotalChunks is null || payload.TotalChunks <= 0)
    {
        return Results.BadRequest("Field 'totalChunks' must be a positive integer.");
    }

    var totalChunks = payload.TotalChunks.Value;
    var uploadDirectoryPath = Path.GetFullPath(Path.Combine(uploadTempRoot, uploadId));
    if (!IsSubPathOf(uploadTempRoot, uploadDirectoryPath))
    {
        return Results.BadRequest("Invalid upload id.");
    }

    if (!Directory.Exists(uploadDirectoryPath))
    {
        return Results.BadRequest("Upload not found.");
    }

    var chunkPaths = new string[totalChunks];
    for (var chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
    {
        var chunkPath = Path.GetFullPath(Path.Combine(uploadDirectoryPath, GetChunkFileName(chunkIndex)));
        if (!IsSubPathOf(uploadDirectoryPath, chunkPath))
        {
            return Results.BadRequest("Invalid chunk path.");
        }

        if (!File.Exists(chunkPath))
        {
            return Results.BadRequest($"Missing chunk at index {chunkIndex}.");
        }

        chunkPaths[chunkIndex] = chunkPath;
    }

    var relativeSegments = pathSegments.Append(fileName).ToArray();
    var destinationPath = Path.GetFullPath(Path.Combine(fileRootFullPath, Path.Combine(relativeSegments)));
    if (!IsSubPathOf(fileRootFullPath, destinationPath))
    {
        return Results.BadRequest("Invalid path.");
    }

    if (File.Exists(destinationPath))
    {
        return Results.Conflict("File already exists.");
    }

    var destinationDirectory = Path.GetDirectoryName(destinationPath) ?? fileRootFullPath;
    if (!IsSubPathOf(fileRootFullPath, destinationDirectory))
    {
        return Results.BadRequest("Invalid path.");
    }

    Directory.CreateDirectory(destinationDirectory);

    try
    {
        await using var outputStream = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        foreach (var chunkPath in chunkPaths)
        {
            await using var chunkStream = new FileStream(
                chunkPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);
            await chunkStream.CopyToAsync(outputStream, request.HttpContext.RequestAborted);
        }
    }
    catch (IOException) when (File.Exists(destinationPath))
    {
        return Results.Conflict("File already exists.");
    }
    catch (OperationCanceledException)
    {
        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        throw;
    }
    catch (Exception)
    {
        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        throw;
    }

    try
    {
        Directory.Delete(uploadDirectoryPath, recursive: true);
    }
    catch (IOException)
    {
        // Ignore cleanup failures; upload is complete.
    }
    catch (UnauthorizedAccessException)
    {
        // Ignore cleanup failures; upload is complete.
    }

    var fileInfo = new FileInfo(destinationPath);
    var relativePath = Path.GetRelativePath(fileRootFullPath, fileInfo.FullName).Replace('\\', '/');
    visibilityStore.SetVisibility(relativePath, isPublic: false);

    return Results.Ok(new FileEntry(fileInfo.Name, fileInfo.Length, fileInfo.LastWriteTimeUtc, relativePath, false));
});

app.MapFallbackToFile("index.html");

app.Run();

static bool TryValidateBasicAuth(string? authorizationHeader, string expectedUsername, string expectedPassword)
{
    if (string.IsNullOrWhiteSpace(authorizationHeader) ||
        !authorizationHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var encodedCredentials = authorizationHeader["Basic ".Length..].Trim();
    if (string.IsNullOrEmpty(encodedCredentials))
    {
        return false;
    }

    byte[] decodedBytes;
    try
    {
        decodedBytes = Convert.FromBase64String(encodedCredentials);
    }
    catch (FormatException)
    {
        return false;
    }

    var decodedCredentials = Encoding.UTF8.GetString(decodedBytes);
    var separatorIndex = decodedCredentials.IndexOf(':');
    if (separatorIndex <= 0)
    {
        return false;
    }

    var username = decodedCredentials[..separatorIndex];
    var password = decodedCredentials[(separatorIndex + 1)..];

    return SecureEquals(username, expectedUsername) && SecureEquals(password, expectedPassword);
}

static bool SecureEquals(string left, string right)
{
    var leftBytes = Encoding.UTF8.GetBytes(left);
    var rightBytes = Encoding.UTF8.GetBytes(right);
    return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

static bool CanBypassAuthForPublicDownload(HttpRequest request, string rootPath, FileVisibilityStore visibilityStore)
{
    if (!HttpMethods.IsGet(request.Method) ||
        !request.Path.StartsWithSegments("/api/files/download", out var remainder))
    {
        return false;
    }

    var encodedPath = remainder.Value?.TrimStart('/');
    if (string.IsNullOrWhiteSpace(encodedPath))
    {
        return false;
    }

    string decodedPath;
    try
    {
        decodedPath = Uri.UnescapeDataString(encodedPath);
    }
    catch (UriFormatException)
    {
        return false;
    }

    if (!TryNormalizeFileRelativePath(decodedPath, out var normalizedRelativePath, out var segments, out _))
    {
        return false;
    }

    if (!TryResolveVisibleFile(rootPath, segments, out _))
    {
        return false;
    }

    return visibilityStore.IsPublic(normalizedRelativePath);
}

static IEnumerable<FileInfo> EnumerateVisibleFiles(DirectoryInfo directory)
{
    if (IsHidden(directory))
    {
        yield break;
    }

    IEnumerable<FileInfo> files;
    try
    {
        files = directory.EnumerateFiles();
    }
    catch (UnauthorizedAccessException)
    {
        yield break;
    }
    catch (DirectoryNotFoundException)
    {
        yield break;
    }
    catch (IOException)
    {
        yield break;
    }

    foreach (var file in files)
    {
        if (!IsHidden(file))
        {
            yield return file;
        }
    }

    IEnumerable<DirectoryInfo> subDirectories;
    try
    {
        subDirectories = directory.EnumerateDirectories();
    }
    catch (UnauthorizedAccessException)
    {
        yield break;
    }
    catch (DirectoryNotFoundException)
    {
        yield break;
    }
    catch (IOException)
    {
        yield break;
    }

    foreach (var subDirectory in subDirectories)
    {
        if (IsHidden(subDirectory))
        {
            continue;
        }

        foreach (var file in EnumerateVisibleFiles(subDirectory))
        {
            yield return file;
        }
    }
}

static bool IsHidden(FileSystemInfo entry)
{
    return entry.Name.StartsWith(".", StringComparison.Ordinal) ||
           entry.Attributes.HasFlag(FileAttributes.Hidden);
}

static bool HasHiddenDirectorySegment(DirectoryInfo? directory, string rootPath)
{
    while (directory is not null && !PathsEqual(directory.FullName, rootPath))
    {
        if (IsHidden(directory))
        {
            return true;
        }

        directory = directory.Parent;
    }

    return false;
}

static bool IsSubPathOf(string rootPath, string candidatePath)
{
    var normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var normalizedCandidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    if (string.Equals(normalizedRoot, normalizedCandidate, comparison))
    {
        return true;
    }

    return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
}

static bool TryNormalizeFileRelativePath(string? rawPath, out string normalizedRelativePath, out string[] segments, out string error)
{
    normalizedRelativePath = string.Empty;
    segments = Array.Empty<string>();
    error = string.Empty;

    if (string.IsNullOrWhiteSpace(rawPath))
    {
        error = "Path is required.";
        return false;
    }

    var normalizedPath = rawPath.Replace('\\', '/').Trim('/');
    if (string.IsNullOrWhiteSpace(normalizedPath))
    {
        error = "Path is required.";
        return false;
    }

    segments = normalizedPath
        .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
    {
        error = "Invalid path.";
        return false;
    }

    if (segments.Any(segment => segment.StartsWith(".", StringComparison.Ordinal)))
    {
        error = "Hidden paths are not allowed.";
        return false;
    }

    normalizedRelativePath = string.Join('/', segments);
    return true;
}

static bool TryResolveVisibleFile(string rootPath, IReadOnlyList<string> segments, out FileInfo fileInfo)
{
    fileInfo = null!;

    var filePath = Path.GetFullPath(Path.Combine(rootPath, Path.Combine(segments.ToArray())));
    if (!IsSubPathOf(rootPath, filePath))
    {
        return false;
    }

    var candidate = new FileInfo(filePath);
    if (!candidate.Exists || IsHidden(candidate))
    {
        return false;
    }

    if (HasHiddenDirectorySegment(candidate.Directory, rootPath))
    {
        return false;
    }

    fileInfo = candidate;
    return true;
}

static bool TryNormalizeRelativePath(string? rawPath, out string[] segments, out string error)
{
    error = string.Empty;
    if (string.IsNullOrWhiteSpace(rawPath))
    {
        segments = Array.Empty<string>();
        return true;
    }

    var normalizedPath = rawPath.Replace('\\', '/').Trim('/');
    if (string.IsNullOrWhiteSpace(normalizedPath))
    {
        segments = Array.Empty<string>();
        return true;
    }

    segments = normalizedPath
        .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
    {
        error = "Invalid path.";
        return false;
    }

    if (segments.Any(segment => segment.StartsWith(".", StringComparison.Ordinal)))
    {
        error = "Hidden paths are not allowed.";
        return false;
    }

    return true;
}

static bool TryNormalizeUploadFileName(string? rawFileName, out string fileName, out string error)
{
    error = string.Empty;
    fileName = string.Empty;

    if (string.IsNullOrWhiteSpace(rawFileName))
    {
        error = "Field 'file' is required.";
        return false;
    }

    var normalized = rawFileName.Replace('\\', '/').Trim('/');
    var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
    {
        error = "Invalid file name.";
        return false;
    }

    if (segments.Any(segment => segment.StartsWith(".", StringComparison.Ordinal)))
    {
        error = "Hidden files are not allowed.";
        return false;
    }

    fileName = segments[^1];
    if (string.IsNullOrWhiteSpace(fileName))
    {
        error = "Invalid file name.";
        return false;
    }

    return true;
}

static bool TryNormalizeUploadId(string? rawUploadId, out string uploadId, out string error)
{
    error = string.Empty;
    uploadId = string.Empty;

    if (string.IsNullOrWhiteSpace(rawUploadId))
    {
        error = "Field 'uploadId' is required.";
        return false;
    }

    var trimmed = rawUploadId.Trim();
    if (trimmed.Length > 128)
    {
        error = "Field 'uploadId' is too long.";
        return false;
    }

    if (trimmed.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
    {
        error = "Field 'uploadId' contains invalid characters.";
        return false;
    }

    uploadId = trimmed;
    return true;
}

static string GetChunkFileName(int chunkIndex)
{
    return $"chunk-{chunkIndex + 1:D6}.part";
}

static bool PathsEqual(string left, string right)
{
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    return string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        comparison);
}

internal sealed record FileEntry(string Name, long Size, DateTime LastModifiedUtc, string RelativePath, bool IsPublic);
internal sealed record CompleteUploadRequest(string? UploadId, string? FileName, string? Path, int? TotalChunks);
internal sealed record VisibilityRequest(string? RelativePath, bool IsPublic);
internal sealed record VisibilityResponse(string RelativePath, bool IsPublic);

internal sealed class FileVisibilityStore
{
    private readonly string _rootPath;
    private readonly string _metadataPath;
    private readonly object _sync = new();
    private HashSet<string> _publicFiles;

    public FileVisibilityStore(string rootPath, string metadataPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _metadataPath = Path.GetFullPath(metadataPath);
        _publicFiles = LoadFromDisk();
    }

    public bool IsPublic(string relativePath)
    {
        if (!TryNormalizeStoredPath(relativePath, out var normalizedRelativePath))
        {
            return false;
        }

        lock (_sync)
        {
            return _publicFiles.Contains(normalizedRelativePath);
        }
    }

    public void SetVisibility(string relativePath, bool isPublic)
    {
        if (!TryNormalizeStoredPath(relativePath, out var normalizedRelativePath))
        {
            return;
        }

        lock (_sync)
        {
            var changed = isPublic
                ? _publicFiles.Add(normalizedRelativePath)
                : _publicFiles.Remove(normalizedRelativePath);

            if (!changed)
            {
                return;
            }

            PersistToDisk();
        }
    }

    private HashSet<string> LoadFromDisk()
    {
        if (!File.Exists(_metadataPath))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        try
        {
            var payload = JsonSerializer.Deserialize<VisibilityMetadata>(File.ReadAllText(_metadataPath));
            var loaded = new HashSet<string>(StringComparer.Ordinal);

            foreach (var rawPath in payload?.PublicFiles ?? Array.Empty<string>())
            {
                if (TryNormalizeStoredPath(rawPath, out var normalizedPath))
                {
                    loaded.Add(normalizedPath);
                }
            }

            return loaded;
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        catch (IOException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        catch (UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private void PersistToDisk()
    {
        Directory.CreateDirectory(_rootPath);

        var metadata = new VisibilityMetadata
        {
            PublicFiles = _publicFiles
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray()
        };

        var tempPath = _metadataPath + ".tmp";
        var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tempPath, json, Encoding.UTF8);
        File.Move(tempPath, _metadataPath, overwrite: true);
    }

    private static bool TryNormalizeStoredPath(string? rawPath, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        var canonicalPath = rawPath.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(canonicalPath))
        {
            return false;
        }

        var segments = canonicalPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0 ||
            segments.Any(segment => segment is "." or ".." || segment.StartsWith(".", StringComparison.Ordinal)))
        {
            return false;
        }

        normalizedPath = string.Join('/', segments);
        return true;
    }
}

internal sealed class VisibilityMetadata
{
    [JsonPropertyName("publicFiles")]
    public string[] PublicFiles { get; init; } = Array.Empty<string>();
}
