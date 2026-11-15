# QAnalyzer — Static T-SQL Query Analyzer

Alat analisis statis untuk query T-SQL. Membaca script sebagai teks, menjalankan aturan risiko deterministik (tanpa mengeksekusi query), lalu menampilkan laporan diagnostic ber-level (Info/Warning/Critical) yang bisa diekspor ke Markdown/CSV.

- **Stack:** .NET 10, WPF (desktop), Core murni yang dapat dipakai ulang.
- **Keamanan:** analyzer TIDAK pernah mengeksekusi query pengguna. Fase "Cek di Server" hanya membaca metadata/plan cache DMV.

## Fitur

- **Analisis statis (Fase 1):** tanpa connection string, jalankan semua rule pada teks query. Temuan Critical langsung ditonjolkan.
- **Severity konteks-aware** — bukan "ditemukan → warning":
  - `SELECT *` → Warning (boros I/O; saran pilih kolom eksplisit).
  - `NOLOCK`/`WITH (NOLOCK)` di SELECT polos/read-only → **Info** (normal, lazim di production).
  - `NOLOCK` + `SELECT *` → Warning (boros + dirty read).
  - `NOLOCK` di UPDATE/DELETE/INSERT → Critical (tulis dgn read-uncommitted).
  - `UPDATE` tanpa `WHERE` → Critical (risiko update massal tanpa rollback).
  - `TRUNCATE` → Critical (destruktif).
- **Transform "Hemat resource" (MAXDOP 1):** menambahkan `OPTION (MAXDOP 1)` di akhir tiap SELECT terluar. Aman: tidak menyentuh query yang sudah punya klausa `OPTION` (hanya satu diperbolehkan, harus di paling akhir).
- **"Cek di Server" (Fase 2):** hanya baca plan cache/DMV — `sys.dm_exec_query_stats`, `sys.dm_exec_cached_plans`, `sys.dm_exec_sql_text`, missing index details. Tidak ada eksekusi query. Butuh SQL Server hidup (koneksi lokal WPF).
- **Tuning:** tombol di WPF mengumpulkan `{query, diagnostic, missing index, statistik plan cache}` lalu mengirim ke gateway Hermes (OpenAI-compatible) untuk saran tuning. Endpoint + API key dari `appsettings.json`; konteks lintas-sesi dari `tuning/memory.md`.
- **Auto-scan `appsettings.json`** di folder yang sama dengan script .sql (profile/connection string).
- **Ekspor laporan:** Markdown (`*.md`) dan CSV (`*.csv`).

## Struktur repo

```
src/QAnalyzer.Core/        # domain murni: model, ISqlRule, SqlAnalyzer
src/QAnalyzer.Wpf/         # UI desktop (MainWindow.xaml + App)
tests/QAnalyzer.Core.Tests/ # unit test Core (xunit)
docs/plans/                # rencana implementasi
TASKS.json                 # manifest progress (dibaca cron)
tuning/memory.md           # konteks tuning (PRIVATE — gitignored)
```

**Core** (`QAnalyzer.Core`) bebas dependensi WPF/infra sehingga bisa dipakai ulang (mis. web demo nanti).

## Prasyarat

- .NET SDK 10 (atau lebih baru).
- Untuk fitur "Cek di Server" / Tuning: SQL Server hidup + paket `Microsoft.Data.SqlClient` (sudah menjadi dependensi Core).

## Cara pakai

1. `cd src/QAnalyzer.Wpf && dotnet run -c Release`.
2. Tempel atau buka file `.sql` (tombol "Buka .sql…").
3. Pilih profile (General/Reporting) dan opsi "Hemat resource (MAXDOP 1)" bila perlu.
4. Klik **Analisis**.
5. Filter hasil per level (Critical/Warning/Info); klik untuk melihat detail & saran.
6. Ekspor laporan (MD/CSV) atau salin Markdown.
7. **Tuning** (opsional): butuh konfigurasi gateway — lihat di bawah.

## Konfigurasi `appsettings.json`

Analyzer membaca `appsettings.json` di folder yang sama dengan script .sql. Contoh lengkap: [`appsettings.example.json`](appsettings.example.json).

```json
{
  "ConnectionStrings": {
    "Default": "Server=YOUR_SERVER;Database=YOUR_DB;User Id=YOUR_LOGIN;Password=***;TrustServerCertificate=True"
  },
  "Analyzer": {
    "Profile": "Reporting",
    "EnableMaxDopTransform": false,
    "TuningEndpoint": "http://localhost:5000/v1",
    "TuningApiKey": "YOUR_HERMES_API_KEY",
    "RuleSeverityOverrides": {
      "NOLOCK": "Warning"
    }
  }
}
```

Catatan:

- **Jangan commit `appsettings.json`** berisi credential — sudah masuk `.gitignore`. Gunakan template `appsettings.example.json` (tanpa isi asli).
- `Profile: "Reporting"` mengecilkan `NOLOCK`/`SELECT *` ke Info.
- `RuleSeverityOverrides` meng-override level default per rule (mis. `NOLOCK → Warning`).

## Keamanan DMV & data prod

- Fitur baca DMV memakai login ber-privilege minim: cukup `GRANT VIEW SERVER STATE` (bukan admin/sa). Jangan beri hak lebih dari yang diperlukan.
- `sys.dm_exec_sql_text` dapat memuat teks query yang berisi data sensitif — waspadai saat memakai di lingkungan produksi.
- Konteks tuning di `tuning/memory.md` adalah **data produksi** dan TIDAK di-commit ke repo publik (folder `tuning/` gitignored). Sinkronisasi ke repo PRIVATE terpisah.

## Pengembangan (khusus kontributor / cron)

- Aturan wajib: file di repo ini diedit via heredoc/python, TDD (test gagal dulu), dan `dotnet test -c Release` hijau sebelum commit.
- Manifest progress: `TASKS.json` (status per task). Rencana: `docs/plans/2026-11-14-qanalyzer.md`.

## Lisensi

Proyek pribadi (repo publik).

## Web Demo (P3.4)

Bukti Core murni dipakai ulang di web. `src/QAnalyzer.Web/` = API minimal
(analize/health) yang memuat `QAnalyzer.Core`; `web/` = frontend React/Vite
**base-aware** (`base=/analyzer/`) per arsitektur deploy rhol-ai.tech (subpath
per app).

Jalankan lokal:
```bash
# 1) backend
dotnet run --project src/QAnalyzer.Web --urls http://localhost:5213
# 2) frontend dev (Vite proxy /analyzer/api -> :5213)
cd web && npm install && npm run dev
```

Produksi: `npm run build` (di `web/`) menulis hasil ke
`src/QAnalyzer.Web/wwwroot/` yang disajikan backend di `/analyzer/`.
