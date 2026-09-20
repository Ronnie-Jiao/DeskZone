using System.Windows;
using System.Windows.Input;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace DeskZone.App;

public partial class SearchInputWindow : Window
{
    private bool _dismissing;

    public event EventHandler? Dismissed;

    public SearchInputWindow()
    {
        InitializeComponent();
        Deactivated += SearchInputWindow_Deactivated;
    }

    public TextBox InputBox => SearchInputBox;

    private void SearchInputWindow_Deactivated(object? sender, EventArgs e) =>
        Dismiss();

    private void SearchInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        SearchInputBox.Clear();
        Dismiss();
        e.Handled = true;
    }

    private void Dismiss()
    {
        if (_dismissing)
        {
            return;
        }

        _dismissing = true;
        Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
        _dismissing = false;
    }
}
