static class GameChoiceInputTests
{
    public static void Run()
    {
        var active = new GameChoiceInput.WispSample(0x10000, new string('A', 24), "e018", 2, 0, 0);
        var hidden = active with { Flags5c = 0x100 };
        var dead = active with { Flags20 = 1 };
        (string Name, bool Accepted)[] cases = [
            ("hidden exact object confirmed twice", GameChoiceInput.VerifyConsumption(active, hidden, hidden, 2)),
            ("dead exact object confirmed twice", GameChoiceInput.VerifyConsumption(active, dead, dead, 2)),
            ("inactive before input rejected", !GameChoiceInput.VerifyConsumption(hidden, hidden, hidden, 2)),
            ("one transient inactive read rejected", !GameChoiceInput.VerifyConsumption(active, hidden, active, 2)),
            ("reused address identity rejected", !GameChoiceInput.VerifyConsumption(active, hidden, hidden with { Identity = new string('B', 24) }, 2)),
            ("different rawcode is not consumption proof", !GameChoiceInput.VerifyConsumption(active, hidden, hidden with { Rawcode = "h003" }, 2)),
            ("different owner is not consumption proof", !GameChoiceInput.VerifyConsumption(active, hidden, hidden with { Owner = 3 }, 2)),
            ("different address is not consumption proof", !GameChoiceInput.VerifyConsumption(active, hidden, hidden with { Address = 0x20000 }, 2)),
            ("player transition rejected", !GameChoiceInput.VerifyConsumption(active, hidden, hidden, 3)),
            ("another live e018 cannot prove consumption", !GameChoiceInput.VerifyConsumption(active, active with { Flags20 = 0x200 }, active, 2))
        ];
        foreach (var test in cases)
            if (!test.Accepted) throw new Exception("Choice consumption test failed: " + test.Name);
        var header = new byte[0x20];
        BitConverter.GetBytes(0x65000000u).CopyTo(header, 0);
        BitConverter.GetBytes(0x100u).CopyTo(header, 0x0C);
        BitConverter.GetBytes(0x200u).CopyTo(header, 0x10);
        var identity = GameChoiceInput.WispIdentity(header);
        foreach (var offset in new[] { 4, 8, 0x14, 0x18, 0x1C })
        {
            var changed = (byte[])header.Clone(); changed[offset]++;
            if (GameChoiceInput.WispIdentity(changed) != identity) throw new Exception("Mutable refcount/cache headers must not replace the native generation identity.");
        }
        foreach (var offset in new[] { 0, 0x0C, 0x10 })
        {
            var changed = (byte[])header.Clone(); changed[offset]++;
            if (GameChoiceInput.WispIdentity(changed) == identity) throw new Exception("Vtable, index and generation changes must invalidate a reused source address.");
        }
        // 위습 개수나 생성 유닛 수는 이 개체 증거의 입력이 아니다. 서비스가 결과 증감을 별도로 검사한다.
        Console.WriteLine($"PASS: exact choice-wisp consumption proof {cases.Length + 2}/{cases.Length + 2} pure cases; no game input or writes.");
    }
}
