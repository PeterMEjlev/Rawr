using System.Windows;

namespace Rawr.App.Dialogs;

/// <summary>
/// "This folder is big — run the automatic analysis passes now?" prompt. Shown
/// from the folder-load path only when a pass is set to Auto, has work to do,
/// and the folder is over <see cref="AppSettings.AutoAnalyzePromptThreshold"/>.
/// Each offered pass gets a checkbox (default on) and a rough time estimate; a
/// "Don't ask again" toggle lets the caller persist the choice and suppress the
/// prompt.
/// </summary>
public partial class AutoAnalyzePromptDialog : Window
{
    /// <summary>Outcome of the prompt; null when the user chose "Not now".</summary>
    public readonly record struct Outcome(bool RunSubjects, bool RunFaces, bool Remember);

    private readonly bool _subjectOffered;
    private readonly bool _faceOffered;

    private AutoAnalyzePromptDialog(
        int photoCount,
        string? subjectEstimate,
        string? faceEstimate)
    {
        InitializeComponent();
        WindowHelper.ApplyDarkTitleBar(this);

        _subjectOffered = subjectEstimate is not null;
        _faceOffered = faceEstimate is not null;

        HeaderText.Text =
            $"This folder has {photoCount:N0} photos. Automatic analysis hasn't finished on it yet.";

        if (_subjectOffered)
        {
            SubjectCheck.IsChecked = true;
            SubjectEstimate.Text = subjectEstimate;
        }
        else SubjectCheck.Visibility = Visibility.Collapsed;

        if (_faceOffered)
        {
            FaceCheck.IsChecked = true;
            FaceEstimate.Text = faceEstimate;
        }
        else FaceCheck.Visibility = Visibility.Collapsed;
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// Show the prompt and return the user's decision, or null if they declined
    /// ("Not now"). Pass null for an estimate to hide that pass's checkbox — only
    /// passes that are eligible (Auto + work pending) should be offered.
    /// </summary>
    public static Outcome? Show(
        Window owner,
        int photoCount,
        string? subjectEstimate,
        string? faceEstimate)
    {
        var dlg = new AutoAnalyzePromptDialog(photoCount, subjectEstimate, faceEstimate) { Owner = owner };
        if (dlg.ShowDialog() != true) return null;

        return new Outcome(
            RunSubjects: dlg._subjectOffered && dlg.SubjectCheck.IsChecked == true,
            RunFaces:    dlg._faceOffered && dlg.FaceCheck.IsChecked == true,
            Remember:    dlg.RememberCheck.IsChecked == true);
    }
}
