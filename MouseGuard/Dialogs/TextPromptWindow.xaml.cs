using System.Windows;
using System.Windows.Input;

namespace MouseGuard.Dialogs;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string message, string initialValue)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        InputBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Save_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Cancel_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };
    }

    public string Result { get; private set; } = string.Empty;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = InputBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
