using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GrepExplorer.UI.Controls;

/// <summary>标签元素（TagListControl 内部使用）：Text = 标签内容，IsEditing = 是否处于行内重编辑状态。</summary>
public sealed class TagItem : ObservableObject
{
    private string _text = "";
    private bool _isEditing;

    public TagItem(string text) => _text = text;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }
}

/// <summary>
/// 标签式列表控件：把"每行一条"的规则列表变成可视化小标签。
/// 输入内容后回车 → 生成标签；双击标签可重新编辑；标签右侧 ✕ 删除。
/// 数据契约与 <see cref="MultiLineListControl"/> 相同（<see cref="ListText"/> = 每行一条，双向绑定），
/// 因此 VM 侧完全不需要改动。
/// </summary>
public partial class TagListControl : UserControl
{
    /// <summary>防止 ListText ↔ 标签集合互相触发形成回环。</summary>
    private bool _syncing;

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(TagListControl), new PropertyMetadata(""));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(TagListControl), new PropertyMetadata(""));

    public static readonly DependencyProperty ListTextProperty = DependencyProperty.Register(
        nameof(ListText), typeof(string), typeof(TagListControl),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnListTextChanged));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>语法说明（分隔方式、通配符、前缀语义）。</summary>
    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>每行一条的文本形式（与 VM 的双向绑定载体）。</summary>
    public string ListText
    {
        get => (string)GetValue(ListTextProperty);
        set => SetValue(ListTextProperty, value);
    }

    public ObservableCollection<TagItem> Tags { get; } = new();

    public TagListControl()
    {
        InitializeComponent();
        TagsControl.ItemsSource = Tags;
        Tags.CollectionChanged += OnTagsChanged;
    }

    // ---------- ListText → 标签 ----------

    private static void OnListTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((TagListControl)d).LoadFromText((string?)e.NewValue ?? "");

    private void LoadFromText(string text)
    {
        if (_syncing) return;
        var items = SplitLines(text);
        if (items.Count == Tags.Count && items.Select((s, i) => s == Tags[i].Text).All(x => x))
        {
            UpdateCount();
            return;
        }
        _syncing = true;
        try
        {
            Tags.Clear();
            foreach (var s in items) Tags.Add(new TagItem(s));
        }
        finally
        {
            _syncing = false;
        }
        UpdateCount();
    }

    // ---------- 标签 → ListText ----------

    private void OnTagsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var item in e.OldItems?.Cast<TagItem>() ?? [])
            item.PropertyChanged -= OnTagPropertyChanged;
        foreach (var item in e.NewItems?.Cast<TagItem>() ?? [])
            item.PropertyChanged += OnTagPropertyChanged;
        PushToListText();
    }

    private void OnTagPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TagItem.Text)) PushToListText();
    }

    /// <summary>把标签集合写回 <see cref="ListText"/>（SetCurrentValue 保留双向绑定，不破坏绑定表达式）。</summary>
    private void PushToListText()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            SetCurrentValue(ListTextProperty,
                string.Join("\n", Tags.Select(t => t.Text.Trim()).Where(s => s.Length > 0)));
        }
        finally
        {
            _syncing = false;
        }
        UpdateCount();
    }

    // ---------- 交互 ----------

    private void OnNewTagKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddTag(NewTagBox.Text);
        NewTagBox.Clear();
    }

    /// <summary>粘贴多行文本时自动拆分（文本框不接受换行，回车键以外不会产生换行）。</summary>
    private void OnNewTagTextChanged(object sender, TextChangedEventArgs e)
    {
        var text = NewTagBox.Text;
        if (!text.Contains('\n') && !text.Contains('\r')) return;
        var parts = SplitLines(text);
        NewTagBox.Clear();
        foreach (var p in parts) AddTag(p);
    }

    private void AddTag(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0) return;
        if (Tags.Any(t => string.Equals(t.Text, text, StringComparison.OrdinalIgnoreCase))) return; // 去重
        Tags.Add(new TagItem(text));
    }

    private void OnChipMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || ((FrameworkElement)sender).DataContext is not TagItem tag) return;
        e.Handled = true;
        foreach (var t in Tags) t.IsEditing = false;
        tag.IsEditing = true;
    }

    private void OnEditBoxVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        var box = (TextBox)sender;
        box.Focus();
        box.SelectAll();
    }

    private void OnEditBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        e.Handled = true;
        CommitEdit(sender);
    }

    private void OnEditBoxLostFocus(object sender, RoutedEventArgs e) => CommitEdit(sender);

    private static void CommitEdit(object sender)
    {
        if (((FrameworkElement)sender).DataContext is TagItem tag)
        {
            tag.Text = tag.Text.Trim();
            tag.IsEditing = false;
        }
    }

    private void OnRemoveChip(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TagItem tag) return;
        Tags.Remove(tag);
        PushToListText();
    }

    private void CopyAll(object sender, RoutedEventArgs e)
        => CopyToClipboard();

    /// <summary>以「每行一条」复制到剪贴板（与文本框版本行为一致）。</summary>
    public void CopyToClipboard()
    {
        var lines = Tags.Select(t => t.Text.Trim()).Where(s => s.Length > 0).ToList();
        if (lines.Count > 0) Clipboard.SetText(string.Join('\n', lines));
    }

    private void ClearAll(object sender, RoutedEventArgs e)
    {
        if (Tags.Count == 0) return;
        Tags.Clear();
        PushToListText();
    }

    private void UpdateCount() => CountText.Text = $"{Tags.Count} 条";

    private static List<string> SplitLines(string text)
        => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
