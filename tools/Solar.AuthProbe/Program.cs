using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Solar.Core;
using Solar.Core.Accounts;

// 验证微软登录链路。默认使用启动器内置的客户端 ID：
//   dotnet run --project tools/Solar.AuthProbe
// 也可以传一个 ID 覆盖它（用于验证自己注册的应用）：
//   dotnet run --project tools/Solar.AuthProbe -- <clientId>

var clientId = args.Length > 0 ? args[0] : "";

var root = Path.Combine(Path.GetTempPath(), "solar-authprobe");
var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
services.AddSolarCore(root);

// 命令行传入的 ID 只覆盖本次运行用的 AuthOptions，不落盘
var isOverride = !string.IsNullOrWhiteSpace(clientId);
if (isOverride)
{
    services.AddSingleton(new AuthOptions { ClientId = clientId.Trim() });
}

using var provider = services.BuildServiceProvider();

var options = provider.GetRequiredService<AuthOptions>();
var configured = !string.IsNullOrWhiteSpace(options.ClientId);
Console.WriteLine($"当前客户端 ID: {(configured ? "已配置" : "未配置")}（{(isOverride ? "命令行参数" : "内置默认值")}）");

if (!configured)
{
    Console.WriteLine();
    Console.WriteLine("未配置客户端 ID，无法走完登录流程。");
    Console.WriteLine("请把 ID 作为参数传入本工具。");
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
