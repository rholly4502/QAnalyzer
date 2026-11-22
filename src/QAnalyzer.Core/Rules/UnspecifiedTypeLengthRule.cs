using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core.Rules;

/// <summary>
/// Deteksi tipe data string/biner (<c>VARCHAR</c>, <c>NVARCHAR</c>, <c>VARBINARY</c>,
/// <c>CHAR</c>, <c>NCHAR</c>, <c>BINARY</c>) dan <c>DECIMAL</c>/<c>NUMERIC</c> tanpa panjang
/// atau presisi eksplisit via AST ScriptDom yang memicu silent truncation
/// (panjang default <c>1</c> pada deklarasi/DDL, <c>30</c> pada konversi, atau <c>(18, 0)</c>).
/// </summary>
public sealed class UnspecifiedTypeLengthRule : IAstSqlRule
{
    public string Id => "UNSPECIFIED_TYPE_LENGTH";
    public string Title => "Tipe data tanpa panjang/presisi eksplisit";
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
        var visitor = new UnspecifiedTypeLengthVisitor(Id, Title);
        fragment.Accept(visitor);
        return visitor.Diagnostics
            .OrderBy(static d => d.Line)
            .ThenBy(static d => d.Column);
    }

    private enum TargetTypeKind
    {
        VariableLength,
        FixedLength,
        DecimalNumeric,
    }

    private readonly record struct TargetTypeSpec(string TypeName, string Unit, TargetTypeKind Kind);

    private sealed class UnspecifiedTypeLengthVisitor(string ruleId, string title) : TSqlFragmentVisitor
    {
        private readonly Dictionary<SqlDataTypeReference, string> _conversionContexts = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<SqlDataTypeReference> _ignoredDataTypes = new(ReferenceEqualityComparer.Instance);

        public List<Diagnostic> Diagnostics { get; } = [];

        public override void ExplicitVisit(CastCall node) => VisitConversion(node, node.DataType, "CAST");

        public override void ExplicitVisit(TryCastCall node) => VisitConversion(node, node.DataType, "TRY_CAST");

        public override void ExplicitVisit(ConvertCall node) => VisitConversion(node, node.DataType, "CONVERT");

        public override void ExplicitVisit(TryConvertCall node) => VisitConversion(node, node.DataType, "TRY_CONVERT");

        public override void ExplicitVisit(ParseCall node) => VisitConversion(node, node.DataType, "PARSE");

        public override void ExplicitVisit(TryParseCall node) => VisitConversion(node, node.DataType, "TRY_PARSE");

        public override void ExplicitVisit(DataTypeSequenceOption node) => VisitIgnoredDataType(node, node.DataType);

        public override void ExplicitVisit(IdentityFunctionCall node) => VisitIgnoredDataType(node, node.DataType);

        public override void Visit(SqlDataTypeReference node)
        {
            if (node.Parameters.Count > 0 || _ignoredDataTypes.Contains(node))
            {
                return;
            }

            if (!TryDescribeTargetType(node.SqlDataTypeOption, out var spec))
            {
                return;
            }

            var isConversion = _conversionContexts.TryGetValue(node, out var conversionName);
            var (level, message, suggestion) = spec.Kind switch
            {
                TargetTypeKind.VariableLength => BuildStringOrBinaryDiagnostic(
                    spec.TypeName, spec.Unit, isVariableLength: true, isConversion, conversionName),
                TargetTypeKind.FixedLength => BuildStringOrBinaryDiagnostic(
                    spec.TypeName, spec.Unit, isVariableLength: false, isConversion, conversionName),
                TargetTypeKind.DecimalNumeric => BuildDecimalDiagnostic(
                    spec.TypeName, isConversion, conversionName),
                _ => default,
            };

            Diagnostics.Add(new Diagnostic(
                level,
                ruleId,
                title,
                message,
                suggestion,
                node.StartLine,
                node.StartColumn));
        }

        private void VisitConversion(TSqlFragment node, DataTypeReference? dataType, string conversionName)
        {
            if (dataType is SqlDataTypeReference sqlType)
            {
                _conversionContexts[sqlType] = conversionName;
            }

            node.AcceptChildren(this);
        }

        private void VisitIgnoredDataType(TSqlFragment node, DataTypeReference? dataType)
        {
            if (dataType is SqlDataTypeReference sqlType)
            {
                _ignoredDataTypes.Add(sqlType);
            }

            node.AcceptChildren(this);
        }
    }

    private static bool TryDescribeTargetType(SqlDataTypeOption option, out TargetTypeSpec spec)
    {
        switch (option)
        {
            case SqlDataTypeOption.VarChar:
                spec = new("VARCHAR", "karakter", TargetTypeKind.VariableLength);
                return true;
            case SqlDataTypeOption.NVarChar:
                spec = new("NVARCHAR", "karakter", TargetTypeKind.VariableLength);
                return true;
            case SqlDataTypeOption.VarBinary:
                spec = new("VARBINARY", "byte", TargetTypeKind.VariableLength);
                return true;
            case SqlDataTypeOption.Char:
                spec = new("CHAR", "karakter", TargetTypeKind.FixedLength);
                return true;
            case SqlDataTypeOption.NChar:
                spec = new("NCHAR", "karakter", TargetTypeKind.FixedLength);
                return true;
            case SqlDataTypeOption.Binary:
                spec = new("BINARY", "byte", TargetTypeKind.FixedLength);
                return true;
            case SqlDataTypeOption.Decimal:
                spec = new("DECIMAL", string.Empty, TargetTypeKind.DecimalNumeric);
                return true;
            case SqlDataTypeOption.Numeric:
                spec = new("NUMERIC", string.Empty, TargetTypeKind.DecimalNumeric);
                return true;
            default:
                spec = default;
                return false;
        }
    }

    private static (RiskLevel Level, string Message, string Suggestion) BuildStringOrBinaryDiagnostic(
        string typeName,
        string unit,
        bool isVariableLength,
        bool isConversion,
        string? conversionName)
    {
        if (isConversion)
        {
            var suggestion = isVariableLength
                ? $"Tentukan panjang eksplisit pada {conversionName}, misalnya {typeName}(100) atau {typeName}(MAX)."
                : $"Tentukan panjang eksplisit pada {conversionName}, misalnya {typeName}(100).";

            return (
                RiskLevel.Warning,
                $"Tipe {typeName} pada {conversionName} tanpa panjang eksplisit secara default hanya berkapasitas 30 {unit} di SQL Server dan dapat memotong data secara diam-diam (silent truncation).",
                suggestion);
        }

        if (isVariableLength)
        {
            return (
                RiskLevel.Critical,
                $"Deklarasi tipe {typeName} tanpa panjang eksplisit secara default hanya berkapasitas 1 {unit} di SQL Server — berisiko memotong data secara diam-diam (silent truncation).",
                $"Tentukan panjang eksplisit sesuai kebutuhan data, misalnya {typeName}(50) atau {typeName}(MAX).");
        }

        return (
            RiskLevel.Warning,
            $"Deklarasi tipe {typeName} tanpa panjang eksplisit secara default hanya berkapasitas 1 {unit} di SQL Server dan berisiko memotong data.",
            $"Tentukan panjang eksplisit sesuai kebutuhan, misalnya {typeName}(1) atau {typeName}(10).");
    }

    private static (RiskLevel Level, string Message, string Suggestion) BuildDecimalDiagnostic(
        string typeName,
        bool isConversion,
        string? conversionName)
    {
        var message = isConversion
            ? $"Tipe {typeName} pada {conversionName} tanpa presisi dan skala eksplisit secara default menjadi {typeName}(18, 0) sehingga nilai pecahan/desimal terpotong menjadi bilangan bulat."
            : $"Deklarasi tipe {typeName} tanpa presisi dan skala eksplisit secara default menjadi {typeName}(18, 0) (skala 0) sehingga angka di belakang koma hilang secara diam-diam.";

        return (
            RiskLevel.Warning,
            message,
            $"Tentukan presisi dan skala secara eksplisit, misalnya {typeName}(18, 2).");
    }
}
