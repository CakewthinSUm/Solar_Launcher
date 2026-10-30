using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Solar.App.ViewModels;
using Windows.UI.ViewManagement;

namespace Solar.App.Views;

public sealed partial class HomePage : PageBase
{
    private Storyboard? _warningStoryboard;
    private RectangleGeometry? _warningClip;
    private bool _warningVisible;

    public HomePage() : this(App.GetService<HomeViewModel>())
    {
    }

    public HomePage(HomeViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // 展开/收起时按当前高度裁剪，避免文字溢出到卡片外面
        _warningClip = new RectangleGeometry();
        NoVersionWarning.Clip = _warningClip;
        NoVersionWarning.SizeChanged += (_, _) => UpdateWarningClip();

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public new HomeViewModel ViewModel { get; }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HomeViewModel.IsNoVersionWarningOpen))
        {
            return;
        }

        if (ViewModel.IsNoVersionWarningOpen)
        {
            ShowNoVersionWarning();
        }
        else
        {
            HideNoVersionWarning();
        }
    }

    /// <summary>从 0 高度展开到内容高度，让「准备启动」卡片平滑长高。</summary>
    private void ShowNoVersionWarning()
    {
        if (_warningVisible)
        {
            return;
        }

        _warningVisible = true;
        StopWarningAnimation();

        // 先量出自然高度，再立刻压回 0；同一轮布局里完成，不会闪一下完整高度
        NoVersionWarning.IsOpen = true;
        NoVersionWarning.Height = double.NaN;
        NoVersionWarning.Opacity = 1;
        NoVersionWarning.UpdateLayout();

        var targetHeight = Math.Max(NoVersionWarning.ActualHeight, NoVersionWarning.DesiredSize.Height);
        if (!AnimationsEnabled() || targetHeight <= 0)
        {
            NoVersionWarning.Height = double.NaN;
            UpdateWarningClip();
            return;
        }

        NoVersionWarning.Height = 0;
        NoVersionWarning.Opacity = 0;
        UpdateWarningClip();

        AnimateWarning(
            fromHeight: 0,
            toHeight: targetHeight,
            fromOpacity: 0,
            toOpacity: 1,
            duration: TimeSpan.FromMilliseconds(180),
            easing: EasingMode.EaseOut,
            onCompleted: () =>
            {
                NoVersionWarning.Height = double.NaN;
                NoVersionWarning.Opacity = 1;
                UpdateWarningClip();
            });
    }

    /// <summary>收起到 0 高度后再关闭，卡片跟着平滑缩回去。</summary>
    private void HideNoVersionWarning()
    {
        if (!_warningVisible)
        {
            return;
        }

        _warningVisible = false;
        StopWarningAnimation();

        var startHeight = NoVersionWarning.ActualHeight;
        if (!AnimationsEnabled() || startHeight <= 0)
        {
            ApplyWarningClosed();
            return;
        }

        AnimateWarning(
            fromHeight: startHeight,
            toHeight: 0,
            fromOpacity: NoVersionWarning.Opacity,
            toOpacity: 0,
            duration: TimeSpan.FromMilliseconds(150),
            easing: EasingMode.EaseIn,
            onCompleted: ApplyWarningClosed);
    }

    private void AnimateWarning(
        double fromHeight,
        double toHeight,
        double fromOpacity,
        double toOpacity,
        TimeSpan duration,
        EasingMode easing,
        Action onCompleted)
    {
        var heightAnimation = new DoubleAnimation
        {
            From = fromHeight,
            To = toHeight,
            Duration = new Duration(duration),
            // 高度是布局属性，不开这个开关动画不会生效
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = easing },
        };
        var opacityAnimation = new DoubleAnimation
        {
            From = fromOpacity,
            To = toOpacity,
            Duration = new Duration(duration),
            EasingFunction = new CubicEase { EasingMode = easing },
        };

        Storyboard.SetTarget(heightAnimation, NoVersionWarning);
        Storyboard.SetTargetProperty(heightAnimation, "Height");
        Storyboard.SetTarget(opacityAnimation, NoVersionWarning);
        Storyboard.SetTargetProperty(opacityAnimation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(heightAnimation);
        storyboard.Children.Add(opacityAnimation);
        storyboard.Completed += (_, _) =>
        {
            // 被新的动画顶掉时不要收尾，否则会把新动画的起始状态改掉
            if (ReferenceEquals(_warningStoryboard, storyboard))
            {
                _warningStoryboard = null;
                onCompleted();
            }
        };

        _warningStoryboard = storyboard;
        storyboard.Begin();
    }

    private void ApplyWarningClosed()
    {
        NoVersionWarning.IsOpen = false;
        NoVersionWarning.Height = double.NaN;
        NoVersionWarning.Opacity = 1;
        UpdateWarningClip();
    }

    private void StopWarningAnimation()
    {
        if (_warningStoryboard is { } storyboard)
        {
            _warningStoryboard = null;
            storyboard.Stop();
        }
    }

    private void UpdateWarningClip()
    {
        if (_warningClip is not null)
        {
            _warningClip.Rect = new Windows.Foundation.Rect(
                0,
                0,
                NoVersionWarning.ActualWidth,
                NoVersionWarning.ActualHeight);
        }
    }

    private static bool AnimationsEnabled()
    {
        try
        {
            return new UISettings().AnimationsEnabled;
        }
        catch
        {
            return true;
        }
    }
}
