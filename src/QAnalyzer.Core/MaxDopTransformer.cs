using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core;

/// <summary>
/// Menghasilkan versi "hemat resource" query via AST ScriptDom: menambahkan
/// `OPTION (MAXDOP 1)` di akhir tiap statement SELECT terluar yang belum memiliki
/// klausa OPTION. Aman terhadap komentar, string literal, CTE, subquery,
/// UNION, serta konteks DDL/cursor/inline-TVF di mana OPTION tidak valid.
/// </summary>
public static class MaxDopTransformer
{
    private const string MaxDopOptionClause = " OPTION (MAXDOP 1)";

    public static string AddMaxDopOne(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return sql;
        }

        var fragment = SqlAstParser.Parse(sql, out var errors);
        if (fragment is null || errors.Count > 0)
        {
            return sql;
        }

        var visitor = new OuterSelectVisitor(sql.Length);
        fragment.Accept(visitor);

        if (visitor.InsertionOffsets.Count == 0)
        {
            return sql;
        }

        var offsets = visitor.InsertionOffsets;
        offsets.Sort();

        var sb = new StringBuilder(sql.Length + offsets.Count * MaxDopOptionClause.Length);
        var cursor = 0;
        var lastInsertedOffset = -1;

        foreach (var offset in offsets)
        {
            if (offset == lastInsertedOffset || offset < cursor || offset > sql.Length)
            {
                continue;
            }

            sb.Append(sql, cursor, offset - cursor);
            sb.Append(MaxDopOptionClause);
            cursor = offset;
            lastInsertedOffset = offset;
        }

        if (cursor < sql.Length)
        {
            sb.Append(sql, cursor, sql.Length - cursor);
        }

        return sb.ToString();
    }

    private sealed class OuterSelectVisitor(int sqlLength) : TSqlFragmentVisitor
    {
        public List<int> InsertionOffsets { get; } = [];

        // Konteks non-standalone di mana klausa OPTION (...) tidak valid atau dilarang T-SQL:
        public override void ExplicitVisit(CreateViewStatement node) { }

        public override void ExplicitVisit(AlterViewStatement node) { }

        public override void ExplicitVisit(CreateOrAlterViewStatement node) { }

        public override void ExplicitVisit(SelectFunctionReturnType node) { }

        public override void ExplicitVisit(CursorDefinition node) { }

        public override void ExplicitVisit(CreateTableStatement node) { }

        public override void ExplicitVisit(CreateExternalTableStatement node) { }

        public override void ExplicitVisit(SelectStatement node)
        {
            // Hanya sentuh SelectStatement terluar; subquery & CTE di dalamnya tidak perlu ditelusuri.
            if (node.OptimizerHints.Count > 0)
            {
                return;
            }

            var tokens = node.ScriptTokenStream;
            if (tokens is null || node.FirstTokenIndex < 0 || node.LastTokenIndex >= tokens.Count)
            {
                return;
            }

            for (var i = node.LastTokenIndex; i >= node.FirstTokenIndex; i--)
            {
                var token = tokens[i];
                if (token.TokenType is TSqlTokenType.Semicolon
                    or TSqlTokenType.WhiteSpace
                    or TSqlTokenType.SingleLineComment
                    or TSqlTokenType.MultilineComment)
                {
                    continue;
                }

                var endOffset = token.Offset + (token.Text?.Length ?? 0);
                if (endOffset >= 0 && endOffset <= sqlLength)
                {
                    InsertionOffsets.Add(endOffset);
                }

                break;
            }
        }
    }
}
