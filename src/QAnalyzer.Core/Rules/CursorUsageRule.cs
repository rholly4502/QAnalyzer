using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core.Rules;

/// <summary>
/// Deteksi deklarasi/inisialisasi T-SQL CURSOR via AST ScriptDom dengan severity kontekstual:
/// <see cref="RiskLevel.Info"/> untuk cursor ringan (<c>LOCAL FAST_FORWARD</c> atau
/// <c>LOCAL FORWARD_ONLY READ_ONLY</c>), dan <see cref="RiskLevel.Warning"/> untuk cursor
/// default/global/scrollable/updatable yang berisiko menahan lock serta membebani memori/tempdb.
/// </summary>
public sealed class CursorUsageRule : IAstSqlRule
{
    public string Id => "CURSOR_USAGE";
    public string Title => "Penggunaan T-SQL CURSOR (baris-per-baris)";
    public RiskLevel Level => RiskLevel.Warning;

    public IEnumerable<Diagnostic> Evaluate(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var fragment = SqlAstParser.Parse(sql);
        return fragment is null ? [] : Evaluate(fragment);
    }

    IEnumerable<Diagnostic> IAstSqlRule.Evaluate(TSqlFragment fragment) => Evaluate(fragment);

    private IEnumerable<Diagnostic> Evaluate(TSqlFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        var visitor = new CursorUsageVisitor(Id, Title);
        fragment.Accept(visitor);
        return visitor.Diagnostics;
    }

    private sealed class CursorUsageVisitor(string ruleId, string title) : TSqlFragmentVisitor
    {
        public List<Diagnostic> Diagnostics { get; } = [];

        public override void Visit(DeclareCursorStatement node) =>
            InspectCursor(node.CursorDefinition, node.Name?.Value, isCursorVariable: false, node);

        public override void Visit(SetVariableStatement node)
        {
            if (node.CursorDefinition is not null)
            {
                InspectCursor(node.CursorDefinition, node.Variable?.Name, isCursorVariable: true, node);
            }
        }

        private void InspectCursor(
            CursorDefinition? definition,
            string? cursorName,
            bool isCursorVariable,
            TSqlFragment anchor)
        {
            if (definition is null)
            {
                return;
            }

            var flags = CursorOptionFlags.From(definition);
            var isLocal = isCursorVariable ? !flags.HasGlobal : (flags.HasLocal && !flags.HasGlobal);
            var isReadOnly = flags.HasReadOnly || flags.HasForReadOnly || flags.HasStatic || flags.HasInsensitive;
            var hasHeavyOption = flags.HasGlobal
                || flags.HasScroll
                || flags.HasDynamic
                || flags.HasKeyset
                || flags.HasScrollLocks
                || flags.HasOptimistic
                || flags.HasForUpdate;

            var isLightweight = isLocal
                && !hasHeavyOption
                && (flags.HasFastForward || (flags.HasForwardOnly && isReadOnly));

            var label = string.IsNullOrWhiteSpace(cursorName) ? "Cursor" : $"Cursor '{cursorName}'";

            if (isLightweight)
            {
                Diagnostics.Add(new Diagnostic(
                    RiskLevel.Info,
                    ruleId,
                    title,
                    $"{label} menggunakan opsi ringan (lokal, forward-only, read-only), namun tetap memproses data baris-per-baris (RBAR) yang umumnya lebih lambat dibanding operasi set-based.",
                    "Evaluasi apakah logika dapat ditulis sebagai operasi set-based (JOIN, CTE, window function, atau MERGE) untuk throughput lebih baik.",
                    anchor.StartLine,
                    anchor.StartColumn));
                return;
            }

            var issues = CollectIssues(isCursorVariable, flags, isReadOnly);
            var message = issues.Count > 0
                ? $"{label} memproses data baris-per-baris (RBAR) dengan opsi kurang optimal ({string.Join(", ", issues)}) — berisiko menahan lock dan membebani memori/tempdb."
                : $"{label} memproses data baris-per-baris (RBAR) — berisiko menahan lock dan membebani memori/tempdb.";

            var suggestion = isCursorVariable
                ? "Ubah pemrosesan ke operasi set-based (JOIN, CTE, window function, atau MERGE). Jika cursor variabel tidak terhindarkan, gunakan opsi FAST_FORWARD (atau STATIC FORWARD_ONLY READ_ONLY) serta pastikan CLOSE dan DEALLOCATE."
                : "Ubah pemrosesan ke operasi set-based (JOIN, CTE, window function, atau MERGE). Jika cursor tidak terhindarkan, gunakan opsi LOCAL FAST_FORWARD (atau LOCAL STATIC FORWARD_ONLY READ_ONLY) serta pastikan CLOSE dan DEALLOCATE.";

            Diagnostics.Add(new Diagnostic(
                RiskLevel.Warning,
                ruleId,
                title,
                message,
                suggestion,
                anchor.StartLine,
                anchor.StartColumn));
        }

        private static List<string> CollectIssues(bool isCursorVariable, CursorOptionFlags flags, bool isReadOnly)
        {
            var issues = new List<string>();
            if (flags.HasGlobal)
            {
                issues.Add("bersifat GLOBAL");
            }
            else if (!isCursorVariable && !flags.HasLocal)
            {
                issues.Add("tanpa opsi LOCAL (berisiko GLOBAL)");
            }

            if (flags.HasScroll) issues.Add("menggunakan SCROLL");
            if (flags.HasDynamic) issues.Add("menggunakan DYNAMIC");
            if (flags.HasKeyset) issues.Add("menggunakan KEYSET");
            if (flags.HasForUpdate) issues.Add("menggunakan FOR UPDATE");
            if (flags.HasScrollLocks) issues.Add("menggunakan SCROLL_LOCKS");
            if (flags.HasOptimistic) issues.Add("menggunakan OPTIMISTIC");

            if (!flags.HasFastForward && !flags.HasForwardOnly && !flags.HasScroll)
            {
                issues.Add("tanpa FAST_FORWARD/FORWARD_ONLY");
            }

            if (!flags.HasFastForward && !isReadOnly && !flags.HasForUpdate && !flags.HasScrollLocks && !flags.HasOptimistic)
            {
                issues.Add("tanpa READ_ONLY");
            }

            return issues;
        }
    }

    private readonly record struct CursorOptionFlags(
        bool HasLocal,
        bool HasGlobal,
        bool HasFastForward,
        bool HasForwardOnly,
        bool HasScroll,
        bool HasStatic,
        bool HasInsensitive,
        bool HasKeyset,
        bool HasDynamic,
        bool HasReadOnly,
        bool HasScrollLocks,
        bool HasOptimistic,
        bool HasForReadOnly,
        bool HasForUpdate)
    {
        public static CursorOptionFlags From(CursorDefinition definition)
        {
            bool hasLocal = false, hasGlobal = false, hasFastForward = false, hasForwardOnly = false,
                hasScroll = false, hasStatic = false, hasInsensitive = false, hasKeyset = false,
                hasDynamic = false, hasReadOnly = false, hasScrollLocks = false, hasOptimistic = false;

            foreach (var option in definition.Options)
            {
                switch (option.OptionKind)
                {
                    case CursorOptionKind.Local: hasLocal = true; break;
                    case CursorOptionKind.Global: hasGlobal = true; break;
                    case CursorOptionKind.FastForward: hasFastForward = true; break;
                    case CursorOptionKind.ForwardOnly: hasForwardOnly = true; break;
                    case CursorOptionKind.Scroll: hasScroll = true; break;
                    case CursorOptionKind.Static: hasStatic = true; break;
                    case CursorOptionKind.Insensitive: hasInsensitive = true; break;
                    case CursorOptionKind.Keyset: hasKeyset = true; break;
                    case CursorOptionKind.Dynamic: hasDynamic = true; break;
                    case CursorOptionKind.ReadOnly: hasReadOnly = true; break;
                    case CursorOptionKind.ScrollLocks: hasScrollLocks = true; break;
                    case CursorOptionKind.Optimistic: hasOptimistic = true; break;
                }
            }

            var forClause = definition.Select?.QueryExpression?.ForClause;
            return new CursorOptionFlags(
                hasLocal,
                hasGlobal,
                hasFastForward,
                hasForwardOnly,
                hasScroll,
                hasStatic,
                hasInsensitive,
                hasKeyset,
                hasDynamic,
                hasReadOnly,
                hasScrollLocks,
                hasOptimistic,
                HasForReadOnly: forClause is ReadOnlyForClause,
                HasForUpdate: forClause is UpdateForClause);
        }
    }
}
