# T1 — Stock reservations (feature)

Repository: a .NET 10 solution `Inventory.slnx` with `src/Inventory` and `tests/Inventory.Tests`.

Add stock reservations to `Inventory.StockLedger`:

- `Guid Reserve(string sku, int quantity)`
  - Validates `sku` and `quantity` exactly like `Receive` (`ArgumentException` for null/blank sku, `ArgumentOutOfRangeException` for quantity <= 0).
  - If `quantity > Available(sku)`, throws `InsufficientStockException(sku, quantity, Available(sku))` and changes nothing.
  - Otherwise records a reservation and returns its new unique id.
- `int Available(string sku)` — `OnHand(sku)` minus the sum of active reservations for that sku.
- `void Release(Guid reservationId)` — removes the reservation; `OnHand` is unchanged. Unknown or already-released id throws `KeyNotFoundException`.
- `void Fulfill(Guid reservationId)` — ships the reserved quantity: decreases `OnHand` by the reserved quantity and removes the reservation. Unknown or already-finished id throws `KeyNotFoundException`.

Do not change the behaviour of `Receive`, `Ship` or `OnHand`.

Done when: `dotnet build Inventory.slnx` and `dotnet test Inventory.slnx` pass, with tests covering the new behaviour.
