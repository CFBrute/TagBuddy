using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>The store of Type 2 tag passwords: remembering by serial a password seen to open a protected
/// NTAG/Ultralight, so the same tag opens on its own next time.</summary>
public class Type2PasswordStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly Type2PasswordStore _store;

    public Type2PasswordStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tagbuddy-pwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new Type2PasswordStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void A_tag_remembers_the_password_that_opened_it()
    {
        _store.Remember("04:11:22:33:44:55:66", [0xDE, 0xAD, 0xBE, 0xEF]);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, _store.For("04112233445566"));   // serial matched without separators
        Assert.Equal(1, _store.CardCount());
    }

    [Fact]
    public void A_new_password_for_the_same_tag_replaces_the_old_one()
    {
        _store.Remember("0411", [0xFF, 0xFF, 0xFF, 0xFF]);
        _store.Remember("04:11", [0x01, 0x02, 0x03, 0x04]);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, _store.For("0411"));
        Assert.Equal(1, _store.CardCount());
    }

    [Fact]
    public void A_password_that_is_not_four_bytes_or_an_empty_serial_is_ignored()
    {
        _store.Remember("0411", [0x01, 0x02, 0x03]);   // too short
        _store.Remember("", [0x01, 0x02, 0x03, 0x04]);  // no serial
        Assert.Equal(0, _store.CardCount());
        Assert.Null(_store.For("0411"));
    }

    [Fact]
    public void Every_remembered_password_is_handed_back_for_the_search_keyed_by_serial()
    {
        _store.Remember("AA", [1, 2, 3, 4]);
        _store.Remember("BB", [5, 6, 7, 8]);
        var all = _store.All();
        Assert.Equal(2, all.Count);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, all["AA"]);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, all["bb"]);   // keyed case-insensitively, as serials are matched
    }

    [Fact]
    public void A_tag_can_be_forgotten_on_its_own_or_all_at_once()
    {
        _store.Remember("AA", [1, 2, 3, 4]);
        _store.Remember("BB", [5, 6, 7, 8]);
        _store.Forget("aa");
        Assert.Null(_store.For("AA"));
        Assert.NotNull(_store.For("BB"));
        _store.Clear();
        Assert.Equal(0, _store.CardCount());
    }

    [Fact]
    public void Remembered_passwords_survive_a_new_store_on_the_same_folder()
    {
        _store.Remember("04AABB", [0x10, 0x20, 0x30, 0x40]);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40 }, new Type2PasswordStore(_dir).For("04:AA:BB"));
    }
}
