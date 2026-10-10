using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace OrbusRebornManager;

public partial class MainWindow
{
    private readonly List<InstanceCard> _allInstanceCards = new();
    private string _instanceSort = "recent";
    private string _instanceFilter = "all";

    private void InstanceSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (InstanceSearchText == null || InstanceSearchHint == null)
            return;

        InstanceSearchHint.Visibility =
            string.IsNullOrEmpty(InstanceSearchText.Text)
                ? Visibility.Visible : Visibility.Collapsed;
        ApplyInstanceFilters();
    }

    private void ApplyInstanceFilters()
    {
        // Search's TextChanged can fire during InitializeComponent.
        if (InstanceCards == null || InstanceSearchText == null ||
            NoMatchingInstancesPanel == null || EmptyInstancesPanel == null)
            return;

        string search = InstanceSearchText.Text.Trim();
        IEnumerable<InstanceCard> items = _allInstanceCards;

        if (_instanceFilter == "managed")
            items = items.Where(x => x.Instance.CreatedByManager);
        else if (_instanceFilter == "existing")
            items = items.Where(x => !x.Instance.CreatedByManager);

        if (search.Length > 0)
            items = items.Where(x =>
                x.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || x.Kind.Contains(search, StringComparison.OrdinalIgnoreCase));

        items = _instanceSort switch
        {
            "name" => items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            "name-desc" => items.OrderByDescending(
                x => x.Name, StringComparer.OrdinalIgnoreCase),
            _ => items.Reverse() // Registry order is the order instances were added.
        };

        InstanceCards.Clear();
        foreach (var item in items)
            InstanceCards.Add(item);

        EmptyInstancesPanel.Visibility = _allInstanceCards.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        NoMatchingInstancesPanel.Visibility =
            _allInstanceCards.Count > 0 && InstanceCards.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
        InstanceCountText.Text = InstanceCards.Count == 1
            ? "1 instance" : InstanceCards.Count + " instances";
    }

    private static void ShowButtonMenu(Button button)
    {
        if (button.ContextMenu == null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private void OpenInstanceSort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) ShowButtonMenu(button);
    }

    private void OpenInstanceFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) ShowButtonMenu(button);
    }

    private void InstanceSortOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value }) return;
        if (value is not ("recent" or "name" or "name-desc")) return;
        _instanceSort = value;
        InstanceSortButton.Content = value switch
        {
            "name" => "Name: A to Z  ▾",
            "name-desc" => "Name: Z to A  ▾",
            _ => "Recently added  ▾"
        };
        ApplyInstanceFilters();
    }

    private void InstanceFilterOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value }) return;
        if (value is not ("all" or "managed" or "existing")) return;
        _instanceFilter = value;
        InstanceFilterButton.Content = value switch
        {
            "managed" => "Modded copies  ▾",
            "existing" => "Existing installations  ▾",
            _ => "All instances  ▾"
        };
        ApplyInstanceFilters();
    }

    private void ClearInstanceFilters_Click(object sender, RoutedEventArgs e)
    {
        _instanceSort = "recent";
        _instanceFilter = "all";
        InstanceSortButton.Content = "Recently added  ▾";
        InstanceFilterButton.Content = "All instances  ▾";
        InstanceSearchText.Text = "";
        ApplyInstanceFilters();
    }

    private void InstanceOptions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        DependencyObject? current = button;
        while (current != null)
        {
            if (current is Border { ContextMenu: not null } card)
            {
                card.ContextMenu.PlacementTarget = button;
                card.ContextMenu.Placement = PlacementMode.Bottom;
                card.ContextMenu.IsOpen = true;
                return;
            }
            current = VisualTreeHelper.GetParent(current);
        }
    }
}
