using System.Text.RegularExpressions;

namespace B2cPusher;

/// <summary>
/// Menerjemahkan pesan YonSuite (Mandarin) untuk tampilan di terminal dan ledger.
/// Pencocokan sukses/gagal tetap memakai teks asli; teks asli disimpan di log dan kolom raw_message.
/// Tambahkan pesan baru ke daftar ini kalau muncul pesan yang belum dikenal.
/// </summary>
public static class MessageText
{
    private static readonly (Regex Pattern, string Replacement)[] Rules =
    {
        (new Regex(@"导入原单成功\s*(\d+)\s*单\s*导入失败\s*(\d+)\s*单"), "$1 order berhasil, $2 order gagal"),
        (new Regex(@"导入原单成功\s*导入失败\s*(\d+)\s*单"), "import berhasil, $1 order gagal"),
        (new Regex(@"导入原单成功\s*(\d+)\s*单"), "$1 order berhasil"),
        (new Regex(@"导入原单成功"), "import berhasil"),
        (new Regex(@"导入失败\s*(\d+)\s*单"), "$1 order gagal"),
        (new Regex(@"店铺编码不存在或没有做门店对照"), "kode toko tidak ada atau belum dipetakan ke store"),
        (new Regex(@"线上交易状态不在店铺档案设置.?原单转单线上交易状态范围.?"),
            "status transaksi di luar rentang 'Original order to online order' di setting toko"),
        (new Regex(@"订单不符合免审策略\s*Auto Approval\s*的免审时间"), "tidak memenuhi syarat waktu Auto Approval"),
        (new Regex(@"操作成功"), "operasi berhasil"),
        (new Regex(@"成功[！!]?"), "berhasil"),
    };

    private static readonly Regex Cjk = new(@"[　-〿一-鿿＀-￯]+");

    public static string ToIndonesian(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = text;
        foreach (var (pattern, replacement) in Rules)
            result = pattern.Replace(result, replacement);

        // Sisa teks Mandarin yang belum ada di daftar: jangan ditampilkan mentah.
        if (Cjk.IsMatch(result))
            result = Cjk.Replace(result, "[?]") + " (pesan YonSuite belum diterjemahkan, lihat raw_message/log)";

        return result.Trim();
    }
}
