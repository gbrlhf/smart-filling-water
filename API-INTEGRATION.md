# Integrasi MES API

Komponen `FillingMachineApi` terpasang pada GameObject yang sama dengan `MesinSimulasi` di scene MesinPengisiBotol.
Ubah Base Url, Mesin Id, Pengguna, dan Interval Telemetri dari Inspector sebelum Play. Default: `mesin-01`, `naya`, 2 detik. Sesi baru dibuat setiap Play.

- POST /api/telemetri: satu snapshot dalam array; GPIO, MCB, mode, sudut, hasil produksi dan tugas operator.
- POST /api/event: perubahan mode dan pesan mesin (termasuk START, E-stop dan MCB).
- POST /api/botol: satu rekaman ketika botol selesai jatuh ke wadah, dengan waktu masuk/pengisian/tutup, level dan status QC. Timestamp tutup hanya ada untuk tutup yang berhasil.
- GET /api/health: periksa API/database sebelum mengirim.

Antrean event dan botol disimpan ke `Application.persistentDataPath/filling-machine-api-outbox.json`. Snapshot telemetri offline diganti dengan snapshot terbaru. Pengiriman ulang memakai id_kirim yang sama. Jangan jalankan dua instance aplikasi dengan direktori persistentDataPath yang sama. Selesaikan pengiriman sebelum menutup Play untuk meminimalkan antrean tertunda; antrean tersimpan dikirim pada sesi berikutnya dengan identitas sesi aslinya.

HTTP error dipertahankan dalam antrean dan dicoba ulang dengan jeda sampai 60 detik. Lihat Status Koneksi, Antrean, serta Console. Tidak ada perubahan kendali START, E-stop, QC, animasi, dan pengemasan oleh klien API.

Pemeriksaan awal server saat implementasi: /api/health mengembalikan database terputus dengan kegagalan autentikasi MongoDB; /api/mesin mengembalikan HTTP 500. Perbaikan kredensial database harus dilakukan pada backend. Pengujian tulis langsung belum dapat diselesaikan.

## API key dan verifikasi

Semua POST memakai header `X-Api-Key`. Kunci dibaca dari environment `FILLING_MACHINE_API_KEY` (atau `API_KEY`), lalu fallback ke file lokal `Application.persistentDataPath/filling-machine-api-key.txt`. Isi file hanya nilai kunci. Jangan simpan nilai kunci di script, scene, atau Git. Restart Play setelah mengganti konfigurasi.

Pemilik proyek telah mengonfirmasi data masuk database setelah pengujian. Catatan error MongoDB di atas merupakan hasil pemeriksaan awal, bukan status server terkini.
