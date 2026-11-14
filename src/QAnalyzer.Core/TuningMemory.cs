using System.Text;

namespace QAnalyzer.Core;

/// <summary>
/// Memori lintas-sesi untuk tuning: membaca file Markdown (biasanya
/// tuning/memory.md), menyematkannya sebagai konteks ke prompt, dan
/// menambahkan log sesi. File bisa diedit manual (tambah index).
/// </summary>
public sealed class TuningMemory
{
    private readonly string _filePath;
    private readonly string _content;

    public TuningMemory(string filePath)
    {
        _filePath = filePath;
        _content = File.Exists(filePath) ? File.ReadAllText(filePath) : string.Empty;
    }

    /// <summary>Konteks yang disisipkan ke prompt (kosong jika file tidak ada).</summary>
    public string BuildContext()
    {
        if (string.IsNullOrWhiteSpace(_content)) return string.Empty;
        return "\n# Memori tuning dari sesi sebelumnya (gunakan sebagai konteks)\n" + _content;
    }

    /// <summary>Tambahkan entri log sesi ke file (di bagian ## Log Sesi Tuning).</summary>
    public void AppendSession(string date, string querySummary, string dmvFindings, string advice)
    {
        try
        {
            var entry = $"- `{date}` — {querySummary} — {dmvFindings} — {advice}.";
            var text = File.Exists(_filePath) ? File.ReadAllText(_filePath) : _content;
            const string header = "## Log Sesi Tuning";
            if (text.Contains(header, StringComparison.Ordinal))
            {
                var idx = text.IndexOf(header, StringComparison.Ordinal) + header.Length;
                text = text.Insert(idx, "\n" + entry);
            }
            else text += "\n" + header + "\n" + entry + "\n";
            File.WriteAllText(_filePath, text);
        }
        catch { /* jangan gagalkan tuning karena gagal menulis memori */ }
    }
}
