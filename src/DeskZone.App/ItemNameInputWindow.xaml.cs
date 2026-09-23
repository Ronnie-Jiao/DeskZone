using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Border = System.Windows.Controls.Border;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace DeskZone.App;

public partial class ItemNameInputWindow : Window
{
    private bool _dismissing;

    public event EventHandler? CommitRequested;
    public event EventHandler? CancelRequested;

    public ItemNameInputWindow()
    {
        InitializeComponent();
        Deactivated += ItemNameInputWindow_Deactivated;
    }

    public TextBox InputBox => ItemNameInputBox;

    public Border InputBorder => ItemNameInputBorder;

    public void HideSilently()
    {
        _dismissing = true;
        if (IsVisible)
        {
            Hide();
        }

        _dismissing = false;
    }

    public void CloseSilently()
    {
        _dismissing = true;
        if (IsVisible)
        {
            Hide();
        }

        Close();
        _dismissing = false;
    }

    private void ItemNameInputWindow_Deactivated(object? sender, EventArgs e) =>
        Dismiss(cancel: false);

    private void ItemNameInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismiss(cancel: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Dismiss(cancel: false);
            e.Handled = true;
        }
    }

    private void Dismiss(bool cancel)
    {
        if (_dismissing)
        {
            return;
        }

        _dismissing = true;
        Hide();
        if (cancel)
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            CommitRequested?.Invoke(this, EventArgs.Empty);
        }

        _dismissing = false;
    }
}
