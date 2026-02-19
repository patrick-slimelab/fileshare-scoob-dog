using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var basicAuthUsername = Environment.GetEnvironmentVariable("FILESHARE_USERNAME") ?? "scoob";
var basicAuthPassword = Environment.GetEnvironmentVariable("FILESHARE_PASSWORD") ?? "choom";
var fileRoot = Environment.GetEnvironmentVariable("FILESHARE_ROOT") ?? "/data/files";
var fileRootFullPath = Path.GetFullPath(fileRoot);

app.Use(async (context, next) =>
{
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
        .Select(file => new FileEntry(
            file.Name,
            file.Length,
            file.LastWriteTimeUtc,
            Path.GetRelativePath(fileRootFullPath, file.FullName).Replace('\\', '/')))
        .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    return Results.Ok(files);
});

app.MapGet("/api/files/download/{**path}", (string path) =>
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return Results.BadRequest("Path is required.");
    }

    var normalizedRelativePath = path.Replace('\\', '/');
    var segments = normalizedRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
    {
        return Results.BadRequest("Invalid path.");
    }

    var filePath = Path.GetFullPath(Path.Combine(fileRootFullPath, Path.Combine(segments)));
    if (!IsSubPathOf(fileRootFullPath, filePath))
    {
        return Results.BadRequest("Invalid path.");
    }

    var fileInfo = new FileInfo(filePath);
    if (!fileInfo.Exists || IsHidden(fileInfo))
    {
        return Results.NotFound();
    }

    if (HasHiddenDirectorySegment(fileInfo.Directory, fileRootFullPath))
    {
        return Results.NotFound();
    }

    return Results.File(fileInfo.FullName, "application/octet-stream", fileInfo.Name, enableRangeProcessing: true);
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

    return Results.Ok(new FileEntry(fileInfo.Name, fileInfo.Length, fileInfo.LastWriteTimeUtc, relativePath));
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

static bool PathsEqual(string left, string right)
{
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    return string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        comparison);
}

internal sealed record FileEntry(string Name, long Size, DateTime LastModifiedUtc, string RelativePath);
