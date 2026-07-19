using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using StartTooler.Data;
using StartTooler.ViewModels;

namespace StartTooler.Views;

public partial class GalleryView : UserControl
{
    public GalleryView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 双击卡片：v0.11 起统一打开灯箱（图片 + 视频缩略图）。
    /// 单击仍走 Button.Command → ToggleSelectionCommand（保持多选行为不变）。
/// </summary>
    private void OnPhotoTileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not MediaFile file)
            return;

        if (DataContext is not GalleryViewModel vm)
            return;

        // 阻止双击事件继续冒泡触发其他处理
        e.Handled = true;

        // 统一进灯箱；视频模式在灯箱里只显示缩略图 + ▶ overlay
        vm.PreviewCommand.Execute(file);
    }

    /// <summary>
    /// 右键点击 photo tile：构建 MenuFlyout 并显示（spec §5.3）。
    ///
    /// v0.8.1: 改为 code-behind 构建，避开 XAML `$parent[ItemsControl]...XxxCommand`
    /// 在 DataTemplate + 编译 binding 模式下解析失败导致 Command=null → 灰显的问题。
    /// MultiBinding 走运行时实例化能解析（IsVisible 看着对），但编译期 binding 的
    /// 链式 cast 路径整个返回 null。
    ///
    /// 多选模式不弹菜单（spec §5.3：避免与多选冲突）。
    /// IsVisible 在代码里一次性快照 MediaFile 状态；菜单生命周期短，不用订阅 PropertyChanged。
    /// </summary>
    private void OnPhotoTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsRightButtonPressed) return;
        if (sender is not Control c || c.DataContext is not MediaFile file) return;
        if (DataContext is not GalleryViewModel vm) return;

        // 多选模式下不弹菜单（spec §5.3）
        if (vm.IsMultiSelectMode) return;

        var menu = BuildPhotoContextMenu(file, vm);
        menu.ShowAt(c, true);
        e.Handled = true;
    }

    /// <summary>
    /// 构建 photo tile 右键菜单。
    /// 直接挂 VM 实例的 ICommand 属性，跳过 binding 解析。
    /// </summary>
    private static MenuFlyout BuildPhotoContextMenu(MediaFile file, GalleryViewModel vm)
    {
        var menu = new MenuFlyout();

        // ── 标签 ──
        menu.Items.Add(new MenuItem
        {
            Header = "AI 打标",
            Command = vm.TagSingleCommand,
            CommandParameter = file,
        });

        menu.Items.Add(new MenuItem
        {
            Header = "编辑标签",
            Command = vm.EditTagsSingleCommand,
            CommandParameter = file,
            IsEnabled = vm.CanEditTagsSingleForMenu(file),
        });

        menu.Items.Add(new Separator());

        // ── 选择 ──
        menu.Items.Add(new MenuItem
        {
            Header = "选择",
            Command = vm.SelectSingleCommand,
            CommandParameter = file,
        });

        menu.Items.Add(new Separator());

        // ── 云端同步 ──
        // 上传到云端：仅本地存在 + 云端未上传
        menu.Items.Add(new MenuItem
        {
            Header = "上传到云端",
            Command = vm.UploadSingleCommand,
            CommandParameter = file,
            IsVisible = file.LocalExists && !file.IsUploaded,
        });

        // 下载到本地：仅云端已上传 + 本地不存在
        menu.Items.Add(new MenuItem
        {
            Header = "下载到本地",
            Command = vm.DownloadSingleCommand,
            CommandParameter = file,
            IsVisible = file.IsUploaded && !file.LocalExists,
        });

        // 释放本地空间：仅云端已备份 + 本地存在
        menu.Items.Add(new MenuItem
        {
            Header = "释放本地空间",
            Command = vm.FreeUpSpaceCommand,
            CommandParameter = file,
            IsVisible = file.IsUploaded && file.LocalExists,
        });

        menu.Items.Add(new Separator());

        // ── 删除 ──
        menu.Items.Add(new MenuItem
        {
            Header = "删除",
            Command = vm.DeleteSingleCommand,
            CommandParameter = file,
        });

        return menu;
    }

    // ============================================================
    //  v0.11: photo tile hover（spec §5.3）+ 键盘导航（spec §7）+ 拖拽框选（spec §8）
    //
    //  1. Hover 集合：PhotoScrollViewer 上 AddHandler(PointerEnteredEvent) +
    //     AddHandler(PointerExitedEvent) → 维护 _hoveredFiles 集合 + 设 MediaFile.IsHovered。
    //     编译 XAML 在 Button/Grid 上对 PointerEntered/Exited 解析失败（AVLN3000），
    //     改走 code-behind 走 AddHandler 路由事件。
    //
    //  2. 键盘导航：OnKeyDown 处理 ←/→ 翻焦点；MediaFile.IsKeyboardFocused 绑 tile 焦点边框。
    //
    //  3. 拖拽框选：多选模式下 PointerPressed/Moved/Released 在 PhotoScrollViewer 上
    //     维护 _marqueeRect Border 并实时更新 SelectedFiles。
    // ============================================================

    private readonly HashSet<MediaFile> _hoveredFiles = new();

    /// <summary>当前键盘焦点照片索引（-1 = 无焦点）。</summary>
    private int _focusedIndex = -1;

    private Point _dragStart;
    private bool _isMarqueeSelecting;
    private Border? _marqueeRect;
    private ScrollViewer? _photoScrollViewer;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        // PhotoScrollViewer 在 ApplyTemplate 之后才能 FindControl 拿到
        if (_photoScrollViewer != null)
        {
            _photoScrollViewer.RemoveHandler(PointerEnteredEvent, OnPhotoPointerEnteredRouted);
            _photoScrollViewer.RemoveHandler(PointerExitedEvent, OnPhotoPointerExitedRouted);
        }
        _photoScrollViewer = this.FindControl<ScrollViewer>("PhotoScrollViewer");
        if (_photoScrollViewer != null)
        {
            // AddHandler 走路由事件，捕获所有 descendant 上的 PointerEntered/Exited
            _photoScrollViewer.AddHandler(PointerEnteredEvent, OnPhotoPointerEnteredRouted, handledEventsToo: false);
            _photoScrollViewer.AddHandler(PointerExitedEvent, OnPhotoPointerExitedRouted, handledEventsToo: false);
        }

        // 拖拽框选：多选模式下 PointerPressed / Moved / Released 在 ScrollViewer 上处理
        // 注意：用 AddHandler + routing 抓 descendant 事件
        if (_photoScrollViewer != null)
        {
            _photoScrollViewer.AddHandler(PointerPressedEvent, OnPhotoGridPointerPressed, handledEventsToo: true);
            _photoScrollViewer.AddHandler(PointerMovedEvent, OnPhotoGridPointerMoved, handledEventsToo: true);
            _photoScrollViewer.AddHandler(PointerReleasedEvent, OnPhotoGridPointerReleased, handledEventsToo: true);
        }
    }

    /// <summary>
    /// 路由 PointerEntered 事件：找到当前指针下的 photo tile（Button），更新 IsHovered。
    /// 用 HitTest 找到最近的 Button + DataContext 是 MediaFile 的节点。
    /// </summary>
    private void OnPhotoPointerEnteredRouted(object? sender, PointerEventArgs e)
    {
        var src = e.Source as Control;
        if (src == null) return;

        // 沿 visual tree 向上找 DataContext 为 MediaFile 的祖先（photo tile）
        var tile = FindPhotoTileAncestor(src);
        if (tile is Button btn && btn.DataContext is MediaFile mf)
        {
            mf.IsHovered = true;
            _hoveredFiles.Add(mf);
        }
    }

    private void OnPhotoPointerExitedRouted(object? sender, PointerEventArgs e)
    {
        var src = e.Source as Control;
        if (src == null) return;

        var tile = FindPhotoTileAncestor(src);
        if (tile is Button btn && btn.DataContext is MediaFile mf)
        {
            // 注意：Exited 时 source 可能不是原 tile；清掉 hover 后让 Entered 重新加
            mf.IsHovered = false;
            _hoveredFiles.Remove(mf);
        }
    }

    private static Control? FindPhotoTileAncestor(Control src)
    {
        var cur = src;
        while (cur != null)
        {
            if (cur is Button b && b.Classes.Contains("photo-tile")) return b;
            cur = cur.Parent as Control;
        }
        return null;
    }

    // ============================================================
    //  键盘左右箭头导航（spec §7）
    // ============================================================

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is not GalleryViewModel vm) { base.OnKeyDown(e); return; }

        if (e.Key == Key.Left || e.Key == Key.Right)
        {
            var files = vm.CurrentMediaFiles;
            if (files.Count == 0) { base.OnKeyDown(e); return; }

            int newIndex = e.Key == Key.Left
                ? Math.Max(0, _focusedIndex - 1)
                : Math.Min(files.Count - 1, _focusedIndex + 1);

            // 第一次按方向键时，从索引 0 开始（_focusedIndex = -1 → 第一次按右键 = 0）
            if (_focusedIndex < 0) newIndex = 0;

            // 清旧焦点
            if (_focusedIndex >= 0 && _focusedIndex < files.Count)
                files[_focusedIndex].IsKeyboardFocused = false;

            // 设新焦点
            files[newIndex].IsKeyboardFocused = true;
            _focusedIndex = newIndex;

            ScrollPhotoIntoView(newIndex);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>滚动 PhotoScrollViewer 让指定索引的 photo tile 可见。</summary>
    private void ScrollPhotoIntoView(int index)
    {
        if (_photoScrollViewer == null) return;
        if (DataContext is not GalleryViewModel vm) return;
        if (index < 0 || index >= vm.CurrentMediaFiles.Count) return;

        // WrapPanel ItemWidth=160 + Margin=8 → 实际 item 占位约 176px
        // WrapPanel 横向排列，按 colWidth=176 推算 row
        const double itemWidth = 176.0;
        var colCount = Math.Max(1, (_photoScrollViewer.Bounds.Width - 48) / itemWidth);
        var row = (int)(index / colCount);
        var y = row * (120 + 16);  // 120 tile height + 16 margin
        _photoScrollViewer.Offset = new Vector(_photoScrollViewer.Offset.X, y);
    }

    // ============================================================
    //  拖拽矩形框选（spec §8）
    // ============================================================

    private void OnPhotoGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not GalleryViewModel vm) return;
        if (!vm.IsMultiSelectMode) return;

        // 只处理左键 + 未在 marquee 中
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (_isMarqueeSelecting) return;

        _dragStart = e.GetPosition(PhotoGridHost);
        _isMarqueeSelecting = true;

        _marqueeRect = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#204FC3F7")),
            BorderBrush = new SolidColorBrush(Color.Parse("#4FC3F7")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            IsHitTestVisible = false,
            IsVisible = false,
        };
        MarqueeLayer?.Children.Add(_marqueeRect);
    }

    private void OnPhotoGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isMarqueeSelecting || _marqueeRect == null) return;
        if (DataContext is not GalleryViewModel vm) return;

        var current = e.GetPosition(PhotoGridHost);
        var x = Math.Min(_dragStart.X, current.X);
        var y = Math.Min(_dragStart.Y, current.Y);
        var w = Math.Abs(current.X - _dragStart.X);
        var h = Math.Abs(current.Y - _dragStart.Y);

        Canvas.SetLeft(_marqueeRect, x);
        Canvas.SetTop(_marqueeRect, y);
        _marqueeRect.Width = w;
        _marqueeRect.Height = h;
        _marqueeRect.IsVisible = w > 5 || h > 5;

        if (w < 5 && h < 5) return;  // < 5px 视作单击，不更新选中

        var rect = new Rect(x, y, w, h);
        foreach (var mf in vm.CurrentMediaFiles)
        {
            var bounds = GetItemBounds(mf);
            if (bounds is null) continue;
            var intersects = rect.Intersects(bounds.Value);
            if (intersects && !vm.SelectedFiles.Contains(mf))
                vm.SelectedFiles.Add(mf);
            else if (!intersects && vm.SelectedFiles.Contains(mf))
                vm.SelectedFiles.Remove(mf);
        }
    }

    private void OnPhotoGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isMarqueeSelecting = false;
        if (_marqueeRect != null)
        {
            MarqueeLayer?.Children.Remove(_marqueeRect);
            _marqueeRect = null;
        }
    }

    /// <summary>根据 MediaFile 在照片网格中的位置反推 Rect（用于 marquee 命中）。</summary>
    private Rect? GetItemBounds(MediaFile mf)
    {
        if (_photoScrollViewer == null) return null;
        if (DataContext is not GalleryViewModel vm) return null;

        var idx = vm.CurrentMediaFiles.IndexOf(mf);
        if (idx < 0) return null;

        // WrapPanel: ItemWidth=160 ItemHeight=120 + Margin=8
        const double itemW = 160.0;
        const double itemH = 120.0;
        const double gap = 16.0;  // Margin=8 on each side → gap 16
        const double marginLeft = 24.0;  // ItemsControl Margin

        var panelWidth = _photoScrollViewer.Bounds.Width;
        var colCount = Math.Max(1, (int)((panelWidth - marginLeft) / (itemW + gap)));
        var col = idx % colCount;
        var row = idx / colCount;

        var x = marginLeft + col * (itemW + gap) - _photoScrollViewer.Offset.X;
        var y = row * (itemH + gap) - _photoScrollViewer.Offset.Y;
        return new Rect(x, y, itemW, itemH);
    }

    // ============================================================
    //  公开给 XAML 的辅助属性（PhotoGridHost 定位 + SelectionCanvas 拖拽层）
    // ============================================================

    /// <summary>XAML 里给拖拽起点用的参考 Grid（外层 Grid 包住 ItemsControl + SelectionCanvas）。</summary>
    private Grid? PhotoGridHost => _photoScrollViewer?.Content as Grid;

    /// <summary>XAML 里给 marquee Border 用的 Canvas（改名避开 Avalonia XAML name generator 冲突）。</summary>
    private Canvas? MarqueeLayer
    {
        get
        {
            if (_photoScrollViewer?.Content is Grid g)
            {
                return g.Children.OfType<Canvas>().FirstOrDefault();
            }
            return null;
        }
    }
}
