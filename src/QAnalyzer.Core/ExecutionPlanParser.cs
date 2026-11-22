using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace QAnalyzer.Core;

/// <summary>
/// Parser XML SQL Server Execution Plan (.sqlplan / SHOWPLAN_XML) yang aman (tanpa DTD/XXE).
/// Memvalidasi elemen root &lt;ShowPlanXML&gt; dan mendeteksi anti-pattern utama:
/// MissingIndex, Key/RID Lookup, Table Scan, SpillToTempDb/ExchangeSpillDetails, PlanAffectingConvert,
/// NoJoinPredicate, ColumnsWithNoStatistics, MemoryGrantWarning, UnmatchedIndexes,
/// StatementOptmEarlyAbortReason, serta selisih estimasi cardinality (EstimateRows vs ActualRows).
/// </summary>
public sealed class ExecutionPlanParser
{
    public const string RuleMissingIndex = "PLAN_MISSING_INDEX";
    public const string RuleKeyLookup = "PLAN_KEY_LOOKUP";
    public const string RuleTableScan = "PLAN_TABLE_SCAN";
    public const string RuleSpillToTempDb = "PLAN_SPILL_TEMPDB";
    public const string RuleImplicitConvert = "PLAN_IMPLICIT_CONVERT";
    public const string RuleNoJoinPredicate = "PLAN_NO_JOIN_PREDICATE";
    public const string RuleMissingStatistics = "PLAN_NO_STATISTICS";
    public const string RuleMemoryGrantWarning = "PLAN_MEMORY_GRANT_WARNING";
    public const string RuleUnmatchedIndex = "PLAN_UNMATCHED_INDEX";
    public const string RuleOptimizerEarlyAbort = "PLAN_OPTIMIZER_ABORT";
    public const string RuleEstimateSkew = "PLAN_ESTIMATE_SKEW";

    public const int MaxXmlDepth = 256;
    public const int MaxDiagnostics = 100;

    internal const double EstimateSkewMinRatio = 10.0;
    internal const double EstimateSkewMinRowDelta = 1000.0;

    private const long MaxPlanCharacters = 10_000_000;
    private const int MaxExpressionPreviewLength = 160;
    private const int MaxIdentifierLength = 128;
    private const int MaxAttributeLength = 64;
    private const int MaxIndexSuffixLength = 64;
    private const int MaxMissingIndexColumnsPerGroup = 16;
    private const int MaxListedMissingStatColumns = 5;
    private const int MaxListedUnmatchedIndexes = 5;

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
            var settings = CreateXmlReaderSettings();
            ValidateXmlStructure(sqlPlanXml, settings);

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

        if (!string.Equals(root.Name.LocalName, "ShowPlanXML", StringComparison.Ordinal))
        {
            throw new FormatException("XML bukan SQL Server Execution Plan yang valid (elemen root harus <ShowPlanXML>).");
        }

        // Prioritaskan temuan Critical terlebih dahulu agar tidak tergeser saat jumlah diagnostic dibatasi.
        CollectTempDbSpills(root, diagnostics);
        CollectNoJoinPredicates(root, diagnostics);
        CollectMissingIndexes(root, diagnostics);
        CollectOperatorIssues(root, diagnostics);
        CollectImplicitConversions(root, diagnostics);
        CollectColumnsWithNoStatistics(root, diagnostics);
        CollectMemoryGrantWarnings(root, diagnostics);
        CollectUnmatchedIndexes(root, diagnostics);
        CollectOptimizerEarlyAborts(root, diagnostics);
        CollectEstimateSkews(root, diagnostics);

        return diagnostics;
    }

    private static XmlReaderSettings CreateXmlReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxPlanCharacters,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    private static void ValidateXmlStructure(string sqlPlanXml, XmlReaderSettings settings)
    {
        using var stringReader = new StringReader(sqlPlanXml);
        using var xmlReader = XmlReader.Create(stringReader, settings);
        var seenRoot = false;

        while (xmlReader.Read())
        {
            if (xmlReader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (!seenRoot)
            {
                if (!string.Equals(xmlReader.LocalName, "ShowPlanXML", StringComparison.Ordinal))
                {
                    throw new FormatException("XML bukan SQL Server Execution Plan yang valid (elemen root harus <ShowPlanXML>).");
                }

                seenRoot = true;
            }

            if (xmlReader.Depth >= MaxXmlDepth)
            {
                throw new FormatException($"XML execution plan melebihi batas kedalaman maksimal {MaxXmlDepth} tingkat.");
            }
        }
    }

    private static void CollectMissingIndexes(XElement root, List<Diagnostic> diagnostics)
    {
        if (diagnostics.Count >= MaxDiagnostics)
        {
            return;
        }

        foreach (var group in DescendantsByLocalName(root, "MissingIndexGroup"))
        {
            if (diagnostics.Count >= MaxDiagnostics)
            {
                return;
            }

            var impactRaw = (string?)group.Attribute("Impact");
            var impactText = FormatImpact(impactRaw);

            foreach (var missing in ElementsByLocalName(group, "MissingIndex"))
            {
                if (diagnostics.Count >= MaxDiagnostics)
                {
                    return;
                }

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
        if (diagnostics.Count >= MaxDiagnostics)
        {
            return;
        }

        foreach (var relOp in DescendantsByLocalName(root, "RelOp"))
        {
            if (diagnostics.Count >= MaxDiagnostics)
            {
                return;
            }

            var physicalOp = NormalizeSingleLine((string?)relOp.Attribute("PhysicalOp"), MaxAttributeLength);
            var indexScan = ElementsByLocalName(relOp, "IndexScan").FirstOrDefault();
            var ridLookup = ElementsByLocalName(relOp, "RIDLookup").FirstOrDefault();
            var hasIndexLookupAttr = indexScan is not null && IsTruthy((string?)indexScan.Attribute("Lookup"));
            var hasRidLookupAttr = ridLookup is not null && IsTruthy((string?)ridLookup.Attribute("Lookup"));
            var isLookupAttr = hasIndexLookupAttr || hasRidLookupAttr;

            if (isLookupAttr
                || string.Equals(physicalOp, "Key Lookup", StringComparison.OrdinalIgnoreCase)
                || string.Equals(physicalOp, "RID Lookup", StringComparison.OrdinalIgnoreCase))
            {
                var lookupContainer = hasIndexLookupAttr
                    ? indexScan!
                    : (hasRidLookupAttr ? ridLookup! : (indexScan ?? ridLookup ?? relOp));
                var obj = FindScopedTargetObject(lookupContainer);
                var table = FormatObjectName((string?)obj?.Attribute("Schema"), (string?)obj?.Attribute("Table"));
                var indexName = SanitizeIdentifier((string?)obj?.Attribute("Index"));
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
                var obj = FindScopedTargetObject(tableScan ?? relOp);
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
        if (diagnostics.Count >= MaxDiagnostics)
        {
            return;
        }

        foreach (var spill in DescendantsByLocalName(root, "SpillToTempDb"))
        {
            if (diagnostics.Count >= MaxDiagnostics)
            {
                return;
            }

            var spillLevel = NormalizeSingleLine((string?)spill.Attribute("SpillLevel"), MaxAttributeLength);
            var parentRelOp = FindParentRelOp(spill);
            var physicalOp = NormalizeSingleLine((string?)parentRelOp?.Attribute("PhysicalOp"), MaxAttributeLength);
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

        if (diagnostics.Count >= MaxDiagnostics)
        {
            return;
        }

        foreach (var exchangeSpill in DescendantsByLocalName(root, "ExchangeSpillDetails"))
        {
            if (diagnostics.Count >= MaxDiagnostics) return;
            if (exchangeSpill.Parent is { Name.LocalName: "Warnings" } parentWarnings
                && ElementsByLocalName(parentWarnings, "SpillToTempDb").Any())
            {
                continue;
            }

            var writesText = FormatNonNegativeLong((string?)exchangeSpill.Attribute("WritesToTempDb"));
            var physicalOp = NormalizeSingleLine((string?)FindParentRelOp(exchangeSpill)?.Attribute("PhysicalOp"), MaxAttributeLength);
            var (line, col) = GetLineInfo(exchangeSpill);
            var opDesc = string.IsNullOrEmpty(physicalOp) ? "operator paralelisme" : $"operator {physicalOp}";
            var writesDesc = string.IsNullOrEmpty(writesText) ? string.Empty : $" ({writesText} halaman ditulis ke tempdb)";

            diagnostics.Add(new Diagnostic(
                RiskLevel.Critical,
                RuleSpillToTempDb,
                "Spill to TempDb Terdeteksi",
                $"Terjadi exchange spill ke tempdb pada {opDesc}{writesDesc} akibat ketidakseimbangan atau hambatan antar thread paralel.",
                "Perbarui statistik tabel (UPDATE STATISTICS), periksa skew data/join, atau pertimbangkan penyesuaian MAXDOP pada query ini.",
                line,
                col));
        }
    }

    private static void CollectImplicitConversions(XElement root, List<Diagnostic> diagnostics)
    {
        if (diagnostics.Count >= MaxDiagnostics)
        {
            return;
        }

        foreach (var convert in DescendantsByLocalName(root, "PlanAffectingConvert"))
        {
            if (diagnostics.Count >= MaxDiagnostics)
            {
                return;
            }

            var convertIssue = NormalizeSingleLine((string?)convert.Attribute("ConvertIssue"), MaxAttributeLength);
            var exprPreview = FormatExpressionPreview((string?)convert.Attribute("Expression"));
            var exprDesc = string.IsNullOrEmpty(exprPreview) ? string.Empty : $" ({exprPreview})";
            var (line, col) = GetLineInfo(convert);

            string message;
            string suggestion;
            if (string.Equals(convertIssue, "Seek Plan", StringComparison.OrdinalIgnoreCase))
            {
                message = $"Konversi tipe data implisit terdeteksi yang mencegah penggunaan Index Seek (ConvertIssue: Seek Plan){exprDesc}.";
                suggestion = "Samakan tipe data parameter/ekspresi dengan tipe kolom (mis. hindari perbandingan VARCHAR vs NVARCHAR) agar tidak memicu CONVERT_IMPLICIT dan tetap memakai Index Seek.";
            }
            else
            {
                var issueDesc = string.IsNullOrWhiteSpace(convertIssue)
                    ? "memengaruhi rencana eksekusi"
                    : $"memengaruhi rencana eksekusi (ConvertIssue: {convertIssue})";
                message = $"Konversi tipe data implisit terdeteksi yang {issueDesc}{exprDesc}.";
                suggestion = "Samakan tipe data kolom dan parameter/ekspresi agar estimasi cardinality akurat dan terhindar dari CONVERT_IMPLICIT.";
            }

            diagnostics.Add(new Diagnostic(
                RiskLevel.Warning,
                RuleImplicitConvert,
                "Implicit Conversion Memengaruhi Plan",
                message,
                suggestion,
                line,
                col));
        }
    }

    private static void CollectNoJoinPredicates(XElement root, List<Diagnostic> diagnostics)
    {
        if (diagnostics.Count >= MaxDiagnostics)
        {
            return;
        }

        foreach (var warnings in DescendantsByLocalName(root, "Warnings"))
        {
            if (diagnostics.Count >= MaxDiagnostics)
            {
                return;
            }

            if (!IsTruthy((string?)warnings.Attribute("NoJoinPredicate")))
            {
                continue;
            }

            var parentRelOp = FindParentRelOp(warnings);
            var physicalOp = NormalizeSingleLine((string?)parentRelOp?.Attribute("PhysicalOp"), MaxAttributeLength);
            var (line, col) = GetLineInfo(warnings);

            var opDesc = string.IsNullOrEmpty(physicalOp) ? "operator join" : $"operator {physicalOp}";

            diagnostics.Add(new Diagnostic(
                RiskLevel.Critical,
                RuleNoJoinPredicate,
                "Join Tanpa Predicate (Cartesian)",
                $"Terdeteksi {opDesc} tanpa predicate JOIN (NoJoinPredicate) — berisiko menghasilkan Cartesian product.",
                "Pastikan klausa ON / WHERE menghubungkan seluruh tabel pada join dengan kolom relasi yang tepat.",
                line,
                col));
        }
    }

    private static void CollectColumnsWithNoStatistics(XElement root, List<Diagnostic> diagnostics)
    {
        if (diagnostics.Count >= MaxDiagnostics)
        {
            return;
        }

        foreach (var missingStats in DescendantsByLocalName(root, "ColumnsWithNoStatistics"))
        {
            if (diagnostics.Count >= MaxDiagnostics)
            {
                return;
            }

            var columns = ElementsByLocalName(missingStats, "ColumnReference")
                .Select(FormatColumnReference)
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var (line, col) = GetLineInfo(missingStats);

            string message;
            if (columns.Count == 0)
            {
                message = "Optimizer tidak menemukan statistik pada satu atau lebih kolom yang digunakan query sehingga estimasi cardinality berisiko tidak akurat.";
            }
            else
            {
                var listed = columns.Take(MaxListedMissingStatColumns);
                var overflow = columns.Count > MaxListedMissingStatColumns
                    ? $" (+{columns.Count - MaxListedMissingStatColumns} lainnya)"
                    : string.Empty;
                var formattedColumns = string.Join(", ", listed) + overflow;
                message = $"Kolom tanpa statistik terdeteksi ({formattedColumns}); estimasi cardinality optimizer berisiko tidak akurat.";
            }

            diagnostics.Add(new Diagnostic(
                RiskLevel.Warning,
                RuleMissingStatistics,
                "Kolom Tanpa Statistik di Execution Plan",
                message,
                "Buat atau perbarui statistik kolom terkait (CREATE STATISTICS / UPDATE STATISTICS) dan pastikan opsi AUTO_CREATE_STATISTICS aktif.",
                line,
                col));
        }
    }

    private static void CollectMemoryGrantWarnings(XElement root, List<Diagnostic> diagnostics)
    {
        if (diagnostics.Count >= MaxDiagnostics) return;

        foreach (var warning in DescendantsByLocalName(root, "MemoryGrantWarning"))
        {
            if (diagnostics.Count >= MaxDiagnostics) return;

            var kind = NormalizeSingleLine((string?)warning.Attribute("GrantWarningKind"), MaxAttributeLength);
            var metrics = new (string Attr, string Label)[]
                {
                    ("RequestedMemory", "requested"),
                    ("GrantedMemory", "granted"),
                    ("MaxUsedMemory", "used"),
                }
                .Select(m => (m.Label, Value: FormatNonNegativeLong((string?)warning.Attribute(m.Attr))))
                .Where(m => !string.IsNullOrEmpty(m.Value))
                .Select(m => $"{m.Label}={m.Value} KB")
                .ToList();

            var metricsDesc = metrics.Count > 0 ? $" ({string.Join(", ", metrics)})" : string.Empty;
            var (message, suggestion) = kind switch
            {
                _ when string.Equals(kind, "Excessive Grant", StringComparison.OrdinalIgnoreCase) => (
                    $"Peringatan memory grant '{kind}'{metricsDesc} — alokasi memori jauh melebihi pemakaian aktual dan berisiko memboroskan workspace memory.",
                    "Perbarui statistik tabel (UPDATE STATISTICS), perbaiki estimasi cardinality, atau tinjau tipe/ukuran kolom dan parameter sniffing."),
                _ when string.Equals(kind, "Used More Than Granted", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(kind, "Grant Increase", StringComparison.OrdinalIgnoreCase) => (
                    $"Peringatan memory grant '{kind}'{metricsDesc} — memori awal tidak mencukupi sehingga berisiko memicu spill ke tempdb atau grant wait.",
                    "Perbarui statistik tabel (UPDATE STATISTICS), perbaiki estimasi cardinality, dan kurangi beban sort/hash dengan index yang sesuai."),
                _ => (
                    $"Peringatan memory grant{(string.IsNullOrEmpty(kind) ? string.Empty : $" '{kind}'")} terdeteksi{metricsDesc} pada eksekusi query.",
                    "Perbarui statistik tabel (UPDATE STATISTICS) dan periksa kesesuaian estimasi cardinality dengan ukuran data aktual."),
            };

            var (line, col) = GetLineInfo(warning);
            diagnostics.Add(new Diagnostic(RiskLevel.Warning, RuleMemoryGrantWarning, "Peringatan Memory Grant di Execution Plan", message, suggestion, line, col));
        }
    }

    private static void CollectUnmatchedIndexes(XElement root, List<Diagnostic> diagnostics)
    {
        if (diagnostics.Count >= MaxDiagnostics) return;

        foreach (var unmatched in DescendantsByLocalName(root, "UnmatchedIndexes"))
        {
            if (diagnostics.Count >= MaxDiagnostics) return;

            var indexes = DescendantsByLocalName(unmatched, "Object")
                .Select(FormatUnmatchedIndexObject)
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var message = indexes.Count == 0
                ? "Filtered index tidak dapat dicocokkan dengan query terparameterisasi (UnmatchedIndexes)."
                : $"Filtered index berikut tidak dapat dipakai akibat parameterisasi ({string.Join(", ", indexes.Take(MaxListedUnmatchedIndexes))}{(indexes.Count > MaxListedUnmatchedIndexes ? $" (+{indexes.Count - MaxListedUnmatchedIndexes} lainnya)" : string.Empty)}).";

            var (line, col) = GetLineInfo(unmatched);
            diagnostics.Add(new Diagnostic(
                RiskLevel.Warning,
                RuleUnmatchedIndex,
                "Filtered Index Tidak Terpakai (UnmatchedIndexes)",
                message,
                "Gunakan nilai literal pada predicate filter index, tambahkan OPTION (RECOMPILE), atau sesuaikan definisi filtered index.",
                line,
                col));
        }
    }

    private static void CollectOptimizerEarlyAborts(XElement root, List<Diagnostic> diagnostics)
    {
        if (diagnostics.Count >= MaxDiagnostics) return;

        foreach (var stmt in root.Descendants().Where(static e => e.Name.LocalName is "StmtSimple" or "StmtCond"))
        {
            if (diagnostics.Count >= MaxDiagnostics) return;

            var reason = NormalizeSingleLine((string?)stmt.Attribute("StatementOptmEarlyAbortReason"), MaxAttributeLength);
            var details = reason switch
            {
                _ when string.Equals(reason, "TimeOut", StringComparison.OrdinalIgnoreCase) => (
                    Message: $"Optimizer berhenti lebih awal ({reason}) sehingga execution plan yang dihasilkan mungkin belum optimal.",
                    Suggestion: "Sederhanakan query, pecah join/CTE yang kompleks, perbarui statistik (UPDATE STATISTICS), atau periksa kondisi index."),
                _ when string.Equals(reason, "MemoryLimitExceeded", StringComparison.OrdinalIgnoreCase) => (
                    Message: $"Optimizer berhenti lebih awal ({reason}) karena batas memori kompilasi tercapai; plan berisiko suboptimal.",
                    Suggestion: "Sederhanakan kompleksitas query/CTE, perbarui statistik, dan periksa tekanan memori SQL Server."),
                _ => default,
            };

            if (details.Message is null) continue;
            var (line, col) = GetLineInfo(stmt);
            diagnostics.Add(new Diagnostic(RiskLevel.Warning, RuleOptimizerEarlyAbort, "Optimizer Berhenti Lebih Awal", details.Message, details.Suggestion, line, col));
        }
    }

    private static void CollectEstimateSkews(XElement root, List<Diagnostic> diagnostics)
    {
        if (diagnostics.Count >= MaxDiagnostics) return;

        foreach (var relOp in DescendantsByLocalName(root, "RelOp"))
        {
            if (diagnostics.Count >= MaxDiagnostics) return;
            if (!TryParseNonNegativeDouble((string?)relOp.Attribute("EstimateRows"), out var estimateRows)) continue;

            var runTimeInfo = ElementsByLocalName(relOp, "RunTimeInformation").FirstOrDefault();
            if (runTimeInfo is null) continue;

            double actualRows = 0, totalExecutions = 0, maxThreadExecutions = 0;
            bool hasActualRows = false, hasActualExecutions = false;
            var activeThreads = 0;

            foreach (var thread in ElementsByLocalName(runTimeInfo, "RunTimeCountersPerThread"))
            {
                if (TryParseNonNegativeDouble((string?)thread.Attribute("ActualRows"), out var threadRows))
                {
                    actualRows += threadRows;
                    hasActualRows = true;
                }

                if (TryParseNonNegativeDouble((string?)thread.Attribute("ActualExecutions"), out var threadExecutions))
                {
                    totalExecutions += threadExecutions;
                    hasActualExecutions = true;
                    if (threadExecutions > maxThreadExecutions) maxThreadExecutions = threadExecutions;
                    if (threadExecutions > 0) activeThreads++;
                }
            }

            if (!hasActualRows || !double.IsFinite(actualRows) || !double.IsFinite(totalExecutions)) continue;

            var isParallel = IsTruthy((string?)relOp.Attribute("Parallel")) || activeThreads > 1;
            double effectiveExecutions;
            if (hasActualExecutions)
            {
                if (totalExecutions <= 0 || (isParallel && maxThreadExecutions > 1)) continue;
                effectiveExecutions = isParallel ? 1.0 : totalExecutions;
            }
            else
            {
                effectiveExecutions = 1.0;
            }

            var expectedTotal = estimateRows * effectiveExecutions;
            if (!double.IsFinite(expectedTotal) || Math.Abs(actualRows - expectedTotal) < EstimateSkewMinRowDelta) continue;

            var ratio = Math.Max(
                Math.Max(actualRows, 1.0) / Math.Max(expectedTotal, 1.0),
                Math.Max(expectedTotal, 1.0) / Math.Max(actualRows, 1.0));
            if (!double.IsFinite(ratio) || ratio < EstimateSkewMinRatio) continue;

            var physicalOp = NormalizeSingleLine((string?)relOp.Attribute("PhysicalOp"), MaxAttributeLength);
            var nodeId = NormalizeSingleLine((string?)relOp.Attribute("NodeId"), MaxAttributeLength);
            var opDesc = (physicalOp, nodeId) switch
            {
                ({ Length: > 0 }, { Length: > 0 }) => $"operator {physicalOp} (NodeId {nodeId})",
                ({ Length: > 0 }, _) => $"operator {physicalOp}",
                (_, { Length: > 0 }) => $"operator NodeId {nodeId}",
                _ => "operator eksekusi",
            };

            var direction = actualRows >= expectedTotal ? "underestimate" : "overestimate";
            var ratioText = ratio >= 1e15
                ? ratio.ToString("0.#E+0", CultureInfo.InvariantCulture)
                : ratio.ToString("0.#", CultureInfo.InvariantCulture);
            var (line, col) = GetLineInfo(relOp);

            diagnostics.Add(new Diagnostic(
                RiskLevel.Warning,
                RuleEstimateSkew,
                "Estimasi Cardinality Meleset Jauh",
                $"Estimasi cardinality {direction} (~{ratioText}x) pada {opDesc}: estimasi {FormatRowCount(expectedTotal)} baris vs aktual {FormatRowCount(actualRows)} baris.",
                "Perbarui statistik tabel (UPDATE STATISTICS), periksa predicate non-SARGable atau parameter sniffing, dan pastikan index serta statistik kolom filter akurat.",
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
            .Select(c => SanitizeIdentifier((string?)c.Attribute("Name")))
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxMissingIndexColumnsPerGroup)
            .ToList();

    private static XElement? FindScopedTargetObject(XElement container)
    {
        var stack = new Stack<XElement>();
        PushChildrenReversed(stack, container);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.Name.LocalName == "RelOp")
            {
                continue;
            }

            if (current.Name.LocalName == "Object")
            {
                return current;
            }

            PushChildrenReversed(stack, current);
        }

        return null;
    }

    private static void PushChildrenReversed(Stack<XElement> stack, XElement parent)
    {
        for (var node = parent.LastNode; node is not null; node = node.PreviousNode)
        {
            if (node is XElement child)
            {
                stack.Push(child);
            }
        }
    }

    private static XElement? FindParentRelOp(XElement element)
    {
        for (var current = element.Parent; current is not null; current = current.Parent)
        {
            if (current.Name.LocalName == "RelOp")
            {
                return current;
            }
        }

        return null;
    }

    private static string BuildCreateIndexSuggestion(string table, List<string> keyCols, List<string> includeCols)
    {
        if (keyCols.Count == 0)
        {
            return $"Evaluasi pembuatan nonclustered index pada {table} sesuai kolom filter query.";
        }

        var safeTableSuffix = new string(table.Where(char.IsLetterOrDigit).Take(MaxIndexSuffixLength).ToArray());
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
        var cleanSchema = SanitizeIdentifier(schema);
        var cleanTable = SanitizeIdentifier(table);

        if (string.IsNullOrEmpty(cleanTable))
        {
            return "(tabel tidak diketahui)";
        }

        return string.IsNullOrEmpty(cleanSchema) ? cleanTable : $"{cleanSchema}.{cleanTable}";
    }

    private static string FormatColumnReference(XElement colRef)
    {
        var schema = SanitizeIdentifier((string?)colRef.Attribute("Schema"));
        var table = SanitizeIdentifier((string?)colRef.Attribute("Table"));
        var column = SanitizeIdentifier((string?)colRef.Attribute("Column"));
        if (string.IsNullOrEmpty(column))
        {
            return string.Empty;
        }

        return string.Join(".", new[] { schema, table, column }.Where(part => !string.IsNullOrEmpty(part)));
    }

    private static string FormatUnmatchedIndexObject(XElement obj)
    {
        var schema = SanitizeIdentifier((string?)obj.Attribute("Schema"));
        var table = SanitizeIdentifier((string?)obj.Attribute("Table"));
        var index = SanitizeIdentifier((string?)obj.Attribute("Index"));

        if (!string.IsNullOrEmpty(table))
        {
            var tableName = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
            return string.IsNullOrEmpty(index) ? tableName : $"{tableName} ({index})";
        }

        return index;
    }

    private static string FormatNonNegativeLong(string? raw) =>
        long.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

    private static bool TryParseNonNegativeDouble(string? raw, out double value)
    {
        if (double.TryParse(raw?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value)
            && value >= 0)
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static string FormatRowCount(double value) =>
        value >= 1e15
            ? value.ToString("0.##E+0", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatExpressionPreview(string? raw) =>
        NormalizeSingleLine(raw, MaxExpressionPreviewLength);

    private static string SanitizeIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return string.Empty;
        }

        var trimmed = identifier.Trim().Trim('[', ']');
        return NormalizeSingleLine(trimmed, MaxIdentifierLength);
    }

    private static string NormalizeSingleLine(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw) || maxLength <= 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(Math.Min(raw.Length, maxLength + 1));
        var pendingSpace = false;
        var truncated = false;

        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                if (sb.Length > 0)
                {
                    pendingSpace = true;
                }

                continue;
            }

            if (pendingSpace)
            {
                if (sb.Length >= maxLength)
                {
                    truncated = true;
                    break;
                }

                sb.Append(' ');
                pendingSpace = false;
            }

            if (sb.Length >= maxLength)
            {
                truncated = true;
                break;
            }

            sb.Append(ch);
        }

        if (truncated)
        {
            while (sb.Length > 0 && sb[^1] == ' ')
            {
                sb.Length--;
            }

            sb.Append('…');
        }

        return sb.ToString();
    }

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
