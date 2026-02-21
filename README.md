# Fileshare

Simple authenticated file browser/download service:
- ASP.NET Core (`net8.0`) backend
- Static React frontend served from `wwwroot`
- HTTP Basic Auth on API/UI routes (public file downloads can be enabled per file)
- Recursive file listing API + secure download API
- Authenticated upload APIs + frontend upload widget

## Configuration

Environment variables:
- `FILESHARE_USERNAME` (default: `scoob`)
- `FILESHARE_PASSWORD` (default: `choom`)
- `FILESHARE_ROOT` (default: `/data/files`)

Visibility metadata:
- Per-file visibility is persisted at `FILESHARE_ROOT/.fileshare-visibility.json`
- Format: `{"publicFiles":["path/a.txt","path/b.txt"]}`

## Local run

```bash
dotnet run --project Fileshare/Fileshare.csproj
```

Then open `http://localhost:5000` or `http://localhost:5001` depending on launch profile.

## Docker Compose

```bash
docker compose up --build -d
```

Service:
- URL: `http://localhost:8080`
- Mounted files: `./shared-files` -> `/data/files` (read/write for uploads)

### Dedicated Cloudflare Tunnel (separate from slimelab.ai)

This stack includes its own `fileshare_tunnel` container.
Set token in a local `.env` file:

```bash
TUNNEL_TOKEN_FILESHARE=your-fileshare-tunnel-token
```

Then configure this tunnel in Cloudflare with only:
- `fileshare.scoob.dog` -> `http://fileshare:8080`

## Uploads

Web UI:
- Use the upload form on the main page to choose a file.
- Optional subfolder path (for example `reports/2026`) uploads into that directory under `FILESHARE_ROOT`.
- Uploads are sent in 4 MB chunks to stay under reverse-proxy request-size limits (for example Cloudflare).
- The UI shows upload state/progress and refreshes the file list after success.

API:
- Chunk endpoint: `POST /api/files/upload/chunk`
  - Content-Type: `multipart/form-data`
  - Required form fields: `chunk`, `uploadId`, `fileName`, `chunkIndex`, `totalChunks`
  - Optional form field: `path` (relative subdirectory under `FILESHARE_ROOT`)
  - Response JSON: `{ok:true,chunkIndex}`
- Complete endpoint: `POST /api/files/upload/complete`
  - Content-Type: `application/json`
  - Request JSON: `{uploadId,fileName,path,totalChunks}`
  - Response JSON: `{name,size,lastModifiedUtc,relativePath}`
- Chunk size: `8 MB` per request
- Overwrite behavior: existing destination files return `409 Conflict`
- Legacy single-request upload endpoint remains available: `POST /api/files/upload`

Security behavior:
- Rejects path traversal (`.` / `..`) and hidden path segments (segments starting with `.`)
- Rejects hidden upload names (file names starting with `.`)
- Normalizes path separators and ensures destination remains inside `FILESHARE_ROOT`

## File visibility

- Files are private by default.
- `GET /api/files` now includes `isPublic` for each file entry.
- `POST /api/files/visibility` (Basic Auth required) updates file visibility.
  - Request JSON: `{relativePath,isPublic}`
  - Response JSON: `{relativePath,isPublic}`
  - Validation rejects traversal and hidden path segments; only existing visible files are allowed.
- Unauthenticated access is only allowed for `GET /api/files/download/{**path}` when the target file is marked public.
- Directory-level visibility is not supported; visibility is file-by-file only.

Example:

```bash
curl -u scoob:choom \
  -F "chunk=@./example.part0" \
  -F "uploadId=demo123" \
  -F "fileName=example.txt" \
  -F "chunkIndex=0" \
  -F "totalChunks=1" \
  -F "path=docs/notes" \
  http://localhost:8080/api/files/upload/chunk

curl -u scoob:choom \
  -H "Content-Type: application/json" \
  -d '{"uploadId":"demo123","fileName":"example.txt","path":"docs/notes","totalChunks":1}' \
  http://localhost:8080/api/files/upload/complete
```

## Reverse proxy note (`fileshare.scoob.dog`)

When proxying `fileshare.scoob.dog` to this app:
- Forward the `Authorization` header so app-level Basic Auth works end-to-end.
- Keep `X-Forwarded-For` and `X-Forwarded-Proto` headers for traceability and scheme awareness.
- If TLS terminates at the proxy, the upstream can remain plain HTTP to the container.
