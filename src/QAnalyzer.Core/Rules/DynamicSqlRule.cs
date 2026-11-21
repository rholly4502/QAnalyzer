using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core.Rules;

/// <summary>
/// Deteksi penggunaan dynamic SQL berisiko via AST ScriptDom:
/// konkatenasi ekspresi dinamis pada EXEC(...)/sp_executesql (Critical) dan
/// EXEC(...) atau sp_executesql tanpa parameterisasi @params (Warning).
/// </summary>
public sealed class DynamicSqlRule : IAstSqlRule
{
    public string Id => "DYNAMIC_SQL";
    public string Title => "Penggunaan dynamic SQL berisiko";
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
        var visitor = new DynamicSqlVisitor(Id, Title);
        fragment.Accept(visitor);
        return visitor.Diagnostics;
    }

    private enum VariableState
    {
        StaticLiteral = 0,
        SafeQuotedDynamic = 1,
        Unknown = 2,
        UnsafeConcatenated = 3,
    }

    private sealed class DynamicSqlVisitor(string ruleId, string title) : TSqlFragmentVisitor
    {
        private Dictionary<string, VariableState> _states = new(StringComparer.OrdinalIgnoreCase);

        public List<Diagnostic> Diagnostics { get; } = [];

        // Isolasi scope per batch/routine agar default parameter & taint antar batch tidak bocor.
        public override void ExplicitVisit(TSqlBatch node) => VisitIsolatedScope(node);
        public override void ExplicitVisit(CreateProcedureStatement node) => VisitIsolatedScope(node);
        public override void ExplicitVisit(AlterProcedureStatement node) => VisitIsolatedScope(node);
        public override void ExplicitVisit(CreateOrAlterProcedureStatement node) => VisitIsolatedScope(node);
        public override void ExplicitVisit(CreateTriggerStatement node) => VisitIsolatedScope(node);
        public override void ExplicitVisit(AlterTriggerStatement node) => VisitIsolatedScope(node);
        public override void ExplicitVisit(CreateOrAlterTriggerStatement node) => VisitIsolatedScope(node);
        public override void ExplicitVisit(CreateFunctionStatement node) => VisitIsolatedScope(node);
        public override void ExplicitVisit(AlterFunctionStatement node) => VisitIsolatedScope(node);
        public override void ExplicitVisit(CreateOrAlterFunctionStatement node) => VisitIsolatedScope(node);

        public override void ExplicitVisit(IfStatement node)
        {
            node.Predicate?.Accept(this);
            var pre = CloneStates(_states);
            node.ThenStatement?.Accept(this);
            var thenStates = CloneStates(_states);
            _states = pre;
            node.ElseStatement?.Accept(this);
            _states = MergeStates(thenStates, _states);
        }

        public override void ExplicitVisit(WhileStatement node)
        {
            node.Predicate?.Accept(this);
            var pre = CloneStates(_states);
            node.Statement?.Accept(this);
            _states = MergeStates(pre, _states);
        }

        public override void ExplicitVisit(TryCatchStatement node)
        {
            var pre = CloneStates(_states);
            node.TryStatements?.Accept(this);
            var afterTry = CloneStates(_states);
            _states = MergeStates(pre, afterTry);
            node.CatchStatements?.Accept(this);
            _states = MergeStates(afterTry, _states);
        }

        public override void ExplicitVisit(DeclareVariableStatement node)
        {
            foreach (var decl in node.Declarations)
            {
                if (decl.VariableName is null) continue;
                _states[decl.VariableName.Value] = decl.Value is null
                    ? VariableState.Unknown
                    : ClassifyExpression(decl.Value);
            }
        }

        public override void ExplicitVisit(SetVariableStatement node)
        {
            if (node.Variable is not null && node.Expression is not null)
                RecordAssignment(node.Variable.Name, node.AssignmentKind, node.Expression);
        }

        public override void ExplicitVisit(SelectSetVariable node)
        {
            if (node.Variable is not null && node.Expression is not null)
                RecordAssignment(node.Variable.Name, node.AssignmentKind, node.Expression);
        }

        public override void ExplicitVisit(ExecuteStatement node) =>
            InspectExecuteSpecification(node.ExecuteSpecification, node);

        public override void ExplicitVisit(ExecuteInsertSource node) =>
            InspectExecuteSpecification(node.Execute, node);

        private void VisitIsolatedScope(TSqlFragment node)
        {
            var previous = _states;
            _states = new Dictionary<string, VariableState>(StringComparer.OrdinalIgnoreCase);
            try { node.AcceptChildren(this); }
            finally { _states = previous; }
        }

        private void RecordAssignment(string name, AssignmentKind kind, ScalarExpression expression)
        {
            var rhs = ClassifyExpression(expression);
            _states[name] = kind is AssignmentKind.AddEquals or AssignmentKind.ConcatEquals
                ? CombineConcat(_states.GetValueOrDefault(name, VariableState.Unknown), rhs)
                : rhs;
        }

        private void InspectExecuteSpecification(ExecuteSpecification? spec, TSqlFragment anchor)
        {
            switch (spec?.ExecutableEntity)
            {
                case ExecutableStringList stringList:
                    InspectStringListExecution(stringList, anchor);
                    break;
                case ExecutableProcedureReference procRef when IsSpExecuteSql(procRef):
                    InspectSpExecuteSql(procRef, anchor);
                    break;
            }
        }

        private void InspectStringListExecution(ExecutableStringList stringList, TSqlFragment anchor)
        {
            if (stringList.Strings.Count == 0) return;

            var state = FoldConcat(stringList.Strings);
            if (state == VariableState.UnsafeConcatenated)
            {
                AddDiagnostic(
                    RiskLevel.Critical,
                    "Dynamic SQL dibangun dari konkatenasi ekspresi dinamis pada EXEC(...) — rentan SQL injection dan memicu ad-hoc plan cache bloat.",
                    "Gunakan sp_executesql dengan deklarasi parameter (@params) untuk nilai, serta QUOTENAME() dan allowlist untuk nama objek/kolom.",
                    anchor);
                return;
            }

            if (state == VariableState.StaticLiteral
                || (state == VariableState.SafeQuotedDynamic && stringList.Parameters.Count > 0))
                return;

            AddDiagnostic(
                RiskLevel.Warning,
                "Dynamic SQL dieksekusi menggunakan EXEC(...) yang tidak mendukung binding parameter dan berisiko memenuhi plan cache.",
                "Gunakan sp_executesql dengan deklarasi parameter (@params) agar nilai terparameterisasi dengan aman dan execution plan dapat dipakai ulang.",
                anchor);
        }

        private void InspectSpExecuteSql(ExecutableProcedureReference procRef, TSqlFragment anchor)
        {
            var (stmtParam, paramsDefParam) = FindSpExecuteSqlParameters(procRef.Parameters);
            if (stmtParam?.ParameterValue is null) return;

            var stmtState = ClassifyExpression(stmtParam.ParameterValue);
            if (stmtState == VariableState.UnsafeConcatenated)
            {
                AddDiagnostic(
                    RiskLevel.Critical,
                    "Statement sp_executesql dibangun dari konkatenasi ekspresi dinamis — tetap rentan SQL injection dan mencegah reuse plan cache.",
                    "Pisahkan nilai dinamis ke definisi parameter @params pada sp_executesql, dan gunakan QUOTENAME() serta allowlist untuk nama objek/kolom.",
                    anchor);
                return;
            }

            if (stmtState == VariableState.Unknown && !HasParameterDefinition(paramsDefParam))
            {
                AddDiagnostic(
                    RiskLevel.Warning,
                    "sp_executesql dipanggil tanpa deklarasi parameter (@params) pada statement dinamis.",
                    "Sertakan parameter @params pada sp_executesql untuk memisahkan data dari sintaks SQL dan mencegah ad-hoc plan cache bloat.",
                    anchor);
            }
        }

        private VariableState ClassifyExpression(ScalarExpression? expression) =>
            Unwrap(expression) switch
            {
                null => VariableState.Unknown,
                StringLiteral or IntegerLiteral or NumericLiteral => VariableState.StaticLiteral,
                VariableReference variable => _states.GetValueOrDefault(variable.Name, VariableState.Unknown),
                CastCall cast => ClassifyExpression(cast.Parameter),
                ConvertCall convert => ClassifyExpression(convert.Parameter),
                TryCastCall tryCast => ClassifyExpression(tryCast.Parameter),
                TryConvertCall tryConvert => ClassifyExpression(tryConvert.Parameter),
                BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add or BinaryExpressionType.Concat } bin
                    => CombineConcat(ClassifyExpression(bin.FirstExpression), ClassifyExpression(bin.SecondExpression)),
                FunctionCall fn when fn.CallTarget is null => ClassifyFunctionCall(fn),
                SimpleCaseExpression sc => ClassifyCaseBranches(sc.WhenClauses.Select(w => w.ThenExpression), sc.ElseExpression),
                SearchedCaseExpression sc => ClassifyCaseBranches(sc.WhenClauses.Select(w => w.ThenExpression), sc.ElseExpression),
                _ => VariableState.Unknown,
            };

        private VariableState ClassifyFunctionCall(FunctionCall fn)
        {
            var name = fn.FunctionName.Value;
            if (name.Equals("QUOTENAME", StringComparison.OrdinalIgnoreCase))
                return VariableState.SafeQuotedDynamic;

            if ((name.Equals("CHAR", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("NCHAR", StringComparison.OrdinalIgnoreCase))
                && fn.Parameters.Count == 1
                && Unwrap(fn.Parameters[0]) is IntegerLiteral)
                return VariableState.StaticLiteral;

            if (name.Equals("CONCAT", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CONCAT_WS", StringComparison.OrdinalIgnoreCase)
                || name.Equals("FORMAT", StringComparison.OrdinalIgnoreCase))
            {
                if (fn.Parameters.Count == 0) return VariableState.Unknown;
                if (fn.Parameters.Count == 1)
                    return ClassifyExpression(fn.Parameters[0]) == VariableState.StaticLiteral
                        ? VariableState.StaticLiteral
                        : VariableState.UnsafeConcatenated;
                return FoldConcat(fn.Parameters);
            }

            return VariableState.Unknown;
        }

        private VariableState ClassifyCaseBranches(IEnumerable<ScalarExpression> thenExpressions, ScalarExpression? elseExpression)
        {
            var branches = (elseExpression is null ? thenExpressions : thenExpressions.Append(elseExpression))
                .Select(ClassifyExpression)
                .ToList();
            if (branches.Count == 0) return VariableState.Unknown;
            if (branches.Contains(VariableState.UnsafeConcatenated)) return VariableState.UnsafeConcatenated;
            return branches.All(s => s is VariableState.StaticLiteral or VariableState.SafeQuotedDynamic)
                ? VariableState.SafeQuotedDynamic
                : VariableState.Unknown;
        }

        private VariableState FoldConcat(IEnumerable<ScalarExpression> expressions)
        {
            using var e = expressions.GetEnumerator();
            if (!e.MoveNext()) return VariableState.Unknown;
            var state = ClassifyExpression(e.Current);
            while (e.MoveNext()) state = CombineConcat(state, ClassifyExpression(e.Current));
            return state;
        }

        private void AddDiagnostic(RiskLevel level, string message, string suggestion, TSqlFragment anchor) =>
            Diagnostics.Add(new Diagnostic(level, ruleId, title, message, suggestion, anchor.StartLine, anchor.StartColumn));

        private static VariableState CombineConcat(VariableState left, VariableState right)
        {
            if (left is VariableState.UnsafeConcatenated or VariableState.Unknown
                || right is VariableState.UnsafeConcatenated or VariableState.Unknown)
                return VariableState.UnsafeConcatenated;

            return left == VariableState.StaticLiteral && right == VariableState.StaticLiteral
                ? VariableState.StaticLiteral
                : VariableState.SafeQuotedDynamic;
        }

        private static Dictionary<string, VariableState> CloneStates(Dictionary<string, VariableState> source) =>
            new(source, StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, VariableState> MergeStates(
            Dictionary<string, VariableState> first,
            Dictionary<string, VariableState> second)
        {
            var merged = new Dictionary<string, VariableState>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in first.Keys.Union(second.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var a = first.GetValueOrDefault(key, VariableState.Unknown);
                var b = second.GetValueOrDefault(key, VariableState.Unknown);
                merged[key] = (VariableState)Math.Max((int)a, (int)b);
            }
            return merged;
        }

        private static ScalarExpression? Unwrap(ScalarExpression? expression)
        {
            while (expression is ParenthesisExpression paren) expression = paren.Expression;
            return expression;
        }

        private static bool IsSpExecuteSql(ExecutableProcedureReference procRef)
        {
            var procRefName = procRef.ProcedureReference;
            if (procRefName is null || procRefName.ProcedureVariable is not null) return false;
            var name = procRefName.ProcedureReference?.Name;
            if (name is null || !string.Equals(name.BaseIdentifier?.Value, "sp_executesql", StringComparison.OrdinalIgnoreCase))
                return false;

            var schema = name.SchemaIdentifier?.Value;
            return schema is null
                || schema.Equals("sys", StringComparison.OrdinalIgnoreCase)
                || schema.Equals("dbo", StringComparison.OrdinalIgnoreCase);
        }

        private static (ExecuteParameter? Stmt, ExecuteParameter? ParamsDef) FindSpExecuteSqlParameters(IList<ExecuteParameter> parameters)
        {
            var stmt = parameters.FirstOrDefault(p => IsNamedParam(p, "@stmt", "@statement"))
                ?? (parameters.Count > 0 && parameters[0].Variable is null ? parameters[0] : null);
            var paramsDef = parameters.FirstOrDefault(p => IsNamedParam(p, "@params", "@parameters"))
                ?? (parameters.Count > 1 && parameters[1].Variable is null ? parameters[1] : null);
            return (stmt, paramsDef);
        }

        private static bool IsNamedParam(ExecuteParameter p, string primary, string alias) =>
            p.Variable?.Name is { } name
            && (name.Equals(primary, StringComparison.OrdinalIgnoreCase) || name.Equals(alias, StringComparison.OrdinalIgnoreCase));

        private static bool HasParameterDefinition(ExecuteParameter? paramsDefParam) =>
            Unwrap(paramsDefParam?.ParameterValue) switch
            {
                null or NullLiteral or DefaultLiteral => false,
                StringLiteral lit => !string.IsNullOrWhiteSpace(lit.Value),
                _ => true,
            };
    }
}
