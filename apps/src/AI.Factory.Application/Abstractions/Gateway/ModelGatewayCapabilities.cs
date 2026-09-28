namespace AI.Factory.Application.Abstractions.Gateway;

[Flags]
public enum ModelGatewayCapabilities
{
    None = 0,
    Chat = 1 << 0,
    Streaming = 1 << 1,
    ToolCalling = 1 << 2,
    StructuredOutput = 1 << 3,
    Vision = 1 << 4
}
