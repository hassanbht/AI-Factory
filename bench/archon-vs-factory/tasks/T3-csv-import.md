# T3 — CSV stock import and case-insensitive SKUs (multi-file feature)

Repository: a .NET 10 solution `Inventory.slnx` with `src/Inventory` and `tests/Inventory.Tests`.

Warehouses want to bulk-receive stock from a CSV export, and they type SKUs inconsistently.

## Part A — SKU normalisation

Across the whole `StockLedger`, SKUs are case-insensitive and surrounding whitespace is ignored:
`Receive("abc", 1)` then `OnHand("ABC")` and `OnHand(" abc ")` both return 1.

## Part B — CSV import

Add, in namespace `Inventory`:

```csharp
public sealed record ImportError(int LineNumber, string Message);
public sealed record ImportResult(int ImportedCount, IReadOnlyList<ImportError> Errors);
public static class CsvStockImporter
{
    public static ImportResult Import(TextReader reader, StockLedger ledger);
}
```

Rules:

- Line 1 must be the header `sku,qty` (compare trimmed, case-insensitive). Missing or different header, or empty input: throw `FormatException` and do not touch the ledger.
- Every following line is a data line. Blank or whitespace-only lines are skipped (not errors) but still count for line numbering.
- A valid data line has exactly two comma-separated fields: a non-blank sku and an integer quantity > 0 (both trimmed). Valid lines are applied with `ledger.Receive`.
- An invalid data line is not applied and produces an `ImportError` with its 1-based physical line number (header is line 1) and a non-empty message. Processing continues with the next line.
- `ImportedCount` is the number of applied lines. `Errors` are in line order.

Done when: `dotnet build Inventory.slnx` and `dotnet test Inventory.slnx` pass, with tests covering both parts.
