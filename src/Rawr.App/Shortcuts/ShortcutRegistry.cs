using System.Windows.Input;
using Rawr.App.ViewModels;
using Rawr.Core.Models;

namespace Rawr.App.Shortcuts;

public static class ShortcutRegistry
{
    public static IReadOnlyList<ShortcutAction> All { get; } = Build();

    private static List<ShortcutAction> Build()
    {
        const string CatFile = "File";
        const string CatNav  = "Navigation";
        const string CatFlag = "Flags";
        const string CatRate = "Ratings";
        const string CatColor = "Color labels";
        const string CatTag  = "Tags";
        const string CatView = "View";
        const string CatFilt = "Filters";
        const string CatEdit = "Edit";

        static Func<MainWindow, ICommand?> Vm(Func<MainViewModel, ICommand> selector) =>
            w => w.DataContext is MainViewModel vm ? selector(vm) : null;

        var list = new List<ShortcutAction>
        {
            // File ops
            new("OpenFolder",     "Open folder",        CatFile, Key.O, ModifierKeys.Control, Vm(vm => vm.OpenFolderCommand)),
            new("ExportFileList", "Export file list",   CatFile, Key.E, ModifierKeys.Control, Vm(vm => vm.ExportFileListCommand)),
            new("CopyPicked",     "Copy picked photos", CatFile, Key.C, ModifierKeys.Control, Vm(vm => vm.CopyPickedCommand)),

            // Navigation
            new("NextPhoto",     "Next photo",          CatNav, Key.Right, ModifierKeys.None,    Vm(vm => vm.NextPhotoCommand)),
            new("NextPhotoAlt",  "Next photo (alt)",    CatNav, Key.Down,  ModifierKeys.None,    Vm(vm => vm.NextPhotoCommand)),
            new("PreviousPhoto", "Previous photo",      CatNav, Key.Left,  ModifierKeys.None,    Vm(vm => vm.PreviousPhotoCommand)),
            new("PreviousPhotoAlt","Previous photo (alt)", CatNav, Key.Up, ModifierKeys.None,    Vm(vm => vm.PreviousPhotoCommand)),
            new("NextBurst",     "Next burst",          CatNav, Key.Right, ModifierKeys.Control, Vm(vm => vm.NextBurstCommand)),
            new("PreviousBurst", "Previous burst",      CatNav, Key.Left,  ModifierKeys.Control, Vm(vm => vm.PreviousBurstCommand)),

            // Flags
            new("TogglePick",       "Toggle pick",                CatFlag, Key.P, ModifierKeys.None,  Vm(vm => vm.TogglePickCommand)),
            new("ToggleReject",     "Toggle reject",              CatFlag, Key.X, ModifierKeys.None,  Vm(vm => vm.ToggleRejectCommand)),
            new("Unflag",           "Unflag",                     CatFlag, Key.U, ModifierKeys.None,  Vm(vm => vm.UnflagCommand)),
            new("PickAndAdvance",   "Pick and advance",           CatFlag, Key.P, ModifierKeys.Shift, Vm(vm => vm.PickAndAdvanceCommand)),
            new("RejectAndAdvance", "Reject and advance",         CatFlag, Key.X, ModifierKeys.Shift, Vm(vm => vm.RejectAndAdvanceCommand)),

            // Ratings 0..5
            new("Rating0", "Set rating 0", CatRate, Key.D0, ModifierKeys.None, Vm(vm => vm.SetRatingCommand), 0),
            new("Rating1", "Set rating 1", CatRate, Key.D1, ModifierKeys.None, Vm(vm => vm.SetRatingCommand), 1),
            new("Rating2", "Set rating 2", CatRate, Key.D2, ModifierKeys.None, Vm(vm => vm.SetRatingCommand), 2),
            new("Rating3", "Set rating 3", CatRate, Key.D3, ModifierKeys.None, Vm(vm => vm.SetRatingCommand), 3),
            new("Rating4", "Set rating 4", CatRate, Key.D4, ModifierKeys.None, Vm(vm => vm.SetRatingCommand), 4),
            new("Rating5", "Set rating 5", CatRate, Key.D5, ModifierKeys.None, Vm(vm => vm.SetRatingCommand), 5),

            // Color labels (Lightroom style 6..9)
            new("ColorRed",    "Color: Red",    CatColor, Key.D6, ModifierKeys.None, Vm(vm => vm.SetColorLabelCommand), ColorLabel.Red),
            new("ColorYellow", "Color: Yellow", CatColor, Key.D7, ModifierKeys.None, Vm(vm => vm.SetColorLabelCommand), ColorLabel.Yellow),
            new("ColorGreen",  "Color: Green",  CatColor, Key.D8, ModifierKeys.None, Vm(vm => vm.SetColorLabelCommand), ColorLabel.Green),
            new("ColorBlue",   "Color: Blue",   CatColor, Key.D9, ModifierKeys.None, Vm(vm => vm.SetColorLabelCommand), ColorLabel.Blue),

            // Edit
            new("DeletePhoto", "Delete photo", CatEdit, Key.Delete, ModifierKeys.None, Vm(vm => vm.DeletePhotoCommand)),

            // View
            new("ToggleFocusPeaking", "Toggle focus peaking", CatView, Key.F, ModifierKeys.None, Vm(vm => vm.ToggleFocusPeakingCommand)),
            new("ToggleBurstCollapse","Toggle burst collapse",CatView, Key.G, ModifierKeys.None, Vm(vm => vm.ToggleBurstCollapseCommand)),
            new("OpenTags",           "Open tags panel",      CatView, Key.T, ModifierKeys.None, w => w.OpenTagsCommand),

            // Filters
            new("ClearFilters", "Clear all filters", CatFilt, Key.X, ModifierKeys.Control | ModifierKeys.Shift, Vm(vm => vm.ClearFiltersCommand)),
        };

        // Tag assignment Shift+1..0 -> indices 0..9 (matches existing Lightroom-ish layout).
        var tagKeys = new[] { Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7, Key.D8, Key.D9, Key.D0 };
        for (int i = 0; i < tagKeys.Length; i++)
        {
            int index = i;
            list.Add(new ShortcutAction(
                $"AssignTag{index}",
                $"Assign tag {index + 1}",
                CatTag,
                tagKeys[i],
                ModifierKeys.Shift,
                Vm(vm => vm.AssignTagByIndexCommand),
                index));
        }

        return list;
    }
}
