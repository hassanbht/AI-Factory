using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Inventory.HiddenTests;

public class HiddenT1Tests
{
    private static StockLedger LedgerWith(string sku, int quantity)
    {
        var ledger = new StockLedger();
        ledger.Receive(sku, quantity);
        return ledger;
    }

    [Fact]
    public void Reserve_reduces_available_not_on_hand()
    {
        var ledger = LedgerWith("A", 10);

        ledger.Reserve("A", 4);

        Assert.Equal(6, ledger.Available("A"));
        Assert.Equal(10, ledger.OnHand("A"));
    }

    [Fact]
    public void Reserve_returns_unique_ids()
    {
        var ledger = LedgerWith("A", 10);

        Assert.NotEqual(ledger.Reserve("A", 1), ledger.Reserve("A", 1));
    }

    [Fact]
    public void Reserve_up_to_available_is_allowed()
    {
        var ledger = LedgerWith("A", 5);

        ledger.Reserve("A", 2);
        ledger.Reserve("A", 3);

        Assert.Equal(0, ledger.Available("A"));
    }

    [Fact]
    public void Reserve_more_than_available_throws_and_changes_nothing()
    {
        var ledger = LedgerWith("A", 5);
        ledger.Reserve("A", 3);

        var ex = Assert.Throws<InsufficientStockException>(() => ledger.Reserve("A", 3));

        Assert.Equal("A", ex.Sku);
        Assert.Equal(3, ex.Requested);
        Assert.Equal(2, ex.Available);
        Assert.Equal(2, ledger.Available("A"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void Reserve_rejects_non_positive_quantity(int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LedgerWith("A", 5).Reserve("A", quantity));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Reserve_rejects_blank_sku(string? sku)
    {
        Assert.ThrowsAny<ArgumentException>(() => LedgerWith("A", 5).Reserve(sku!, 1));
    }

    [Fact]
    public void Available_is_zero_for_unknown_sku()
    {
        Assert.Equal(0, new StockLedger().Available("nope"));
    }

    [Fact]
    public void Available_is_per_sku()
    {
        var ledger = LedgerWith("A", 5);
        ledger.Receive("B", 7);

        ledger.Reserve("A", 5);

        Assert.Equal(7, ledger.Available("B"));
    }

    [Fact]
    public void Release_restores_available_and_keeps_on_hand()
    {
        var ledger = LedgerWith("A", 5);
        var id = ledger.Reserve("A", 4);

        ledger.Release(id);

        Assert.Equal(5, ledger.Available("A"));
        Assert.Equal(5, ledger.OnHand("A"));
    }

    [Fact]
    public void Release_unknown_or_twice_throws()
    {
        var ledger = LedgerWith("A", 5);
        var id = ledger.Reserve("A", 1);
        ledger.Release(id);

        Assert.Throws<KeyNotFoundException>(() => ledger.Release(id));
        Assert.Throws<KeyNotFoundException>(() => ledger.Release(Guid.NewGuid()));
    }

    [Fact]
    public void Fulfill_ships_reserved_quantity()
    {
        var ledger = LedgerWith("A", 10);
        var id = ledger.Reserve("A", 4);
        ledger.Reserve("A", 1);

        ledger.Fulfill(id);

        Assert.Equal(6, ledger.OnHand("A"));
        Assert.Equal(5, ledger.Available("A"));
    }

    [Fact]
    public void Fulfill_unknown_released_or_twice_throws()
    {
        var ledger = LedgerWith("A", 10);
        var fulfilled = ledger.Reserve("A", 1);
        var released = ledger.Reserve("A", 1);
        ledger.Fulfill(fulfilled);
        ledger.Release(released);

        Assert.Throws<KeyNotFoundException>(() => ledger.Fulfill(fulfilled));
        Assert.Throws<KeyNotFoundException>(() => ledger.Fulfill(released));
        Assert.Throws<KeyNotFoundException>(() => ledger.Fulfill(Guid.NewGuid()));
        Assert.Equal(9, ledger.OnHand("A"));
    }

    [Fact]
    public void Receive_and_on_hand_behaviour_unchanged()
    {
        var ledger = LedgerWith("A", 2);
        ledger.Receive("A", 3);
        ledger.Ship("A", 1);

        Assert.Equal(4, ledger.OnHand("A"));
        Assert.Equal(4, ledger.Available("A"));
    }
}
