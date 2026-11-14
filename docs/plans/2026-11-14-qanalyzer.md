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
