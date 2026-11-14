# QAnalyzer — Static T-SQL Query Analyzer

Rencana implementasi (source of truth untuk cron). Tiap subtask = unit kerja kecil dengan hasil yang bisa dicek & di-commit.

## Stack & Konvensi
- .NET 10, WPF (UI), Core murni (domain) + Tests (xunit).
- TDD: tulis test gagal → implementasi → hijau → commit.
- Core **wajib** bebas dependensi UI/infra biar bisa dipakai ulang (nanti: web).
- Semua file di `src/` & `tests/`; commit topik, jangan satu commit gede.
- WPF meniru pola MigrationCompare (MainWindow.xaml + Core terpisah), tapi **dibagusin**: logika 100% di Core, UI hanya presentasi.

## Fokus (MVP)
Analyzer statis yang **tidak mengeksekusi query** (aman, offline-s testable):
1. Parse/baca script T-SQL sebagai teks.
2. Jalankan aturan risiko deterministik (rule engine).
3. Keluarkan laporan diagnostic (level, rule, pesan, saran, baris/kolom).
4. Tampilkan di WPF + export laporan (MD/CSV).

## Opsi tambahan: "Hemat resource" (MAXDOP 1)
- Fitur transformasi: bila diaktifkan, analyzer menghasilkan versi query yang ditambah `OPTION (MAXDOP 1)` di akhir tiap SELECT (membatasi parallelism → hemat CPU/I/O, stabil di shared server).
- **Keselamatan (wajib):** jangan menambahkan jika query **sudah punya** klausa `OPTION (...)` (hanya boleh satu, dan harus di paling akhir). Jangan sentuh subquery—cukup SELECT terluar. Labeli hasil sebagai "versi hemat resource" agar user tahu itu modifikasi.
- Implementasi sebagai satu transform/pipeline terpisah (bukan rule diagnostic), dengan test tersendiri.

## Alur fitur (MVP)
1. Tempel / buka file .sql di WPF.
2. `SqlAnalyzer` jalankan semua `ISqlRule`.
3. Tampilkan daftar diagnostic + bermatan `SELECT *`, `NOLOCK`, `UPDATE` tanpa WHERE, dll.
4. Simpan laporan (markdown/CSV).

## Struktur
```
src/QAnalyzer.Core/        # domain murni: model, ISqlRule, SqlAnalyzer
src/QAnalyzer.Wpf/         # UI desktop (MainWindow.xaml + App)
tests/QAnalyzer.Core.Tests/ # unit test Core
docs/plans/2026-11-14-qanalyzer.md
TASKS.json                 # manifest progress (dibaca cron)
```

## Fase analisis (2 fase — keputusan desain)
- **Fase 1 — Static scan**: tanpa connection string. Jalankan semua rule dari teks; Critical langsung diberi peringatan menonjol.
- **Fase 2 — "Cek di Server" (opsional, butuh connection string)**: HANYA baca plan cache/DMV — tidak mengeksekusi query. Cari query yang sama/mirip di `sys.dm_exec_query_stats` + `sys.dm_exec_cached_plans` + `sys.dm_exec_sql_text`, tampilkan statistik historisnya (`total_elapsed_time`, `total_worker_time`, `total_logical_reads`, `execution_count`). Tidak ada UPDATE/DELETE/INSERT; murni baca.

## Aturan risiko (severity — keputusan desain, REVISI)
Lok: severity ditentukan **konteks**, bukan "ditemukan → warning". Setiap rule punya default + bisa di-override lewat policy/kel.

- `SELECT *` → Warning (boros I/O; saran: pilih kolom eksplisit).
- `NOLOCK`/`WITH (NOLOCK)` di SELECT polos (read-only/reporting) → **Info** (normal, lazim di script production). Bukan warning.
- `NOLOCK` **naik Warning** bila digabung `SELECT *` (boros + dirty read).
- `NOLOCK` **naik Critical** bila muncul di UPDATE/DELETE/INSERT (menulis dgn read-uncommitted = berbahaya).
- `UPDATE` tanpa `WHERE` (dan tanpa log table) → Critical (risiko update massal tanpa rollback).
- `TRUNCATE` → Critical (destruktif).
- Severity bisa disetel per-policy di UI/WPF (mis. mode "reporting" mengecilkan NOLOCK/`SELECT *` → Info).
- (Daftar ini akan berkembang di P1; tiap rule + test mengikuti level di atas.)

## Task (urutan eksekusi)
### P0 — Foundation
- [x] P0.1 Scaffold solusi (Core/Wpf/Tests) + build hijau
- [x] P0.2 Tulis rencana + TASKS.json
- [ ] P0.3 Rangka WPF: MainWindow + tampilan input/report (meniru MigrationCompare)
- [ ] P0.4 Commit awal + push repo publik

### P1 — Core (rule engine)
- [ ] P1.1 Rule `SelectStarRule` (deteksi `SELECT *`) + test
- [ ] P1.2 Rule `NoLockRule` (deteksi `NOLOCK`) + test
- [ ] P1.3 Rule `UpdateWithoutWhereRule` (update tanpa WHERE) + test
- [ ] P1.4 Report formatter (MD/CSV) + test
- [ ] P1.5 Level dashboard di WPF (hasil + filter)
- [ ] P1.6 Transform `MaxDopTransformer` (tambah OPTION MAXDOP 1, deteksi OPTION lama) + test

### P2 — WPF (UI)
- [ ] P2.1 Buka file .sql via dialog
- [ ] P2.2 editor teks script + tombol Analisis
- [ ] P2.3 list diagnostic + detail (saran)
- [ ] P2.4 export laporan MD/CSV

### P3 — Hardening & polish
- [ ] P3.1 Validasi input & error handling
- [ ] P3.2 README + dokumentasi
- [ ] P3.3 e2e manual + polish dark/light
- [ ] P3.4 (nanti) Web demo memakai Core yang sama
