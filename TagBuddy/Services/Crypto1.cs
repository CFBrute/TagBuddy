namespace TagBuddy.Services;

/// <summary>The MIFARE Classic Crypto1 cipher and the "nested" key-recovery math — a port of the public-domain
/// crapto1 (Karsten Nohl / bla et al.). It recovers a sector key from the card's OWN encrypted nonces, which a
/// dictionary cannot do, for the owner's own cards and authorised research (the use the Proxmark here is for).
///
/// <para><b>What is verified and what is not.</b> The cipher, rollback and PRNG below are exercised by a unit test
/// (<c>Crypto1Tests</c>) that round-trips them and checks a known filter value — run <c>dotnet test</c>. The
/// <see cref="LfsrRecovery32"/> state-intersection is the intricate part of crapto1; it is ported here but NOT yet
/// confirmed to recover a key, and it is guarded so it can never run away. The recovery test is the gate: once it
/// passes, the "recover keys" path can be trusted. Until then this is a foundation to iterate on, not a finished
/// attack.</para></summary>
public sealed class Crypto1State(uint odd = 0, uint even = 0)
{
    /// <summary>The 24 odd-indexed LFSR bits (the filter reads these).</summary>
    public uint Odd = odd;

    /// <summary>The 24 even-indexed LFSR bits.</summary>
    public uint Even = even;

    public Crypto1State Copy() => new(Odd, Even);

    /// <summary>The 48-bit key this state represents, in the 6-byte order a reader uses (inverse of <see cref="FromKey"/>).</summary>
    public ulong ToKey()
    {
        ulong key = 0;
        for (int i = 0; i < 24; i++)
        {
            key |= (ulong)((Even >> i) & 1) << (2 * i);
            key |= (ulong)((Odd >> i) & 1) << (2 * i + 1);
        }
        return SwapKeyBytes(key);
    }

    /// <summary>Loads a 48-bit key into a fresh split state (crypto1_init).</summary>
    public static Crypto1State FromKey(ulong key48)
    {
        key48 = SwapKeyBytes(key48);
        var s = new Crypto1State();
        for (int i = 47; i > 0; i -= 2)
        {
            s.Odd = (s.Odd << 1) | ((uint)(key48 >> ((i - 1) ^ 7)) & 1);
            s.Even = (s.Even << 1) | ((uint)(key48 >> (i ^ 7)) & 1);
        }
        return s;
    }

    private static ulong SwapKeyBytes(ulong k)
    {
        ulong r = 0;
        for (int i = 0; i < 6; i++) r |= ((k >> (8 * i)) & 0xFF) << (8 * (5 - i));
        return r;
    }
}

public static class Crypto1
{
    private const uint LfPolyOdd = 0x29CE5C;
    private const uint LfPolyEven = 0x870804;

    /// <summary>Even parity (XOR of all bits) of a 32-bit word.</summary>
    public static uint Parity(uint x)
    {
        x ^= x >> 16;
        x ^= x >> 8;
        x ^= x >> 4;
        x ^= x >> 2;
        x ^= x >> 1;
        return x & 1;
    }

    /// <summary>The Crypto1 non-linear filter: one keystream bit from the 20 odd-state bits.</summary>
    public static uint Filter(uint x)
    {
        uint f;
        f  = (0xf22c0u >> (int)(x & 0xf)) & 16;
        f |= (0x6c9c0u >> (int)((x >> 4) & 0xf)) & 8;
        f |= (0x3c8b0u >> (int)((x >> 8) & 0xf)) & 4;
        f |= (0x1e458u >> (int)((x >> 12) & 0xf)) & 2;
        f |= (0x0d938u >> (int)((x >> 16) & 0xf)) & 1;
        return (0xEC57E80Au >> (int)f) & 1;
    }

    /// <summary>Clocks the cipher one bit, mixing in one feed bit, and returns the keystream bit (crypto1_bit).</summary>
    public static uint Bit(Crypto1State s, uint input, bool isEncrypted)
    {
        uint ret = Filter(s.Odd);
        uint feedin = ret & (isEncrypted ? 1u : 0u);
        feedin ^= input & 1;
        feedin ^= LfPolyOdd & s.Odd;
        feedin ^= LfPolyEven & s.Even;
        s.Even = (s.Even << 1) | Parity(feedin);
        (s.Odd, s.Even) = (s.Even, s.Odd);   // the halves clock alternately
        return ret;
    }

    /// <summary>Clocks the cipher 32 times, returning the keystream word (crypto1_word).</summary>
    public static uint Word(Crypto1State s, uint input, bool isEncrypted)
    {
        uint ret = 0;
        for (int i = 0; i < 32; i++)
            ret |= Bit(s, (input >> (24 ^ i)) & 1, isEncrypted) << (24 ^ i);
        return ret;
    }

    /// <summary>The MIFARE nonce PRNG successor: <paramref name="n"/> steps of the 16-bit LFSR on from
    /// <paramref name="x"/> (prng_successor), used to predict the plaintext tag nonce.</summary>
    public static uint PrngSuccessor(uint x, int n)
    {
        x = SwapEndian(x);
        while (n-- > 0)
            x = (x >> 1) | (((x >> 16) ^ (x >> 18) ^ (x >> 19) ^ (x >> 21)) << 31);
        return SwapEndian(x);
    }

    private static uint SwapEndian(uint x) =>
        ((x & 0x000000FF) << 24) | ((x & 0x0000FF00) << 8) | ((x & 0x00FF0000) >> 8) | ((x & 0xFF000000) >> 24);

    /// <summary>Rolls the cipher back one bit, returning the keystream bit produced at that step (lfsr_rollback_bit).</summary>
    public static uint RollbackBit(Crypto1State s, uint input, bool fb)
    {
        (s.Odd, s.Even) = (s.Even, s.Odd);
        uint outBit = s.Even & 1;
        s.Even >>= 1;
        uint ret = Filter(s.Odd);
        uint feedin = outBit & 1;
        feedin ^= LfPolyEven & s.Even;
        feedin ^= LfPolyOdd & s.Odd;
        feedin ^= input & 1;
        feedin ^= fb ? ret : 0u;
        s.Even |= Parity(feedin) << 23;
        return ret;
    }

    /// <summary>Rolls the cipher back one word (lfsr_rollback_word).</summary>
    public static uint RollbackWord(Crypto1State s, uint input, bool fb)
    {
        uint ret = 0;
        for (int i = 31; i >= 0; i--)
            ret |= RollbackBit(s, (input >> (24 ^ i)) & 1, fb) << (24 ^ i);
        return ret;
    }

    // ------------------------------------------------------------------ recovery (crapto1 lfsr_recovery32)
    //
    // UNVERIFIED — the crapto1 odd/even table intersection, ported but not yet confirmed to recover a key.
    // It extends an odd and an even half-state table to match the split keystream, then pairs them and keeps
    // the pairs that regenerate the keystream. A hard cap stops it ever running away. The recovery unit test
    // is the gate; if it fails, the fix is here (likely the table extension or the pairing).

    /// <summary>Extends a half-state table by one keystream bit (extend_table_simple).</summary>
    private static List<uint> ExtendTableSimple(List<uint> tbl, uint bit)
    {
        var next = new List<uint>(tbl.Count);
        foreach (var s0 in tbl)
        {
            uint s = s0 << 1;
            uint f0 = Filter(s);
            if (f0 != Filter(s | 1))
                next.Add(s | (f0 ^ bit));
            else if (f0 == bit)
            {
                next.Add(s);
                next.Add(s | 1);
            }
        }
        return next;
    }

    /// <summary>The maximum half-table size before recovery gives up (keeps it bounded on a phone).</summary>
    private const int MaxTable = 1 << 21;

    /// <summary>Every 48-bit state consistent with keystream word <paramref name="ks2"/> and feed word
    /// <paramref name="input"/> (lfsr_recovery32). Empty if the tables exceed <see cref="MaxTable"/>.</summary>
    public static List<Crypto1State> LfsrRecovery32(uint ks2, uint input)
    {
        uint oks = 0, eks = 0;
        for (int i = 31; i >= 0; i -= 2) oks = (oks << 1) | ((ks2 >> (24 ^ i)) & 1);
        for (int i = 30; i >= 0; i -= 2) eks = (eks << 1) | ((ks2 >> (24 ^ i)) & 1);

        var odd = new List<uint>();
        var even = new List<uint>();
        for (uint i = 0; i < (1u << 20); i++)
        {
            if (Filter(i) == (oks & 1)) odd.Add(i);
            if (Filter(i) == (eks & 1)) even.Add(i);
        }

        for (int b = 1; b < 16; b++)
        {
            odd = ExtendTableSimple(odd, (oks >> b) & 1);
            even = ExtendTableSimple(even, (eks >> b) & 1);
            if (odd.Count > MaxTable || even.Count > MaxTable) return [];
        }

        // Pair the halves and keep those that regenerate ks2. Correct by construction; its cost is the product
        // of the two table sizes, which is why the cap above matters.
        var states = new List<Crypto1State>();
        input = ((input >> 16) & 0xFF) | (input & 0xFF00) | ((input & 0xFF) << 16);
        if ((long)odd.Count * even.Count > (1L << 40)) return [];
        foreach (var o in odd)
            foreach (var e in even)
            {
                var s = new Crypto1State(o & 0xFFFFFF, e & 0xFFFFFF);
                if (Word(s.Copy(), input, false) == ks2) states.Add(s);
            }
        return states;
    }

    /// <summary>Recovers candidate keys from one nested nonce: <paramref name="uid"/>, the plaintext target nonce
    /// <paramref name="nt"/> (predicted with <see cref="PrngSuccessor"/>), and the keystream <paramref name="ks1"/>
    /// the card used on it.</summary>
    public static List<ulong> Nonce2Key(uint uid, uint nt, uint ks1)
    {
        var keys = new List<ulong>();
        foreach (var s in LfsrRecovery32(ks1, uid ^ nt))
        {
            var roll = s.Copy();
            RollbackWord(roll, uid ^ nt, false);
            keys.Add(roll.ToKey());
        }
        return keys.Distinct().ToList();
    }
}
