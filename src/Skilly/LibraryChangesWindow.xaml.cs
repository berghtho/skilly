using System.Windows;
using Skilly.Skills;

namespace Skilly;

public partial class LibraryChangesWindow : Window
{
    public LibraryChangesWindow(string libraryLabel, int updatedCount, LibraryChangeSummary changes)
    {
        InitializeComponent();
        LibraryHeading.Text = libraryLabel;
        SummaryText.Text =
            $"Updated {updatedCount} Skill(s) in this Skill Library. The library's Skill membership changed during the update: "
            + $"{changes.AddedSkills.Count} Skill(s) appeared and {changes.RemovedSkills.Count} disappeared. "
            + "New Skills are listed as Unmanaged in the Workbench until they are adopted or installed.";
        AddedList.ItemsSource = changes.AddedSkills;
        RemovedList.ItemsSource = changes.RemovedSkills;
        AddedSection.Visibility = changes.AddedSkills.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RemovedSection.Visibility = changes.RemovedSkills.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
