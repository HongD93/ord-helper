// 명령·재료·편집기를 확인한 뒤 전송 키를 한 번만 호출한다. 재시도는 읽기 관측에만 사용한다.
static class GameChatSubmission
{
    internal static Task<string?> SubmitAsync(string expected, ChatTextState typed,
        Func<ChatTextState> readText, Func<bool> inputValid, Func<bool> materialsReady,
        Action sendEnter, Func<bool> chatOpen, CancellationToken cancel,
        Func<CancellationToken, Task>? pause = null, Action<string>? trace = null) =>
        SubmitWithAsyncEnterAsync(expected, typed, readText, inputValid, materialsReady,
            _ => { sendEnter(); return Task.CompletedTask; }, chatOpen, inputValid, cancel, pause, trace);

    internal static async Task<string?> SubmitWithAsyncEnterAsync(string expected, ChatTextState typed,
        Func<ChatTextState> readText, Func<bool> inputValid, Func<bool> materialsReady,
        Func<CancellationToken, Task> sendEnter, Func<bool> chatOpen, Func<bool> postSubmitContextValid,
        CancellationToken cancel, Func<CancellationToken, Task>? pause = null, Action<string>? trace = null)
    {
        if (!GameInput.IsKoreanChatCommand(expected)) throw new ArgumentException("A verified Korean command is required.", nameof(expected));
        pause ??= token => Task.Delay(25, token);
        void Stage(string value) { try { trace?.Invoke(value); } catch (Exception error) when (error is IOException or InvalidOperationException) { } }
        cancel.ThrowIfCancellationRequested();
        Stage("validating_typed_command");
        if (!GameChatText.Matches(typed, expected))
            return "한글 명령이 정확히 입력되지 않아 전송하지 않았습니다. 채팅창을 닫고 다시 눌러 주세요.";
        if (!inputValid()) return "게임 화면이 바뀌어 명령을 전송하지 않았습니다.";
        if (!materialsReady())
            return "조합 재료가 바뀌어 명령을 전송하지 않았습니다. 채팅창을 닫고 보유량을 확인해 주세요.";

        ChatTextState? stable = null;
        var commandReady = false;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            if (!inputValid()) return "게임 화면이 바뀌어 명령을 전송하지 않았습니다.";
            Stage("reading_committed_command");
            ChatTextState current;
            try { current = readText(); }
            catch (IOException error) when (error.Message is "chat_buffer_unstable" or "chat_memory_read_failed")
            {
                stable = null;
                Stage("waiting_for_stable_chat_buffer");
                await pause(cancel);
                continue;
            }
            if (current.Edit != typed.Edit || !GameChatText.Matches(current, expected))
                return "입력한 명령이나 게임 화면이 바뀌어 전송하지 않았습니다.";
            if (stable is not null && GameChatText.SameBuffer(stable, current))
            {
                Stage("rechecking_materials");
                if (!materialsReady()) return "조합 재료가 바뀌어 명령을 전송하지 않았습니다.";
                ChatTextState last;
                try { last = readText(); }
                catch (IOException error) when (error.Message is "chat_buffer_unstable" or "chat_memory_read_failed")
                {
                    stable = null;
                    Stage("waiting_for_stable_chat_buffer");
                    await pause(cancel);
                    continue;
                }
                if (!inputValid() || !GameChatText.SameBuffer(current, last) || !GameChatText.Matches(last, expected))
                    return "입력한 명령이나 게임 화면이 바뀌어 전송하지 않았습니다.";
                commandReady = true;
                break;
            }
            stable = current;
            await pause(cancel);
        }
        if (!commandReady) return "채팅 입력이 안정되지 않아 명령을 전송하지 않았습니다.";
        cancel.ThrowIfCancellationRequested();
        Stage("enter_requested");
        await sendEnter(cancel);
        Stage("enter_released");
        for (var attempt = 0; attempt < 80; attempt++)
        {
            cancel.ThrowIfCancellationRequested();
            if (!postSubmitContextValid()) return "명령을 보낸 뒤 게임 화면이 바뀌었습니다. 게임에서 조합 결과를 확인해 주세요.";
            if (!chatOpen()) { Stage("chat_closed_after_submit"); return null; }
            if (!inputValid()) return "명령을 보낸 뒤 게임 화면이 바뀌었습니다. 게임에서 조합 결과를 확인해 주세요.";
            await pause(cancel);
        }
        Stage("submit_unconfirmed_no_retry");
        // 전송 결과가 불명확할 때 추가 Enter는 명령 중복이나 채팅 재열림을 일으킬 수 있다.
        return !chatOpen() && postSubmitContextValid() ? null : "채팅 명령의 전송을 확인하지 못했습니다. 게임의 채팅창을 확인해 주세요.";
    }

    // 키 누름/해제를 다른 입력 묶음으로 분리한다. 취소·포커스 변경에도 누른 키는 해제만 한다.
    internal static async Task PressOnceAsync(Action keyDown, Action keyUp, Func<bool> inputValid,
        CancellationToken cancel, Func<CancellationToken, Task>? hold = null)
    {
        cancel.ThrowIfCancellationRequested();
        if (!inputValid()) throw new IOException("Chat input changed before key down.");
        hold ??= token => Task.Delay(60, token);
        var attempted = false;
        try { attempted = true; keyDown(); await hold(cancel); }
        finally { if (attempted) keyUp(); }
    }
}
