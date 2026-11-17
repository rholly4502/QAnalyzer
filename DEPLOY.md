# Deploy QAnalyzer (rhol-ai.tech/analyzer)

Frontend (Vite) + API .NET dalam satu container. Route Caddy: `/analyzer/*` -> `qanalyzer-web:8080`.

## Prasyarat
1. Buat shared Docker network sekali: `docker network create shared_bridge`
2. `.env` dengan `SHARED_NETWORK=shared_bridge` (atau default).

## Build & run
```bash
docker compose up -d --build
docker compose ps
curl -s http://localhost:8080/api/health   # {"status":"ok"}
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8080/analyzer/
```

## Catatan
- Dockerfile multi-stage: node build Vite (`base=/analyzer/`) -> wwwroot, lalu dotnet publish.
- `Program.cs` memakai `MapStaticFiles(RequestPath="/analyzer")` + fallback `index.html`.
- `web/vite.config.ts` outDir = `../src/QAnalyzer.Web/wwwroot`.
