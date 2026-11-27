# KilatDesk v2 — Remote Desktop langsung via IP publik

Aplikasi remote desktop seperti AnyDesk, tapi **koneksinya langsung** dari PC ke PC lewat IP publik kamu —
tanpa server perantara (relay). Cukup satu file `KilatDesk-v2.exe` yang di-copy ke **2 komputer**.

**Baru di v2** (detail + hasil tes: [FITUR-BARU-v2.md](FITUR-BARU-v2.md)):
- ganti-ganti monitor lewat tombol `1★ 2 3 Semua` atau `Ctrl+Alt+←/→`, termasuk mode semua monitor;
- monitor portrait yang diputar sekarang cepat;
- transfer file dua arah (bisa drag & drop);
- chat;
- suara dari PC host;
- kualitas otomatis;
- layar privasi;
- kunci kontrol dan persetujuan koneksi;
- menu tombol khusus dan screenshot.

## Kenapa cepat

| Bagian | Yang dipakai |
|---|---|
| Koneksi | TCP langsung ke IP publik, tanpa relay, Nagle dimatikan |
| Tangkap layar | DXGI Desktop Duplication (GPU), hanya mengirim saat layar berubah |
| Kompresi | H.264 **di GPU** (NVIDIA NVENC / Intel QuickSync / AMD) otomatis, cadangan H.264 CPU, atau JPEG tajam |
| Latensi | Mode low-latency tanpa B-frame + kontrol aliran: frame yang telat dibuang di sumber, bukan diantrekan |
| Kursor | Digambar di PC viewer sendiri, jadi gerak mouse terasa instan |

Hasil ukur di PC pengembang (monitor 2560x1600, GTX 1650, uji loopback):
encode GPU ±9 ms, decode ±7–9 ms, 40–60 fps, ±6–9 Mbps saat layar terus bergerak.
Lewat internet tinggal ditambah ping jaringan kamu. **Belum dibandingkan langsung dengan AnyDesk** —
kecepatan nyata terutama ditentukan oleh **ping** dan **kecepatan upload PC yang dikontrol**.

## Kebutuhan

- Windows 10 (versi 1903 ke atas) atau Windows 11, di kedua komputer. Tidak perlu install apa-apa
  (.NET Framework 4.8 sudah bawaan Windows).
- Edisi Windows "N" perlu *Media Feature Pack* untuk H.264 (kalau tidak ada, otomatis pakai JPEG).

## Cara pakai — mode normal (PC yang dikontrol punya IP publik)

**Di PC yang mau dikontrol (Host):**
1. Jalankan `KilatDesk.exe` → tab **Izinkan dikontrol**.
2. Catat **Password** (atau klik **Acak** untuk buat baru). Port default **7171**.
3. Klik **Aktifkan**. Kalau muncul jendela *Windows Firewall*, pilih **Allow**
   (atau klik tombol **Izinkan di Firewall**).
4. Centang **Aktif otomatis saat login Windows** kalau mau PC ini selalu siap dikontrol.

**Di router PC host** — teruskan port (*port forwarding*):
- Protokol **TCP**, port luar **7171** → IP lokal PC host (lihat baris *Alamat kamu*), port **7171**.
- Atau coba tombol **Buka port di router (UPnP)** kalau router mendukung.

**Di PC kamu (Viewer):**
1. Jalankan `KilatDesk.exe` → tab **Kontrol PC lain**.
2. Isi alamat `IP_PUBLIK:7171` (contoh `203.0.113.10:7171`) dan password-nya → **Hubungkan**.

## Mode terbalik — kalau IP publiknya ada di PC kamu (viewer)

Berguna untuk membantu PC orang lain yang tidak punya IP publik:
1. Di PC kamu: tab **Kontrol PC lain** → isi **Password** → bagian *Mode terbalik* → port **7172** → **Mulai menunggu**.
   Teruskan port TCP 7172 di router kamu ke PC ini.
2. Di PC yang mau dikontrol: tab **Izinkan dikontrol** → isi password yang **sama** → kolom *Mode terbalik*
   isi `IP_PUBLIK_KAMU:7172` → **Hubungkan ke viewer**.

## Pastikan IP publiknya benar-benar publik (bukan CGNAT)

Klik **Cek IP publik** di tab host, lalu bandingkan dengan *WAN IP* di halaman router.
Kalau WAN IP router berawalan `100.64–100.127`, `10.`, `172.16–31.` atau `192.168.`, berarti ISP memakai **CGNAT**
dan port forwarding tidak akan bisa — minta IP publik ke ISP, atau pakai *mode terbalik* dari sisi yang punya IP publik.

## Tips biar makin lancar

- **Kualitas** (toolbar viewer): *Hemat 2 Mbps* untuk upload pelan, *Tinggi 8 Mbps* (default), *Maksimal 40 Mbps* untuk LAN.
- **Resolusi 75% / 50%** dan **30 fps** menghemat bandwidth di koneksi lambat.
- Angka di kanan toolbar: `ping | fps diterima (fps tampil) | Mbps | resolusi encoder`.
- **Mode gambar JPEG** membuat teks paling tajam tapi butuh bandwidth sangat besar — pakai di LAN saja.

## Shortcut & fitur

- **Ctrl+Alt+Enter** — layar penuh / keluar layar penuh.
- Saat jendela viewer aktif, **semua tombol** (Win, Alt+Tab, Alt+F4, dll.) dikirim ke PC tujuan.
- **Clipboard** teks tersinkron dua arah (bisa dimatikan di toolbar).
- **Ganti monitor:** tombol `Layar: 1★ 2 3 Semua` di toolbar, atau **Ctrl+Alt+← / →**. "Semua" = semua monitor
  digabung jadi satu gambar sesuai posisi aslinya.
- **Kirim file:** tombol *Kirim file* atau seret file ke jendela viewer. Dari host: *Kirim file ke viewer*.
  File masuk ke `Downloads\KilatDesk`.
- **Chat**, **Screenshot** (ke `Pictures\KilatDesk`), menu **Tombol** (Task Manager, Win+D, Alt+Tab, …).
- **Opsi:** suara dari PC tujuan, sinkron clipboard, hanya lihat, layar privasi di PC tujuan.
- Di PC host muncul **strip merah** "sedang dikontrol dari …" dengan tombol **Chat** dan **Putuskan**
  (strip ini tidak ikut terkirim ke viewer).
- Di host bisa diatur: **izinkan kontrol mouse & keyboard** (atau hanya boleh melihat), dan
  **minta persetujuan** setiap ada yang terhubung.

## Keamanan

- Semua data dienkripsi **TLS 1.2**. Password **tidak pernah dikirim**: kedua sisi saling membuktikan tahu password
  (PBKDF2 + HMAC yang terikat ke sertifikat TLS), jadi penyadap di tengah jalan tidak bisa menyamar.
- Sidik jari sertifikat host diingat; kalau berubah, viewer memberi peringatan sebelum lanjut.
- Salah password 5x dari satu IP → IP itu diblokir 15 menit.
- Password disimpan terenkripsi (Windows DPAPI). **Pakai password panjang** — port ini terbuka ke internet.

## Batasan saat ini

- Tidak bisa melihat/mengontrol **layar login, lock screen, dan jendela UAC** (KilatDesk berjalan sebagai aplikasi
  user, bukan service). Jangan kunci PC host; untuk mengontrol aplikasi yang "Run as administrator",
  jalankan KilatDesk di host sebagai Administrator.
- **Ctrl+Alt+Del** tidak bisa dikirim (dibatasi Windows). Transfer folder belum didukung (kirim file-filenya).
- Satu viewer per host dalam satu waktu.

## Build ulang dari source

Butuh .NET SDK 8 atau lebih baru: jalankan `build.ps1` → hasil `dist\KilatDesk-v2.exe`
(`dist\KilatDesk.exe` versi 1 tidak disentuh).

Uji komponen di sebuah PC (capture tiap monitor, encoder GPU/CPU, decoder, suara, enkripsi, transfer file,
streaming loopback dengan pindah-pindah monitor): `KilatDesk-v2.exe --selftest hasil.txt` lalu buka `hasil.txt`.

Pengaturan, log, dan sertifikat tersimpan di `%APPDATA%\KilatDesk`.
