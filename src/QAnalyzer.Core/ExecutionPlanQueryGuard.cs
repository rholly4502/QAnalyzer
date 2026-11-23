using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core;

/// <summary>
/// Guard berbasis AST ScriptDom dan normalisasi token stream untuk pola plan cache agar pengambilan
/// execution plan lokal tidak pernah mengeksekusi mutasi data/skema atau koneksi linked server.
/// </summary>
public static class ExecutionPlanQueryGuard
{
    private const int MaxCachePatternLength = 250;
    private const char WildcardSentinel = '\0';

    /// <summary>
    /// Memastikan script T-SQL hanya berisi satu batch read-only (<c>SELECT</c> beserta
    /// opsional <c>DECLARE</c>/<c>SET</c> variabel skalar) tanpa mutasi data, DDL, linked server, atau eksekusi prosedur.
    /// </summary>
    public static bool CanSafelyEstimatePlan(string sql, out string? rejectionReason)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return Fail("Script SQL kosong.", out rejectionReason);

        if (!SqlAstParser.TryParse(sql, out var fragment, out var errors, out var complexityError))
            return Fail(complexityError ?? "Script T-SQL melebihi batas kompleksitas yang aman.", out rejectionReason);
        if (fragment is not TSqlScript script || errors.Count > 0)
            return Fail("Script T-SQL tidak valid atau mengandung syntax error.", out rejectionReason);

        if (script.Batches.Count != 1)
            return Fail("Estimated plan hanya mendukung satu batch T-SQL tanpa pemisah GO.", out rejectionReason);

        var statements = script.Batches[0].Statements;
        if (statements.Count == 0)
            return Fail("Script T-SQL tidak memiliki statement.", out rejectionReason);

        var hasSelect = false;
        foreach (var stmt in statements)
        {
            switch (stmt)
            {
                case SelectStatement s:
                    if (s.Into is not null)
                        return Fail("SELECT ... INTO memodifikasi skema/data dan tidak diizinkan.", out rejectionReason);
                    hasSelect = true;
                    break;
                case DeclareVariableStatement d:
                    if (d.Declarations.Any(static decl =>
                        decl.DataType is null
                        || (decl.DataType is SqlDataTypeReference sqlType
                            && sqlType.SqlDataTypeOption == SqlDataTypeOption.Cursor)))
                    {
                        return Fail("Deklarasi variabel CURSOR tidak diizinkan untuk estimated plan otomatis.", out rejectionReason);
                    }
                    break;
                case SetVariableStatement sv:
                    if (sv.CursorDefinition is not null)
                        return Fail("Inisialisasi CURSOR pada variabel SET tidak diizinkan.", out rejectionReason);
                    if (sv.FunctionCallExists)
                        return Fail("Pemanggilan method UDT pada variabel SET tidak diizinkan.", out rejectionReason);
                    break;
                default:
                    return Fail($"Statement '{stmt.GetType().Name}' tidak diizinkan untuk estimated plan otomatis.", out rejectionReason);
            }
        }

        if (!hasSelect)
            return Fail("Estimated plan membutuhkan minimal satu statement SELECT.", out rejectionReason);

        var visitor = new UnsafeNodeVisitor();
        script.Accept(visitor);
        if (visitor.RejectionReason is not null)
            return Fail(visitor.RejectionReason, out rejectionReason);

        rejectionReason = null;
        return true;
    }

    /// <summary>
    /// Membangun pola <c>LIKE ... ESCAPE '\'</c> berbasis token stream ScriptDom untuk pencarian teks query
    /// di <c>sys.dm_exec_sql_text</c> yang aman dari komentar, literal kompleks, dan wildcard injection.
    /// </summary>
    public static string BuildPlanCacheLikePattern(string sqlText)
    {
        if (string.IsNullOrWhiteSpace(sqlText)) return string.Empty;

        var sanitized = sqlText.Replace("\0", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(sanitized)) return string.Empty;

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        IList<TSqlParserToken> tokens;
        using (var reader = new StringReader(sanitized))
        {
            tokens = parser.GetTokenStream(reader, out _);
        }

        var firstIndex = FindFirstSignificantTokenIndex(tokens);
        var lastIndex = FindLastSignificantTokenIndex(tokens);
        if (firstIndex < 0 || lastIndex < firstIndex) return string.Empty;

        var normalized = BuildNormalizedTemplate(tokens, firstIndex, lastIndex);
        if (normalized.Count(static c => c != WildcardSentinel && !char.IsWhiteSpace(c)) < 3) return string.Empty;

        if (normalized.Length > MaxCachePatternLength) normalized = normalized[..MaxCachePatternLength];
        var startsWild = normalized.StartsWith(WildcardSentinel);
        var endsWild = normalized.EndsWith(WildcardSentinel);

        var escaped = normalized
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal)
            .Replace("[", @"\[", StringComparison.Ordinal)
            .Replace(WildcardSentinel.ToString(), "%", StringComparison.Ordinal);

        return (startsWild ? string.Empty : "%") + escaped + (endsWild ? string.Empty : "%");
    }

    private static string BuildNormalizedTemplate(IList<TSqlParserToken> tokens, int firstIndex, int lastIndex)
    {
        var sb = new StringBuilder();
        var pendingSpace = false;
        var pendingCommentWildcard = false;
        TSqlParserToken? prevSignificant = null;

        for (var i = firstIndex; i <= lastIndex; i++)
        {
            var token = tokens[i];
            if (token.TokenType == TSqlTokenType.EndOfFile) break;

            if (token.TokenType == TSqlTokenType.WhiteSpace)
            {
                if (!pendingCommentWildcard && sb.Length > 0) pendingSpace = true;
                continue;
            }

            if (IsComment(token.TokenType))
            {
                if (sb.Length > 0)
                {
                    pendingCommentWildcard = true;
                    pendingSpace = false;
                }
                continue;
            }

            if (pendingCommentWildcard)
            {
                AppendWildcard(sb);
                pendingCommentWildcard = false;
                pendingSpace = false;
            }
            else if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            if (TryConsumeSignedNumericLiteral(tokens, i, lastIndex, prevSignificant, out var consumedIndex))
            {
                AppendWildcard(sb);
                prevSignificant = tokens[consumedIndex];
                i = consumedIndex;
                continue;
            }

            if (IsLiteralToken(token))
            {
                AppendWildcard(sb);
                prevSignificant = token;
                continue;
            }

            if (!string.IsNullOrEmpty(token.Text))
            {
                sb.Append(token.Text);
                prevSignificant = token;
            }
        }

        return sb.ToString();
    }

    private static bool TryConsumeSignedNumericLiteral(
        IList<TSqlParserToken> tokens,
        int index,
        int lastIndex,
        TSqlParserToken? prevSignificant,
        out int numericTokenIndex)
    {
        numericTokenIndex = index;
        if (tokens[index].TokenType is not (TSqlTokenType.Minus or TSqlTokenType.Plus)
            || IsOperandEndingToken(prevSignificant))
        {
            return false;
        }

        for (var j = index + 1; j <= lastIndex; j++)
        {
            var next = tokens[j];
            if (next.TokenType == TSqlTokenType.WhiteSpace) continue;
            if (IsNumericLiteralToken(next))
            {
                numericTokenIndex = j;
                return true;
            }
            return false;
        }

        return false;
    }

    private static void AppendWildcard(StringBuilder sb)
    {
        if (sb.Length == 0 || sb[^1] != WildcardSentinel) sb.Append(WildcardSentinel);
    }

    private static int FindFirstSignificantTokenIndex(IList<TSqlParserToken> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!IsBoundaryTriviaOrSemicolon(tokens[i].TokenType)) return i;
        }
        return -1;
    }

    private static int FindLastSignificantTokenIndex(IList<TSqlParserToken> tokens)
    {
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            if (!IsBoundaryTriviaOrSemicolon(tokens[i].TokenType)) return i;
        }
        return -1;
    }

    private static bool IsBoundaryTriviaOrSemicolon(TSqlTokenType type) =>
        type is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment
            or TSqlTokenType.MultilineComment or TSqlTokenType.Semicolon or TSqlTokenType.EndOfFile;

    private static bool IsComment(TSqlTokenType type) =>
        type is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment;

    private static bool IsNumericLiteralToken(TSqlParserToken token) =>
        token.TokenType is TSqlTokenType.Integer or TSqlTokenType.Numeric
            or TSqlTokenType.Real or TSqlTokenType.Money;

    private static bool IsLiteralToken(TSqlParserToken token)
    {
        if (IsNumericLiteralToken(token)
            || token.TokenType is TSqlTokenType.AsciiStringLiteral
                or TSqlTokenType.UnicodeStringLiteral
                or TSqlTokenType.HexLiteral)
        {
            return true;
        }

        var text = token.Text;
        return !string.IsNullOrEmpty(text)
            && (text[0] == '\''
                || (text.Length >= 2 && (text[0] is 'N' or 'n') && text[1] == '\'')
                || (text.Length >= 2 && text[0] == '0' && (text[1] is 'x' or 'X')));
    }

    private static bool IsOperandEndingToken(TSqlParserToken? token) =>
        token is not null
        && (IsLiteralToken(token)
            || token.TokenType is TSqlTokenType.Identifier
                or TSqlTokenType.QuotedIdentifier
                or TSqlTokenType.AsciiStringOrQuotedIdentifier
                or TSqlTokenType.Variable
                or TSqlTokenType.RightParenthesis);

    private static bool Fail(string message, out string? reason) { reason = message; return false; }

    private sealed class UnsafeNodeVisitor : TSqlFragmentVisitor
    {
        public string? RejectionReason { get; private set; }
        public override void Visit(DataModificationTableReference node) => RejectionReason ??= "Referensi DML di dalam query tidak diizinkan.";
        public override void Visit(DataModificationStatement node) => RejectionReason ??= "Operasi modifikasi data tidak diizinkan.";
        public override void Visit(ExecuteSpecification node) => RejectionReason ??= "Eksekusi prosedur/SQL dinamis tidak diizinkan.";
        public override void Visit(SetOnOffStatement node) => RejectionReason ??= "Statement SET sesi tidak diizinkan.";
        public override void Visit(SetCommandStatement node) => RejectionReason ??= "Statement SET sesi tidak diizinkan.";
        public override void Visit(SetTransactionIsolationLevelStatement node) => RejectionReason ??= "Statement SET sesi tidak diizinkan.";
        public override void Visit(SetRowCountStatement node) => RejectionReason ??= "Statement SET sesi tidak diizinkan.";
        public override void Visit(SetTextSizeStatement node) => RejectionReason ??= "Statement SET sesi tidak diizinkan.";
        public override void Visit(SetErrorLevelStatement node) => RejectionReason ??= "Statement SET sesi tidak diizinkan.";
        public override void Visit(SetUserStatement node) => RejectionReason ??= "Statement SET sesi tidak diizinkan.";
        public override void Visit(OpenRowsetTableReference node) => RejectionReason ??= "Akses OPENROWSET tidak diizinkan.";
        public override void Visit(AdHocTableReference node) => RejectionReason ??= "Akses ad-hoc OPENDATASOURCE tidak diizinkan.";
        public override void Visit(BulkOpenRowset node) => RejectionReason ??= "Akses BULK OPENROWSET tidak diizinkan.";
        public override void Visit(OpenQueryTableReference node) => RejectionReason ??= "Akses OPENQUERY tidak diizinkan.";
        public override void Visit(SchemaObjectName node)
        {
            if (node.ServerIdentifier is not null)
                RejectionReason ??= "Referensi objek linked server (4-part name) tidak diizinkan.";
        }
        public override void Visit(MultiPartIdentifierCallTarget node)
        {
            if (node.MultiPartIdentifier?.Identifiers.Count >= 3)
                RejectionReason ??= "Referensi fungsi linked server (4-part name) tidak diizinkan.";
        }
        public override void Visit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier?.Identifiers.Count >= 5)
                RejectionReason ??= "Referensi kolom linked server (5-part name) tidak diizinkan.";
        }
        public override void Visit(SelectStatement node)
        {
            if (node.Into is not null)
                RejectionReason ??= "SELECT ... INTO memodifikasi skema/data dan tidak diizinkan.";
        }
    }
}
