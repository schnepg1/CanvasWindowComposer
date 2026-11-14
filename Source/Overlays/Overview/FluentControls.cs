using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CanvasDesktop;

internal class FluentButton : Wpf.Ui.Controls.Button
{
    public bool Enabled
    {
        get => IsEnabled;
        set => IsEnabled = value;
    }
    public void PerformClick() => RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    public void ResetFont() => FontWeight = FontWeights.Normal;
}

internal class FluentTextBox : Wpf.Ui.Controls.TextBox
{
    public bool Enabled
    {
        get => IsEnabled;
        set => IsEnabled = value;
    }
    public int TextLength => Text?.Length ?? 0;
}

internal class FluentComboBox : System.Windows.Controls.ComboBox
{
    public bool Enabled
    {
        get => IsEnabled;
        set => IsEnabled = value;
    }

    public System.Windows.Forms.ComboBoxStyle DropDownStyle
    {
        get => IsEditable ? System.Windows.Forms.ComboBoxStyle.DropDown : System.Windows.Forms.ComboBoxStyle.DropDownList;
        set => IsEditable = (value == System.Windows.Forms.ComboBoxStyle.DropDown);
    }

    public event EventHandler? SelectedIndexChanged;
    public event TextChangedEventHandler? TextChanged;

    public FluentComboBox()
    {
        AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((s, e) => TextChanged?.Invoke(s, e)));
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class FluentCheckedListBox : ListBox
{
    public bool Enabled
    {
        get => IsEnabled;
        set => IsEnabled = value;
    }

    public sealed class ItemEntry
    {
        private readonly FluentCheckedListBox _owner;
        public object Value { get; }
        private bool _isChecked;

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked != value)
                {
                    _isChecked = value;
                    _owner.RaiseItemCheck();
                }
            }
        }

        public ItemEntry(FluentCheckedListBox owner, object value, bool isChecked)
        {
            _owner = owner;
            Value = value;
            _isChecked = isChecked;
        }

        public override string ToString() => Value?.ToString() ?? "";
    }

    public sealed class ItemCollectionAdapter : IEnumerable
    {
        private readonly FluentCheckedListBox _owner;
        public ItemCollectionAdapter(FluentCheckedListBox owner) => _owner = owner;
        public int Count => _owner._entries.Count;
        public void Clear()
        {
            _owner._entries.Clear();
            _owner.RefreshList();
        }
        public void Add(object item, bool isChecked = true)
        {
            _owner._entries.Add(new ItemEntry(_owner, item, isChecked));
            _owner.RefreshList();
        }
        public IEnumerator GetEnumerator() => _owner._entries.Select(e => e.Value).GetEnumerator();
    }

    public sealed class CheckedItemCollectionAdapter : IEnumerable
    {
        private readonly FluentCheckedListBox _owner;
        public CheckedItemCollectionAdapter(FluentCheckedListBox owner) => _owner = owner;
        public int Count => _owner._entries.Count(e => e.IsChecked);
        public IEnumerator GetEnumerator() => _owner._entries.Where(e => e.IsChecked).Select(e => e.Value).GetEnumerator();
    }

    private readonly List<ItemEntry> _entries = [];
    public new ItemCollectionAdapter Items { get; }
    public CheckedItemCollectionAdapter CheckedItems { get; }
    public event Action? ItemCheck;

    public bool Visible
    {
        get => Visibility == Visibility.Visible;
        set => Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool GetItemChecked(int index) => index >= 0 && index < _entries.Count && _entries[index].IsChecked;

    public FluentCheckedListBox()
    {
        Items = new(this);
        CheckedItems = new(this);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(this, ScrollBarVisibility.Auto);

        var factory = new FrameworkElementFactory(typeof(CheckBox));
        factory.SetBinding(CheckBox.IsCheckedProperty, new System.Windows.Data.Binding(nameof(ItemEntry.IsChecked)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        factory.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding(nameof(ItemEntry.Value)));
        factory.SetValue(Control.ForegroundProperty, (SolidColorBrush)new BrushConverter().ConvertFromString("#F1F5F9")!);
        factory.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 4, 2, 4));
        factory.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
        ItemTemplate = new DataTemplate { VisualTree = factory };
    }

    internal void RaiseItemCheck() => ItemCheck?.Invoke();

    internal void RefreshList()
    {
        ItemsSource = null;
        ItemsSource = _entries;
    }
}
