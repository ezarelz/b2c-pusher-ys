# Contoh pengaturan koneksi ke tenant YonSuite (sama dengan environment Postman).
#
# Cara pakai (PowerShell, dari folder repo):
#   1. Salin file ini menjadi  set-env.local.ps1  (file .local.ps1 tidak ikut ter-commit)
#   2. Isi nilainya dari environment Postman
#   3. Jalankan:   . .\set-env.local.ps1      (perhatikan titik + spasi di depan)
#   4. Tes:        dotnet run -- token
#
# Variabel hanya berlaku di terminal tempat file ini dijalankan.

$env:YS_TOKEN_URL = "isi {{tokenUrl}} dari Postman, tanpa /open-auth/..."
$env:YS_GATEWAY   = "isi {{gatewayUrl}} dari Postman"
$env:YS_APPKEY    = "isi {{appKey}}"
$env:YS_APPSECRET = "isi {{appSecret}}"

# Kosongkan token statis supaya aplikasi meminta token sendiri dari appKey/appSecret.
# (Alternatif: isi dengan access_token dari Postman, maka appKey/appSecret tidak dipakai.)
$env:YS_TOKEN = ""

Write-Host "Target gateway : $env:YS_GATEWAY"
Write-Host "Token URL      : $env:YS_TOKEN_URL"
