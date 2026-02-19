# Fileshare

Simple authenticated file browser/download service:
- ASP.NET Core (`net8.0`) backend
- Static React frontend served from `wwwroot`
- HTTP Basic Auth on all routes
- Recursive file listing API + secure download API

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
- Mounted files: `./shared-files` -> `/data/files` (read-only)

## Reverse proxy note (`fileshare.scoob.dog`)

When proxying `fileshare.scoob.dog` to this app:
- Forward the `Authorization` header so app-level Basic Auth works end-to-end.
- Keep `X-Forwarded-For` and `X-Forwarded-Proto` headers for traceability and scheme awareness.
- If TLS terminates at the proxy, the upstream can remain plain HTTP to the container.
