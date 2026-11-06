# Menguji alur utama Recall

Panduan ini memeriksa pencarian berdasarkan **isi**, memakai folder uji terpisah.
Gunakan dokumen buatan sendiri untuk percobaan mengubah, memindahkan, menghapus,
atau merusak file. Jangan memakai dokumen asli untuk percobaan tersebut.

## 1. Siapkan file uji dan jalankan aplikasi

Dari terminal:

```bash
cd ~/Workspace/Projects/recall
dotnet build Recall.slnx
dotnet test

recall_test_dir=$(mktemp -d /tmp/recall-uji-XXXXXX)
printf 'Catatan percobaan: jejak surya7421 ditemukan di sini.\n' > "$recall_test_dir/opaque-a.txt"
cat > "$recall_test_dir/opaque-d.ipynb" <<'JSON'
{
  "nbformat": 4,
  "nbformat_minor": 5,
  "metadata": {},
  "cells": [{
    "cell_type": "code",
    "id": "recall-uji",
    "metadata": {},
    "source": ["calculate()"],
    "execution_count": null,
    "outputs": [{"output_type": "stream", "name": "stdout", "text": "jejak surya7421"}]
  }]
}
JSON
printf 'Folder uji: %s\n' "$recall_test_dir"

dotnet run --project src/Recall.Desktop/Recall.Desktop.csproj --no-build
```

Catat lokasi folder uji yang tercetak sebelum jendela terbuka. Notebook tersebut
hanya dibaca sebagai JSON; Recall tidak menjalankan kode di dalamnya.
Menjalankan dari terminal memakai build project terbaru. Jika ingin menguji lewat
launcher, tutup Recall lalu jalankan `bash scripts/install-launcher.sh` terlebih dahulu.

## 2. Cari isi yang tidak ada pada nama file

1. Klik **Index → Indexed Folders... → Add Folder...**.
2. Pilih folder uji yang lokasinya tercetak tadi, lalu klik **Scan Folder**.
3. Tunggu proses selesai. Di kotak Search, ketik `jejak surya7421` dan tekan Enter.
4. `opaque-a.txt` dan `opaque-d.ipynb` harus muncul. Kata pencarian tidak terdapat
   pada nama file; hasil ini membuktikan pencarian isi bekerja.
5. Klik masing-masing hasil. Panel **Content preview** harus memperlihatkan
   potongan isi dan highlight kata yang cocok.
6. Double-click hasil TXT untuk membukanya dengan aplikasi default. Dari klik
   kanan hasil, coba **Open Containing Folder** dan **Copy Path**. Tempel path
   ke editor teks dan pastikan menunjuk file uji yang dipilih.
7. Pilih salah satu hasil, buka **Index → Indexed Folders...**, lalu klik Close
   tanpa mengubah daftar folder. Query, halaman, hasil, dan pilihan file harus
   tetap sama. Jika hasil pencarianmu memiliki beberapa halaman, ulangi dari halaman kedua.
8. Cari `opaque`: kata itu harus di-highlight di kolom Name. Cari kembali
   `jejak surya7421`: highlight harus muncul di preview isi; nama `opaque-a.txt`
   dan `opaque-d.ipynb` tetap ditampilkan tanpa highlight yang tidak terkait.
9. Pilih beberapa kata di **Content preview** dengan mouse, lalu tekan **F5**
   tanpa mengubah file. Pilihan teks harus tetap ada dan bisa disalin dengan
   **Ctrl+C**. Jika preview dapat di-scroll, posisi scroll juga harus tetap.
   Coba lagi dengan pilihan teks dari arah kanan ke kiri. Pilihan teks harus
   dibersihkan jika snippet berubah atau file yang dipilih tidak lagi muncul.

Recall melewati symbolic link saat indexing. Untuk folder yang biasa dibuka
melalui link, pilih lokasi aslinya di Add Folder. Jika picker mengembalikan path
link, Recall harus menampilkan penjelasan dan tidak menambahkan path itu. Jika
picker otomatis memakai lokasi asli, folder dapat ditambahkan seperti biasa.
Path link yang sudah tersimpan dari versi sebelumnya dapat dihapus dari daftar
lalu diganti dengan lokasi aslinya; dokumen tidak perlu dipindahkan.

Di Indexed Folders, klik satu folder dan periksa status lokasinya di bawah daftar.
Folder normal harus berstatus ada; folder yang tidak tersedia atau path symlink
menampilkan penjelasan. Status ini memeriksa lokasi folder, sedangkan Scan Folder
tetap memeriksa akses ke file individual. Berpindahlah cepat antara dua folder:
hasil pemeriksaan folder lama tidak boleh mengganti status pilihan terbaru.
Untuk mengecek folder yang sama setelah drive disambungkan kembali, buka ulang
dialog lalu pilih folder tersebut.

Untuk menguji PDF dan DOCX, tambahkan dokumen uji `opaque-b.pdf` dan
`opaque-c.docx` yang berisi kalimat sama ke folder tersebut, lalu pilih
**Index → Scan Folders**. Gunakan PDF dengan text layer, bukan hasil scan gambar.
Keempat file harus ditemukan oleh query yang sama. Jika file sudah pernah
di-index versi lama, pilih foldernya di Indexed Folders lalu **Reindex Folder**.

## 3. Periksa perubahan file dan pencarian ulang

1. Di **Tools → Settings...**, catat pilihan Automatic Updates dan Scan on Startup.
   Matikan keduanya sementara, lalu Save, agar percobaan ini memeriksa scan manual.
2. Dengan editor/file manager, ubah isi TXT uji menjadi `hasil berubah9817 selesai`.
   Jika ada PDF uji, ubah namanya menjadi `renamed-b.pdf`.
3. Di Recall, ketik `hasil berubah9817`. Sebelum scan, isi baru belum ditemukan.
4. Pilih **Index → Scan Folders**. Setelah berhasil, Recall harus otomatis mencari
   query itu lagi dan menemukan TXT dengan snippet baru, tanpa menekan Find.
5. Jika ada PDF uji, cari `renamed b`: nama barunya harus ditemukan dan nama lama
   tidak lagi muncul. Penghapusan dokumen uji juga harus menghapus hasilnya setelah scan.
6. Tekan **F5** atau pilih **File → Refresh Results** untuk membaca ulang hasil
   dari index. Refresh ini tidak membaca perubahan isi file dari disk.
7. Saat scan/reindex berlangsung cukup lama untuk terlihat, tombol **Cancel Scan**
   harus muncul di status bar bawah. Klik tombol itu: label menjadi **Cancelling...**
   dan tombol dinonaktifkan selama pembatalan diproses. Setelah selesai, tombol
   hilang dan status pembatalan terlihat. Coba juga pembatalan melalui
   **Index → Cancel Scan**; tombol bawah harus mengikuti keadaan yang sama.
   Jika scan selesai sangat cepat, tombol bisa hanya terlihat sebentar.

## 4. Periksa penyimpanan dan laporan

1. Tutup Recall, buka lagi, lalu cari `hasil berubah9817`. TXT harus tetap
   ditemukan tanpa menambahkan folder atau menjalankan scan lagi.
2. Jalankan **Index → Scan Folders**, lalu buka **Index → Last Completed Scan...**. Periksa ringkasan dan
   coba **Copy Report**. Laporan hanya tersedia selama sesi tersebut.
3. Kembalikan pengaturan Automatic Updates dan Scan on Startup ke pilihan semula.
4. Setelah selesai, hapus **pilihan folder uji** melalui Indexed Folders → Remove
   Folder. Dokumen uji tetap ada; penghapusan pilihan tidak menghapus file asli.

Jika hasil tidak sesuai, catat query, nama/jenis file, status scan, dan informasi
versi dari **Help → About Recall...**. Laporan scan dan log lokal dapat memuat path
pribadi; periksa isinya sebelum membagikannya.

## 5. Review tampilan jendela utama

1. Periksa pada ukuran awal dan ketika diperkecil sampai minimum: input Search,
   Find, Clear, pagination, dan status bawah harus tetap dapat digunakan.
2. Arahkan mouse ke tombol serta baris hasil, lalu pilih salah satu hasil.
   Hover, selection, teks folder/tanggal/ukuran, dan highlight harus terbaca jelas.
3. Gunakan Ctrl+F dan Tab untuk memeriksa fokus keyboard. Enter menjalankan
   pencarian; shortcut hasil dan menu tetap mengikuti bagian sebelumnya.
4. Path pada preview memakai ukuran lebih kecil dan warna lebih redup daripada
   snippet. Coba pilih teks, Ctrl+C, resize panel dengan pemisahnya, dan refresh.
5. Periksa query tanpa hasil serta scan yang berjalan. Pada jendela pendek,
   pesan panjang dapat di-scroll di dalam area hasil. Tombol pagination dan
   Cancel Scan tetap berada pada tempatnya.

## 6. Review dialog Indexed Folders

1. Buka Index → Indexed Folders. Warna, tombol, dan selection harus konsisten
   dengan jendela utama. Jumlah folder terlihat pada header daftar.
2. Saat daftar kosong, Add Folder menjadi tombol utama. Setelah ada folder,
   Scan Folder menjadi tombol utama; tombol scan tetap perlu pilihan baris.
3. Tambah atau hapus pilihan folder uji dan pastikan jumlahnya ikut berubah.
   Coba hover, selection, serta Tab untuk berpindah antar kontrol.
4. Periksa path panjang: horizontal scroll dan tooltip menampilkan path lengkap.
5. Perkecil dialog, lalu periksa pesan/status yang panjang. Area informasi dapat
   di-scroll, sementara tombol Close dan aksi folder tetap dapat dijangkau.
   Setelah memilih folder lain atau menjalankan aksi, status/pesan baru kembali
   terlihat di bagian atas area informasi.
6. Jalankan Scan Folder dan Reindex Folder untuk memastikan keduanya menutup
   dialog dan memproses folder yang dipilih seperti pada langkah sebelumnya.
7. Kembali ke jendela utama dan periksa tombol Find/Clear serta fokus keyboard:
   style tombol bersama harus tetap konsisten pada kedua jendela.

## 7. Review dialog Settings

1. Buka Tools → Settings. Kedua pilihan indexing harus terbaca dalam bagian
   terpisah, dengan Save sebagai tombol utama dan Cancel di sampingnya.
2. Ubah salah satu pilihan lalu klik Cancel. Buka kembali Settings dan pastikan
   nilai sebelumnya tetap ada. Ubah lagi lalu klik Save; buka kembali dan
   pastikan pilihan baru tersimpan.
3. Perkecil dialog sampai ukuran minimum. Penjelasan kedua pilihan tetap dapat
   di-scroll, sementara tombol Save dan Cancel tetap terlihat.
4. Coba Tab, Space pada checkbox, Enter untuk Save, dan Escape untuk Cancel.
   Fokus serta status checkbox harus terlihat jelas.
5. Jika penyimpanan gagal atau berkas settings tidak dapat dibaca, pesan error
   harus tetap terlihat di atas tombol tanpa menutupi opsi atau aksi.

## 8. Review dialog Last Completed Scan

1. Selesaikan Scan Folders, lalu buka Index → Last Completed Scan. Ringkasan,
   daftar sample masalah, dan detail pilihan harus mudah dibedakan.
2. Pilih baris masalah yang berbeda. Path dan alasan lengkap muncul di kotak
   Selected issue; path panjang tetap dapat dibaca dengan scroll atau tooltip.
3. Klik Copy Report, tempel ke editor teks, dan cocokkan ringkasan serta sample
   masalahnya. Status penyalinan harus muncul di samping tombol.
4. Perkecil dialog hingga ukuran minimum. Daftar masalah bisa di-scroll dan
   tombol Copy Report serta Close tetap terlihat. Coba juga laporan tanpa
   detail masalah: pesan kosong harus tampak di tengah daftar.

## 9. Review dialog Search Tips

1. Buka Help → Search Tips atau tekan F1. Judul dan bagian Find a file,
   Search rules, If a file is missing, serta Keyboard shortcuts harus mudah
   dibedakan saat membaca.
2. Scroll dengan mouse dan keyboard sampai daftar shortcut paling bawah.
   Isi penjelasan tetap dapat dibaca; tidak ada teks terpotong di sisi kanan.
3. Perkecil dialog hingga ukuran minimum. Area bantuan tetap bisa di-scroll,
   sedangkan tombol Close selalu terlihat. Coba Enter dan Escape untuk menutup.
