using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using QAnalyzer.Core;
using QAnalyzer.Core.Rules;

namespace QAnalyzer.Wpf;

public partial class MainWindow : Window
{
    private static readonly ISqlRule[] DefaultRules =
    [
        new SqlSyntaxErrorRule(),
        new SelectStarRule(),
        new NoLockRule(),
        new UpdateWithoutWhereRule(),
        new DeleteWithoutWhereRule(),
        new TruncateTableRule(),
        new NonSargablePredicateRule(),
    ];

    private readonly ObservableCollection<Diagnostic> _diagnostics = [];
    private readonly List<Diagnostic> _allDiagnostics = [];
    private LevelFilter _filter = new();
    private readonly AppSettingsScanner _scanner = new();
    private readonly HttpClient _http = new();
    private readonly ExecutionPlanParser _planParser = new();
    private SqlAnalyzer _analyzer = new(DefaultRules);
    private AnalyzerSettings _settings = new();
    private ISqlMetadataReader? _metadataReader;
    private CancellationTokenSource? _planCts;
    private bool _dark = false;

    public MainWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _diagnostics;
        // Auto-scan appsettings.json di folder yang sama dengan exe, jika ada.
        var dir = Path.GetDirectoryName(AppContext.BaseDirectory);
        var scanned = _scanner.ScanForDirectory(dir ?? string.Empty);
        if (scanned is not null)
        {
            _settings = scanned.Settings;
            if (scanned.Settings.Profile == AnalysisProfile.Reporting)
                ProfileCombo.SelectedIndex = 1;
            if (!string.IsNullOrWhiteSpace(scanned.ConnectionString))
                _metadataReader = new SqlDmvReader(scanned.ConnectionString);
            StatusText.Text = "appsettings.json terbaca.";
        }

        UpdateDashboard();
    }

    private void OnProfileChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var profile = ProfileCombo.SelectedIndex == 1 ? AnalysisProfile.Reporting : AnalysisProfile.General;
        _settings = _settings with { Profile = profile };
        _analyzer = new SqlAnalyzer(DefaultRules, _settings);
    }

    private void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                ScriptEditor.Text = File.ReadAllText(dlg.FileName);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Gagal membaca file: {ex.Message}";
                return;
            }
            var scanned = _scanner.ScanForDirectory(Path.GetDirectoryName(dlg.FileName) ?? string.Empty);
            if (scanned is not null)
            {
                _settings = scanned.Settings;
                if (_settings.Profile == AnalysisProfile.Reporting) ProfileCombo.SelectedIndex = 1;
                if (!string.IsNullOrWhiteSpace(scanned.ConnectionString))
                {
                    _metadataReader?.Dispose();
                    _metadataReader = new SqlDmvReader(scanned.ConnectionString);
                }
                StatusText.Text = $"appsettings.json terbaca dari folder script ({(string.IsNullOrWhiteSpace(scanned.ConnectionString) ? "tanpa connection string" : "ada connection string")}).";
            }
        }
    }

    private void OnAnalyzeClick(object sender, RoutedEventArgs e)
    {
        _diagnostics.Clear();
        ResultsGrid.Visibility = Visibility.Visible;
        TuningOutput.Visibility = Visibility.Collapsed;
        var sql = ScriptEditor.Text;
        if (string.IsNullOrWhiteSpace(sql))
        {
            StatusText.Text = "Kosong — tempel script T-SQL dulu.";
            return;
        }

        try
        {
            var analyzed = sql;
            if (MaxDopCheck.IsChecked == true)
                analyzed = MaxDopTransformer.AddMaxDopOne(sql);

            var report = _analyzer.Analyze(analyzed);
            SetDiagnostics(report.Diagnostics);

            var counts = $"Critical: {report.Diagnostics.Count(d => d.Level == RiskLevel.Critical)}, " +
                     $"Warning: {report.Diagnostics.Count(d => d.Level == RiskLevel.Warning)}, " +
                     $"Info: {report.Diagnostics.Count(d => d.Level == RiskLevel.Info)}";
        StatusText.Text = $"{report.Total} diagnostic — {counts}" +
                          (MaxDopCheck.IsChecked == true ? " (MAXDOP 1 diterapkan)" : string.Empty);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Gagal menganalisis: {ex.Message}";
        }
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        _filter = new LevelFilter
        {
            Critical = FilterCritical.IsChecked == true,
            Warning = FilterWarning.IsChecked == true,
            Info = FilterInfo.IsChecked == true,
        };
        RefreshResults();
    }

    private void SetDiagnostics(IEnumerable<Diagnostic> items)
    {
        _allDiagnostics.Clear();
        _allDiagnostics.AddRange(items.OrderByDescending(x => x.Level).ThenBy(x => x.Line));
        RefreshResults();
    }

    private void RefreshResults()
    {
        _diagnostics.Clear();
        foreach (var d in DiagnosticFilter.Apply(_allDiagnostics, _filter))
            _diagnostics.Add(d);
        UpdateDashboard();
    }

    private void UpdateDashboard()
    {
        var c = DiagnosticFilter.CountByLevel(_allDiagnostics);
        DashCritical.Text = $"Critical: {c.Critical}";
        DashWarning.Text = $"Warning: {c.Warning}";
        DashInfo.Text = $"Info: {c.Info}";
    }

    private async void OnCheckPlanClick(object sender, RoutedEventArgs e)
    {
        ResultsGrid.Visibility = Visibility.Visible;
        TuningOutput.Visibility = Visibility.Collapsed;
        var sql = ScriptEditor.Text;
        if (string.IsNullOrWhiteSpace(sql)) { StatusText.Text = "Kosong — tempel script T-SQL dulu."; return; }
        if (_metadataReader is null) { StatusText.Text = "Koneksi server belum dikonfigurasi — isi ConnectionStrings:Default di appsettings.json."; return; }

        _planCts?.Cancel();
        _planCts?.Dispose();
        var cts = _planCts = new CancellationTokenSource();
        CheckPlanButton.IsEnabled = false;
        StatusText.Text = "Mengambil XML Execution Plan dari server…";

        try
        {
            var targetSql = MaxDopCheck.IsChecked == true ? MaxDopTransformer.AddMaxDopOne(sql) : sql;
            if (_metadataReader is SqlDmvReader live && !live.IsConnected) await live.OpenAsync(cts.Token);

            var result = await new LocalExecutionPlanAnalyzer(DefaultRules, _settings, _planParser)
                .AnalyzeAsync(targetSql, _metadataReader, cts.Token);
            SetDiagnostics(result.Diagnostics);

            var sourceLabel = result.PlanSource switch
            {
                ExecutionPlanSource.PlanCache => $"Plan Cache ({result.PlanDiagnosticCount} temuan plan)",
                ExecutionPlanSource.EstimatedShowPlan => $"Estimated Plan SHOWPLAN_XML ({result.PlanDiagnosticCount} temuan plan)",
                _ => result.PlanNote ?? "Execution plan tidak tersedia",
            };
            StatusText.Text = $"{result.Diagnostics.Count} diagnostic — {sourceLabel}";
        }
        catch (OperationCanceledException) { StatusText.Text = "Pengambilan execution plan dibatalkan."; }
        catch (Exception ex) { StatusText.Text = $"Gagal mengambil execution plan: {ex.Message}"; }
        finally { if (ReferenceEquals(_planCts, cts)) CheckPlanButton.IsEnabled = true; }
    }

    private async void OnTuningClick(object sender, RoutedEventArgs e)
    {
        var sql = ScriptEditor.Text;
        if (string.IsNullOrWhiteSpace(sql))
        {
            TuningStatus.Text = "Tempel query dulu.";
            return;
        }
        if (string.IsNullOrWhiteSpace(_settings.TuningEndpoint) || string.IsNullOrWhiteSpace(_settings.TuningApiKey))
        {
            TuningStatus.Text = "Tuning belum dikonfigurasi — isi TuningEndpoint/TuningApiKey di appsettings.json.";
            return;
        }

        TuningStatus.Text = "Menghubungi gateway…";
        try
        {
            var report = _analyzer.Analyze(sql);
            if (_metadataReader is SqlDmvReader live && !live.IsConnected) await live.OpenAsync();
            var missing = _metadataReader is not null ? _metadataReader.GetMissingIndexes() : [];
            CachedQueryStat? cached = null;
            var combinedDiagnostics = new List<Diagnostic>(report.Diagnostics);
            if (_metadataReader is not null)
            {
                cached = _metadataReader.GetStatsForSql(sql);
                var plan = await _metadataReader.GetExecutionPlanXmlAsync(sql);
                if (plan is not null && !string.IsNullOrWhiteSpace(plan.PlanXml))
                {
                    try { combinedDiagnostics.AddRange(_planParser.Parse(plan.PlanXml)); }
                    catch (FormatException) { /* Abaikan XML plan korup agar tuning tetap berjalan. */ }
                }
            }

            var advisor = new HermesTuningAdvisor(_http, _settings.TuningEndpoint, _settings.TuningApiKey);
            var resp = await advisor.AskAsync(new TuningRequest(sql, combinedDiagnostics, missing, cached));

            if (resp.Success)
            {
                TuningOutput.Text = resp.Content;
                TuningOutput.Visibility = Visibility.Visible;
                ResultsGrid.Visibility = Visibility.Collapsed;
                TuningStatus.Text = "Saran tuning diterima.";
            }
            else
            {
                TuningStatus.Text = $"Gagal: {resp.Error}";
            }
        }
        catch (Exception ex)
        {
            TuningStatus.Text = $"Error: {ex.Message}";
        }
    }

    private void OnResultSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var d = ResultsGrid.SelectedItem as Diagnostic;
        DetailText.Text = d is null ? string.Empty : DiagnosticDetailFormatter.Format(d);
    }

    private void OnCopyMarkdownClick(object sender, RoutedEventArgs e)
    {
        if (_diagnostics.Count == 0)
        {
            StatusText.Text = "Tidak ada diagnostic untuk disalin.";
            return;
        }
        var sb = new StringBuilder();
        sb.AppendLine("# QAnalyzer Report");
        foreach (var g in _diagnostics.GroupBy(d => d.Level).OrderByDescending(g => g.Key))
        {
            sb.AppendLine($"## {g.Key}");
            foreach (var d in g)
                sb.AppendLine($"- **[Line {d.Line}]** {d.RuleId}: {d.Message}"
                              + (string.IsNullOrWhiteSpace(d.Suggestion) ? string.Empty : $" → *{d.Suggestion}*"));
        }
        Clipboard.SetText(sb.ToString());
        StatusText.Text = "Laporan Markdown disalin ke clipboard.";
    }

    private void OnExportMarkdownClick(object sender, RoutedEventArgs e)
        => ExportReport("Markdown file (*.md)|*.md|All files (*.*)|*.*", ".md", ReportFormatter.ToMarkdown);

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
        => ExportReport("CSV file (*.csv)|*.csv|All files (*.*)|*.*", ".csv", ReportFormatter.ToCsv);

    private void ExportReport(string filter, string ext, Func<IEnumerable<Diagnostic>, string> formatter)
    {
        if (_allDiagnostics.Count == 0)
        {
            StatusText.Text = "Tidak ada diagnostic untuk diekspor.";
            return;
        }
        var dlg = new SaveFileDialog { Filter = filter, DefaultExt = ext, FileName = $"qanalyzer-report{ext}" };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                File.WriteAllText(dlg.FileName, formatter(_allDiagnostics));
                StatusText.Text = $"Laporan disimpan: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Gagal menyimpan: {ex.Message}";
            }
        }
    }

    private void OnToggleThemeClick(object sender, RoutedEventArgs e)
    {
        var md = Application.Current.Resources.MergedDictionaries;
        var light = md.FirstOrDefault(d => d.Source?.OriginalString.EndsWith("Light.xaml") == true);
        var dark = md.FirstOrDefault(d => d.Source?.OriginalString.EndsWith("Dark.xaml") == true);
        if (light is null || dark is null) return;
        if (_dark)
        {
            md.Remove(dark);
            md.Insert(0, light);
            _dark = false;
            ThemeToggle.Content = "Tema: Gelap";
        }
        else
        {
            md.Remove(light);
            md.Insert(0, dark);
            _dark = true;
            ThemeToggle.Content = "Tema: Terang";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _planCts?.Cancel();
        _planCts?.Dispose();
        _metadataReader?.Dispose();
        _http.Dispose();
        base.OnClosed(e);
    }

}