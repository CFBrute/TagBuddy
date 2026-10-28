# TODO — Proxmark MIFARE Classic key recovery (real, no dictionary)

Goal: recover a sector key the attacker was **never told** — i.e. a genuine Crypto1 attack
(**nested** and/or **darkside**), not a dictionary lookup. Dictionary "recovery" only ever finds
keys you already have; a random key defeats it. This file is the handoff to build the real thing.

Status date: 2026-10-28. Hardware: Proxmark3 Easy (willok 2016 clone), OLD 544-byte protocol, no NG.
Test card available: a gen2 **CUID**, UID `C743195F`, SAK 08, ATQA 0004 (currently all-default FF keys).

---

## Already proven (foundation — don't redo)

Authenticated (keyed) read/write works over the Proxmark on the gen2 CUID. Verified on the spike
(`spike/Program.cs`, commands `mfread` / `mfdump` / `mfwrite` / `mfwritekey`). Full 64/64 dump with
`FFFFFFFFFFFF`; wrote a custom KeyA to a sector trailer and confirmed the old key then fails; restored.

**Command ids (verified against the ORIGINAL `Proxmark/proxmark3` repo = the OLD protocol; the
`RfidResearchGroup` fork is NG — do NOT use it for OLD layouts):**

| Command                         | id       |
|---------------------------------|----------|
| CMD_MIFARE_READBL               | `0x0620` |
| CMD_MIFARE_READSC               | `0x0621` |
| CMD_MIFARE_WRITEBL              | `0x0622` |
| CMD_MIFARE_CHKKEYS              | `0x0623` |
| CMD_MIFARE_NESTED               | `0x0612` |
| CMD_READER_MIFARE (darkside)    | `0x0611` |
| CMD_MIFARE_ACQUIRE_ENCRYPTED_NONCES | `0x0613` |
| CMD_ACK                         | `0x00ff` |

**READBL / WRITEBL layout (verified, arm + client):**
- `arg0` = blockNo, `arg1` = keyType (**0 = A, 1 = B**).
- key = `data[0..5]`; WRITEBL's 16 data bytes = `data[10..25]` (there is a 4-byte gap at 6..9).
- Reply uses the proven **symmetric 544-byte OLD layout**: `arg0 @ offset 8` = isOK (1 = ok),
  `data @ offset 32` = 16 block bytes.

**GOTCHA — debug string before the ACK.** The firmware emits a `DEBUG_PRINT_STRING` (cmd `0x0100`)
*before* the real `CMD_ACK`: "READ BLOCK FINISHED" / "WRITE BLOCK FINISHED", or on a wrong key
"Authentication failed. Card timeout." + "Auth error". Reading the first 544-byte frame grabs the
debug string (its `arg0` = string length, data = ASCII) and looks like a false read. Scan the reply
window for the frame whose `cmd == 0x00ff`. Spike `ReadAck()` does this and prints the `[fw]` lines.

---

## The real work: nested attack

Nested needs **one known key** on the card (e.g. still-default sector 0) and recovers the other
sectors' keys from the card's own encrypted nonces. Two parts:

1. **Device side — collect nonces (NESTED `0x0612`). ARM arg layout verified:**
   - `arg0` = blockNo | keyType<<8   (the block/keytype we CAN auth)
   - `arg1` = targetBlock | targetKeyType<<8   (the block/keytype we want the key for)
   - `arg2` = calibrate (1 on the first call to measure the card's timing, then 0)
   - key = `data[0..5]` (the known key)
   - Reply `buf` (28 bytes): `cuid[0..3]`, `target_nt[0][4..7]`, `target_ks[0][8..11]`,
     `target_nt[1][12..15]`, `target_ks[1][16..19]`, `auth_timeout[20..23]`, `fixed_nt[24..27]`.

2. **Client side — the Crypto1 solver (THIS is the hard part, not yet built).** Port the nested
   solver to C#: given `(nt, ks)` pairs recover the key. This is the `crapto1` / `nonce2key` /
   `lfsr_recovery` math from the Proxmark/Crapto1 sources. It's a self-contained, well-known
   algorithm but a meaty port (LFSR rollback, parity filtering, candidate pruning). Prototype it on
   the spike first (`spike/Program.cs` is the right place — it already talks to the card), verify it
   recovers a random key, THEN port to the app.

**Still to fetch/verify (a subagent got the ids + READBL/WRITEBL/NESTED-arm but 404'd on the client):**
- The **nested client builder** (`client/mifarehost.c`, `mfnested`) — confirms how arg0/arg1 and the
  key buffer are packed, and how many nonce pairs per call / how many calls per sector.
- The **darkside** path: ARM `ReaderMifare` handler (wasn't in `mifarecmd.c`) + client `mfDarkside`
  builder. Darkside needs NO known key, so it's the better demo on a card with all keys changed, but
  it's a separate solver.

---

## Clean test protocol (proves it's real)

1. On the spike, write a **random** key to one sector's KeyA (keep access bits `FF 07 80 69`), using
   `mfwritekey`. Do NOT record it anywhere the solver can read.
2. Leave sector 0 at default `FFFFFFFFFFFF` (the known key nested needs).
3. Run nested: known = sector 0 key A, target = the changed sector's key A.
4. Success = the solver prints the key we wrote, having only ever seen nonces. If it matches, it's a
   genuine recovery. (A dictionary would return "not found" here — that's the whole point.)
5. Restore the card to default afterward.

---

## Then port to the app

Order: (a) port the proven **auth read/write** into `TagBuddy/Services/ProxmarkUsb.cs` first
(`MifareReadBlockAsync` / `MifareWriteBlockAsync` with the READBL/WRITEBL ids above + the
read-to-ACK fix) so the app can read/write real and gen2 cards — this is the foundation nested
builds on, and gives gen2 block-0 write for free (WRITEBL to block 0 with the sector-0 key; add a
BCC check + confirm, since gen2 has no backdoor to undo a bad BCC). (b) Then the nested solver and a
"recover keys" action on the RF page.
