using System.Windows;

namespace Rawr.App.Dialogs;

public partial class InputDialog : Window
{
    public static readonly DependencyProperty PromptProperty =
        DependencyProperty.Register(nameof(Prompt), typeof(string), typeof(InputDialog), new PropertyMetadata(""));

    public static readonly DependencyProperty InputTextProperty =
        DependencyProperty.Register(nameof(InputText), typeof(string), typeof(InputDialog), new PropertyMetadata(""));

    public string Prompt
    {
        get => (string)GetValue(PromptProperty);
        set => SetValue(PromptProperty, value);
    }

    public string InputText
    {
        get => (string)GetValue(InputTextProperty);
        set => SetValue(InputTextProperty, value);
    }

    public InputDialog(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        Title = title;
        Prompt = prompt;
        InputText = initial;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>
    /// Shows the dialog and returns the entered text, or null if cancelled.
    /// </summary>
    public static string? Show(Window owner, string title, string prompt, string initial = "")
    {
        var dlg = new InputDialog(title, prompt, initial) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.InputText.Trim() : null;
    }
}
