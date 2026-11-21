# B2cPusher (prototipe M1)

Prototipe untuk membuktikan alur di dokumen "Kebutuhan Fitur Ecolink: Push Order ke YonSuite B2C":
push `tradevouch/import` dari file JSON, ledger status per order, verifikasi lewat
`tradevouch/query`, dan koreksi tanpa mengubah file asli.

Tanpa paket NuGet dan tanpa Docker. Cukup .NET 10 SDK dan VS Code.

## Isi

| File | Fungsi |
| --- | --- |
| `Program.cs` | Perintah `mock`, `push`, `verify`, `report` |
| `Pusher.cs` | Alur per order: cek ledger, validasi, tulis `SENT_UNCONFIRMED`, kirim, baca hasil |
| `Ledger.cs` | Ledger berbasis file `data/ledger.json` (kunci shopcode + tid) |
| `YonSuiteClient.cs` | Token (cache), import, query |
| `Corrections.cs` | Koreksi dari `corrections.csv` |
| `MockServer.cs` | YonSuite palsu di `localhost:5055` |
| `appsettings.json` | Konfigurasi (default ke mock) |

Input yang diharapkan adalah file payload `tradevouch/import` yang sama seperti di Postman:
`{"data":[ {order}, {order}, ... ]}`. Setiap order dikirim satu per satu dengan bentuk
`{"data":[order]}`. Properti lain di root file ikut terkirim.

## Langkah tes dengan mock (sekitar 10 menit)

Buka dua terminal di folder ini.

**Terminal A**

```
dotnet run -- mock
```

**Terminal B**, jalankan berurutan:

### 1. Push pertama

```
dotnet run -- push --file samples/orders_mock.json
```

Hasil yang diharapkan (sekitar 35 detik, karena ada dua order yang sengaja dibuat lambat):

| tid | Hasil | Skenario |
| --- | --- | --- |
| SIM-MOCK-001, 002 | SUCCESS | Normal |
| SIM-MOCK-003-TIMEOUT | SENT_UNCONFIRMED | Order tercatat di YonSuite, tapi respons tidak sampai |
| SIM-MOCK-004-BADSTORE | FAILED_PUSH | `code` 200 tapi `导入失败1单` |
| SIM-MOCK-005 | FAILED_VALIDATION | `orgCode` kosong, tidak dikirim |
| SIM-MOCK-006-DROP | SENT_UNCONFIRMED | Request hilang, order TIDAK tercatat |
| SIM-MOCK-007 | FAILED_VALIDATION | Shopcode E0099 tidak ada di daftar toko |

Baris TOTAL harus KLOP (7).

### 2. Verifikasi

```
dotnet run -- verify
```

003 ditemukan lewat query lalu menjadi SUCCESS. 006 tidak ditemukan, jadi aman dikirim ulang.

### 3. Push ulang file yang sama (tes idempotensi)

```
dotnet run -- push --file samples/orders_mock.json
```

001–003 dilewati, 006 dikirim ulang dan menjadi SUCCESS, 004 tetap FAILED_PUSH.
Buka http://localhost:5055/mock/state: `duplicates` harus **0**.

### 4. Koreksi

Salin `samples/corrections_example.csv` menjadi `corrections.csv` di folder ini, lalu:

```
dotnet run -- push --file samples/orders_mock.json
```

005 (orgCode diisi) dan 007 (shopcode diganti E0004) menjadi SUCCESS. File JSON asli tidak berubah.

### 5. Data berubah setelah sukses

Ubah `payment` SIM-MOCK-001 di file JSON, lalu push lagi. Hasilnya `CHANGED_AFTER_PUSH`
dan order tidak dikirim. Kembalikan nilainya setelah selesai.

### 6. Simulasi crash

Hentikan semuanya. Hapus folder `data/` dan `corrections.csv`, jalankan ulang mock (atau
`POST /mock/reset`), lalu push. Saat sampai di 003-TIMEOUT (menunggu 15 detik), tekan **Ctrl+C**.
Buka `data/ledger.json`: 003 berstatus `SENT_UNCONFIRMED`. Push lagi: 003 dikonfirmasi lewat
query tanpa dikirim ulang, dan `/mock/state` tetap tanpa duplikat.

### 7. Laporan

```
dotnet run -- report
```

Hasilnya ringkasan per status dan per toko, serta `data/ledger_export.csv`.
Request dan respons tiap pengiriman ada di `data/logs/`.

## Ke tenant test YonSuite

1. Ubah `GatewayUrl` dan `TokenUrl` di `appsettings.json` sesuai environment Postman.
2. Isi secret lewat environment variable (jangan ditulis di file):
   ```
   $env:YS_APPKEY="..."; $env:YS_APPSECRET="..."      # PowerShell
   ```
   Cara paling cepat: tempel access token dari Postman ke `$env:YS_TOKEN="..."`.
3. Samakan `QueryBodyTemplate` dengan body `tradevouch/query` yang berhasil di Postman.
4. Hapus folder `data/` supaya ledger hasil tes mock tidak ikut terbawa.
5. Jalankan `--dry-run` dulu, lalu bandingkan isi `data/dryrun/` dengan payload Postman yang sudah sukses.
6. Push dengan file kecil (2–3 order) ke toko test (112026 atau S0001) dengan `tid` baru.
   Jangan pakai tid yang pernah dikirim lewat Postman, karena ledger belum tahu order itu sudah masuk.

## Batasan prototipe

- Ledger berupa file JSON, cocok untuk ratusan sampai beberapa ribu order. Di Ecolink, ledger harus tabel database dengan unique key.
- Belum ada mapping dari JSON mentah Jubelio. Input sudah berupa payload YonSuite.
- Koreksi hanya untuk field di level order, belum field bersarang.
- Rumus tanda tangan token mengikuti pola self-app auth YonBIP. Kalau berbeda dengan Postman, pakai `YS_TOKEN`.
