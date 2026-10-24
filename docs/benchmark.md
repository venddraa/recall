# Benchmark lokal Recall

Alat ini mengukur jalur core yang dipakai Recall: scanner, ekstraksi teks,
penyimpanan SQLite/FTS5, pemakaian kembali teks yang belum berubah, dan pencarian.
Tidak perlu menambahkan project ke solution atau memasang dependency baru.
Alat memakai file-based app .NET 10 dan mereferensikan `Recall.Core` yang sudah ada.

## Menjalankan

Dari root repository, gunakan SDK yang dipilih `global.json`:

```bash
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet run --file scripts/benchmark.cs --configuration Release -- --files 10000 --iterations 25
```

Untuk pemeriksaan singkat, ganti jumlah file menjadi `1000` dan iterasi menjadi `5`.
Default: 10.000 file, 25 iterasi. Batas argumen: 1–100.000 file dan 5–100 iterasi.
Gunakan `-- --help` untuk melihat penggunaan. Setelah build pertama, `--no-build`
bisa ditambahkan sebelum `--` jika source benchmark dan `Recall.Core` belum berubah.
Build pertama dapat menjalankan restore dependency Recall yang sudah ada.

Corpus berupa TXT UTF-8 sintetis, sekitar 1,2 KiB per file, dipecah ke subfolder
berisi paling banyak 100 file. Setiap file memiliki penanda unik. Satu dari
20 file memuat `clear-sky index`; satu dari 10 memiliki nama berawalan `BAPP`.
Semua file berisi kata `synthetic` untuk mengukur query dengan banyak kecocokan.

Alat membuat corpus dan database baru di direktori sementara sistem (`/tmp` pada
Fedora secara default). Tidak membaca konfigurasi atau database aplikasi dan
tidak mengindeks dokumen pengguna. Data sementara milik benchmark dihapus setelah
selesai atau dibatalkan dengan Ctrl+C. Jika pembersihan gagal, alat menampilkan
lokasinya. Jangan menjalankan benchmark dengan `sudo`.

## Yang diukur dan diperiksa

- Scan awal dan scan ulang tanpa perubahan, dengan scanner Recall yang sebenarnya.
- Index awal: ekstraksi teks, transaksi SQLite, dan penulisan indeks FTS5.
- Index ulang tanpa perubahan, disertai pemeriksaan bahwa ekstraktor tidak dipanggil ulang.
- Query isi langka, isi umum, prefix nama, halaman kedua, filter subfolder, dan query tanpa hasil.
- Jumlah hasil, tidak adanya duplikat dalam satu halaman, serta highlight snippet isi.
- Pencarian tetap menemukan isi dari database setelah folder corpus uji dipindahkan.

Waktu pembuatan corpus dan inisialisasi schema tidak masuk ke waktu indexing.
Setiap query dipanaskan sekali, lalu diukur sesuai jumlah iterasi. p50 dan p95
memakai metode nearest rank. Pengukuran query meliputi pemanggilan async core,
koneksi SQLite, filter folder, ranking, dan pembuatan snippet. Validasi hasil
dilakukan setelah timer berhenti. Batas hasil 101 baris mengikuti kebutuhan UI
untuk menampilkan 100 hasil dan mendeteksi apakah halaman berikutnya tersedia.

Exit code: `0` berhasil, `1` kegagalan benchmark/validasi, `2` argumen tidak valid,
dan `130` pembatalan dengan Ctrl+C.

## Baseline awal

Pengukuran pada 24 Oktober 2026, Fedora 44 x64, .NET SDK 10.0.112,
runtime 10.0.12, build Release, 16 logical processors. Corpus 10.000 file
dengan total 11,21 MiB, 25 iterasi per query.

| Tahap | Waktu |
| --- | ---: |
| Scan awal | 53,87 ms |
| Index awal | 1.380,41 ms |
| Scan ulang tanpa perubahan | 23,78 ms |
| Index ulang tanpa perubahan | 221,14 ms |

| Query | p50 | p95 | Baris dikembalikan |
| --- | ---: | ---: | ---: |
| Isi langka | 0,44 ms | 0,60 ms | 1 |
| Isi `clear sky index` | 3,20 ms | 3,50 ms | 101 |
| Prefix nama `BAP` | 4,33 ms | 4,76 ms | 101 |
| Isi umum, halaman 1 | 14,02 ms | 14,91 ms | 101 |
| Isi umum, halaman 2 | 14,56 ms | 14,83 ms | 101 |
| Isi pada satu subfolder | 3,71 ms | 4,00 ms | 100 |
| Tanpa hasil | 0,34 ms | 0,38 ms | 0 |

Ukuran database, termasuk WAL/SHM jika ada: 30,77 MiB. Seluruh validasi lulus.

Angka ini satu baseline sintetis dengan cache hangat, bukan target performa atau
benchmark koleksi dokumen pribadi. Tidak mengukur render Avalonia, cold-cache,
PDF/DOCX/IPYNB, filesystem watcher, ataupun koleksi 750 ribu file. Bandingkan
perubahan menggunakan jumlah file, iterasi, mesin, build Release, dan beban mesin
yang sebanding. Untuk perubahan performa berikutnya, simpan output sebelum dan
sesudah; jangan memakai batas waktu yang rapuh sebagai unit test.
