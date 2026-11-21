using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace QAnalyzer.Core;

/// <summary>
/// Parser XML SQL Server Execution Plan (.sqlplan / SHOWPLAN_XML) yang aman (tanpa DTD/XXE).
/// Mendeteksi anti-pattern utama: MissingIndex, Key/RID Lookup, Table Scan, dan SpillToTempDb.
/// </summary>
public sealed class ExecutionPlanParser
{
    public const string RuleMissingIndex = "PLAN_MISSING_INDEX";
    public const string RuleKeyLookup = "PLAN_KEY_LOOKUP";
    public const string RuleTableScan = "PLAN_TABLE_SCAN";
    public const string RuleSpillToTempDb = "PLAN_SPILL_TEMPDB";

    private const long MaxPlanCharacters = 10_000_000;

    public IReadOnlyList<Diagnostic> Parse(string sqlPlanXml)
    {
        ArgumentNullException.ThrowIfNull(sqlPlanXml);

        if (string.IsNullOrWhiteSpace(sqlPlanXml))
        {
            return [];
        }

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxPlanCharacters,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };

            using var stringReader = new StringReader(sqlPlanXml);
            using var xmlReader = XmlReader.Create(stringReader, settings);
            document = XDocument.Load(xmlReader, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            throw new FormatException($"XML execution plan tidak valid: {ex.Message}", ex);
        }

        var diagnostics = new List<Diagnostic>();
        var root = document.Root;
        if (root is null)
        {
            return diagnostics;
        }

        CollectMissingIndexes(root, diagnostics);
        CollectOperatorIssues(root, diagnostics);
        CollectTempDbSpills(root, diagnostics);

        return diagnostics;
    }

    private static void CollectMissingIndexes(XElement root, List<Diagnostic> diagnostics)
    {
        foreach (var group in DescendantsByLocalName(root, "MissingIndexGroup"))
        {
            var impactRaw = (string?)group.Attribute("Impact");
            var impactText = FormatImpact(impactRaw);

            foreach (var missing in ElementsByLocalName(group, "MissingIndex"))
            {
                var table = FormatObjectName((string?)missing.Attribute("Schema"), (string?)missing.Attribute("Table"));
                var equalityCols = ReadColumns(missing, "EQUALITY");
                var inequalityCols = ReadColumns(missing, "INEQUALITY");
                var includeCols = ReadColumns(missing, "INCLUDE");

                var keyCols = equalityCols.Concat(inequalityCols).ToList();
                var (line, col) = GetLineInfo(missing);

                var message = string.IsNullOrEmpty(impactText)
                    ? $"SQL Server menyarankan index tambahan pada tabel {table}."
                    : $"SQL Server menyarankan index tambahan pada tabel {table} (estimasi dampak ~{impactText}%).";

                var suggestion = BuildCreateIndexSuggestion(table, keyCols, includeCols);

                diagnostics.Add(new Diagnostic(
                    RiskLevel.Warning,
                    RuleMissingIndex,
                    "Missing Index di Execution Plan",
                    message,
                    suggestion,
                    line,
                    col));
            }
        }
    }

    private static void CollectOperatorIssues(XElement root, List<Diagnostic> diagnostics)
    {
        foreach (var relOp in DescendantsByLocalName(root, "RelOp"))
        {
            var physicalOp = ((string?)relOp.Attribute("PhysicalOp"))?.Trim() ?? string.Empty;
            var indexScan = ElementsByLocalName(relOp, "IndexScan").FirstOrDefault();
            var isLookupAttr = indexScan is not null && IsTruthy((string?)indexScan.Attribute("Lookup"));

            if (isLookupAttr
                || string.Equals(physicalOp, "Key Lookup", StringComparison.OrdinalIgnoreCase)
                || string.Equals(physicalOp, "RID Lookup", StringComparison.OrdinalIgnoreCase))
            {
                var obj = FindTargetObject(indexScan ?? relOp);
                var table = FormatObjectName((string?)obj?.Attribute("Schema"), (string?)obj?.Attribute("Table"));
                var indexName = StripBrackets((string?)obj?.Attribute("Index"));
                var (line, col) = GetLineInfo(relOp);

                var targetDesc = string.IsNullOrEmpty(indexName)
                    ? table
                    : $"{table} ({indexName})";

                diagnostics.Add(new Diagnostic(
                    RiskLevel.Warning,
                    RuleKeyLookup,
                    "Key / RID Lookup Terdeteksi",
                    $"Operator lookup ditemukan pada {targetDesc}; setiap baris memicu pembacaan tambahan ke clustered index/heap.",
                    "Pertimbangkan menambahkan kolom yang diambil ke klausa INCLUDE pada nonclustered index agar menjadi covering index.",
                    line,
                    col));

                continue;
            }

            if (string.Equals(physicalOp, "Table Scan", StringComparison.OrdinalIgnoreCase))
            {
                var tableScan = ElementsByLocalName(relOp, "TableScan").FirstOrDefault();
                var obj = FindTargetObject(tableScan ?? relOp);
                var table = FormatObjectName((string?)obj?.Attribute("Schema"), (string?)obj?.Attribute("Table"));
                var (line, col) = GetLineInfo(relOp);

                diagnostics.Add(new Diagnostic(
                    RiskLevel.Warning,
                    RuleTableScan,
                    "Table Scan (Heap) Terdeteksi",
                    $"Operator Table Scan membaca seluruh halaman tabel {table}.",
                    "Tambahkan clustered index atau nonclustered index yang sesuai dengan kolom filter WHERE/JOIN.",
                    line,
                    col));
            }
        }
    }

    private static void CollectTempDbSpills(XElement root, List<Diagnostic> diagnostics)
    {
        foreach (var spill in DescendantsByLocalName(root, "SpillToTempDb"))
        {
            var spillLevel = ((string?)spill.Attribute("SpillLevel"))?.Trim();
            var parentRelOp = spill.Ancestors().FirstOrDefault(a => a.Name.LocalName == "RelOp");
            var physicalOp = ((string?)parentRelOp?.Attribute("PhysicalOp"))?.Trim();
            var (line, col) = GetLineInfo(spill);

            var opDesc = string.IsNullOrEmpty(physicalOp) ? "operator eksekusi" : $"operator {physicalOp}";
            var levelDesc = string.IsNullOrEmpty(spillLevel) ? string.Empty : $" (SpillLevel {spillLevel})";

            diagnostics.Add(new Diagnostic(
                RiskLevel.Critical,
                RuleSpillToTempDb,
                "Spill to TempDb Terdeteksi",
                $"Terjadi spill ke tempdb pada {opDesc}{levelDesc} akibat alokasi memory grant tidak mencukupi.",
                "Perbarui statistik tabel (UPDATE STATISTICS), periksa estimasi cardinality, atau tambahkan index untuk menghindari operasi Sort/Hash besar.",
                line,
                col));
        }
    }

    private static IEnumerable<XElement> DescendantsByLocalName(XElement root, string localName) =>
        root.Descendants().Where(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> ElementsByLocalName(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName == localName);

    private static List<string> ReadColumns(XElement missingIndex, string usage) =>
        ElementsByLocalName(missingIndex, "ColumnGroup")
            .Where(cg => string.Equals((string?)cg.Attribute("Usage"), usage, StringComparison.OrdinalIgnoreCase))
            .SelectMany(cg => ElementsByLocalName(cg, "Column"))
            .Select(c => StripBrackets((string?)c.Attribute("Name")))
            .Where(name => !string.IsNullOrEmpty(name))
            .ToList();

    private static XElement? FindTargetObject(XElement container) =>
        DescendantsByLocalName(container, "Object").FirstOrDefault();

    private static string BuildCreateIndexSuggestion(string table, List<string> keyCols, List<string> includeCols)
    {
        if (keyCols.Count == 0)
        {
            return $"Evaluasi pembuatan nonclustered index pada {table} sesuai kolom filter query.";
        }

        var safeTableSuffix = new string(table.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrEmpty(safeTableSuffix))
        {
            safeTableSuffix = "Table";
        }

        var keys = string.Join(", ", keyCols);
        var includePart = includeCols.Count > 0
            ? $" INCLUDE ({string.Join(", ", includeCols)})"
            : string.Empty;

        return $"CREATE NONCLUSTERED INDEX IX_{safeTableSuffix}_Auto ON {table} ({keys}){includePart};";
    }

    private static string FormatObjectName(string? schema, string? table)
    {
        var cleanSchema = StripBrackets(schema);
        var cleanTable = StripBrackets(table);

        if (string.IsNullOrEmpty(cleanTable))
        {
            return "(tabel tidak diketahui)";
        }

        return string.IsNullOrEmpty(cleanSchema) ? cleanTable : $"{cleanSchema}.{cleanTable}";
    }

    private static string StripBrackets(string? identifier) =>
        string.IsNullOrWhiteSpace(identifier)
            ? string.Empty
            : identifier.Trim().Trim('[', ']');

    private static string FormatImpact(string? impactRaw) =>
        double.TryParse(impactRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var impact)
            ? impact.ToString("0.##", CultureInfo.InvariantCulture)
            : string.Empty;

    private static bool IsTruthy(string? value) =>
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value?.Trim(), "1", StringComparison.Ordinal);

    private static (int Line, int Column) GetLineInfo(XElement element) =>
        element is IXmlLineInfo info && info.HasLineInfo()
            ? (info.LineNumber, info.LinePosition)
            : (0, 0);
}
