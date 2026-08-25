using HeadlessClient.Domain.Abstractions;
using HeadlessClient.Domain.Protocol;
using HeadlessClient.Domain.World;
using HeadlessClient.Infrastructure.Chat;
using HeadlessClient.Infrastructure.Protocol;

namespace HeadlessClient.Infrastructure.Protocol;

public sealed class WorldInboundProjector
{
    private readonly IChatLog _chat;
    private readonly UpdateObjectProjector _updates;
    private readonly ChatMediator? _mediator;
    private readonly string _observerAccount;
    private readonly string _ownerUserId;

    public WorldInboundProjector(
        IWorldClient world,
        IChatLog chat,
        UpdateObjectProjector updates,
        ChatMediator? mediator = null,
        string? observerAccount = null,
        string? ownerUserId = null)
    {
        _chat = chat;
        _updates = updates;
        _mediator = mediator;
        _observerAccount = observerAccount ?? "";
        _ownerUserId = ownerUserId ?? "";
        world.PacketReceived += OnPacket;
    }

    private void OnPacket(Packet packet)
    {
        if (packet.Opcode is Opcodes.SmsgMessageChat or Opcodes.SmsgGmMessageChat or Opcodes.SmsgNotification)
        {
            if (packet.Opcode == Opcodes.SmsgNotification)
            {
                if (packet.Payload.Length > 0)
                {
                    var msg = ReadCString(packet.Payload.Span);
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        _chat.Append(new ChatLine(
                            DateTimeOffset.UtcNow,
                            ChatTypes.System,
                            "0",
                            "",
                            "NOTIFICATION",
                            msg.Trim(),
                            ReadableText: msg.Trim(),
                            Scope: "shared",
                            ObserverAccount: _observerAccount));
                    }
                }

                return;
            }

            if (ChatPacketDecoder.TryDecode(packet.Payload.Span, out var line))
            {
                line = _mediator?.EnrichIncoming(line) ?? line;
                line = line with
                {
                    Scope = "shared",
                    ObserverAccount = _observerAccount
                };
                if (ChatTrafficClassifier.IsLootCollector(line))
                {
                    _chat.AppendLoot(line);
                }
                else
                {
                    _chat.Append(line);
                }
            }

            return;
        }

        if (packet.Opcode is Opcodes.SmsgUpdateObject or Opcodes.SmsgCompressedUpdateObject)
        {
            _updates.Project(packet);
        }
    }

    private static string ReadCString(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        if (end < 0)
        {
            end = data.Length;
        }

        return System.Text.Encoding.UTF8.GetString(data[..end]);
    }
}
