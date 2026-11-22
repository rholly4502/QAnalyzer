using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core.Rules;

/// <summary>
/// Deteksi perbandingan eksplisit dengan <c>NULL</c> (<c>= NULL</c>, <c>&lt;&gt; NULL</c>,
/// operator relasional, <c>LIKE NULL</c>, <c>BETWEEN</c>, serta <c>CASE ... WHEN NULL</c>)
/// via AST ScriptDom dengan mempertimbangkan konteks <c>SET ANSI_NULLS</c> per batch/routine.
/// </summary>
public sealed class NullComparisonRule : IAstSqlRule
{
    public string Id => "NULL_COMPARISON";
    public string Title => "Perbandingan eksplisit dengan NULL";
    public RiskLevel Level => RiskLevel.Critical;

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
        var visitor = new NullComparisonVisitor(Id, Title);
        fragment.Accept(visitor);
        return visitor.Diagnostics;
    }

    private sealed class NullComparisonVisitor(string ruleId, string title) : TSqlFragmentVisitor
    {
        private bool _ansiNullsOff;
        private bool _inRoutine;

        public List<Diagnostic> Diagnostics { get; } = [];

        // Routine (SP/trigger/function) mewarisi ANSI_NULLS saat pembuatan; SET di dalamnya tidak mengubah status batch luar.
        public override void ExplicitVisit(CreateProcedureStatement node) => VisitRoutine(node);
        public override void ExplicitVisit(AlterProcedureStatement node) => VisitRoutine(node);
        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) => VisitRoutine(node);
        public override void ExplicitVisit(CreateTriggerStatement node) => VisitRoutine(node);
        public override void ExplicitVisit(AlterTriggerStatement node) => VisitRoutine(node);
        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => VisitRoutine(node);
        public override void ExplicitVisit(CreateFunctionStatement node) => VisitRoutine(node);
        public override void ExplicitVisit(AlterFunctionStatement node) => VisitRoutine(node);
        public override void ExplicitVisit(CreateOrAlterFunctionStatement node) => VisitRoutine(node);

        public override void ExplicitVisit(PredicateSetStatement node)
        {
            if (!_inRoutine && (node.Options & (SetOptions.AnsiNulls | SetOptions.AnsiDefaults)) != 0)
            {
                _ansiNullsOff = !node.IsOn;
            }
        }

        public override void ExplicitVisit(IfStatement node)
        {
            node.Predicate?.Accept(this);
            var pre = _ansiNullsOff;
            node.ThenStatement?.Accept(this);
            var thenOff = _ansiNullsOff;
            _ansiNullsOff = pre;
            node.ElseStatement?.Accept(this);
            _ansiNullsOff = thenOff && _ansiNullsOff;
        }

        public override void ExplicitVisit(WhileStatement node)
        {
            node.Predicate?.Accept(this);
            var pre = _ansiNullsOff;
            node.Statement?.Accept(this);
            _ansiNullsOff = pre;
        }

        public override void ExplicitVisit(TryCatchStatement node)
        {
            var pre = _ansiNullsOff;
            node.TryStatements?.Accept(this);
            var tryOff = _ansiNullsOff;
            _ansiNullsOff = pre;
            node.CatchStatements?.Accept(this);
            _ansiNullsOff = tryOff && _ansiNullsOff;
        }

        public override void Visit(BooleanComparisonExpression node)
        {
            if (node.ComparisonType is BooleanComparisonType.IsDistinctFrom
                or BooleanComparisonType.IsNotDistinctFrom
                or BooleanComparisonType.LeftOuterJoin
                or BooleanComparisonType.RightOuterJoin)
            {
                return;
            }

            var leftIsNull = IsDefinitelyNull(node.FirstExpression, out var leftDirect);
            var rightIsNull = IsDefinitelyNull(node.SecondExpression, out var rightDirect);
            if (!leftIsNull && !rightIsNull)
            {
                return;
            }

            var comparison = DescribeComparison(node.ComparisonType);
            if (comparison is not var (opSymbol, isEqualityOp, suggestedIsOp))
            {
                return;
            }

            var allNullSidesDirectLiteral = (!leftIsNull || leftDirect) && (!rightIsNull || rightDirect);
            if (isEqualityOp && _ansiNullsOff && allNullSidesDirectLiteral)
            {
                AddDiagnostic(
                    RiskLevel.Warning,
                    $"Perbandingan '{opSymbol} NULL' bergantung pada opsi deprecated SET ANSI_NULLS OFF yang mengubah logika saat ANSI_NULLS ON.",
                    $"Ganti dengan '{suggestedIsOp}' (atau 'IS [NOT] DISTINCT FROM' pada SQL Server 2022+) agar konsisten di bawah ANSI_NULLS ON.",
                    node);
                return;
            }

            if (isEqualityOp)
            {
                AddDiagnostic(
                    RiskLevel.Critical,
                    $"Perbandingan '{opSymbol} NULL' selalu bernilai UNKNOWN di T-SQL (three-valued logic dengan ANSI_NULLS ON) sehingga tidak pernah cocok.",
                    $"Ganti dengan '{suggestedIsOp}' (atau gunakan 'IS [NOT] DISTINCT FROM' untuk perbandingan null-safe di SQL Server 2022+).",
                    node);
                return;
            }

            AddDiagnostic(
                RiskLevel.Critical,
                $"Perbandingan '{opSymbol}' dengan NULL selalu bernilai UNKNOWN di T-SQL (three-valued logic) dan tidak pernah menghasilkan baris.",
                "Gunakan pemeriksaan 'IS NULL' atau 'IS NOT NULL' (serta tangani nilai NULL secara eksplisit bila diperlukan).",
                node);
        }

        public override void Visit(LikePredicate node)
        {
            if (!IsDefinitelyNull(node.FirstExpression, out _)
                && !IsDefinitelyNull(node.SecondExpression, out _)
                && !IsDefinitelyNull(node.EscapeExpression, out _))
                return;

            var op = node.NotDefined ? "NOT LIKE" : "LIKE";
            AddDiagnostic(
                RiskLevel.Critical,
                $"Predicate '{op}' dengan NULL selalu bernilai UNKNOWN di T-SQL sehingga kondisi filter tidak pernah terpenuhi.",
                "Periksa nilai NULL secara eksplisit dengan 'IS NULL' atau 'IS NOT NULL' sebelum menerapkan operator LIKE.",
                node);
        }

        public override void Visit(BooleanTernaryExpression node)
        {
            var firstIsNull = IsDefinitelyNull(node.FirstExpression, out _);
            var secondIsNull = IsDefinitelyNull(node.SecondExpression, out _);
            var thirdIsNull = IsDefinitelyNull(node.ThirdExpression, out _);
            if (!firstIsNull && !secondIsNull && !thirdIsNull)
                return;

            if (node.TernaryExpressionType == BooleanTernaryExpressionType.Between)
            {
                AddDiagnostic(
                    RiskLevel.Critical,
                    "Predicate BETWEEN dengan operand NULL selalu bernilai UNKNOWN di T-SQL dan tidak akan mengembalikan baris.",
                    "Pastikan seluruh operand BETWEEN non-NULL atau tambahkan pemeriksaan 'IS NULL' / 'IS NOT NULL' secara eksplisit.",
                    node);
                return;
            }

            if (firstIsNull || (secondIsNull && thirdIsNull))
            {
                AddDiagnostic(
                    RiskLevel.Critical,
                    "Predicate NOT BETWEEN dengan target atau kedua batas NULL selalu bernilai UNKNOWN di T-SQL dan tidak akan mengembalikan baris.",
                    "Gunakan pemeriksaan 'IS NULL' / 'IS NOT NULL' secara eksplisit dan pastikan batas rentang bernilai non-NULL.",
                    node);
                return;
            }

            AddDiagnostic(
                RiskLevel.Warning,
                "Predicate NOT BETWEEN memiliki satu batas bernilai NULL — sebagian evaluasi menghasilkan UNKNOWN pada three-valued logic T-SQL.",
                "Hindari batas NULL pada NOT BETWEEN; gunakan perbandingan eksplisit (< / >) ditambah pemeriksaan 'IS NULL' / 'IS NOT NULL'.",
                node);
        }

        public override void Visit(SimpleCaseExpression node)
        {
            if (IsDefinitelyNull(node.InputExpression, out _))
            {
                AddDiagnostic(
                    RiskLevel.Critical,
                    "Ekspresi CASE NULL ... WHEN tidak akan pernah cocok dengan cabang WHEN mana pun karena perbandingan implisit dengan NULL selalu UNKNOWN.",
                    "Gunakan searched CASE (CASE WHEN expr IS NULL THEN ... END) untuk memeriksa nilai NULL.",
                    node);
                return;
            }

            foreach (var when in node.WhenClauses)
            {
                if (IsDefinitelyNull(when.WhenExpression, out _))
                {
                    AddDiagnostic(
                        RiskLevel.Critical,
                        "Cabang 'CASE ... WHEN NULL' tidak pernah dieksekusi di T-SQL karena perbandingan kesetaraan dengan NULL selalu UNKNOWN (termasuk saat ANSI_NULLS OFF).",
                        "Ubah ke searched CASE (CASE WHEN kolom IS NULL THEN ... END) agar kondisi NULL dievaluasi dengan benar.",
                        when);
                }
            }
        }

        private void VisitRoutine(TSqlFragment node)
        {
            var savedAnsiNullsOff = _ansiNullsOff;
            var savedInRoutine = _inRoutine;
            _inRoutine = true;
            try { node.AcceptChildren(this); }
            finally
            {
                _ansiNullsOff = savedAnsiNullsOff;
                _inRoutine = savedInRoutine;
            }
        }

        private void AddDiagnostic(RiskLevel level, string message, string suggestion, TSqlFragment anchor) =>
            Diagnostics.Add(new Diagnostic(level, ruleId, title, message, suggestion, anchor.StartLine, anchor.StartColumn));
    }

    private static (string OpSymbol, bool IsEqualityOp, string? SuggestedIsOp)? DescribeComparison(BooleanComparisonType type) =>
        type switch
        {
            BooleanComparisonType.Equals => ("=", true, "IS NULL"),
            BooleanComparisonType.NotEqualToBrackets => ("<>", true, "IS NOT NULL"),
            BooleanComparisonType.NotEqualToExclamation => ("!=", true, "IS NOT NULL"),
            BooleanComparisonType.LessThan => ("<", false, null),
            BooleanComparisonType.LessThanOrEqualTo => ("<=", false, null),
            BooleanComparisonType.GreaterThan => (">", false, null),
            BooleanComparisonType.GreaterThanOrEqualTo => (">=", false, null),
            BooleanComparisonType.NotLessThan => ("!<", false, null),
            BooleanComparisonType.NotGreaterThan => ("!>", false, null),
            BooleanComparisonType.NotLike => ("NOT LIKE", false, null),
            _ => null,
        };

    private static bool IsDefinitelyNull(ScalarExpression? expression, out bool isDirectNullLiteral)
    {
        var unwrapped = Unwrap(expression);
        if (unwrapped is NullLiteral)
        {
            isDirectNullLiteral = true;
            return true;
        }

        isDirectNullLiteral = false;
        return unwrapped switch
        {
            CastCall cast => IsDefinitelyNull(cast.Parameter, out _),
            ConvertCall convert => IsDefinitelyNull(convert.Parameter, out _),
            TryCastCall tryCast => IsDefinitelyNull(tryCast.Parameter, out _),
            TryConvertCall tryConvert => IsDefinitelyNull(tryConvert.Parameter, out _),
            UnaryExpression unary => IsDefinitelyNull(unary.Expression, out _),
            CoalesceExpression coalesce => coalesce.Expressions.Count > 0
                && coalesce.Expressions.All(e => IsDefinitelyNull(e, out _)),
            FunctionCall fn when fn.CallTarget is null
                && fn.FunctionName.Value.Equals("ISNULL", StringComparison.OrdinalIgnoreCase)
                && fn.Parameters.Count == 2 => IsDefinitelyNull(fn.Parameters[0], out _)
                    && IsDefinitelyNull(fn.Parameters[1], out _),
            _ => false,
        };
    }

    private static ScalarExpression? Unwrap(ScalarExpression? expression)
    {
        while (expression is ParenthesisExpression paren)
        {
            expression = paren.Expression;
        }

        return expression;
    }
}
