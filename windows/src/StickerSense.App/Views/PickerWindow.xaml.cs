using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StickerSense.App.Platform;

namespace StickerSense.App.Views;

/// <summary>
/// Search box, source filter and results list. Enter in the search box pastes the first result;
/// Enter on a result pastes it; Shift+F10 opens the actions; Escape returns to the chat.
/// </summary>
internal partial class PickerWindow : Window
{
    private readonly PickerViewModel model;
    private bool returning;

    public PickerWindow(PickerViewModel model)
    {
        InitializeComponent();
        this.model = model;
        DataContext = model;
        model.Close = () =>
        {
            returning = true; // the paster brings the chat forward itself
            Close();
        };
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await model.SearchAsync();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
        model.MoveFocusTo += FocusItem;
        Closed += (_, _) =>
        {
            model.MoveFocusTo -= FocusItem;
            model.Stop();
            // Cancelled: go back to the chat the picker was opened from.
            if (!returning && model.TargetWindow != IntPtr.Zero) NativeMethods.SetForegroundWindow(model.TargetWindow);
        };
    }

    private async void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await model.SendFirstAsync();
        }
        else if (e.Key == Key.Down && model.Items.Count > 0)
        {
            e.Handled = true;
            FocusItem(model.Selected ?? model.Items[0]);
        }
    }

    private async void Results_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (model.Selected is not { } item) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await model.SendAsync(item);
                break;
            case Key.D when ctrl:
                e.Handled = true;
                await model.DescribeInDetailAsync(item);
                break;
            case Key.C when ctrl:
                e.Handled = true;
                await model.CopyAsync(item);
                break;
            case Key.Delete:
                e.Handled = true;
                if (!item.IsLibrary)
                {
                    model.Remove(item); // explains that GIPHY results can't be removed
                    break;
                }
                var index = Results.SelectedIndex;
                model.Remove(item);
                if (model.Items.Count > 0) FocusItem(model.Items[Math.Min(index, model.Items.Count - 1)]);
                else SearchBox.Focus();
                break;
        }
    }

    private async void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (model.Selected is { } item) await model.SendAsync(item);
    }

    private void FocusItem(PickerItem item)
    {
        model.Selected = item;
        Results.ScrollIntoView(item);
        Results.UpdateLayout();
        (Results.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem)?.Focus();
    }

    private static PickerItem? MenuItemTarget(object sender) =>
        (sender as MenuItem)?.DataContext as PickerItem;

    private async void PasteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemTarget(sender) is { } item) await model.SendAsync(item);
    }

    private async void DetailMenu_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemTarget(sender) is { } item) await model.DescribeInDetailAsync(item);
    }

    private async void CopyMenu_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemTarget(sender) is { } item) await model.CopyAsync(item);
    }

    private void RemoveMenu_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemTarget(sender) is { } item) model.Remove(item);
    }
}
