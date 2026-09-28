namespace AI.Factory.Application.Abstractions.Gateway;

public class ModelGatewayException : Exception
{
    public ModelGatewayException(string message) : base(message) { }
    public ModelGatewayException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ModelGatewayNotFoundException : ModelGatewayException
{
    public string GatewayKey { get; }

    public ModelGatewayNotFoundException(string gatewayKey)
        : base($"Model gateway '{gatewayKey}' is not registered.")
    {
        GatewayKey = gatewayKey;
    }
}

public sealed class ModelGatewayDisabledException : ModelGatewayException
{
    public string GatewayKey { get; }

    public ModelGatewayDisabledException(string gatewayKey)
        : base($"Model gateway '{gatewayKey}' is disabled in configuration.")
    {
        GatewayKey = gatewayKey;
    }
}

public sealed class DuplicateModelGatewayException : ModelGatewayException
{
    public string GatewayKey { get; }

    public DuplicateModelGatewayException(string gatewayKey)
        : base($"A model gateway with key '{gatewayKey}' is already registered.")
    {
        GatewayKey = gatewayKey;
    }
}
