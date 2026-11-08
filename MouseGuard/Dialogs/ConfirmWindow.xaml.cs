using System.Windows;

namespace MouseGuard.Dialogs;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string message, string confirmText = "确认")
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    public bool Confirmed { get; private set; }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
        Close();
    }
}
