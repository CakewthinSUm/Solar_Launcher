using Solar.Core.Accounts;
using Solar.Core.Paths;
using Xunit;

namespace Solar.Core.Tests;

public class AccountStoreTests
{
    private static AppPaths NewPaths() =>
        new(Path.Combine(Path.GetTempPath(), "solar-acc-" + Guid.NewGuid().ToString("N")));

    private static GameAccount NewAccount(string id, string name = "Steve") => new()
    {
        Id = id,
        Username = name,
        Uuid = MicrosoftAuthService.FormatUuid(id),
        Xuid = "2535400000000000",
    };

    [Fact]
    public async Task SaveAndGetRoundTrip()
    {
        var paths = NewPaths();
        var store = new AccountStore(paths);

        await store.SaveAsync(NewAccount("8667ba71b85a4004af54457a9734eed7"));

        var loaded = await store.GetAsync("8667ba71b85a4004af54457a9734eed7");
        Assert.NotNull(loaded);
        Assert.Equal("Steve", loaded.Username);
        Assert.Equal("2535400000000000", loaded.Xuid);
    }

    [Fact]
    public async Task SavingSameIdUpdatesInsteadOfDuplicating()
    {
        var paths = NewPaths();
        var store = new AccountStore(paths);

        await store.SaveAsync(NewAccount("abc", "Steve"));
        await store.SaveAsync(NewAccount("abc", "Alex"));

        var all = await store.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("Alex", all[0].Username);
    }

    [Fact]
    public async Task FirstSavedAccountBecomesActive()
    {
        var paths = NewPaths();
        var store = new AccountStore(paths);

        await store.SaveAsync(NewAccount("abc"));
        Assert.Equal("abc", store.ActiveAccountId);
    }

    [Fact]
    public async Task DeletingActiveAccountFallsBackToRemaining()
    {
        var paths = NewPaths();
        var store = new AccountStore(paths);

        await store.SaveAsync(NewAccount("first"));
        await store.SaveAsync(NewAccount("second"));

        store.ActiveAccountId = "first";
        await store.DeleteAsync("first");

        Assert.Equal("second", store.ActiveAccountId);
        Assert.Single(await store.GetAllAsync());
    }

    [Fact]
    public async Task PersistsAcrossInstances()
    {
        var paths = NewPaths();
        var first = new AccountStore(paths);
        await first.SaveAsync(NewAccount("abc", "Steve"));

        var second = new AccountStore(paths);
        var loaded = await second.GetAsync("abc");

        Assert.NotNull(loaded);
        Assert.Equal("Steve", loaded.Username);
    }

    [Fact]
    public async Task EmptyStoreReturnsNothing()
    {
        var store = new AccountStore(NewPaths());
        Assert.Empty(await store.GetAllAsync());
        Assert.Null(await store.GetAsync("missing"));
    }

    [Fact]
    public async Task AccountFileContainsNoTokens()
    {
        var paths = NewPaths();
        var store = new AccountStore(paths);
        await store.SaveAsync(NewAccount("abc"));

        var json = await File.ReadAllTextAsync(paths.AccountsFile);

        // 令牌由 CmlLib 的账户管理器单独保管，这个文件只放展示信息
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("xsts", json, StringComparison.OrdinalIgnoreCase);
    }
}

public class UuidFormattingTests
{
    [Theory]
    [InlineData("8667ba71b85a4004af54457a9734eed7", "8667ba71-b85a-4004-af54-457a9734eed7")]
    [InlineData("8667BA71B85A4004AF54457A9734EED7", "8667ba71-b85a-4004-af54-457a9734eed7")]
    [InlineData("8667ba71-b85a-4004-af54-457a9734eed7", "8667ba71-b85a-4004-af54-457a9734eed7")]
    public void FormatUuidNormalizesBothForms(string input, string expected)
    {
        Assert.Equal(expected, MicrosoftAuthService.FormatUuid(input));
    }

    [Fact]
    public void NormalizeIdStripsDashesAndLowercases()
    {
        Assert.Equal("8667ba71b85a4004af54457a9734eed7",
            MicrosoftAuthService.NormalizeId("8667BA71-B85A-4004-AF54-457A9734EED7"));
    }

    [Fact]
    public void MalformedUuidIsReturnedUnchanged()
    {
        Assert.Equal("not-a-uuid", MicrosoftAuthService.FormatUuid("not-a-uuid"));
    }
}

public class AccountErrorTranslationTests
{
    [Theory]
    [InlineData("2148916227", AccountErrorKind.XboxBanned)]
    [InlineData("2148916233", AccountErrorKind.XboxAccountRequired)]
    [InlineData("2148916235", AccountErrorKind.RegionBlocked)]
    [InlineData("2148916236", AccountErrorKind.NeedsAdultVerification)]
    [InlineData("2148916237", AccountErrorKind.NeedsAdultVerification)]
    [InlineData("2148916238", AccountErrorKind.ChildAccountRestricted)]
    public void XstsErrorCodesMapToKinds(string xerr, AccountErrorKind expected)
    {
        // XSTS 失败时错误码藏在异常消息里，UI 需要据此给出具体指引
        var ex = new InvalidOperationException($"Xbox auth failed. XErr: {xerr}");
        Assert.Equal(expected, MicrosoftAuthService.Translate(ex).Kind);
    }

    [Fact]
    public void OwnershipErrorIsDetected()
    {
        var ex = new InvalidOperationException("{\"error\":\"NOT_FOUND\"}");
        Assert.Equal(AccountErrorKind.NoMinecraftOwnership, MicrosoftAuthService.Translate(ex).Kind);
    }

    [Fact]
    public void UnknownErrorFallsBack()
    {
        var ex = new InvalidOperationException("something odd");
        Assert.Equal(AccountErrorKind.Unknown, MicrosoftAuthService.Translate(ex).Kind);
    }

    [Fact]
    public void TranslatedMessageIsUserFacingChinese()
    {
        var ex = new InvalidOperationException("2148916233");
        var translated = MicrosoftAuthService.Translate(ex);

        Assert.Contains("Xbox", translated.Message);
        Assert.DoesNotContain("2148916233", translated.Message);
    }
}

public class AuthOptionsTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("00000000-0000-0000-0000-000000000000", true)]
    public void IsConfiguredRequiresNonBlankClientId(string? clientId, bool expected)
    {
        var options = new AuthOptions { ClientId = clientId ?? "" };
        Assert.Equal(expected, options.IsConfigured);
    }
}
