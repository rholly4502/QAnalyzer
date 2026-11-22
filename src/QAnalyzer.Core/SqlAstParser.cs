using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core;

/// <summary>
/// Kontrak internal untuk rule statis berbasis AST ScriptDom agar satu script
/// hanya perlu di-parse sekali per panggilan <see cref="SqlAnalyzer.Analyze"/>.
/// </summary>
internal interface IAstSqlRule : ISqlRule
{
    IEnumerable<Diagnostic> Evaluate(TSqlFragment fragment);
}

/// <summary>
/// Kontrak internal untuk rule yang mengevaluasi daftar <see cref="ParseError"/> ScriptDom
/// agar satu script tetap hanya di-parse sekali per panggilan <see cref="SqlAnalyzer.Analyze"/>.
/// </summary>
internal interface IAstParseErrorRule : ISqlRule
{
    IEnumerable<Diagnostic> Evaluate(IReadOnlyList<ParseError> errors);
}

/// <summary>
/// Helper internal pembungkus <see cref="TSql170Parser"/> agar tipe ScriptDom
/// tidak bocor ke API publik <c>QAnalyzer.Core</c>, sekaligus menjaga parser tetap aman dari nesting DoS.
/// </summary>
internal static class SqlAstParser
{
    private static readonly HashSet<TSqlTokenType> StandaloneStatementStarters =
    [
        TSqlTokenType.Declare, TSqlTokenType.Exec, TSqlTokenType.Execute, TSqlTokenType.Print,
        TSqlTokenType.Return, TSqlTokenType.Break, TSqlTokenType.Continue, TSqlTokenType.Truncate,
        TSqlTokenType.Create, TSqlTokenType.Alter, TSqlTokenType.Drop, TSqlTokenType.With,
        TSqlTokenType.Commit, TSqlTokenType.Rollback, TSqlTokenType.Raiserror, TSqlTokenType.WaitFor,
        TSqlTokenType.Open, TSqlTokenType.Fetch, TSqlTokenType.Close, TSqlTokenType.Deallocate,
        TSqlTokenType.Use, TSqlTokenType.Grant, TSqlTokenType.Revoke, TSqlTokenType.Deny, TSqlTokenType.GoTo,
    ];

    private static readonly HashSet<TSqlTokenType> ContextualStatementStarters =
    [
        TSqlTokenType.Select, TSqlTokenType.Set, TSqlTokenType.Update,
        TSqlTokenType.Delete, TSqlTokenType.Insert, TSqlTokenType.Merge,
    ];

    private static readonly HashSet<TSqlTokenType> FromTerminators =
    [
        TSqlTokenType.Where, TSqlTokenType.Group, TSqlTokenType.Having, TSqlTokenType.Order,
        TSqlTokenType.Option, TSqlTokenType.For, TSqlTokenType.Union, TSqlTokenType.Except, TSqlTokenType.Intersect,
    ];

    private static readonly HashSet<TSqlTokenType> ChainedOperators =
    [
        TSqlTokenType.Plus, TSqlTokenType.Minus, TSqlTokenType.Star, TSqlTokenType.Divide,
        TSqlTokenType.PercentSign, TSqlTokenType.Ampersand, TSqlTokenType.VerticalLine, TSqlTokenType.Circumflex,
        TSqlTokenType.Concat, TSqlTokenType.And, TSqlTokenType.Or,
        TSqlTokenType.Union, TSqlTokenType.Except, TSqlTokenType.Intersect, TSqlTokenType.Join,
    ];

    public static TSqlFragment? Parse(string sql) => Parse(sql, out _);

    public static TSqlFragment? Parse(string sql, out IList<ParseError> errors)
    {
        if (!TryParse(sql, out var fragment, out errors, out var complexityError))
        {
            throw new FormatException(complexityError);
        }

        return fragment;
    }

    public static bool TryParse(
        string sql,
        out TSqlFragment? fragment,
        out IList<ParseError> errors,
        out string? complexityError)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (string.IsNullOrWhiteSpace(sql))
        {
            fragment = null;
            errors = [];
            complexityError = null;
            return true;
        }

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        IList<TSqlParserToken> tokens;
        IList<ParseError> lexErrors;
        using (var reader = new StringReader(sql))
        {
            tokens = parser.GetTokenStream(reader, out lexErrors);
        }

        var validator = new ComplexityValidator();
        if (!validator.Validate(tokens))
        {
            fragment = null;
            errors = [];
            complexityError = validator.Error;
            return false;
        }

        if (lexErrors.Count > 0)
        {
            // Pertahankan akurasi error lexer (mis. komentar/string tidak tertutup) tanpa mem-parse AST kompleks.
            using var fallbackReader = new StringReader(sql);
            fragment = parser.Parse(fallbackReader, out errors);
            complexityError = null;
            return true;
        }

        fragment = parser.Parse(tokens, out errors);
        complexityError = null;
        return true;
    }

    public static IEnumerable<QuerySpecification> GetDirectQuerySpecifications(QueryExpression? expression)
    {
        switch (expression)
        {
            case QuerySpecification spec:
                yield return spec;
                break;
            case QueryParenthesisExpression paren:
                foreach (var inner in GetDirectQuerySpecifications(paren.QueryExpression)) yield return inner;
                break;
            case BinaryQueryExpression binary:
                foreach (var left in GetDirectQuerySpecifications(binary.FirstQueryExpression)) yield return left;
                foreach (var right in GetDirectQuerySpecifications(binary.SecondQueryExpression)) yield return right;
                break;
        }
    }

    private ref struct ComplexityValidator
    {
        private int _parenDepth, _blockDepth, _consecutiveUnaryOps, _chainedOperators, _totalControlDepth;
        private bool _prevWasElse, _sawSemicolon;
        private TSqlTokenType _prevTokenType;
        private readonly List<ScopeFrame> _scopes;
        private readonly Stack<int> _fromClauseParenDepths;
        public string? Error;

        public ComplexityValidator()
        {
            _prevTokenType = TSqlTokenType.EndOfFile;
            _scopes = [new(0, false, 0, isCase: false)];
            _fromClauseParenDepths = new Stack<int>();
        }

        public bool Validate(IList<TSqlParserToken> tokens)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (IsTriviaOrEof(token.TokenType)) continue;

                if (token.TokenType == TSqlTokenType.Go) { ResetBatch(); continue; }

                if (_sawSemicolon && token.TokenType != TSqlTokenType.Semicolon)
                {
                    _sawSemicolon = false;
                    if (token.TokenType != TSqlTokenType.Else) ResetCompletedControlScope(_scopes[^1]);
                }

                if (token.TokenType is TSqlTokenType.Not or TSqlTokenType.Plus or TSqlTokenType.Minus or TSqlTokenType.Tilde)
                {
                    if (++_consecutiveUnaryOps > SqlAnalyzer.MaxConsecutiveUnaryOperators)
                        return Fail($"Script T-SQL melebihi batas operator unary beruntun maksimal {SqlAnalyzer.MaxConsecutiveUnaryOperators}.");
                }
                else
                {
                    _consecutiveUnaryOps = 0;
                }

                if (token.TokenType == TSqlTokenType.Semicolon) { HandleSemicolon(); continue; }

                var ok = token.TokenType switch
                {
                    TSqlTokenType.LeftParenthesis => EnterParenthesis(),
                    TSqlTokenType.RightParenthesis => ExitParenthesis(),
                    TSqlTokenType.Begin => HandleBegin(tokens, i),
                    TSqlTokenType.Case => EnterBlockScope(isCase: true),
                    TSqlTokenType.End => HandleEnd(),
                    TSqlTokenType.If or TSqlTokenType.While => HandleControlKeyword(),
                    TSqlTokenType.Else => HandleElseKeyword(),
                    _ => HandleGeneralToken(token),
                };
                if (!ok) return false;

                if (token.TokenType != TSqlTokenType.Else) _prevWasElse = false;
                _prevTokenType = token.TokenType;
            }

            return true;
        }

        private void ResetBatch()
        {
            _parenDepth = _blockDepth = _consecutiveUnaryOps = _chainedOperators = _totalControlDepth = 0;
            _prevWasElse = _sawSemicolon = false;
            _prevTokenType = TSqlTokenType.Go;
            _fromClauseParenDepths.Clear();
            _scopes.Clear();
            _scopes.Add(new(0, false, 0, isCase: false));
        }

        private void HandleSemicolon()
        {
            _chainedOperators = 0;
            _fromClauseParenDepths.Clear();
            _prevWasElse = false;
            var top = _scopes[^1];
            if (!top.IsCase && _parenDepth == top.ParenDepth)
            {
                top.AwaitingBody = false;
                _sawSemicolon = true;
            }
            _prevTokenType = TSqlTokenType.Semicolon;
        }

        private bool EnterParenthesis() { _parenDepth++; return !ExceedsNesting(); }

        private bool ExitParenthesis()
        {
            if (_parenDepth > 0) _parenDepth--;
            while (_fromClauseParenDepths.Count > 0 && _parenDepth < _fromClauseParenDepths.Peek()) _fromClauseParenDepths.Pop();
            return true;
        }

        private bool HandleBegin(IList<TSqlParserToken> tokens, int index)
        {
            CompleteOrResetScope(_scopes[^1]);
            ResetStatementOperators();
            return IsNonBlockBegin(NextSignificantToken(tokens, index)) || EnterBlockScope(isCase: false);
        }

        private bool EnterBlockScope(bool isCase)
        {
            _blockDepth++;
            _scopes.Add(new(0, false, _parenDepth, isCase));
            return !ExceedsNesting();
        }

        private bool HandleEnd()
        {
            if (_blockDepth > 0) _blockDepth--;
            if (_scopes.Count > 1)
            {
                var popped = _scopes[^1];
                _scopes.RemoveAt(_scopes.Count - 1);
                _totalControlDepth = Math.Max(0, _totalControlDepth - popped.ControlDepth);
                if (!popped.IsCase) ResetStatementOperators();
            }
            return true;
        }

        private bool HandleControlKeyword()
        {
            ResetStatementOperators();
            var scope = _scopes[^1];
            if (scope.IsCase) return true;
            if (_prevWasElse) _prevWasElse = false;
            else if (scope.AwaitingBody) { scope.ControlDepth++; _totalControlDepth++; }
            else
            {
                _totalControlDepth = Math.Max(0, _totalControlDepth - scope.ControlDepth) + 1;
                scope.ControlDepth = 1;
                scope.AwaitingBody = true;
            }
            return !ExceedsNesting();
        }

        private bool HandleElseKeyword()
        {
            var scope = _scopes[^1];
            if (scope.IsCase) return true;
            ResetStatementOperators();
            scope.ControlDepth++;
            _totalControlDepth++;
            scope.AwaitingBody = true;
            _prevWasElse = true;
            return !ExceedsNesting();
        }

        private bool HandleGeneralToken(TSqlParserToken token)
        {
            var scope = _scopes[^1];
            if (!scope.IsCase && _parenDepth == scope.ParenDepth)
            {
                if (IsStandaloneStatementStarter(token))
                {
                    CompleteOrResetScope(scope);
                    ResetStatementOperators();
                }
                else if (ContextualStatementStarters.Contains(token.TokenType))
                {
                    scope.AwaitingBody = false;
                    var continuesSetOp = token.TokenType == TSqlTokenType.Select
                        && _prevTokenType is TSqlTokenType.Union or TSqlTokenType.Except or TSqlTokenType.Intersect or TSqlTokenType.All;
                    if (!continuesSetOp) ResetStatementOperators();
                }
            }

            if (token.TokenType == TSqlTokenType.From)
            {
                if (_fromClauseParenDepths.Count == 0 || _fromClauseParenDepths.Peek() != _parenDepth)
                    _fromClauseParenDepths.Push(_parenDepth);
            }
            else if (FromTerminators.Contains(token.TokenType))
            {
                while (_fromClauseParenDepths.Count > 0 && _fromClauseParenDepths.Peek() == _parenDepth)
                    _fromClauseParenDepths.Pop();
            }

            if (IsChainedOperator(token) && ++_chainedOperators > SqlAnalyzer.MaxChainedOperatorsPerStatement)
                return Fail($"Script T-SQL melebihi batas kompleksitas operator berantai maksimal {SqlAnalyzer.MaxChainedOperatorsPerStatement} per statement.");

            return true;
        }

        private bool ExceedsNesting() =>
            _parenDepth + _blockDepth + _totalControlDepth > SqlAnalyzer.MaxNestingDepth
            && !Fail($"Script T-SQL melebihi batas kedalaman nesting maksimal {SqlAnalyzer.MaxNestingDepth} tingkat.");

        private bool Fail(string message) { Error = message; return false; }

        private void ResetCompletedControlScope(ScopeFrame scope)
        {
            if (!scope.IsCase && _parenDepth == scope.ParenDepth && !scope.AwaitingBody && scope.ControlDepth > 0)
            {
                _totalControlDepth = Math.Max(0, _totalControlDepth - scope.ControlDepth);
                scope.ControlDepth = 0;
            }
        }

        private void CompleteOrResetScope(ScopeFrame scope)
        {
            if (scope.IsCase || _parenDepth != scope.ParenDepth) return;
            if (scope.AwaitingBody) scope.AwaitingBody = false;
            else if (scope.ControlDepth > 0)
            {
                _totalControlDepth = Math.Max(0, _totalControlDepth - scope.ControlDepth);
                scope.ControlDepth = 0;
            }
        }

        private void ResetStatementOperators()
        {
            _chainedOperators = 0;
            while (_fromClauseParenDepths.Count > 0 && _fromClauseParenDepths.Peek() >= _parenDepth)
                _fromClauseParenDepths.Pop();
        }

        private bool IsChainedOperator(TSqlParserToken token) =>
            ChainedOperators.Contains(token.TokenType)
            || (token.TokenType == TSqlTokenType.Identifier
                && string.Equals(token.Text, "APPLY", StringComparison.OrdinalIgnoreCase)
                && _prevTokenType is TSqlTokenType.Cross or TSqlTokenType.Outer)
            || (token.TokenType == TSqlTokenType.Comma
                && _fromClauseParenDepths.Count > 0
                && _fromClauseParenDepths.Peek() == _parenDepth);
    }

    private static bool IsTriviaOrEof(TSqlTokenType tokenType) =>
        tokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment
            or TSqlTokenType.MultilineComment or TSqlTokenType.EndOfFile;

    private static TSqlParserToken? NextSignificantToken(IList<TSqlParserToken> tokens, int currentIndex)
    {
        for (var j = currentIndex + 1; j < tokens.Count; j++)
            if (!IsTriviaOrEof(tokens[j].TokenType)) return tokens[j];
        return null;
    }

    private static bool IsNonBlockBegin(TSqlParserToken? nextToken) =>
        nextToken is not null
        && (nextToken.TokenType is TSqlTokenType.Tran or TSqlTokenType.Transaction or TSqlTokenType.Distributed
            || (nextToken.TokenType == TSqlTokenType.Identifier
                && (string.Equals(nextToken.Text, "DIALOG", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(nextToken.Text, "CONVERSATION", StringComparison.OrdinalIgnoreCase))));

    private static bool IsStandaloneStatementStarter(TSqlParserToken token) =>
        StandaloneStatementStarters.Contains(token.TokenType)
        || (token.TokenType == TSqlTokenType.Identifier
            && string.Equals(token.Text, "THROW", StringComparison.OrdinalIgnoreCase));

    private sealed class ScopeFrame(int controlDepth, bool awaitingBody, int parenDepth, bool isCase)
    {
        public int ControlDepth { get; set; } = controlDepth;
        public bool AwaitingBody { get; set; } = awaitingBody;
        public int ParenDepth { get; } = parenDepth;
        public bool IsCase { get; } = isCase;
    }
}