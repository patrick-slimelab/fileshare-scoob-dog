# Fileshare

Simple authenticated file browser/download service:
- ASP.NET Core (`net8.0`) backend
- Static React frontend served from `wwwroot`
- HTTP Basic Auth on all routes
- Recursive file listing API + secure download API
- Authenticated multipart upload API + frontend upload widget

## Configuration

Environment variables:
- `FILESHARE_USERNAME` (default: `scoob`)
- `FILESHARE_PASSWORD` (default: `choom`)
- `FILESHARE_ROOT` (default: `/data/files`)

## Local run

```bash
dotnet run --project Fileshare/Fileshare.csproj
```

Then open `http://localhost:5000` or `http://localhost:5001` depending on launch profile.

## Docker Compose

```bash
docker compose up --build
```

Service:
- URL: `http://localhost:8080`
- Mounted files: `./shared-files` -> `/data/files` (read/write for uploads)

## Uploads

Web UI:
- Use the upload form on the main page to choose a file.
- Optional subfolder path (for example `reports/2026`) uploads into that directory under `FILESHARE_ROOT`.
- The UI shows upload state and refreshes the file list after success.

API:
- Endpoint: `POST /api/files/upload`
- Content-Type: `multipart/form-data`
- Required form field: `file`
- Optional form field: `path` (relative subdirectory under `FILESHARE_ROOT`)
- Overwrite behavior: existing destination files return `409 Conflict`
- Response JSON: `{name,size,lastModifiedUtc,relativePath}`

Security behavior:
- Rejects path traversal (`.` / `..`) and hidden path segments (segments starting with `.`)
- Rejects hidden upload names (file names starting with `.`)
- Normalizes path separators and ensures destination remains inside `FILESHARE_ROOT`

Example:

```bash
curl -u scoob:choom \
  -F "file=@./example.txt" \
  -F "path=docs/notes" \
  http://localhost:8080/api/files/upload
```

## Reverse proxy note (`fileshare.scoob.dog`)

When proxying `fileshare.scoob.dog` to this app:
- Forward the `Authorization` header so app-level Basic Auth works end-to-end.
- Keep `X-Forwarded-For` and `X-Forwarded-Proto` headers for traceability and scheme awareness.
- If TLS terminates at the proxy, the upstream can remain plain HTTP to the container.
