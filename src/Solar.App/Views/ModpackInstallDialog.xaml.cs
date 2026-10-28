using Microsoft.UI.Xaml.Controls;
using Solar.App.ViewModels;
using Solar.Core.Modpacks;
using Solar.Core.Mods;

namespace Solar.App.Views;

/// <summary>
/// 整合包安装对话框。
/// <para>
/// 打开时就开始下载并解析整合包（可能几秒钟到十几秒），解析完才允许点安装。
/// 安装过程中不允许用 Esc 关掉，但会把下载任务停下来。
/// </para>
/// </summary>
public sealed partial class ModpackInstallDialog : ContentDialog
{
    public ModpackInstallDialog(ModVersion version)
        : this(App.GetService<ModpackInstallDialogViewModel>(), version)
    {
    }

    public ModpackInstallDialog(ModpackInstallDialogViewModel viewModel, ModVersion version)
    {
        ViewModel = viewModel;
        DataContext = viewModel;

        InitializeComponent();

        XamlRoot = App.MainWindow?.Content?.XamlRoot;
        PrimaryButtonClick += OnPrimaryButtonClick;
        Closing += OnClosing;
        Closed += (_, _) => ViewModel.Cleanup();

        _ = ViewModel.LoadAsync(version);
    }

    public ModpackInstallDialogViewModel ViewModel { get; }

    /// <summary>安装成功后的结果；用户取消或失败时为 null。</summary>
    public ModpackInstallResult? Result { get; private set; }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 先拦住关闭，等安装结束
        args.Cancel = true;

        if (await ViewModel.InstallAsync())
        {
            Result = ViewModel.Result;
            Hide();
        }
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (ViewModel.IsBusyNow)
        {
            args.Cancel = true;
            ViewModel.Cancel();
        }
    }
}
