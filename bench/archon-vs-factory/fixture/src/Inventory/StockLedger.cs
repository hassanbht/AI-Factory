namespace Inventory;

public sealed class StockLedger
{
    private readonly Dictionary<string, int> _onHand = new();

    public void Receive(string sku, int quantity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        _onHand[sku] = OnHand(sku) + quantity;
    }

    public void Ship(string sku, int quantity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        _onHand[sku] = OnHand(sku) - quantity;
    }

    public int OnHand(string sku) => _onHand.TryGetValue(sku, out var quantity) ? quantity : 0;
}
