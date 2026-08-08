using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using StartTooler.Models;

namespace StartTooler.Controls;

public partial class FeaturedPhotoPicker : Window
{
    public static readonly DirectProperty<FeaturedPhotoPicker, ObservableCollection<FeaturedPhotoSelectableItem>> ItemsProperty =
        AvaloniaProperty.RegisterDirect<FeaturedPhotoPicker, ObservableCollection<FeaturedPhotoSelectableItem>>(
            nameof(Items),
            o => o.Items,
            (o, v) => o.Items = v);

    private ObservableCollection<FeaturedPhotoSelectableItem> _items = new();

    public ObservableCollection<FeaturedPhotoSelectableItem> Items
    {
        get => _items;
        set
        {
            SetAndRaise(ItemsProperty, ref _items, value);
            if (this.FindControl<ItemsControl>("PhotosItemsControl") is { } ic)
            {
                ic.ItemsSource = value;
            }
        }
    }

    /// <summary>
    /// 用户点击保存后返回的已选照片 ID 列表；取消或关闭窗口时为 null。
    /// </summary>
    public IReadOnlyList<long>? SelectedIds { get; private set; }

    public FeaturedPhotoPicker()
    {
        InitializeComponent();
        DataContext = this;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (this.FindControl<ItemsControl>("PhotosItemsControl") is { } ic)
        {
            ic.ItemsSource = Items;
        }
    }

    private void OnPhotoClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FeaturedPhotoSelectableItem item })
        {
            item.IsSelected = !item.IsSelected;
        }
    }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (var item in Items)
        {
            item.IsSelected = true;
        }
    }

    private void OnClearAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        SelectedIds = Items
            .Where(i => i.IsSelected)
            .Select(i => i.Photo.Id)
            .ToList();
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        SelectedIds = null;
        Close();
    }

    public async Task<IReadOnlyList<long>?> ShowDialogAsync(Window? owner)
    {
        if (owner != null)
        {
            await ShowDialog(owner);
        }
        else
        {
            Show();
            var tcs = new TaskCompletionSource<object?>();
            Closed += (_, _) => tcs.SetResult(null);
            await tcs.Task;
        }
        return SelectedIds;
    }
}
