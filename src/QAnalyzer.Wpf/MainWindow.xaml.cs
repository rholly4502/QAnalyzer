using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using QAnalyzer.Core;

namespace QAnalyzer.Wpf;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<Diagnostic> _diagnostics = [];
    private readonly SqlAnalyzer _analyzer = new(Array.Empty<ISqlRule>());

    public MainWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _diagnostics;
    }

    private void OnAnalyzeClick(object sender, RoutedEventArgs e)
    {
        _diagnostics.Clear();
        var report = _analyzer.Analyze(ScriptEditor.Text);
        foreach (var d in report.Diagnostics)
            _diagnostics.Add(d);
        StatusText.Text = $"{report.Total} diagnostic";
    }
}
