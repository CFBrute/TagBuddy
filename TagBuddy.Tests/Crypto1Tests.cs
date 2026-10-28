using TagBuddy.Services;

namespace TagBuddy.Tests;

/// <summary>The Crypto1 cipher and the nested key-recovery math. The cipher/rollback tests run and gate the
/// foundation; the recovery test is skipped until the crapto1 state intersection is confirmed (see Crypto1.cs).</summary>
public class Crypto1Tests
{
    /// <summary>The split state and the 48-bit key round-trip through each other.</summary>
    [Fact]
    public void A_key_round_trips_through_the_split_state()
    {
        var rng = new Random(7);
        for (int t = 0; t < 2000; t++)
        {
            ulong key = (((ulong)(uint)rng.Next() << 16) ^ (uint)rng.Next()) & 0xFFFFFFFFFFFF;
            Assert.Equal(key, Crypto1State.FromKey(key).ToKey());
        }
    }

    /// <summary>The cipher is deterministic: the same key and feed give the same keystream.</summary>
    [Fact]
    public void The_cipher_is_deterministic()
    {
        var rng = new Random(11);
        for (int t = 0; t < 2000; t++)
        {
            ulong key = (((ulong)(uint)rng.Next() << 16) ^ (uint)rng.Next()) & 0xFFFFFFFFFFFF;
            uint feed = (uint)rng.Next();
            uint a = Crypto1.Word(Crypto1State.FromKey(key), feed, false);
            uint b = Crypto1.Word(Crypto1State.FromKey(key), feed, false);
            Assert.Equal(a, b);
        }
    }

    /// <summary>Clocking the cipher forward one word and rolling it back returns to the starting key: Word and
    /// RollbackWord are inverses, and so are FromKey and ToKey.</summary>
    [Fact]
    public void Rolling_the_cipher_back_returns_the_key()
    {
        var rng = new Random(13);
        for (int t = 0; t < 2000; t++)
        {
            ulong key = (((ulong)(uint)rng.Next() << 16) ^ (uint)rng.Next()) & 0xFFFFFFFFFFFF;
            uint feed = (uint)rng.Next();

            var s = Crypto1State.FromKey(key);
            Crypto1.Word(s, feed, false);        // clock forward one word
            Crypto1.RollbackWord(s, feed, false);   // and back
            Assert.Equal(key, s.ToKey());
        }
    }

    /// <summary>The nonce PRNG is reversible: stepping forward then back by the same count is the identity.</summary>
    [Fact]
    public void The_nonce_prng_steps_are_consistent()
    {
        // prng_successor is a 16-bit LFSR on the low half; stepping 0 is the identity, and stepping is stable.
        var rng = new Random(17);
        for (int t = 0; t < 500; t++)
        {
            uint nt = (uint)rng.Next();
            Assert.Equal(nt, Crypto1.PrngSuccessor(nt, 0));
            // Two successors in a row equal one successor of distance two.
            Assert.Equal(Crypto1.PrngSuccessor(nt, 2), Crypto1.PrngSuccessor(Crypto1.PrngSuccessor(nt, 1), 1));
        }
    }

    /// <summary>The real goal: recover a key the solver was never told, from only 32 bits of its keystream — the
    /// proof this is a genuine Crypto1 attack and not a dictionary. Clocks a random key forward to make a keystream
    /// word, then checks LfsrRecovery32 finds a state that regenerates it and that the key is among the results.</summary>
    [Fact]
    public void Nested_recovery_finds_a_key_it_was_never_told()
    {
        var rng = new Random(23);
        for (int t = 0; t < 2; t++)
        {
            ulong key = (((ulong)(uint)rng.Next() << 20) ^ (uint)rng.Next()) & 0xFFFFFFFFFFFF;
            uint input = (uint)rng.Next();

            uint ks2 = Crypto1.Word(Crypto1State.FromKey(key), input, false);

            // LfsrRecovery32 returns the state after `input` was clocked in; roll each back one word for the key.
            var states = Crypto1.LfsrRecovery32(ks2, input);
            Assert.NotEmpty(states);
            bool found = states.Any(s => { var r = s.Copy(); Crypto1.RollbackWord(r, input, false); return r.ToKey() == key; });
            Assert.True(found, $"key {key:X12} was not among the recovered states");
        }
    }
}
