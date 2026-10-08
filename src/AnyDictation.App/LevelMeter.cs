using System;
using System.Windows;
using System.Windows.Media;

namespace AnyDictation.App;

/// <summary>入力レベルの履歴を縦棒で描く。右端が最新。棒の高さは最大の 20〜100%。</summary>
internal sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public int BarCount { get; init; } = 14;
    public double BarWidth { get; init; } = 3;
    public double Gap { get; init; } = 2;

    double[] _levels = [];

    /// <summary>0〜1 のレベルを末尾に追加する。</summary>
    public void Push(double level)
    {
        if (_levels.Length != BarCount) _levels = new double[BarCount];
        Array.Copy(_levels, 1, _levels, 0, BarCount - 1);
        _levels[^1] = Math.Clamp(level, 0, 1);
        InvalidateVisual();
    }

    public void Clear()
    {
        Array.Clear(_levels);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(BarCount * BarWidth + (BarCount - 1) * Gap, double.IsInfinity(availableSize.Height) ? 18 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        double height = ActualHeight;
        for (int i = 0; i < BarCount; i++)
        {
            double level = i < _levels.Length ? _levels[i] : 0;
            double h = height * (0.2 + 0.8 * level);
            double x = i * (BarWidth + Gap);
            dc.DrawRoundedRectangle(BarBrush, null, new Rect(x, (height - h) / 2, BarWidth, h), BarWidth / 2, BarWidth / 2);
        }
    }
}
