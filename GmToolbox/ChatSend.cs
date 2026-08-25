using System.Text;

namespace AscensionNetTool;

/// <summary>Lua + CMSG helpers to send in-game chat through ExtProxy.</summary>
static class ChatSend
{
    public static string BuildSendScript(string channel, string message)
    {
        string ch = LuaQuote(channel);
        string msg = LuaQuote(message);
        return
            "do local ch=" + ch + " local msg=" + msg + " " +
            "local function resolveId() " +
            "local id=0 " +
            "local function idOf(name) " +
            "if type(GetChannelName)~='function' or not name then return 0 end " +
            "local n=GetChannelName(name) " +
            "n=tonumber(n) or 0 " +
            "return n end " +
            "id=idOf(ch) " +
            "if id==0 then id=idOf(string.lower(ch)) end " +
            "if id==0 then id=idOf(string.upper(ch)) end " +
            "if id==0 and type(GetChannelList)=='function' then " +
            "local list={GetChannelList()} " +
            "local want=string.lower(ch) " +
            "for i=1,#list,3 do " +
            "local cid=tonumber(list[i]) or 0 " +
            "local cname=tostring(list[i+1] or '') " +
            "if cid>0 and string.lower(cname)==want then id=cid break end end " +
            "if id==0 then for i=1,#list,2 do " +
            "local cid=tonumber(list[i]) or 0 " +
            "local cname=tostring(list[i+1] or '') " +
            "if type(list[i+1])=='string' and cid>0 and string.lower(cname)==want then id=cid break end " +
            "end end end " +
            "return id end " +
            "local function announce(tag, detail) " +
            "local line='|cffE6007E[GM chat]|r '..tostring(tag or '')..' '..tostring(detail or '') " +
            "if DEFAULT_CHAT_FRAME and DEFAULT_CHAT_FRAME.AddMessage then " +
            "pcall(DEFAULT_CHAT_FRAME.AddMessage, DEFAULT_CHAT_FRAME, line) end " +
            "if type(print)=='function' then pcall(print, line) end end " +
            "local function reportOut() " +
            "local me=(UnitName and UnitName('player')) or 'me' " +
            "local guid=(UnitGUID and UnitGUID('player')) or '' " +
            "if type(GmReportChat)=='function' then pcall(GmReportChat, ch, me, msg, guid) end end " +
            "local function trySend() " +
            "local id=resolveId() " +
            "if not id or id<=0 then return false end " +
            "if type(SendChatMessage)~='function' then announce('FAIL','SendChatMessage missing') return true end " +
            "local ok,err=pcall(SendChatMessage, msg, 'CHANNEL', nil, id) " +
            "if not ok then " +
            "ok,err=pcall(SendChatMessage, msg, 'CHANNEL', nil, tostring(id)) " +
            "end " +
            "if ok then announce('SENT','#'..ch..' id='..tostring(id)) reportOut() return true end " +
            "announce('FAIL', tostring(err)) return true end " +
            "if trySend() then return end " +
            "announce('JOIN', ch) " +
            "if type(JoinPermanentChannel)=='function' then pcall(JoinPermanentChannel, ch) " +
            "elseif type(JoinTemporaryChannel)=='function' then pcall(JoinTemporaryChannel, ch) end " +
            "if type(CreateFrame)~='function' then announce('FAIL','no CreateFrame for deferred send') return end " +
            "local f=CreateFrame('Frame') f.t=0 f.n=0 " +
            "f:SetScript('OnUpdate', function(self, dt) " +
            "self.t=(self.t or 0)+(dt or 0) if self.t<0.25 then return end " +
            "self.t=0 self.n=(self.n or 0)+1 " +
            "if trySend() or self.n>=16 then self:SetScript('OnUpdate', nil) " +
            "if self.n>=16 then announce('FAIL','channel id never resolved for '..ch) end end end) end";
    }

    public static byte[] BuildCmsgMessageChatChannel(string channel, string message, uint language = 0)
    {
        channel ??= "";
        message ??= "";
        if (message.Length > 255) message = message[..255];
        var chBytes = Encoding.UTF8.GetBytes(channel);
        var msgBytes = Encoding.UTF8.GetBytes(message);
        var pkt = new byte[4 + 4 + 4 + chBytes.Length + 1 + msgBytes.Length + 1];
        BitConverter.TryWriteBytes(pkt.AsSpan(0, 4), (uint)0x0095);
        BitConverter.TryWriteBytes(pkt.AsSpan(4, 4), (uint)0x11);
        BitConverter.TryWriteBytes(pkt.AsSpan(8, 4), language);
        int o = 12;
        Buffer.BlockCopy(chBytes, 0, pkt, o, chBytes.Length);
        o += chBytes.Length;
        pkt[o++] = 0;
        Buffer.BlockCopy(msgBytes, 0, pkt, o, msgBytes.Length);
        o += msgBytes.Length;
        pkt[o] = 0;
        return pkt;
    }

    static string LuaQuote(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        sb.Append('\'');
        foreach (char c in s)
        {
            if (c is '\'' or '\\') sb.Append('\\').Append(c);
            else if (c is '\n' or '\r' or '\t') sb.Append(' ');
            else if (c < 32) continue;
            else sb.Append(c);
        }
        sb.Append('\'');
        return sb.ToString();
    }
}
