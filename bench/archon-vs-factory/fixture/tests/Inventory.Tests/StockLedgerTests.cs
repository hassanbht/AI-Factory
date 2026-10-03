namespace Inventory.Tests;

public class StockLedgerTests
{
    [Fact]
    public void Receive_increases_on_hand()
    {
        var ledger = new StockLedger();

        ledger.Receive("A-1", 5);
        ledger.Receive("A-1", 3);

        Assert.Equal(8, ledger.OnHand("A-1"));
    }

    [Fact]
    public void OnHand_is_zero_for_unknown_sku()
    {
        Assert.Equal(0, new StockLedger().OnHand("missing"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Receive_rejects_non_positive_quantity(int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StockLedger().Receive("A-1", quantity));
    }

    [Fact]
    public void Ship_decreases_on_hand()
    {
        var ledger = new StockLedger();
        ledger.Receive("A-1", 5);

        ledger.Ship("A-1", 2);

        Assert.Equal(3, ledger.OnHand("A-1"));
    }
}
