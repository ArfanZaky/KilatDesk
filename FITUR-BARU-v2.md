# KilatDesk v2 — yang baru (untuk direview)

**File:** `dist\KilatDesk-v2.exe`. Versi 1 (`dist\KilatDesk.exe`) **tidak diubah** dan tetap bisa dipakai.
v2 boleh jalan **bersamaan** dengan v1 (instance terpisah). Hanya port host-nya yang harus beda,
dan v2 akan memberi tahu kalau port masih dipakai v1.

v1 dan v2 bisa saling terhubung. Fitur baru hanya aktif kalau **kedua PC memakai v2**. Kalau tersambung
ke host v1, tombol fitur yang tidak didukung otomatis disembunyikan.

---

## 1. Ganti-ganti monitor (fitur utama)

- **Tombol di toolbar viewer:** `Layar: 1★ 2 3 Semua`. Klik untuk pindah layar. ★ = monitor utama.
  Tombol yang menyala = monitor yang sedang tampil.
- **Shortcut:** `Ctrl+Alt+←` / `Ctrl+Alt+→` untuk pindah ke monitor sebelumnya/berikutnya. Bisa juga di layar penuh.
  Muncul info singkat di atas gambar, misalnya "Monitor 3 dari 3 (900x1440)".
- **Mode "Semua":** semua monitor digabung jadi satu gambar sesuai posisi aslinya. Gambar otomatis
  diperkecil ke maksimal ±5 megapiksel supaya tetap lancar. Klik mouse tetap tepat sasaran.
- **Monitor diputar (portrait) sekarang cepat.** Di v1, monitor 900x1440 yang diputar jatuh ke metode GDI:
  lambat, dan **jendela game/DirectX tampil hitam**. Di v2 pakai DXGI dengan rotasi sendiri, terbukti
  identik dengan layar aslinya.
- **Tidak ada lagi kedipan hitam saat pindah monitor.** Frame pertama DXGI ternyata selalu kosong, dan
  sekarang dilewati.
- **Monitor terakhir diingat** per alamat host. Saat connect lagi, langsung tampil monitor yang sama.
- **Hot-plug:** kalau monitor dicolok/dicabut, diputar, atau resolusinya berubah, daftar tombol
  diperbarui otomatis.

## 2. Transfer file (dua arah)

- **Viewer → host:** tombol **Kirim file**, atau **seret (drag & drop) file** ke jendela viewer.
- **Host → viewer:** tab *Izinkan dikontrol* → **Kirim file ke viewer**.
- File masuk ke `Downloads\KilatDesk` di PC penerima. Nama file diamankan, dan file tidak menimpa file
  yang sudah ada (diberi akhiran "(1)" dan seterusnya).
- Progres dan kecepatan tampil di status bar viewer atau di log host. Klik pesan "File diterima" untuk
  membuka foldernya.
- Data file dikirim dengan **prioritas rendah**, jadi gambar dan mouse tetap lancar selama transfer.
- Folder belum didukung; pilih file-filenya saja.

## 3. Chat

- **Viewer:** tombol **Chat**.
- **Host:** tombol **Chat** di strip merah, di tab host, atau di menu tray.
- Jendela chat muncul sendiri saat ada pesan, tanpa mencuri ketikan.

## 4. Suara dari PC host

- Suara yang sedang diputar di PC host (WASAPI loopback) dikirim ke viewer.
- Dikompres IMA-ADPCM sekitar **390 kbps**, jeda kecil (±100–150 ms).
- Aktif secara default; bisa dimatikan di **Opsi → Suara dari PC tujuan**.
- Kalau host tidak memutar suara apa pun, tidak ada data yang dikirim.

## 5. Kualitas otomatis

- **Kualitas → Otomatis** (default baru): bitrate diturunkan cepat kalau koneksi tersendat, lalu dinaikkan
  pelan-pelan saat lancar (rentang 0.8–20 Mbps).
- Bitrate yang sedang dipakai tampil di status bar, misalnya `auto 6.0 Mbps`.
- Preset tetap (2/5/8/15/40 Mbps) masih ada.

## 6. Layar privasi

- **Opsi → Layar privasi di PC tujuan:** layar PC host jadi hitam untuk orang yang ada di depannya,
  sementara viewer tetap melihat dan mengontrol desktop seperti biasa.
- Butuh Windows 10 versi 2004 ke atas di host.
- Mati otomatis saat sesi selesai.
- Darurat di host: klik kanan ikon tray → **Matikan layar privasi**.

## 7. Kontrol akses di host

- **Izinkan viewer mengontrol mouse && keyboard:** kalau dimatikan, viewer hanya bisa melihat, dan viewer
  diberi tahu ("kontrol dikunci host").
- **Minta persetujuan saya setiap ada yang terhubung:** walaupun password benar, orang di depan PC host
  harus klik **Izinkan**. Kalau tidak dijawab dalam 30 detik, otomatis ditolak.
  Jangan dicentang kalau PC host mau diakses tanpa ada orang.
- Tab host punya baris **Sesi aktif**: Kirim file ke viewer, Chat, Putuskan sesi, Folder file masuk.

## 8. Lainnya di viewer

- **Menu Tombol:** Task Manager (Ctrl+Shift+Esc), Win, Win+D, Win+E, Win+R, Alt+Tab, Alt+F4, Ctrl+Esc,
  Print Screen.
- **Screenshot:** menyimpan gambar layar PC tujuan ke `Pictures\KilatDesk`.
- **Opsi → Hanya lihat:** mouse dan keyboard tidak dikirim, aman untuk sekadar memantau.
- **Status bar bawah:** pesan, progres file, dan statistik `ping | fps diterima (fps tampil) | Mbps | encoder`.

---

## Hasil tes semalam (di PC ini, 3 monitor, GTX 1650)

Semua tes otomatis lulus:

- **Self-test** (`KilatDesk-v2.exe --selftest hasil.txt`):
  - Capture DXGI tiap monitor, termasuk portrait yang diputar (cocok 74 dB dengan layar asli), plus mode gabungan.
  - H.264 GPU/CPU dan JPEG.
  - Codec suara ADPCM (SNR 35 dB), rekam suara WASAPI 48 kHz, putar waveOut.
  - Logika bitrate otomatis.
  - TLS + password: salah password, mode sama, dan "ditolak pemilik" semuanya tertolak dengan benar.
  - Chat.
  - **File 3 MB dua arah identik byte-per-byte (~45 MB/s)**.
  - Sesi lengkap yang **pindah ke monitor 1 → 2 → 3 → Semua** dalam satu sesi.
- **Tes UI** (20 pengecekan): klik tombol monitor 2, 3, dan Semua di toolbar, chat dua arah, kirim file,
  layar privasi (terverifikasi tidak ikut ter-capture), kunci input, screenshot, dialog persetujuan,
  mode terbalik.
- **Tes input:** mouse mendarat tepat **0 px** dari target, baik di mode satu monitor maupun mode Semua.

Yang **belum** diuji: koneksi v2 lewat internet sungguhan antar 2 PC, dan v2 ↔ v1 secara langsung.
v1 sedang kamu pakai, jadi sengaja tidak aku ganggu.

## Checklist review cepat

1. Jalankan `KilatDesk-v2.exe` di PC host. Matikan dulu v1 (tray → Keluar) supaya port 7171 bebas,
   atau pakai port lain untuk v2.
2. Copy `KilatDesk-v2.exe` ke PC viewer, lalu connect seperti biasa.
3. Coba tombol `1★ 2 3 Semua` dan `Ctrl+Alt+←/→`. Cek monitor portrait: harus lancar dan jendela game tidak hitam.
4. Seret satu file ke jendela viewer, lalu cek folder `Downloads\KilatDesk` di host.
5. Putar video atau musik di host, dan suara harus terdengar di viewer.
6. Coba **Chat**, **Screenshot**, **Opsi → Layar privasi**.
7. Kalau mau autostart pakai v2: di v2, hilangkan lalu centang lagi "Aktif otomatis saat login",
   supaya path-nya pindah ke v2.

## Catatan

- Port default tetap **7171**. Port 7070 dipakai AnyDesk di PC ini.
- Mode tes (`--selftest`, `--uitest`, `--inputtest`) memakai folder sementara, tidak mengubah setelan
  atau folder Downloads/Pictures kamu.
- `--inputtest` menggerakkan mouse sebentar, lalu mengembalikannya ke posisi semula.
