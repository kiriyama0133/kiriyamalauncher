using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;

namespace kiriyamalauncher.Presentation.Base.Controls;

/// <summary>
/// 字体大小设置组件：一根可点击/拖动的进度条 + 当前数值。
/// </summary>
public partial class FontSizeControl : UserControl
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<FontSizeControl, double>(nameof(Value), defaultValue: 14d, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<FontSizeControl, double>(nameof(Minimum), defaultValue: 12d);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<FontSizeControl, double>(nameof(Maximum), defaultValue: 22d);

    private bool _isDragging;

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public FontSizeControl()
    {
        InitializeComponent();

        PART_Bar.AddHandler(PointerPressedEvent, OnBarPointerPressed, RoutingStrategies.Tunnel);
        PART_Bar.AddHandler(PointerMovedEvent, OnBarPointerMoved, RoutingStrategies.Tunnel);
        PART_Bar.AddHandler(PointerReleasedEvent, OnBarPointerReleased, RoutingStrategies.Tunnel);
    }

    private void OnBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PART_Bar).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDragging = true;
        e.Pointer.Capture(PART_Bar);
        UpdateValueFromPointer(e.GetPosition(PART_Bar), PART_Bar.Bounds.Width);
    }

    private void OnBarPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging)
        {
            UpdateValueFromPointer(e.GetPosition(PART_Bar), PART_Bar.Bounds.Width);
        }
    }

    private void OnBarPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDragging = false;
        e.Pointer.Capture(null);
    }

    /// <summary>按指针在进度条上的横向位置换算出字号。</summary>
    private void UpdateValueFromPointer(Point position, double barWidth)
    {
        if (barWidth <= 0d)
        {
            return;
        }

        double ratio = Math.Clamp(position.X / barWidth, 0d, 1d);
        Value = Math.Round(Minimum + ratio * (Maximum - Minimum));
    }
}
