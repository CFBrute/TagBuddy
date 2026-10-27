using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>The store of known MIFARE Classic keys: parsing an imported dictionary, and remembering per card the
/// keys that opened its sectors so the same card opens quickly next time.</summary>
public class MifareKeyStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly MifareKeyStore _store;

    public MifareKeyStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tagbuddy-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new MifareKeyStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Used_keys_are_listed_newest_first_and_a_reuse_moves_it_up()
    {
        _store.MarkUsed(["FFFFFFFFFFFF", "a0a1a2a3a4a5"]);   // mixed case: stored upper
        System.Threading.Thread.Sleep(1100);                 // the store keeps whole-second times
        _store.MarkUsed(["112233445566"]);

        var used = _store.UsedKeys();
        Assert.Equal("112233445566", used[0].Hex);           // the most recent is first
        Assert.Equal(["112233445566", "A0A1A2A3A4A5", "FFFFFFFFFFFF"], used.Select(k => k.Hex).Order());  // all three, upper-cased

        System.Threading.Thread.Sleep(1100);
        _store.MarkUsed(["ffffffffffff"]);                   // reusing an old key brings it to the front, no duplicate
        var after = _store.UsedKeys();
        Assert.Equal("FFFFFFFFFFFF", after[0].Hex);
        Assert.Equal(3, after.Count);
    }

    [Fact]
    public void ParseKeys_reads_keys_and_ignores_comments_separators_and_junk()
    {
        var text = """
            # a dictionary
            FFFFFFFFFFFF
            a0a1a2a3a4a5   # the MAD key, lower case
            A0:A1:A2:A3:A4:A5
            00-00-00-00-00-00
            not a key
            DEADBEEF
            112233445566778899
            """;
        Assert.Equal(["FFFFFFFFFFFF", "A0A1A2A3A4A5", "000000000000"], MifareKeyStore.ParseKeys(text));
    }

    [Fact]
    public void Import_adds_new_keys_and_reports_the_count()
    {
        Assert.Equal(2, _store.Import("FFFFFFFFFFFF\nA0A1A2A3A4A5"));
        Assert.Equal(1, _store.Import("A0A1A2A3A4A5\nB0B1B2B3B4B5"));   // one already there, one new
        Assert.Equal(3, _store.ImportedCount());
    }

    [Fact]
    public void Imported_keys_come_back_as_six_bytes()
    {
        _store.Import("A0A1A2A3A4A5");
        var keys = _store.ImportedKeys();
        Assert.Single(keys);
        Assert.Equal(new byte[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 }, keys[0]);
    }

    [Fact]
    public void Imported_keys_survive_a_new_store_on_the_same_folder()
    {
        _store.Import("D3F7D3F7D3F7");
        Assert.Equal(1, new MifareKeyStore(_dir).ImportedCount());
    }

    [Fact]
    public void A_card_remembers_the_keys_that_opened_it()
    {
        _store.Remember("3F:24:5C:6B", ["A0A1A2A3A4A5", "FFFFFFFFFFFF"]);
        var keys = _store.RememberedFor("3f245c6b");   // serial matched without separators, any case
        Assert.Equal(2, keys.Count);
        Assert.Contains(keys, k => Convert.ToHexString(k) == "A0A1A2A3A4A5");
    }

    [Fact]
    public void Remembering_the_same_key_again_does_not_duplicate_it()
    {
        _store.Remember("04AABB", ["FFFFFFFFFFFF"]);
        _store.Remember("04AABB", ["FFFFFFFFFFFF", "A0A1A2A3A4A5"]);
        Assert.Equal(2, _store.RememberedFor("04AABB").Count);
    }

    [Fact]
    public void All_remembered_keys_span_every_card_without_repeats()
    {
        _store.Remember("CARD01", ["FFFFFFFFFFFF", "A0A1A2A3A4A5"]);
        _store.Remember("CARD02", ["A0A1A2A3A4A5", "B0B1B2B3B4B5"]);
        Assert.Equal(3, _store.AllRemembered().Count);
        Assert.Equal(2, _store.RememberedCardCount());
    }

    [Theory]
    [InlineData(0b000, 0b001, false)]   // factory: A reads and writes, B is plain data
    [InlineData(0b100, 0b011, true)]    // A reads, B reads and writes: B
    [InlineData(0b011, 0b011, true)]    // only B reads and writes
    [InlineData(0b010, 0b011, true)]    // both only read: B, which also changes the keys
    [InlineData(0b000, 0b011, true)]    // both read and write: B, which also changes the keys
    public void The_key_remembered_is_the_strongest(int data, int trailer, bool keyB)
    {
        var rules = new SectorRules(data, data, data, trailer);
        bool bStronger = MifareClassicKeys.KeyStrength(rules, keyB: true) > MifareClassicKeys.KeyStrength(rules, keyB: false);
        Assert.Equal(keyB, bStronger);
    }

    [Fact]
    public void A_card_given_a_new_serial_keeps_its_keys_under_both()
    {
        _store.Remember("04:AA:BB:CC", ["A0A1A2A3A4A5", "B0B1B2B3B4B5"]);
        _store.CarryOver("04:AA:BB:CC", "DE:AD:BE:EF");
        Assert.Equal(2, _store.RememberedFor("DEADBEEF").Count);
        Assert.Equal(2, _store.RememberedFor("04AABBCC").Count);   // kept, in case the serial is put back
        _store.CarryOver("11:22:33:44", "55:66:77:88");             // nothing known for it: nothing made up
        Assert.Empty(_store.RememberedFor("55667788"));
    }

    [Fact]
    public void Remembered_keys_survive_a_new_store_on_the_same_folder()
    {
        _store.Remember("04AABB", ["FFFFFFFFFFFF"]);
        Assert.Single(new MifareKeyStore(_dir).RememberedFor("04:AA:BB"));
    }

    [Fact]
    public void An_unknown_card_has_no_remembered_keys() =>
        Assert.Empty(_store.RememberedFor("DEADBEEF"));
}
