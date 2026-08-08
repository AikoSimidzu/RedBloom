using System.Windows;
using System.Windows.Media.Animation;

namespace RedBloom.Controls;

/// <summary>
/// Animates a <see cref="GridLength"/> in pixels. WPF ships no such animation, so a column
/// that should slide open or shut has to be driven by one of these.
/// </summary>
public sealed class GridLengthAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty =
        DependencyProperty.Register(nameof(From), typeof(double), typeof(GridLengthAnimation));

    public static readonly DependencyProperty ToProperty =
        DependencyProperty.Register(nameof(To), typeof(double), typeof(GridLengthAnimation));

    public double From
    {
        get => (double)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public double To
    {
        get => (double)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public IEasingFunction? EasingFunction { get; set; }

    public override Type TargetPropertyType => typeof(GridLength);

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    public override object GetCurrentValue(
        object defaultOriginValue,
        object defaultDestinationValue,
        AnimationClock animationClock)
    {
        var progress = animationClock.CurrentProgress ?? 1.0;
        if (EasingFunction is not null)
        {
            progress = EasingFunction.Ease(progress);
        }

        return new GridLength(From + ((To - From) * progress), GridUnitType.Pixel);
    }
}
