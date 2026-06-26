using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using PayrixLauncher.Models;

namespace PayrixLauncher;

public partial class CollectionSaveDialog : Window
{
    private const string NewEntry = "＋  New collection…";

    public string        RequestName       { get; private set; } = "";
    public HttpCollection? TargetCollection { get; private set; }   // null = create new
    public string        NewCollectionName { get; private set; } = "";

    private readonly ObservableCollection<HttpCollection> _collections;

    public CollectionSaveDialog(ObservableCollection<HttpCollection> collections)
    {
        InitializeComponent();
        _collections = collections;

        // Populate combo
        foreach (var c in _collections)
            CollectionCombo.Items.Add(c.Name);
        CollectionCombo.Items.Add(NewEntry);
        CollectionCombo.SelectedIndex = _collections.Count > 0 ? 0 : CollectionCombo.Items.Count - 1;

        Loaded += (_, _) => RequestNameBox.Focus();
    }

    private void CollectionCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        NewColPanel.Visibility =
            CollectionCombo.SelectedItem?.ToString() == NewEntry
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RequestNameBox.Text))
        {
            RequestNameBox.BorderBrush = System.Windows.Media.Brushes.Red;
            RequestNameBox.Focus();
            return;
        }

        RequestName = RequestNameBox.Text.Trim();

        if (CollectionCombo.SelectedItem?.ToString() == NewEntry)
        {
            if (string.IsNullOrWhiteSpace(NewColNameBox.Text))
            {
                NewColNameBox.BorderBrush = System.Windows.Media.Brushes.Red;
                NewColNameBox.Focus();
                return;
            }
            TargetCollection  = null;
            NewCollectionName = NewColNameBox.Text.Trim();
        }
        else
        {
            var idx = CollectionCombo.SelectedIndex;
            TargetCollection = idx >= 0 && idx < _collections.Count ? _collections[idx] : null;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
