using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solar.Core;
using Solar.Core.Accounts;

// 验证微软登录链路。需要先填好客户端 ID：
//   dotnet run --project tools/Solar.AuthProbe -- <clientId>
// 留空则只检查配置与端点可达性。

var clientId = args.Length > 0 ? args[0] : "";

var root = Path.Combine(Path.GetTempPath(), "solar-authprobe");
var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
services.AddSolarCore(root);

using var provider = services.BuildServiceProvider();

var settings = provider.GetRequiredService<Solar.Core.Settings.ISettingsService>();
await settings.LoadAsync();

if (!string.IsNullOrWhiteSpace(clientId))
{
    settings.Current.MicrosoftClientId = clientId.Trim();
    await settings.SaveAsync();
    Console.WriteLine($"已写入客户端 ID: {clientId[..Math.Min(8, clientId.Length)]}…");
}

// 服务实际读取的是设置里的值（AuthOptions 只是默认值兜底），
// 所以这里也要按设置判断，否则刚写入的 ID 会被误判为未配置。
var configured = !string.IsNullOrWhiteSpace(settings.Current.MicrosoftClientId);
Console.WriteLine($"当前客户端 ID 配置: {(configured ? "已配置" : "未配置")}");

if (!configured)
{
    Console.WriteLine();
    Console.WriteLine("未配置客户端 ID，无法走完登录流程。");
    Console.WriteLine("请在 Azure 门户注册应用后，把 ID 作为参数传入本工具，");
    Console.WriteLine("或在启动器「设置 → 账户」中填写。");
    return 2;
}

var auth = provider.GetRequiredService<IMicrosoftAuthService>();
var store = provider.GetRequiredService<IAccountStore>();

try
{
    var account = await auth.SignInAsync(prompt =>
    {
        Console.WriteLine();
        Console.WriteLine("================ 设备码登录 ================");
        Console.WriteLine($"  1. 打开: {prompt.VerificationUri}");
        Console.WriteLine($"  2. 输入代码: {prompt.UserCode}");
        Console.WriteLine("===========================================");
        Console.WriteLine();
        return Task.CompletedTask;
    });

    Console.WriteLine($"登录成功: {account.Username}");
    Console.WriteLine($"  UUID: {account.Uuid}");
    Console.WriteLine($"  XUID: {account.Xuid ?? "(无)"}");

    // 再走一次静默刷新，确认令牌可复用
    var session = await auth.AcquireSessionAsync(account.Id);
    Console.WriteLine($"静默刷新成功，令牌长度: {session.AccessToken.Length}");
    Console.WriteLine($"  UserType: {session.UserType}");

    Console.WriteLine();
    Console.WriteLine("清理测试账户…");
    await auth.SignOutAsync(account.Id);
    Console.WriteLine("完成。");
    return 0;
}
catch (AccountException ex)
{
    Console.Error.WriteLine($"[{ex.Kind}] {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"失败: {ex}");
    return 1;
}
