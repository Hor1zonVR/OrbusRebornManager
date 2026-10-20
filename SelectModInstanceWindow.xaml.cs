using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OrbusRebornManager;

/// <summary>
/// Requires a fresh, explicit instance selection for global catalogue installs.
/// Never guesses the target from the last opened or played installation.
/// </summary>
public partial class SelectModInstanceWindow : Window
{
    public sealed class TargetOption
    {
        public GameInstance Instance { get; }
        public string Name => Instance.Name;
        public string Path => Instance.Path;
        public string Detail => (Instance.CreatedByManager ? "Managed instance" : "Existing game · use with care")
            + (string.IsNullOrWhiteSpace(Instance.GameVersion) ? "" : " · " + Instance.GameVersion);

        public TargetOption(GameInstance instance) => Instance = instance;
    }

    public GameInstance? SelectedInstance { get; private set; }

    public SelectModInstanceWindow(string modName, IEnumerable<GameInstance> instances)
    {
        InitializeComponent();
        ModDescriptionText.Text = "Installing " + modName + ". The mod will only be changed inside the instance you choose.";
        InstanceOptions.ItemsSource = instances.OrderBy(x => !x.CreatedByManager)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new TargetOption(x)).ToList();
        if (InstanceOptions.Items.Count == 0)
            SelectionHint.Text = "No valid instances. Create a game instance first.";
    }

    private void InstanceOptions_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ConfirmButton == null) return;
        bool selected = InstanceOptions.SelectedItem is TargetOption;
        ConfirmButton.IsEnabled = selected;
        SelectionHint.Text = selected ? "Only the selected game will be changed." : "Select one instance to continue.";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (InstanceOptions.SelectedItem is not TargetOption selected) return;
        if (!selected.Instance.CreatedByManager &&
            MessageBox.Show(this,
                "You selected an existing game installation, not a managed copy. Installing the mod will change that original game's files. Continue?",
                "Modify original installation?",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (!Directory.Exists(selected.Path))
        {
            MessageBox.Show(this, "The chosen instance directory no longer exists.", "Instance unavailable");
            return;
        }
        SelectedInstance = selected.Instance;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            try { DragMove(); } catch (InvalidOperationException) { }
    }
}
