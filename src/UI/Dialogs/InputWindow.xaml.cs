using System.Windows;
using System.Windows.Input;

namespace GrepExplorer.UI.Dialogs;

public partial class InputWindow : Window
{
    public string InputText { get; private set; } = "";

    public InputWindow(string title, string message, string initial = "")
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        InputBox.Text = initial;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        InputText = InputBox.Text;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            InputText = InputBox.Text;
            DialogResult = true;
        }
    }
}
