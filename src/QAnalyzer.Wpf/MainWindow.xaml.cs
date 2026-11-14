using System.Collections.ObjectModel;
using System.IO;
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
        new SelectStarRule(),
        new NoLockRule(),
        new UpdateWithoutWhereRule(),
    ];

    private readonly ObservableCollection<Diagnostic> _diagnostics = [];
    private readonly AppSettingsScanner _scanner = new();
    private SqlAnalyzer _analyzer = new(DefaultRules);
    private AnalyzerSettings _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _diagnostics;
        // Auto-scan appsettings.json di folder yang sama dengan exe, jika ada.
        var dir = Path.GetDirectoryName(AppContext.BaseDirectory);
        var scanned = _scanner.ScanForDirectory(dir ?? string.Empty);
        if (scanned is not null && scanned.Settings.Profile == AnalysisProfile.Reporting)
        {
            ProfileCombo.SelectedIndex = 1;
            _settings = scanned.Settings;
            StatusText.Text = "appsettings.json terbaca (Reporting).";
        }
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
            ScriptEditor.Text = File.ReadAllText(dlg.FileName);
            var scanned = _scanner.ScanForDirectory(Path.GetDirectoryName(dlg.FileName) ?? string.Empty);
            if (scanned is not null)
            {
                _settings = scanned.Settings;
                if (_settings.Profile == AnalysisProfile.Reporting) ProfileCombo.SelectedIndex = 1;
                StatusText.Text = $"appsettings.json terbaca dari folder script ({(string.IsNullOrWhiteSpace(scanned.ConnectionString) ? "tanpa connection string" : "ada connection string")}).";
            }
        }
    }

    private void OnAnalyzeClick(object sender, RoutedEventArgs e)
    {
        _diagnostics.Clear();
        var sql = ScriptEditor.Text;
        if (string.IsNullOrWhiteSpace(sql))
        {
            StatusText.Text = "Kosong — tempel script T-SQL dulu.";
            return;
        }

        var analyzed = sql;
        if (MaxDopCheck.IsChecked == true)
            analyzed = MaxDopTransformer.AddMaxDopOne(sql);

        var report = _analyzer.Analyze(analyzed);
        foreach (var d in report.Diagnostics.OrderByDescending(x => x.Level).ThenBy(x => x.Line))
            _diagnostics.Add(d);

        var counts = $"Critical: {report.Diagnostics.Count(d => d.Level == RiskLevel.Critical)}, " +
                     $"Warning: {report.Diagnostics.Count(d => d.Level == RiskLevel.Warning)}, " +
                     $"Info: {report.Diagnostics.Count(d => d.Level == RiskLevel.Info)}";
        StatusText.Text = $"{report.Total} diagnostic — {counts}" +
                          (MaxDopCheck.IsChecked == true ? " (MAXDOP 1 diterapkan)" : string.Empty);

        if (MaxDopCheck.IsChecked == true && !ReferenceEquals(analyzed, sql))
            StatusText.Text += $" | {counts}";
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
}
