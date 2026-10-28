using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Solar.App.ViewModels;
using Windows.UI.ViewManagement;

namespace Solar.App.Views.Controls;

/// <summary>
/// 把右下角的启动按钮和底部进度卡片封装在一起：空闲是按钮，启动时展开成通栏卡片。
/// 形变动画在 code-behind 里完成，支持减少动态效果和窗口重排。
/// </summary>
public sealed partial class LaunchDock : UserControl
{
    private enum MorphState { Idle, Opening, Open, Closing }

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(HomeViewModel), typeof(LaunchDock),
        new PropertyMetadata(null, OnViewModelChanged));

    private Storyboard? _morph;
    private MorphState _state = MorphState.Idle;
    private bool _isLoaded;
    private RectangleGeometry? _clipGeometry;
    private HomeViewModel? _subscribed;

    public LaunchDock()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public HomeViewModel? ViewModel
    {
        get => (HomeViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        if (ViewModel?.IsLaunchCardOpen == true)
        {
            ApplyOpenState();
        }
        else
        {
            ApplyIdleState();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        StopMorph();
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var dock = (LaunchDock)d;

        if (dock._subscribed is { } oldVm)
        {
            oldVm.PropertyChanged -= dock.OnViewModelPropertyChanged;
        }

        dock._subscribed = e.NewValue as HomeViewModel;
        if (dock._subscribed is { } vm)
        {
            vm.PropertyChanged += dock.OnViewModelPropertyChanged;
        }

        if (dock._isLoaded && dock.ViewModel is { } viewModel)
        {
            if (viewModel.IsLaunchCardOpen)
            {
                dock.Open();
            }
            else
            {
                dock.Close();
            }
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeViewModel.IsLaunchCardOpen))
        {
            if (ViewModel?.IsLaunchCardOpen == true)
            {
                Open();
            }
            else
            {
                Close();
            }
        }
    }

    private void Open()
    {
        if (!_isLoaded || DockRoot.ActualWidth <= 0 || _state == MorphState.Open)
        {
            return;
        }

        // 收起动画进行到一半又要展开：先落到空闲态，再从头量尺寸
        if (_state == MorphState.Closing)
        {
            ApplyIdleState();
        }

        StopMorph();
        _state = MorphState.Opening;

        // 量出空闲状态下按钮的尺寸
        var buttonWidth = LaunchButton.ActualWidth > 0 ? LaunchButton.ActualWidth : 180;
        var buttonHeight = LaunchButton.ActualHeight > 0 ? LaunchButton.ActualHeight : 40;
        var targetWidth = DockRoot.ActualWidth;

        // 固定内容尺寸后再展开，避免每帧重排文字
        Surface.Opacity = 1;
        CardContent.Visibility = Visibility.Visible;
        CardContent.Opacity = 0;
        CardContent.IsHitTestVisible = false;
        CardContent.Width = targetWidth;
        CardContent.Height = double.NaN;
        CardContent.HorizontalAlignment = HorizontalAlignment.Right;
        CardContent.VerticalAlignment = VerticalAlignment.Bottom;
        CardContent.Measure(new Windows.Foundation.Size(targetWidth, double.PositiveInfinity));
        var targetHeight = Math.Max(CardContent.DesiredSize.Height, buttonHeight);

        CardContent.Width = targetWidth;
        CardContent.Height = targetHeight;
        Surface.Width = buttonWidth;
        Surface.Height = buttonHeight;
        Surface.HorizontalAlignment = HorizontalAlignment.Right;
        Surface.VerticalAlignment = VerticalAlignment.Bottom;
        DockRoot.UpdateLayout();

        EnsureClip();

        if (!AnimationsEnabled())
        {
            ApplyOpenState();
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(280));
        var widthAnim = new DoubleAnimation
        {
            From = buttonWidth,
            To = targetWidth,
            Duration = duration,
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var heightAnim = new DoubleAnimation
        {
            From = buttonHeight,
            To = targetHeight,
            Duration = duration,
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var buttonFadeAnim = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(120)),
        };
        var contentFadeAnim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(80),
            Duration = duration,
        };

        Storyboard.SetTarget(widthAnim, Surface);
        Storyboard.SetTargetProperty(widthAnim, "Width");
        Storyboard.SetTarget(heightAnim, Surface);
        Storyboard.SetTargetProperty(heightAnim, "Height");
        Storyboard.SetTarget(buttonFadeAnim, LaunchButton);
        Storyboard.SetTargetProperty(buttonFadeAnim, "Opacity");
        Storyboard.SetTarget(contentFadeAnim, CardContent);
        Storyboard.SetTargetProperty(contentFadeAnim, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(widthAnim);
        storyboard.Children.Add(heightAnim);
        storyboard.Children.Add(buttonFadeAnim);
        storyboard.Children.Add(contentFadeAnim);
        storyboard.Completed += OnOpenCompleted;

        _morph = storyboard;
        storyboard.Begin();
    }

    private void OnOpenCompleted(object? sender, object e)
    {
        if (ReferenceEquals(_morph, sender) && _state == MorphState.Opening)
        {
            ApplyOpenState();
        }
    }

    private void Close()
    {
        if (!_isLoaded)
        {
            ApplyIdleState();
            return;
        }

        if (_state == MorphState.Idle)
        {
            return;
        }

        // 展开到一半就要收起：先落到展开态，保证起点尺寸稳定
        if (_state == MorphState.Opening)
        {
            ApplyOpenState();
        }

        StopMorph();
        _state = MorphState.Closing;

        var startWidth = Surface.ActualWidth;
        var startHeight = Surface.ActualHeight;
        var contentWidth = CardContent.ActualWidth > 0 ? CardContent.ActualWidth : startWidth;
        var contentHeight = CardContent.ActualHeight > 0 ? CardContent.ActualHeight : startHeight;

        CardContent.Visibility = Visibility.Visible;
        CardContent.Width = contentWidth;
        CardContent.Height = contentHeight;
        CardContent.HorizontalAlignment = HorizontalAlignment.Right;
        CardContent.VerticalAlignment = VerticalAlignment.Bottom;
        Surface.Width = startWidth;
        Surface.Height = startHeight;
        Surface.HorizontalAlignment = HorizontalAlignment.Right;
        Surface.VerticalAlignment = VerticalAlignment.Bottom;
        LaunchButton.Visibility = Visibility.Visible;
        LaunchButton.Opacity = 0;
        DockRoot.UpdateLayout();

        var buttonWidth = LaunchButton.ActualWidth > 0 ? LaunchButton.ActualWidth : 180;
        var buttonHeight = LaunchButton.ActualHeight > 0 ? LaunchButton.ActualHeight : 40;

        if (!AnimationsEnabled())
        {
            ApplyIdleState();
            return;
        }

        EnsureClip();

        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        var widthAnim = new DoubleAnimation
        {
            From = startWidth,
            To = buttonWidth,
            Duration = duration,
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        var heightAnim = new DoubleAnimation
        {
            From = startHeight,
            To = buttonHeight,
            Duration = duration,
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        var contentFadeAnim = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = duration,
        };
        var buttonFadeAnim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = duration,
        };

        Storyboard.SetTarget(widthAnim, Surface);
        Storyboard.SetTargetProperty(widthAnim, "Width");
        Storyboard.SetTarget(heightAnim, Surface);
        Storyboard.SetTargetProperty(heightAnim, "Height");
        Storyboard.SetTarget(contentFadeAnim, CardContent);
        Storyboard.SetTargetProperty(contentFadeAnim, "Opacity");
        Storyboard.SetTarget(buttonFadeAnim, LaunchButton);
        Storyboard.SetTargetProperty(buttonFadeAnim, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(widthAnim);
        storyboard.Children.Add(heightAnim);
        storyboard.Children.Add(contentFadeAnim);
        storyboard.Children.Add(buttonFadeAnim);
        storyboard.Completed += OnCloseCompleted;

        _morph = storyboard;
        storyboard.Begin();
    }

    private void OnCloseCompleted(object? sender, object e)
    {
        if (ReferenceEquals(_morph, sender) && _state == MorphState.Closing)
        {
            ApplyIdleState();
        }
    }

    private void ApplyOpenState()
    {
        _state = MorphState.Open;
        StopMorph();

        _clipGeometry = null;
        ClipHost.Clip = null;
        CardContent.Visibility = Visibility.Visible;
        CardContent.Opacity = 1;
        CardContent.Width = double.NaN;
        CardContent.Height = double.NaN;
        CardContent.HorizontalAlignment = HorizontalAlignment.Stretch;
        CardContent.VerticalAlignment = VerticalAlignment.Stretch;
        CardContent.IsHitTestVisible = true;
        Surface.Width = double.NaN;
        Surface.Height = double.NaN;
        Surface.HorizontalAlignment = HorizontalAlignment.Stretch;
        Surface.Opacity = 1;
        LaunchButton.Visibility = Visibility.Collapsed;
    }

    private void ApplyIdleState()
    {
        _state = MorphState.Idle;
        StopMorph();

        _clipGeometry = null;
        ClipHost.Clip = null;
        Surface.Width = double.NaN;
        Surface.Height = double.NaN;
        Surface.HorizontalAlignment = HorizontalAlignment.Right;
        Surface.VerticalAlignment = VerticalAlignment.Bottom;
        Surface.Opacity = 0;
        CardContent.Visibility = Visibility.Collapsed;
        CardContent.Opacity = 0;
        CardContent.Width = double.NaN;
        CardContent.Height = double.NaN;
        CardContent.HorizontalAlignment = HorizontalAlignment.Right;
        CardContent.VerticalAlignment = VerticalAlignment.Bottom;
        LaunchButton.Visibility = Visibility.Visible;
        LaunchButton.Opacity = 1;
    }

    private void StopMorph()
    {
        if (_morph is { } storyboard)
        {
            _morph = null;
            storyboard.Stop();
        }
    }

    private void EnsureClip()
    {
        _clipGeometry ??= new RectangleGeometry();
        ClipHost.Clip = _clipGeometry;
        UpdateClip();
    }

    private void UpdateClip()
    {
        if (_clipGeometry is not null)
        {
            _clipGeometry.Rect = new Windows.Foundation.Rect(0, 0, ClipHost.ActualWidth, ClipHost.ActualHeight);
        }
    }

    private void ClipHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateClip();

    private void DockRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 窗口在展开过程中改变尺寸：直接落到最终态，避免朝旧宽度继续动。
        // 只比较宽度：展开动画本身会改变 DockRoot 的高度，不能当成窗口重排。
        if (_state == MorphState.Opening && e.PreviousSize.Width != e.NewSize.Width)
        {
            ApplyOpenState();
        }

        UpdateClip();
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
