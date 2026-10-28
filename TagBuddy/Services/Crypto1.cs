namespace TagBuddy.Services;

/// <summary>The MIFARE Classic Crypto1 cipher and the "nested" key-recovery (crapto1) — a faithful port of the
/// public-domain crapto1 by bla / Proxmark contributors (common/crapto1/crapto1.c, crypto1.c, bucketsort.c). It
/// recovers a sector key from the card's OWN encrypted nonces, which a dictionary cannot do, for the owner's own
/// cards and authorised research (what the Proxmark here is for).
///
/// <para>The cipher, rollback, PRNG and the <see cref="LfsrRecovery32"/> state recovery are exercised by
/// <c>Crypto1Tests</c> (run <c>dotnet test</c>); the recovery test is the gate — it recovers a random key from only
/// its keystream, the proof this is real and not a dictionary.</para></summary>
public sealed class Crypto1State(uint odd = 0, uint even = 0)
{
    /// <summary>The 24 odd-indexed LFSR bits (the filter reads these).</summary>
    public uint Odd = odd;

    /// <summary>The 24 even-indexed LFSR bits.</summary>
    public uint Even = even;

    public Crypto1State Copy() => new(Odd, Even);

    /// <summary>Loads a 48-bit key into a fresh split state (crypto1_init).</summary>
    public static Crypto1State FromKey(ulong key)
    {
        var s = new Crypto1State();
        for (int i = 47; i > 0; i -= 2)
        {
            s.Odd = (s.Odd << 1) | (uint)((key >> ((i - 1) ^ 7)) & 1);
            s.Even = (s.Even << 1) | (uint)((key >> (i ^ 7)) & 1);
        }
        return s;
    }

    /// <summary>The 48-bit key this state holds (crypto1_get_lfsr), the inverse of <see cref="FromKey"/>.</summary>
    public ulong ToKey()
    {
        ulong lfsr = 0;
        for (int i = 23; i >= 0; --i)
        {
            lfsr = (lfsr << 1) | ((Odd >> (i ^ 3)) & 1);
            lfsr = (lfsr << 1) | ((Even >> (i ^ 3)) & 1);
        }
        return lfsr;
    }
}

public static class Crypto1
{
    private const uint LfPolyOdd = 0x29CE5C;
    private const uint LfPolyEven = 0x870804;

    /// <summary>Even parity (XOR of all bits) of a 32-bit word (evenparity32).</summary>
    public static uint Parity(uint x)
    {
        x ^= x >> 16;
        x ^= x >> 8;
        x ^= x >> 4;
        x ^= x >> 2;
        x ^= x >> 1;
        return x & 1;
    }

    /// <summary>The Crypto1 non-linear filter: one keystream bit from the odd-state bits.</summary>
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

    /// <summary>Clocks the cipher one bit, returning the keystream bit (crypto1_bit).</summary>
    public static uint Bit(Crypto1State s, uint input, bool isEncrypted)
    {
        uint ret = Filter(s.Odd);
        uint feedin = ret & (isEncrypted ? 1u : 0u);
        feedin ^= input & 1;
        feedin ^= LfPolyOdd & s.Odd;
        feedin ^= LfPolyEven & s.Even;
        s.Even = (s.Even << 1) | Parity(feedin);
        (s.Odd, s.Even) = (s.Even, s.Odd);
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

    /// <summary>The MIFARE nonce PRNG successor, <paramref name="n"/> steps on from <paramref name="x"/>
    /// (prng_successor), used to predict the plaintext tag nonce in the nested attack.</summary>
    public static uint PrngSuccessor(uint x, int n)
    {
        x = SwapEndian(x);
        while (n-- > 0)
            x = (x >> 1) | (((x >> 16) ^ (x >> 18) ^ (x >> 19) ^ (x >> 21)) << 31);
        return SwapEndian(x);
    }

    private static uint SwapEndian(uint x)
    {
        x = ((x >> 8) & 0xff00ff) | ((x & 0xff00ff) << 8);
        return (x >> 16) | (x << 16);
    }

    /// <summary>Rolls the cipher back one bit (lfsr_rollback_bit), returning the keystream bit from that step.</summary>
    public static uint RollbackBit(Crypto1State s, uint input, bool fb)
    {
        s.Odd &= 0xffffff;
        (s.Odd, s.Even) = (s.Even, s.Odd);
        uint outv = s.Even & 1;
        s.Even >>= 1;
        outv ^= LfPolyEven & s.Even;
        outv ^= LfPolyOdd & s.Odd;
        outv ^= input & 1;
        uint ret = Filter(s.Odd);
        outv ^= fb ? ret : 0u;
        s.Even |= Parity(outv) << 23;
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

    /// <summary>Packs two linear-feedback parity contributions into the high bits of a table entry (update_contribution),
    /// so the bucket sort can intersect the odd and even tables.</summary>
    private static uint UpdateContribution(uint item, uint mask1, uint mask2)
    {
        uint p = item >> 25;
        p = (p << 1) | Parity(item & mask1);
        p = (p << 1) | Parity(item & mask2);
        return (p << 24) | (item & 0xffffff);
    }

    /// <summary>Extends a half-state table by one keystream bit, carrying the feed word and contribution (extend_table).</summary>
    private static List<uint> ExtendTable(List<uint> tbl, uint bit, uint m1, uint m2, uint input)
    {
        uint inHi = input << 24;
        var next = new List<uint>(tbl.Count);
        foreach (var v in tbl)
        {
            uint cur = v << 1;
            uint f0 = Filter(cur);
            if ((f0 ^ Filter(cur | 1)) != 0)
            {
                uint r = cur | (f0 ^ bit);
                next.Add(UpdateContribution(r, m1, m2) ^ inHi);
            }
            else if (f0 == bit)
            {
                next.Add(UpdateContribution(cur, m1, m2) ^ inHi);
                next.Add(UpdateContribution(cur | 1, m1, m2) ^ inHi);
            }
            // else: neither child matches the keystream bit — drop
        }
        return next;
    }

    /// <summary>Extends a half-state table by one keystream bit, no feed/contribution (extend_table_simple).</summary>
    private static List<uint> ExtendTableSimple(List<uint> tbl, uint bit)
    {
        var next = new List<uint>(tbl.Count);
        foreach (var v in tbl)
        {
            uint cur = v << 1;
            uint f0 = Filter(cur);
            if ((f0 ^ Filter(cur | 1)) != 0)
                next.Add(cur | (f0 ^ bit));
            else if (f0 == bit)
            {
                next.Add(cur);
                next.Add(cur | 1);
            }
        }
        return next;
    }

    /// <summary>Groups the even and odd tables into the 256 contribution buckets and returns the buckets where both
    /// are non-empty — the only odd/even pairs that can combine (bucket_sort_intersect).</summary>
    private static List<(List<uint> Even, List<uint> Odd)> BucketIntersect(List<uint> even, List<uint> odd)
    {
        var eb = new List<uint>?[256];
        var ob = new List<uint>?[256];
        foreach (var v in even) (eb[(v >> 24) & 0xff] ??= []).Add(v);
        foreach (var v in odd) (ob[(v >> 24) & 0xff] ??= []).Add(v);
        var res = new List<(List<uint>, List<uint>)>();
        for (int b = 0; b < 256; b++)
            if (eb[b] is { Count: > 0 } e && ob[b] is { Count: > 0 } o) res.Add((e, o));
        return res;
    }

    /// <summary>Narrows the odd/even tables four keystream bits at a time, then emits the recovered states
    /// (recover). <paramref name="rem"/> starts at 11 and ends the recursion at -1.</summary>
    private static void Recover(List<uint> odd, uint oks, List<uint> even, uint eks, int rem, uint input, List<Crypto1State> outp)
    {
        if (rem == -1)
        {
            foreach (var e0 in even)
            {
                uint e = (e0 << 1) ^ Parity(e0 & LfPolyEven) ^ ((input & 4) != 0 ? 1u : 0u);
                foreach (var o in odd)
                    outp.Add(new Crypto1State(odd: e ^ Parity(o & LfPolyOdd), even: o));
            }
            return;
        }

        for (int i = 0; i < 4 && rem-- != 0; i++)
        {
            oks >>= 1;
            eks >>= 1;
            input >>= 2;
            odd = ExtendTable(odd, oks & 1, (LfPolyEven << 1) | 1, LfPolyOdd << 1, 0);
            if (odd.Count == 0) return;
            even = ExtendTable(even, eks & 1, LfPolyOdd, (LfPolyEven << 1) | 1, input & 3);
            if (even.Count == 0) return;
        }

        foreach (var (eb, ob) in BucketIntersect(even, odd))
            Recover(ob, oks, eb, eks, rem, input, outp);
    }

    /// <summary>Every 48-bit cipher state that could have produced keystream word <paramref name="ks2"/> while the
    /// feed word <paramref name="input"/> was clocked in (lfsr_recovery32). The caller checks each against a second
    /// nonce (or re-encrypts) to keep the real one.</summary>
    public static List<Crypto1State> LfsrRecovery32(uint ks2, uint input)
    {
        uint oks = 0, eks = 0;
        for (int i = 31; i >= 0; i -= 2) oks = (oks << 1) | ((ks2 >> ((i ^ 24) & 31)) & 1);
        for (int i = 30; i >= 0; i -= 2) eks = (eks << 1) | ((ks2 >> ((i ^ 24) & 31)) & 1);

        var odd = new List<uint>();
        var even = new List<uint>();
        for (uint i = 0; i <= (1u << 20); i++)
        {
            uint f = Filter(i);
            if (f == (oks & 1)) odd.Add(i);
            if (f == (eks & 1)) even.Add(i);
        }

        for (int i = 0; i < 4; i++)
        {
            oks >>= 1; odd = ExtendTableSimple(odd, oks & 1);
            eks >>= 1; even = ExtendTableSimple(even, eks & 1);
        }

        input = ((input >> 16) & 0xff) | (input << 16) | (input & 0xff00);   // byte swap
        var outp = new List<Crypto1State>();
        Recover(odd, oks, even, eks, 11, input << 1, outp);
        return outp;
    }

    /// <summary>Candidate keys from one nested nonce: <paramref name="uid"/>, the plaintext target nonce
    /// <paramref name="nt"/> (predict with <see cref="PrngSuccessor"/>), and the keystream <paramref name="ks1"/>
    /// the card used on it. <see cref="LfsrRecovery32"/> returns the state after the nonce was fed in, so each is
    /// rolled back one word to the key. One nonce yields tens of thousands of candidates; intersect the sets from
    /// two or more nonces (<see cref="Intersect"/>) to pin down the real key.</summary>
    public static List<ulong> Nonce2Key(uint uid, uint nt, uint ks1)
    {
        var keys = new HashSet<ulong>();
        foreach (var s in LfsrRecovery32(ks1, uid ^ nt))
        {
            var r = s.Copy();
            RollbackWord(r, uid ^ nt, false);
            keys.Add(r.ToKey());
        }
        return [.. keys];
    }

    /// <summary>The keys common to two candidate sets — the real key survives across nonces while the false
    /// candidates do not.</summary>
    public static List<ulong> Intersect(IEnumerable<ulong> a, IEnumerable<ulong> b)
    {
        var set = new HashSet<ulong>(a);
        set.IntersectWith(b);
        return [.. set];
    }
}
