using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Inventory.HiddenTests;

public class HiddenT2Tests
{
    [Fact]
    public void Ship_more_than_on_hand_throws_with_details()
    {
        var ledger = new StockLedger();
        ledger.Receive("A-1", 3);

        var ex = Assert.Throws<InsufficientStockException>(() => ledger.Ship("A-1", 10));

        Assert.Equal("A-1", ex.Sku);
        Assert.Equal(10, ex.Requested);
        Assert.Equal(3, ex.Available);
    }

    [Fact]
    public void Failed_ship_leaves_stock_unchanged()
    {
        var ledger = new StockLedger();
        ledger.Receive("A-1", 3);

        Assert.Throws<InsufficientStockException>(() => ledger.Ship("A-1", 4));

        Assert.Equal(3, ledger.OnHand("A-1"));
    }

    [Fact]
    public void Ship_unknown_sku_throws_and_creates_no_negative_stock()
    {
        var ledger = new StockLedger();

        var ex = Assert.Throws<InsufficientStockException>(() => ledger.Ship("nope", 1));

        Assert.Equal(0, ex.Available);
        Assert.Equal(0, ledger.OnHand("nope"));
    }

    [Fact]
    public void Ship_exact_on_hand_leaves_zero()
    {
        var ledger = new StockLedger();
        ledger.Receive("A-1", 3);

        ledger.Ship("A-1", 3);

        Assert.Equal(0, ledger.OnHand("A-1"));
        Assert.Throws<InsufficientStockException>(() => ledger.Ship("A-1", 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Ship_still_rejects_non_positive_quantity(int quantity)
    {
        var ledger = new StockLedger();
        ledger.Receive("A-1", 3);

        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Ship("A-1", quantity));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Ship_still_rejects_blank_sku(string? sku)
    {
        Assert.ThrowsAny<ArgumentException>(() => new StockLedger().Ship(sku!, 1));
    }
}
