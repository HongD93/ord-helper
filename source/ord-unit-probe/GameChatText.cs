using System.ComponentModel;
using System.Text;

internal sealed record ChatTextState(uint Edit, uint Buffer, byte[] Bytes);

// 채팅 편집기만 읽는다. 채팅 입력·전송과 게임 상태 변경은 호출하지 않는다.
static class GameChatText
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static ChatTextState Read(MemoryReader memory, uint moduleBase, Roots roots)
    {
        try { return ReadCore(memory, moduleBase, roots); }
        catch (Win32Exception error) { throw new IOException("chat_memory_read_failed", error); }
    }

    private static ChatTextState ReadCore(MemoryReader memory, uint moduleBase, Roots roots)
    {
        if (memory.U32(moduleBase + 0xD326F0) != roots.Ui) throw new IOException("chat_ui_changed");
        var bar = memory.U32(roots.Ui + 0x3FC);
        if (bar < 0x10000 || memory.U32(bar) != moduleBase + 0xAADFC4 || memory.U32(bar + 0x1DC) != 1)
            throw new IOException("chat_bar_changed");
        var edit = memory.U32(bar + 0x1E0);
        if (edit < 0x10000 || memory.U32(edit) != moduleBase + 0xAADE84) throw new IOException("chat_edit_changed");
        var buffer = memory.U32(edit + 0x1E4);
        var length = memory.U32(edit + 0x1E8);
        var capacity = memory.U32(edit + 0x1EC);
        if (length > 4096 || capacity > 65536 || length > capacity) throw new IOException("chat_length_invalid");
        byte[] bytes;
        if (buffer == 0 && length == 0) bytes = [];
        else
        {
            var raw = memory.Read(buffer, checked((int)length + 1));
            if (raw[^1] != 0 || raw.AsSpan(0, (int)length).Contains((byte)0)) throw new IOException("chat_termination_invalid");
            bytes = raw.AsSpan(0, (int)length).ToArray();
        }
        if (memory.U32(moduleBase + 0xD326F0) != roots.Ui || memory.U32(roots.Ui + 0x3FC) != bar ||
            memory.U32(bar) != moduleBase + 0xAADFC4 || memory.U32(bar + 0x1DC) != 1 || memory.U32(bar + 0x1E0) != edit ||
            memory.U32(edit) != moduleBase + 0xAADE84 || memory.U32(edit + 0x1E4) != buffer ||
            memory.U32(edit + 0x1E8) != length || memory.U32(edit + 0x1EC) != capacity)
            throw new IOException("chat_buffer_unstable");
        return new(edit, buffer, bytes);
    }

    internal static bool Matches(ChatTextState text, string expected)
    {
        try { return text.Bytes.AsSpan().SequenceEqual(StrictUtf8.GetBytes(expected)); }
        catch (EncoderFallbackException) { return false; }
    }

    internal static bool SameBuffer(ChatTextState first, ChatTextState second) =>
        first.Edit == second.Edit && first.Buffer == second.Buffer && first.Bytes.AsSpan().SequenceEqual(second.Bytes);
}
