namespace Checkout;

public sealed class OrderApi
{
    public string CreateOrder(string basketId) =>
        $"Order accepted for basket {basketId}.";
}
