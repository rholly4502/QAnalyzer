using System.Text;

namespace QAnalyzer.Core;

/// <summary>Aturan analisis statis yang beroperasi pada teks T-SQL.</summary>
public interface ISqlRule
{
    string Id { get; }
    string Title { get; }
    RiskLevel Level { get; }
    IEnumerable<Diagnostic> Evaluate(string sql);
}
