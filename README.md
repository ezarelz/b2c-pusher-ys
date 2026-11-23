# B2cPusher

Prototipe untuk membuktikan alur di dokumen **"Informasi Fitur Ecolink: Push Order ke YonSuite B2C"**:
raw JSON order dari Ecolink diubah menjadi payload `tradevouch/import`, dikirim ke YonSuite B2C
dengan ledger anti-dobel, lalu diverifikasi lewat `tradevouch/query`.

Prototipe ini adalah **contoh acuan** untuk tim Ecolink, bukan aplikasi production. Logika dan
aturannya yang dipindahkan ke Ecolink; ledger file JSON di sini di Ecolink menjadi tabel database.

Tanpa paket NuGet dan tanpa Docker. Cukup .NET 10 SDK dan VS Code.

## Status per 21 Nov 2026

Alur penuh sudah terbukti di **tenant NAC**:

raw JSON Ecolink → transform → push → Original Transaction Document → Order Approval → Shipped → Daily Sales Report + A/R

| Order uji | Channel | Kasus | A/R |
| --- | --- | --- | --- |
| TEST-SP-261119P1W4XK7W | Shopee E0001 | bundle, voucher penjual, `grand_total` sudah dipotong biaya Shopee | 39.048 |
| TEST-LZ-2790936857856268-93836 | Lazada E0004 | 2 bundle (5 komponen) | 118.400 |
| TEST-TT-587076589608732602-93837 | TikTok Shop E0006 | COD, 2 baris material sama | 57.800 |

Push ulang file yang sama di tenant tidak membuat order dobel. Skenario timeout, request hilang,
crash, dan koreksi sudah lolos di mock.

## Isi repo

| File | Fungsi |
| --- | --- |
| `Program.cs` | Perintah: `mock`, `token`, `query`, `transform`, `push`, `verify`, `report` |
| `Transformer.cs` | Raw JSON Jubelio (dari Ecolink) → payload `externalData` + `partParam` |
| `Pusher.cs` | Alur per order: cek ledger, validasi, tulis `SENT_UNCONFIRMED`, kirim, baca hasil |
| `Ledger.cs` | Ledger file `data/ledger.json` (kunci shopcode + tid) |
| `YonSuiteClient.cs` | Token (signature HmacSHA256, cache, minta ulang otomatis), import, query |
| `MessageText.cs` | Terjemahan pesan YonSuite (Mandarin) untuk tampilan; teks asli disimpan di log |
| `Corrections.cs` | Koreksi dari `corrections.csv` tanpa mengubah file asli |
| `MockServer.cs` | YonSuite palsu di `localhost:5055` untuk uji lokal |
| `appsettings.json` | Konfigurasi: endpoint, template query, mapping toko dan gudang |
| `set-env.example.ps1` | Template koneksi ke tenant (salin jadi `set-env.local.ps1`) |
| `samples/` | Contoh payload mock, contoh koreksi, folder `raw/` untuk raw JSON (tidak di-commit) |

## Perintah

| Perintah | Fungsi |
| --- | --- |
| `dotnet run -- mock` | Jalankan YonSuite palsu di `localhost:5055` |
| `dotnet run -- token` | Tes ambil token (seperti step 1 di Postman); token ditampilkan tersamar |
| `dotnet run -- query --shop E0001 --tid <tid>` | Cek satu order di YonSuite dan tampilkan respons mentah |
| `dotnet run -- transform --in <folder/file> --out <file.json> --tid-prefix TEST-` | Raw JSON Ecolink → payload. Hanya order COMPLETED. |
| `dotnet run -- push --file <file.json> [--dry-run]` | Push semua order di file. `--dry-run` hanya validasi dan menulis payload. |
| `dotnet run -- push --file <file.json> --only <tid1,tid2> [--force]` | Hanya order terpilih. `--force` = kirim ulang walaupun ledger sudah SUCCESS (wajib bersama `--only`). |
| `dotnet run -- verify [--sample N]` | Query semua `SENT_UNCONFIRMED`, plus N sampel SUCCESS |
| `dotnet run -- report` | Ringkasan per status dan toko, ekspor `data/ledger_export.csv` |

## Menghubungkan ke tenant YonSuite

1. Salin `set-env.example.ps1` menjadi `set-env.local.ps1`, lalu isi dari environment Postman:

   | Postman | Environment variable |
   | --- | --- |
   | `{{tokenUrl}}` | `YS_TOKEN_URL` (tanpa `/open-auth/...`, ditambahkan otomatis) |
   | `{{gatewayUrl}}` | `YS_GATEWAY` |
   | `{{appKey}}` | `YS_APPKEY` |
   | `{{appSecret}}` | `YS_APPSECRET` |

   `tenantId`, `access_token`, dan variabel token lain tidak diperlukan. Biarkan `YS_TOKEN` kosong
   supaya token diminta dan diperbarui otomatis.
2. Jalankan di **PowerShell** (variabel hanya berlaku di terminal itu):
   ```
   . .\set-env.local.ps1
   dotnet run -- token
   ```
   Baris `Target:` harus menampilkan gateway tenant, bukan `localhost:5055`.
3. Cek query dengan order yang pasti sudah ada:
   ```
   dotnet run -- query --shop E0001 --tid <tid yang sudah ada>
   ```
4. Hapus `data/ledger.json` sisa tes mock, lalu transform, dry-run, push, verify:
   ```
   dotnet run -- transform --in samples/raw --out data/payload/tenant.json --tid-prefix TEST-
   dotnet run -- push --file data/payload/tenant.json --dry-run
   dotnet run -- push --file data/payload/tenant.json
   dotnet run -- verify --sample 3
   ```
5. Pastikan stok material tersedia di WH001, dan tanggal aktif Retail Store lebih awal dari
   tanggal transaksi order (lihat Temuan teknis).

Jangan tulis appKey/appSecret di `appsettings.json`; file itu ikut ter-commit.

## Aturan transform (keputusan 21 Nov 2026)

| Field YonSuite | Sumber Jubelio |
| --- | --- |
| `tid`, `storeorder_id` | prefix + `salesorder_no` (production: tanpa prefix) |
| `shopcode`, `store_id` | `store_id` lewat `StoreMap`, dicek silang dengan `source` (channel) dan kata "Nerman" di `store_name` (brand) |
| `status` | selalu "Shipped" (API tetap membuat To Ship; YonSuite melanjutkan otomatis) |
| `created`, `consign_time` | `transaction_date` dalam WIB (mirroring Jubelio) |
| `pay_time` | `payment_date` dalam WIB, atau `transaction_date` kalau kosong |
| `seller_memo` | `Last updated <tanggal COMPLETED> WIB|Jubelio <ID>|store_id <id>|<metode bayar>` |
| `type` | `is_cod` → COD / Online Payment |
| `buyer_nick` / `receiver_name` | `customer_name` / `shipping_full_name` |
| `receiver_state/city/district` | ketiganya `shipping_city` |
| `receiver_mobile` | nomor utuh: angka saja; nomor tersamar: placeholder `081234567890` yang ujungnya diganti digit terakhir yang terlihat (maks. 4) |
| `post_fee` | `shipping_cost` |
| Per baris: `itemcode`, `num`, `price`, `total_fee` | `item_code` (bundle `A+B` apa adanya), `qty`, `price`, `price` × `qty` |
| Per baris: `payment` | `amount` item; selisih dengan `grand_total` dibagi proporsional, sehingga total `payment` + `post_fee` = `grand_total` |
| Per baris: `discount_fee` / `dealPrice` | `total_fee` − `payment` / `payment` ÷ `num` |
| Per baris: `sub_order_tax_fee` | 0 (PPN dihitung YonSuite dari material) |
| Per baris: `warehouseCode` | `loc_name` lewat `WarehouseMap` (fase uji: semua → WH001) |
| Per baris: `logistics_company`, `invoice_no` | `courier`, `tracking_no` |
| `partParam.orgCode` | `OrgCode` di config ("ecolink") |

Query: body `{"partParam":{"pageIndex":1,"pageSize":10,"shopcode":"…","tid":"…"}}`.
`pageIndex`/`pageSize` wajib di dalam `partParam`. Respons dengan code selain 200 dihitung
**query gagal**, bukan "tidak ditemukan", supaya order tidak terkirim dobel.

## Temuan teknis di tenant

| Temuan | Catatan |
| --- | --- |
| YonSuite membaca waktu sebagai UTC+8 | Tampil 1 jam lebih awal dari WIB. Diterima sementara. |
| Tanggal aktif Retail Store | Order yang lebih awal dari tanggal aktif toko berhenti di Original Transaction Document. Mundurkan tanggal aktif, lalu transfer ulang. |
| Express Company tidak terbaca | Tetap "test EC". Resi masuk ke Primary Express Waybill No. Kandidat field: `shipExpressCode`. |
| Province/City/District kosong | Master wilayah tenant masih China. |
| `source` TikTok Shop | "Shop \| Tokopedia" |
| Toko Nerman | Belum ada order COMPLETED di Ecolink (sampai 21 Nov) |

## Uji lokal dengan mock

Dua terminal: `dotnet run -- mock` di terminal A, lalu di terminal B:

```
dotnet run -- push --file samples/orders_mock.json
dotnet run -- verify
dotnet run -- push --file samples/orders_mock.json
```

| tid | Hasil push pertama | Skenario |
| --- | --- | --- |
| SIM-MOCK-001, 002 | SUCCESS | Normal |
| SIM-MOCK-003-TIMEOUT | SENT_UNCONFIRMED → verify → SUCCESS | Order tercatat, respons tidak sampai |
| SIM-MOCK-004-BADSTORE | FAILED_PUSH | `code` 200 tapi gagal 1 order |
| SIM-MOCK-005 | FAILED_VALIDATION | `orgCode` kosong |
| SIM-MOCK-006-DROP | SENT_UNCONFIRMED → push ulang → SUCCESS | Request hilang |
| SIM-MOCK-007 | FAILED_VALIDATION | Shopcode E0099 tidak ada |

Cek `http://localhost:5055/mock/state`: `duplicates` harus **0**. Tes koreksi: salin
`samples/corrections_example.csv` menjadi `corrections.csv`, lalu push lagi.

## Data dan keamanan

- `data/` (ledger, log, payload), `corrections.csv`, `set-env.local.ps1`, dan semua raw JSON
  order (`samples/**/salesorder-*.json`) tidak di-commit. Raw JSON berisi data pembeli asli.
- Token tidak pernah ditampilkan utuh oleh aplikasi.

## Batasan prototipe

- Ledger berupa file JSON. Di Ecolink harus tabel database dengan unique key (shopcode, tid).
- Push dijalankan manual per file, satu order per panggilan. Di Ecolink: antrean, batch tiap 5–15 menit,
  verify tiap jam, rekonsiliasi harian (ukuran batch menunggu YY).
- Koreksi hanya untuk field di level order, lewat file CSV (belum halaman UI).
- Batal order, retur, refund, dan jalur uang (statement) belum termasuk.
