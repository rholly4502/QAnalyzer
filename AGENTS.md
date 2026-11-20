# AGENTS.md — QAnalyzer

Panduan arsitektur, batas keamanan, dan aturan kerja wajib bagi seluruh AI coding agent (termasuk scheduled improvement worker) yang berkontribusi di repository `QAnalyzer`.

---

## 1. Struktur & Batas Arsitektur

Repository ini menggunakan .NET 10 dan React/Vite dengan pemisahan peran yang ketat:

- `src/QAnalyzer.Core` — Engine analisis statis T-SQL, rules (`ISqlRule`), formatter, `MaxDopTransformer`, `HermesTuningAdvisor`, `TuningMemory`, dan abstraksi/implementasi DMV (`ISqlMetadataReader`, `SqlDmvReader`).
- `src/QAnalyzer.Wpf` — Aplikasi desktop lokal (Windows). Diperbolehkan membaca `appsettings.json`, menggunakan connection string lokal, serta menarik metadata/DMV dan execution plan langsung dari SQL Server.
- `src/QAnalyzer.Web` — Backend ASP.NET Core untuk demo/layanan web publik.
- `web/` — Frontend React + Vite (base path `/analyzer/`).
- `tests/QAnalyzer.Core.Tests` & `tests/QAnalyzer.Web.Tests` — Unit & service tests.

### Batas Keamanan Mutlak (Web vs WPF)
- **`QAnalyzer.Web` DILARANG KERAS** memiliki referensi ke `Microsoft.Data.SqlClient`, menerima/menyimpan connection string, atau membuka koneksi ke database mana pun.
- Fitur tuning di `QAnalyzer.Web` **hanya boleh** mengandalkan:
  1. Analisis statis dan transformasi teks T-SQL (`QAnalyzer.Core`).
  2. Parsing teks/file yang di-paste atau di-upload user (mis. XML `.sqlplan` atau skema DDL).
  3. API wrapper ke layanan AI (`HermesTuningAdvisor`) dengan kredensial/API key yang hanya dibaca dari environment variable di server (tidak pernah diekspos ke browser).
- `TuningMemory` di jalur Web tidak boleh saling menimpa antar pengguna (gunakan mode read-only atau isolasi per sesi).

---

## 2. Hard Guardrails (Dilarang Dilanggar)

1. **Anti-Over-Refactoring:** Jangan merombak struktur folder, mengganti arsitektur secara masif, melakukan mass-renaming, atau mengubah file yang tidak terkait langsung dengan task yang dikerjakan.
2. **Satu Task = Satu PR Kecil:** Jaga perubahan tetap fokus (maksimal ~300 baris perubahan logic per PR, di luar unit test dan lockfile). Pecah perubahan besar (mis. migrasi rule ke `ScriptDom`) menjadi beberapa PR kecil bertahap.
3. **Zero Warning/Test Cheating:**
   - Dilarang menambahkan `#pragma warning disable`, `NoWarn`, atau mematikan analyzer demi membuat build lolos.
   - Dilarang menghapus, men-`Skip`, atau melemahkan assertion unit test yang sudah ada. Perbaiki akar masalahnya.
4. **Kontrol Antrean PR (Khusus Worker Otomatis):**
   - Sebelum membuat branch/PR baru, cek jumlah PR yang masih `open`. Jika sudah ada **>= 3 PR terbuka**, hentikan eksekusi (no-op) agar tidak menumpuk antrean review.
   - Hindari menyentuh area/file yang sedang dimodifikasi oleh PR terbuka lain.
5. **Dilarang Push/Merge ke `main`:** Agent hanya boleh bekerja di branch terpisah (`agent/<tipe>-<slug-singkat>`) dan membuka Pull Request. Jangan pernah melakukan force-push atau auto-merge.
6. **No-Op Lebih Baik dari PR Kosong:** Jika tidak ada perbaikan nyata yang terukur, keluar tanpa membuat PR.

---

## 3. Standar Kode & Verifikasi Wajib

- Terapkan **TDD**: tulis atau perbarui unit test untuk membuktikan bug atau perilaku baru sebelum/bersamaan dengan implementasi.
- Untuk setiap method `async` baru, teruskan `CancellationToken` dengan benar.
- Untuk endpoint publik baru di `QAnalyzer.Web`, wajib sertakan validasi input (termasuk batas panjang payload), rate limiting, dan konfigurasi CORS yang aman.
- Jalankan seluruh verifikasi berikut sebelum membuka PR:
  - `dotnet build` — harus 0 error.
  - `dotnet test` — seluruh test di `QAnalyzer.Core.Tests` dan `QAnalyzer.Web.Tests` harus PASS.
  - Jika ada perubahan di folder `web/`: jalankan `npm run build` di dalam `web/` dan pastikan build sukses.
- Jika task yang diselesaikan tercatat di `TASKS.json`, perbarui statusnya di commit yang sama.

---

## 4. Prioritas Improvement (Urutan Kerja Worker)

Pilih **satu** task terkecil dengan dampak tertinggi dari prioritas teratas:

1. **P1 — Stabilitas, CI & Security Hardening:** Perbaikan build/test gagal, penambahan CI gate (`dotnet test` + build `web/`) sebelum deploy, rate limiting, validasi ukuran payload, dan hardening CORS di `QAnalyzer.Web`.
2. **P2 — Backlog Resmi:** Task yang masih pending di `TASKS.json` atau `docs/plans/`.
3. **P3 — Fitur & Engine Upgrade:**
   - Endpoint `POST /api/tune` di `QAnalyzer.Web` yang membungkus `HermesTuningAdvisor` secara aman.
   - Parser XML `.sqlplan` (`ExecutionPlanParser`) di `QAnalyzer.Core` untuk mendeteksi *MissingIndex*, *Key Lookup*, *Table Scan*, dan *SpillToTempDb*.
   - Migrasi bertahap rule statis & `MaxDopTransformer` dari Regex ke AST (`Microsoft.SqlServer.TransactSql.ScriptDom`), atau penambahan rule baru (mis. deteksi predicate non-SARGable).
   - Pengambilan XML Execution Plan lokal di `SqlDmvReader` / `QAnalyzer.Wpf` tanpa mengeksekusi mutasi data.
4. **P4 — Test Coverage & UX Polish:** Penambahan test edge case (komentar SQL, string literal, CTE, input malformed) dan penyempurnaan terarah di `web/src/App.jsx`.

---

## 5. Format Deskripsi Pull Request

Setiap PR yang dibuat agent wajib memuat:
- **Tujuan:** Masalah atau improvement yang diselesaikan (1–2 kalimat).
- **Perubahan Utama:** Daftar ringkas file dan perubahan logic.
- **Verifikasi:** Ringkasan hasil `dotnet test` (dan `npm run build` bila menyentuh `web/`).
- **Cek Keamanan:** Konfirmasi bahwa boundary `QAnalyzer.Web` (tanpa koneksi DB / connection string) tetap terjaga.
