using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FH6LocalCryptoTool;

public partial class CarRelatedExportWindow : Window
{
    private readonly IReadOnlyList<CarRelatedDbExport.Car> _cars = Array.Empty<CarRelatedDbExport.Car>();
    private readonly HashSet<long> _selectedCarIds = new();
    private bool _filtering;
    public IReadOnlyList<long> SelectedCarIds => _selectedCarIds.Order().ToArray();
    public long? SelectedCarId => _selectedCarIds.Count == 1 ? _selectedCarIds.Single() : null;

    public CarRelatedExportWindow(string sourcePath, IReadOnlyList<CarRelatedDbExport.Car> cars)
    {
        InitializeComponent();
        _cars = cars;
        SourceText.Text = "Source: " + Path.GetFileName(sourcePath);
        SourceText.ToolTip = sourcePath;
        CarChoices.ItemsSource = cars;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (SearchBox is null || CarChoices is null) return;
        string query = SearchBox.Text.Trim();
        _filtering = true;
        try
        {
            CarChoices.ItemsSource = query.Length == 0 ? _cars :
                _cars.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                 c.Year.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                 c.Id.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            // Searching for the next car must not discard cars already picked.
            foreach (CarRelatedDbExport.Car car in CarChoices.Items)
                if (_selectedCarIds.Contains(car.Id)) CarChoices.SelectedItems.Add(car);
        }
        finally { _filtering = false; }
        UpdateSelection();
    }
    private void CarChoices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filtering) return;
        foreach (CarRelatedDbExport.Car car in e.RemovedItems) _selectedCarIds.Remove(car.Id);
        foreach (CarRelatedDbExport.Car car in e.AddedItems) _selectedCarIds.Add(car.Id);
        UpdateSelection();
    }
    private void UpdateSelection()
    {
        if (ExportButton is null || SelectionText is null) return;
        int hidden = _selectedCarIds.Count - CarChoices.SelectedItems.Count;
        SelectionText.Text = $"{_selectedCarIds.Count:n0} car(s) selected" +
            (hidden > 0 ? $" ({hidden:n0} hidden by search)" : "");
        SelectionText.ToolTip = string.Join("\n", _cars.Where(c => _selectedCarIds.Contains(c.Id)));
        ExportButton.IsEnabled = _selectedCarIds.Count > 0;
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _selectedCarIds.Clear();
        CarChoices.UnselectAll();
        UpdateSelection();
    }
    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else DragMove();
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCarIds.Count == 0) return;
        DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
