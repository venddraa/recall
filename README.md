# Recall

Aplikasi desktop lokal untuk Linux/Fedora: menemukan file ketika nama atau
lokasinya terlupa, tetapi sebagian isinya masih diingat.

## Status

Milestone 1: aplikasi Avalonia minimal berhasil dibangun dalam konfigurasi Debug
dan Release menggunakan SDK Fedora 10.0.112. Jendela Recall berhasil dibuka dari
Terminal Fedora berdasarkan verifikasi pengguna. Belum ada fitur pencarian atau
indexing.

## Fondasi

- C# dengan nullable reference types.
- .NET 10 LTS; SDK Fedora 10.0.112 dipin melalui `global.json`.
- Avalonia 12.1.3 dengan SimpleTheme.
- Satu project desktop; core dan unit test ditambahkan saat logic non-UI dibuat.

Jendela awal hanya menampilkan nama Recall. Desain UI utama akan dibahas pada
Milestone 2. Di GNOME/Wayland, backend desktop default berjalan melalui XWayland.

## Development

Prasyarat: .NET SDK sesuai `global.json` dan environment desktop Linux.
Di Fedora, library X11, ICE, SM, fontconfig, dan XWayland perlu tersedia.

Dari root repository:

```bash
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export AVALONIA_TELEMETRY_OPTOUT=1

dotnet --info
dotnet restore Recall.slnx
dotnet build Recall.slnx --no-restore
dotnet run --project src/Recall.Desktop/Recall.Desktop.csproj --no-build
```

Restore pertama membutuhkan internet untuk mengambil paket NuGet. Recall sendiri
tidak mempunyai kode akses jaringan, akun, analytics, atau telemetry. Aset paket
`Avalonia.BuildServices` dikecualikan agar build task telemetry tidak diimpor.

Belum ada unit test karena milestone ini hanya bootstrap desktop. Sebelum review
commit, verifikasi build Debug dan Release, lalu buka jendela, resize,
minimize/restore, dan tutup aplikasi secara normal. Unit test dimulai ketika ada
logic scanner/index/search yang perlu diuji.

## Struktur

```text
Recall.slnx
global.json
src/
    Recall.Desktop/
        Recall.Desktop.csproj
        Program.cs
        App.axaml
        App.axaml.cs
        MainWindow.axaml
        MainWindow.axaml.cs
```

## Scope dan workflow

V0.1 menggunakan SQLite FTS5 untuk pencarian nama dan isi file pada folder yang
dipilih pengguna. SQLite, ekstraksi dokumen, dan watcher belum ditambahkan.
Dokumen pengguna harus tetap read-only; database/config/log nantinya disimpan
dalam direktori data aplikasi sesuai XDG.

Setiap milestone ditinjau sebelum commit dan push. Pesan commit menggunakan
Bahasa Indonesia, misalnya `chore: menginisialisasi aplikasi desktop Recall`.
