using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Inventory.HiddenTests;

public class HiddenT3Tests
{
    private static ImportResult Import(string csv, StockLedger ledger) =>
        CsvStockImporter.Import(new StringReader(csv), ledger);

    [Fact]
    public void Sku_is_case_insensitive_and_trimmed()
    {
        var ledger = new StockLedger();

        ledger.Receive("abc", 1);
        ledger.Receive(" ABC ", 2);

        Assert.Equal(3, ledger.OnHand("Abc"));
        Assert.Equal(3, ledger.OnHand(" abc"));
    }

    [Fact]
    public void Ship_uses_normalised_sku()
    {
        var ledger = new StockLedger();
        ledger.Receive("abc", 5);

        ledger.Ship("ABC ", 2);

        Assert.Equal(3, ledger.OnHand("abc"));
    }

    [Fact]
    public void Imports_valid_lines()
    {
        var ledger = new StockLedger();

        var result = Import("sku,qty\nA,5\nB,2\na,1\n", ledger);

        Assert.Equal(3, result.ImportedCount);
        Assert.Empty(result.Errors);
        Assert.Equal(6, ledger.OnHand("A"));
        Assert.Equal(2, ledger.OnHand("B"));
    }

    [Fact]
    public void Header_is_trimmed_and_case_insensitive()
    {
        var ledger = new StockLedger();

        var result = Import("  SKU,QTY  \r\nA,1\r\n", ledger);

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, ledger.OnHand("A"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sku;qty\nA,1")]
    [InlineData("A,1\nB,2")]
    [InlineData("sku,qty,extra\nA,1")]
    public void Bad_or_missing_header_throws_and_touches_nothing(string csv)
    {
        var ledger = new StockLedger();

        Assert.Throws<FormatException>(() => Import(csv, ledger));

        Assert.Equal(0, ledger.OnHand("A"));
    }

    [Fact]
    public void Invalid_lines_reported_with_physical_line_numbers_and_rest_applied()
    {
        var ledger = new StockLedger();
        var csv = string.Join("\n",
            "sku,qty",   // 1
            "A,5",       // 2 ok
            "",          // 3 blank, skipped
            "B,abc",     // 4 non-int
            " ,3",       // 5 blank sku
            "C,0",       // 6 qty not > 0
            "D,-1",      // 7 negative
            "E",         // 8 missing field
            "F,1,2",     // 9 too many fields
            "   ",       // 10 whitespace, skipped
            " g , 4 ");  // 11 ok, trimmed

        var result = Import(csv, ledger);

        Assert.Equal(2, result.ImportedCount);
        Assert.Equal(new[] { 4, 5, 6, 7, 8, 9 }, result.Errors.Select(e => e.LineNumber).ToArray());
        Assert.All(result.Errors, e => Assert.False(string.IsNullOrWhiteSpace(e.Message)));
        Assert.Equal(5, ledger.OnHand("A"));
        Assert.Equal(4, ledger.OnHand("G"));
        Assert.Equal(0, ledger.OnHand("B"));
        Assert.Equal(0, ledger.OnHand("C"));
        Assert.Equal(0, ledger.OnHand("F"));
    }

    [Fact]
    public void Header_only_imports_nothing()
    {
        var result = Import("sku,qty\n", new StockLedger());

        Assert.Equal(0, result.ImportedCount);
        Assert.Empty(result.Errors);
    }
}
