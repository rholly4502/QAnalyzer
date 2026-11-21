# Deploy QAnalyzer (rhol-ai.tech/analyzer)

Frontend (Vite) + API .NET dalam satu container. Route Caddy: `/analyzer/*` -> `qanalyzer-web:8080`.

## Prasyarat
1. Buat shared Docker network sekali: `docker network create shared_bridge`
2. `.env` dengan `SHARED_NETWORK=shared_bridge` (atau default).
3. Opsional di `.env`:
   - `ALLOWED_ORIGINS` — daftar origin CORS dipisah koma (mis. `https://rhol-ai.tech`). Kosong = hanya same-origin.
   - `TRUSTED_PROXIES` — IP/CIDR reverse proxy Caddy yang dipercaya untuk header `X-Forwarded-For` (mis. `172.16.0.0/12` sesuai subnet Docker `shared_bridge`) agar rate limiter membedakan IP asli pengguna. Kosong = abaikan header forwarded (aman dari spoofing).

## Build & run
```bash
docker compose up -d --build
docker compose ps
```

## Verifikasi (contoh curl — sudah diuji)
API:
```bash
# Health check
curl -s http://localhost:8080/api/health        # {"status":"ok"}

# Analisis SQL (report + transform MAXDOP bila diminta)
curl -s -X POST http://localhost:8080/api/analyze \
  -H 'Content-Type: application/json' \
  -d '{"sql":"SELECT * FROM dbo.Orders WHERE Status = 1;","enableMaxDopTransform":true}'
# -> diagnostics (mis. SELECT_STAR) + transformedSql: "... OPTION (MAXDOP 1);"

# SQL kosong ditolak
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8080/api/analyze \
  -X POST -H 'Content-Type: application/json' -d '{"sql":""}'   # 400
```

Static SPA (route `/analyzer/*`):
```bash
curl -s -o /dev/null -w 'index %{http_code} %{content_type}\n' \
  http://localhost:8080/analyzer/              # index 200 text/html
curl -s -o /dev/null -w 'asset %{http_code} %{content_type}\n' \
  http://localhost:8080/analyzer/assets/index-*.js   # 200 text/javascript
```
Catatan: `index.html` memakai base absolut `/analyzer/...` (Vite `base=/analyzer/`),
jadi SPA harus disajikan di subpath `/analyzer/` (via Caddy route), bukan di root.

## Catatan teknis
- Dockerfile multi-stage: node build Vite (`base=/analyzer/`) -> wwwroot, lalu dotnet publish.
- `Program.cs` memakai `MapStaticFiles(RequestPath="/analyzer")` + fallback `index.html` (hanya bila `wwwroot` ada).
- `web/vite.config.ts` outDir = `../src/QAnalyzer.Web/wwwroot`; proxy dev `/analyzer/api` -> `http://localhost:5213`.
- `docker-compose.yml` join network eksternal `shared_bridge` (dibuat oleh workflow deploy).
