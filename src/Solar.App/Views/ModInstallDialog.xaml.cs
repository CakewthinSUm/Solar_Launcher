using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Solar.App.ViewModels;
using Solar.Core.Instances;
using Solar.Core.Mods;

namespace Solar.App.Views;

/// <summary>
/// 安装对话框。
/// <para>
/// 不用 <c>ContentDialog.ShowAsync()</c> 的默认按钮流程：安装要下载几十 MB，
/// 点「安装」后对话框不能立刻关，必须先完成（或失败）再决定关不关。
/// 所以 PrimaryButtonClick 里把 <see cref="ContentDialogButtonClickEventArgs.Cancel"/> 置 true，
/// 装完再手动 <see cref="ContentDialog.Hide"/>。
/// </para>
/// </summary>
public sealed partial class ModInstallDialog : ContentDialog
{
    public ModInstallDialog(ModProject project, ModVersion version, GameInstance instance)
        : this(App.GetService<ModInstallDialogViewModel>(), project, version, instance)
    {
    }

    public ModInstallDialog(
        ModInstallDialogViewModel viewModel,
        ModProject project,
        ModVersion version,
        GameInstance instance)
    {
        ViewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        XamlRoot = App.MainWindow?.Content?.XamlRoot;
        PrimaryButtonClick += OnPrimaryButtonClick;
        Closing += OnClosing;

        // 勾选变化要更新「安装 N 个文件」，这里统一监听清单项
        ViewModel.Items.CollectionChanged += (_, _) => SubscribeItems();

        _ = LoadAsync(project, version, instance);
    }

    public ModInstallDialogViewModel ViewModel { get; }

    /// <summary>安装成功后的结果；用户取消或失败时为 null。</summary>
    public ModInstallResult? Result { get; private set; }

    private async Task LoadAsync(ModProject project, ModVersion version, GameInstance instance)
    {
        await ViewModel.LoadAsync(project, version, instance);
        SubscribeItems();
    }

    private void SubscribeItems()
    {
        foreach (var item in ViewModel.Items)
        {
            item.PropertyChanged -= OnItemChanged;
            item.PropertyChanged += OnItemChanged;
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModInstallItemViewModel.IsChecked))
        {
            ViewModel.RefreshSummary();
        }
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 先拦住关闭，等安装结束
        args.Cancel = true;

        if (await ViewModel.InstallAsync())
        {
            Result = ViewModel.Result;
            Hide();
            return;
        }

        // 失败或取消后重新放开按钮，让用户能重试或直接关掉
        ViewModel.RefreshSummary();
    }

    /// <summary>
    /// 安装中不允许用 Esc / 取消关掉对话框，否则用户会以为已经装完了；
    /// 但要把正在下载的任务停掉，不然它会在后台继续写文件。
    /// </summary>
    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (ViewModel.IsInstalling)
        {
            args.Cancel = true;
            ViewModel.Cancel();
            return;
        }

        ViewModel.Cancel();
    }
}
