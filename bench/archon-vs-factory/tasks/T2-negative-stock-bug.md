# T2 — Shipping can make stock negative (bug)

Repository: a .NET 10 solution `Inventory.slnx` with `src/Inventory` and `tests/Inventory.Tests`.

Bug report:

> Calling `StockLedger.Ship("A-1", 10)` when only 3 units of `A-1` are on hand succeeds and `OnHand("A-1")` becomes `-7`.

Expected behaviour:

- Shipping more than is on hand throws `Inventory.InsufficientStockException` (already exists) with `Sku`, `Requested` and `Available` set to the sku, the requested quantity and the current on-hand quantity.
- A failed ship leaves the stock unchanged.
- Shipping exactly the on-hand quantity is allowed and leaves `OnHand` at 0.
- Existing validation (blank sku, non-positive quantity) keeps working.

Done when: the root cause is fixed, a regression test exists, and `dotnet build Inventory.slnx` and `dotnet test Inventory.slnx` pass.
