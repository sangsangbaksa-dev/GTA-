using Harbor.Core.Economy;
using Xunit;

namespace Harbor.Core.Tests.Economy;

public class LedgerTests
{
    private static readonly AccountId Faucet = AccountId.Faucet("test");
    private static readonly AccountId A = AccountId.Wallet("a");
    private static readonly AccountId B = AccountId.Wallet("b");

    [Fact]
    public void Transfers_Conserve_Money_And_Track_Circulation()
    {
        var l = new Ledger();
        var rng = new Random(5);
        l.Post(new Transfer("seed", Faucet, A, 10_000, Currency.Dirty, "seed"));
        for (var i = 0; i < 500; i++)
        {
            var (from, to) = rng.Next(2) == 0 ? (A, B) : (B, A);
            l.Post(new Transfer($"t{i}", from, to, rng.Next(1, 300), Currency.Dirty, "move"));
            if (i % 50 == 0) l.Post(new Transfer($"s{i}", A, AccountId.Sink("fee"), 10, Currency.Dirty, "fee"));
        }
        Assert.Equal(0, l.Total(Currency.Dirty));
        Assert.Equal(l.Balance(A, Currency.Dirty) + l.Balance(B, Currency.Dirty), l.Circulating(Currency.Dirty));
        Assert.True(l.Balance(A, Currency.Dirty) >= 0 && l.Balance(B, Currency.Dirty) >= 0);
    }

    [Fact]
    public void Same_TxId_Applies_Once()
    {
        var l = new Ledger();
        var t = new Transfer("pay-1", Faucet, A, 500, Currency.Clean, "x");
        Assert.Equal(TxStatus.Applied, l.Post(t).Status);
        Assert.Equal(TxStatus.Duplicate, l.Post(t).Status);
        Assert.Equal(500, l.Balance(A, Currency.Clean));
    }

    [Fact]
    public void Players_Cannot_Overdraw_But_Faucets_Can()
    {
        var l = new Ledger();
        Assert.Equal(TxStatus.InsufficientFunds, l.Post(new Transfer("x", A, B, 1, Currency.Dirty, "x")).Status);
        Assert.Equal(TxStatus.Applied, l.Post(new Transfer("y", Faucet, A, 1, Currency.Dirty, "x")).Status);
        Assert.Equal(-1, l.Balance(Faucet, Currency.Dirty));
    }

    [Fact]
    public void Batch_Is_All_Or_Nothing()
    {
        var l = new Ledger();
        l.Post(new Transfer("seed", Faucet, A, 100, Currency.Dirty, "seed"));
        var r = l.PostBatch("b", new[]
        {
            new Transfer("", A, B, 80, Currency.Dirty, "x"),
            new Transfer("", A, B, 30, Currency.Dirty, "x"), // would overdraw after the first
        });
        Assert.Equal(TxStatus.InsufficientFunds, r.Status);
        Assert.Equal(100, l.Balance(A, Currency.Dirty));
        Assert.Equal(0, l.Balance(B, Currency.Dirty));
        Assert.False(l.WasApplied("b"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_Positive_Amounts_Are_Invalid(long amount)
        => Assert.Equal(TxStatus.Invalid, new Ledger().Post(new Transfer("z", Faucet, A, amount, Currency.Dirty, "x")).Status);

    [Fact]
    public void Governor_Applies_Diminishing_Returns()
    {
        var g = new PayoutGovernor(new EconomyConfig.DiminishingDef(3, 0.2, 0.4, 3600));
        var muls = new List<double>();
        for (var i = 0; i < 6; i++) { muls.Add(g.Multiplier("p", "atm", i * 60)); g.Record("p", "atm", i * 60); }
        Assert.Equal(new[] { 1.0, 1.0, 0.8, 0.6, 0.4, 0.4 }, muls.Select(m => System.Math.Round(m, 6)));
        Assert.Equal(1.0, g.Multiplier("p", "register", 400)); // per activity
        Assert.Equal(1.0, g.Multiplier("p", "atm", 10_000));   // window expired
    }

    [Fact]
    public void Rep_Levels_Up_With_Xp()
    {
        var p = new PlayerProfile("p", "P");
        Assert.Equal(1, p.AddXp(PlayerProfile.XpForLevel(0)));
        Assert.Equal(1, p.Rep);
        Assert.Equal(0, p.AddXp(10));
    }
}
