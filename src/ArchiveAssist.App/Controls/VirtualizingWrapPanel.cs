using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ArchiveAssist.App.Controls;

/// <summary>
/// A recycling, vertically scrolling wrap panel with fixed-size page-card slots.
/// Only rows near the viewport are materialized.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ThumbnailSizeProperty =
        DependencyProperty.Register(
            nameof(ThumbnailSize),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(
                150d,
                FrameworkPropertyMetadataOptions.AffectsMeasure,
                OnLayoutPropertyChanged),
            value => value is double size && double.IsFinite(size) && size >= 50);

    public static readonly DependencyProperty CacheRowsProperty =
        DependencyProperty.Register(
            nameof(CacheRows),
            typeof(int),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(
                2,
                FrameworkPropertyMetadataOptions.AffectsMeasure),
            value => value is int rows && rows >= 0 && rows <= 20);

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _itemsPerRow = 1;

    public double ThumbnailSize
    {
        get => (double)GetValue(ThumbnailSizeProperty);
        set => SetValue(ThumbnailSizeProperty, value);
    }

    public int CacheRows
    {
        get => (int)GetValue(CacheRowsProperty);
        set => SetValue(CacheRowsProperty, value);
    }

    public bool CanHorizontallyScroll { get; set; }

    public bool CanVerticallyScroll { get; set; } = true;

    public double ExtentHeight => _extent.Height;

    public double ExtentWidth => _extent.Width;

    public double HorizontalOffset => _offset.X;

    public double VerticalOffset => _offset.Y;

    public double ViewportHeight => _viewport.Height;

    public double ViewportWidth => _viewport.Width;

    public ScrollViewer? ScrollOwner { get; set; }

    public int RealizedItemCount => InternalChildren.Count;

    public int ItemsPerRow => _itemsPerRow;

    private double SlotWidth => ThumbnailSize + 54;

    private double SlotHeight => ThumbnailSize + 96;

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemsControl = ItemsControl.GetItemsOwner(this);
        var itemCount = itemsControl?.Items.Count ?? 0;
        var viewportWidth = double.IsFinite(availableSize.Width)
            ? Math.Max(1, availableSize.Width)
            : Math.Max(1, ActualWidth);
        var viewportHeight = double.IsFinite(availableSize.Height)
            ? Math.Max(1, availableSize.Height)
            : Math.Max(SlotHeight, ActualHeight);
        _itemsPerRow = Math.Max(1, (int)Math.Floor(viewportWidth / SlotWidth));
        var rowCount = itemCount == 0
            ? 0
            : (int)Math.Ceiling(itemCount / (double)_itemsPerRow);

        UpdateScrollInfo(
            new Size(viewportWidth, rowCount * SlotHeight),
            new Size(viewportWidth, viewportHeight));

        if (itemCount == 0)
        {
            CleanupItems(0, -1);
            return FiniteDesiredSize(availableSize);
        }

        var firstVisibleRow = Math.Max(0, (int)Math.Floor(VerticalOffset / SlotHeight));
        var visibleRowCount = Math.Max(
            1,
            (int)Math.Ceiling(ViewportHeight / SlotHeight) + 1);
        var firstRealizedRow = Math.Max(0, firstVisibleRow - CacheRows);
        var lastRealizedRow = Math.Min(
            rowCount - 1,
            firstVisibleRow + visibleRowCount + CacheRows);
        var firstIndex = firstRealizedRow * _itemsPerRow;
        var lastIndex = Math.Min(
            itemCount - 1,
            ((lastRealizedRow + 1) * _itemsPerRow) - 1);

        RealizeItems(firstIndex, lastIndex);
        CleanupItems(firstIndex, lastIndex);
        return FiniteDesiredSize(availableSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var centeredLeft = Math.Max(
            0,
            (finalSize.Width - (_itemsPerRow * SlotWidth)) / 2);
        var owner = ItemsControl.GetItemsOwner(this);
        var generator = owner?.ItemContainerGenerator;
        if (generator is null)
        {
            return finalSize;
        }

        foreach (UIElement child in InternalChildren)
        {
            var itemIndex = generator.IndexFromContainer(child);
            if (itemIndex < 0)
            {
                continue;
            }

            var row = itemIndex / _itemsPerRow;
            var column = itemIndex % _itemsPerRow;
            child.Arrange(new Rect(
                centeredLeft + (column * SlotWidth),
                (row * SlotHeight) - VerticalOffset,
                SlotWidth,
                SlotHeight));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        var itemCount = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        if (index < 0 || index >= itemCount)
        {
            return;
        }

        SetVerticalOffset((index / Math.Max(1, _itemsPerRow)) * SlotHeight);
    }

    public void LineDown() => SetVerticalOffset(VerticalOffset + 48);

    public void LineLeft()
    {
    }

    public void LineRight()
    {
    }

    public void LineUp() => SetVerticalOffset(VerticalOffset - 48);

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is not DependencyObject dependencyObject)
        {
            return rectangle;
        }

        var owner = ItemsControl.GetItemsOwner(this);
        var container = owner is null
            ? null
            : ItemsControl.ContainerFromElement(owner, dependencyObject);
        var itemIndex = owner?.ItemContainerGenerator.IndexFromContainer(container)
                        ?? -1;
        if (itemIndex < 0)
        {
            return rectangle;
        }

        var itemTop = (itemIndex / _itemsPerRow) * SlotHeight;
        var itemBottom = itemTop + SlotHeight;
        if (itemTop < VerticalOffset)
        {
            SetVerticalOffset(itemTop);
        }
        else if (itemBottom > VerticalOffset + ViewportHeight)
        {
            SetVerticalOffset(itemBottom - ViewportHeight);
        }

        return new Rect(0, itemTop, SlotWidth, SlotHeight);
    }

    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + 144);

    public void MouseWheelLeft()
    {
    }

    public void MouseWheelRight()
    {
    }

    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - 144);

    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);

    public void PageLeft()
    {
    }

    public void PageRight()
    {
    }

    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);

    public void SetHorizontalOffset(double offset)
    {
    }

    public void SetVerticalOffset(double offset)
    {
        if (!double.IsFinite(offset))
        {
            return;
        }

        var maximum = Math.Max(0, ExtentHeight - ViewportHeight);
        var coerced = Math.Clamp(offset, 0, maximum);
        if (Math.Abs(coerced - _offset.Y) < 0.1)
        {
            return;
        }

        _offset.Y = coerced;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
        InvalidateArrange();
    }

    private static void OnLayoutPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not VirtualizingWrapPanel panel)
        {
            return;
        }

        panel.SetVerticalOffset(0);
        panel.InvalidateMeasure();
    }

    private void UpdateScrollInfo(Size extent, Size viewport)
    {
        var changed = extent != _extent || viewport != _viewport;
        _extent = extent;
        _viewport = viewport;

        var maximumOffset = Math.Max(0, ExtentHeight - ViewportHeight);
        if (_offset.Y > maximumOffset)
        {
            _offset.Y = maximumOffset;
            changed = true;
        }

        if (changed)
        {
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    private Size FiniteDesiredSize(Size availableSize) =>
        new(
            double.IsFinite(availableSize.Width)
                ? availableSize.Width
                : Math.Max(SlotWidth, ExtentWidth),
            double.IsFinite(availableSize.Height)
                ? availableSize.Height
                : Math.Max(SlotHeight, ExtentHeight));

    private void RealizeItems(int firstIndex, int lastIndex)
    {
        var generator = GetPanelGenerator();
        if (generator is null)
        {
            return;
        }

        var startPosition = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = startPosition.Offset == 0
            ? startPosition.Index
            : startPosition.Index + 1;
        childIndex = Math.Max(0, childIndex);

        using (generator.StartAt(
                   startPosition,
                   GeneratorDirection.Forward,
                   allowStartAtRealizedItem: true))
        {
            for (var itemIndex = firstIndex;
                 itemIndex <= lastIndex;
                 itemIndex++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out var newlyRealized);
                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count)
                    {
                        AddInternalChild(child);
                    }
                    else
                    {
                        InsertInternalChild(childIndex, child);
                    }

                    generator.PrepareItemContainer(child);
                }

                child.Measure(new Size(SlotWidth, SlotHeight));
            }
        }
    }

    private void CleanupItems(int firstIndex, int lastIndex)
    {
        var generator = GetPanelGenerator();
        var owner = ItemsControl.GetItemsOwner(this);
        if (generator is null || owner is null)
        {
            return;
        }

        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var child = InternalChildren[childIndex];
            var itemIndex = owner.ItemContainerGenerator.IndexFromContainer(child);
            if (itemIndex >= firstIndex && itemIndex <= lastIndex)
            {
                continue;
            }

            if (itemIndex >= 0)
            {
                var generatorPosition = generator.GeneratorPositionFromIndex(itemIndex);
                if (generator is IRecyclingItemContainerGenerator recyclingGenerator)
                {
                    recyclingGenerator.Recycle(generatorPosition, 1);
                }
                else
                {
                    generator.Remove(generatorPosition, 1);
                }
            }

            RemoveInternalChildRange(childIndex, 1);
        }
    }

    private IItemContainerGenerator? GetPanelGenerator()
    {
        var owner = ItemsControl.GetItemsOwner(this);
        return owner is null
            ? null
            : ((IItemContainerGenerator)owner.ItemContainerGenerator)
            .GetItemContainerGeneratorForPanel(this);
    }
}
