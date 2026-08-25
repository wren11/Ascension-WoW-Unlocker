namespace HeadlessClient.Domain.Protocol;

public sealed record Packet(uint Opcode, ReadOnlyMemory<byte> Payload)
{
    public static Packet FromOpcodeAndBody(uint opcode, byte[] body) =>
        new(opcode, body);
}
