namespace Inventory;

public sealed class InsufficientStockException(string sku, int requested, int available)
    : InvalidOperationException($"Insufficient stock for '{sku}': requested {requested}, available {available}.")
{
    public string Sku { get; } = sku;
    public int Requested { get; } = requested;
    public int Available { get; } = available;
}
