using System.Windows;
using System.Windows.Controls;
using GrepExplorer.UI.ViewModels;

namespace GrepExplorer.UI.Controls;

public partial class ContextFieldsControl : UserControl
{
    public ContextFieldsControl()
    {
        InitializeComponent();
        // 键盘过滤非法字符（-、.、字母）
        PreviewTextInput += (_, e) =>
        {
            if (DataContext is ContextFieldsViewModel vm)
                e.Handled = e.Text.Length > 0 && e.Text.Any(c => !ContextFieldsViewModel.IsKeyAllowed(c));
        };
        // 粘贴 sanitize（DataObject.Pasting）
        Loaded += (_, _) =>
        {
            foreach (var tb in FindTextBoxes(this))
                DataObject.AddPastingHandler(tb, OnPaste);
        };
    }

    private static IEnumerable<TextBox> FindTextBoxes(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBox tb) yield return tb;
            foreach (var inner in FindTextBoxes(child)) yield return inner;
        }
    }

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (DataContext is not ContextFieldsViewModel vm) return;
        if (!e.DataObject.GetDataPresent(DataFormats.Text)) { e.CancelCommand(); return; }
        var raw = (string)e.DataObject.GetData(DataFormats.Text)!;
        var sanitized = Core.Grep.ContextValidation.Sanitize(raw);
        if (sanitized != raw)
        {
            e.CancelCommand();
            if (sender is TextBox tb)
            {
                var path = tb.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path;
                vm.SetFieldByName(path, sanitized);
            }
        }
    }
}
