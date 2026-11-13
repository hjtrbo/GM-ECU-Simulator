# SecurityAccess ($27) - algorithm catalogue and exchange flow

What this folder contains:

| File | Role |
|---|---|
| `ISeedKeyAlgorithm.cs`     | Strategy interface every cipher implements. |
| `ISecurityAccessModule.cs` | Module-level interface (one wraps each cipher with the full GMW3110 protocol envelope). |
| `Modules/Gmw3110_2010_Generic.cs` | Generic module that handles all the protocol bookkeeping; takes an `ISeedKeyAlgorithm` strategy. |
| `Modules/Gmw3110Permissive5Byte.cs` | Standalone "accept any key" module for 5-byte families we haven't pinned down. |
| `SecurityModuleRegistry.cs` | String-id -> factory map. Each ECU picks one module by id. |
| `Algorithms/*Algorithm.cs` | The seed-to-key math for each pinned cipher. |
| `Algorithms/Gm5BytePasswords.cs` | 256-entry per-algoId payload table for the 5-byte cipher. |

The rest of this README is the wire flow and a per-algorithm catalogue.


## 1. The exchange flow

GMW3110-2010 $27 SecurityAccess is a two-step handshake on a single physical CAN ID pair:

```
Tester (DPS, PowerPCM, etc.)                           Simulator (this codebase)
============================                           ========================

                                                       (locked: SecurityUnlockedLevel = 0)

  $27 $01                              ----->
  (requestSeed, level 1)
                                                       Service27Handler.Handle()
                                                         -> ISecurityAccessModule.Handle()
                                                            -> Gmw3110_2010_Generic.HandleRequestSeed()
                                                               -> ISeedKeyAlgorithm.GenerateSeed(level, ...)
                                                       state.SecurityPendingSeedLevel = level
                                                       state.SecurityLastIssuedSeed   = seed

                                       <-----    $67 $01 <seed bytes>
                                                  (positive response: 2 or 5 bytes)

  (tester computes key from seed,
   algorithm chosen out-of-band or
   indicated in the wire protocol)

  $27 $02 <key bytes>                  ----->
  (sendKey)
                                                       Gmw3110_2010_Generic.HandleSendKey()
                                                         -> ISeedKeyAlgorithm.ComputeExpectedKey(level, seed, ...)
                                                         -> compare expected vs received
                                                       if match:
                                                         state.SecurityUnlockedLevel = level
                                                         state.SecurityPendingSeedLevel = 0
                                                         state.SecurityLastIssuedSeed = null
                                       <-----    $67 $02      (positive response)

                                                       (unlocked: SecurityUnlockedLevel = level)

  $34 ...                              ----->
  (DownloadRequest etc. - now permitted)
```

Failure paths the generic module handles transparently:

- Wrong key three times in a row -> NRC `$36 ExceededNumberOfAttempts` and a 10-second lockout.
- Lockout still active -> NRC `$37 RequiredTimeDelayNotExpired` on every $27 step.
- `$27 02` arrives without a prior `$27 01` at the same level -> NRC `$24 ConditionsNotCorrectOrSequenceError`.
- Already unlocked at the requested level -> `$67 01` returns a seed of zeros (the spec's "no further authentication needed" marker).
- Algorithm in BypassAll mode (e.g. `gm-bypass-5byte`) -> first `$27 01` returns a zero seed AND silently flips `SecurityUnlockedLevel` so the tester can skip `$27 02` entirely.

Two locations carry the state machine logic:

- `Core/Services/Service27Handler.cs` is the dispatch entry point - delegates straight to the module.
- `Core/Security/Modules/Gmw3110_2010_Generic.cs` handles every NRC, the attempt counter, the lockout, and the BypassAll short-circuit. Algorithms only see the seed and produce a key.


## 2. Algorithm namespace - why "Algo 92" can mean two different ciphers

GM assigns each ECU a single-byte algorithm id (`0x00..0xFF`) via SPO CCA. That byte is the row index into a table - it is not a globally unique key. Several disjoint tables exist, picked by axes that fire BEFORE the algoId is consulted:

- **Seed width** - 2-byte legacy vs Enhanced 5-byte. Different dispatchers, different tables.
- **Security Table selector** (high nibble of AC2 in the utility-file $27 instruction) - `GMLAN-0` (legacy 256-entry pool) vs `GMLAN-1` (second 256-entry pool added when GM exhausted GMLAN-0).
- **Vendor** - GM vs non-GM, the latter living in its own table.

Effective algorithm coordinate: `(table_selector, seed_width, algoId)`. So `Algo 92` 2-byte (E38 ECM) and `Algo 92` 5-byte (E92 ECM family) are unrelated ciphers that share an algoId byte by SPO assignment, not by implementation.

The simulator's registry reflects this with distinct ids - see the catalogue below.


## 3. Catalogue

### 3.1 `E38Algorithm` - 2-byte, GMLAN-0, algoId `0x92` -> E38 ECM

Registry id: `gm-e38`. Community-sourced (jakka351, pcmhacking forum, opensourcetuning/GM), cross-validated against `KeyAlgoGm_$92` in PowerPCM_Flasher_0006.

```csharp
public sealed class E38Algorithm : ISeedKeyAlgorithm
{
    public string Id => "gm-e38";
    public int SeedLength => 2;
    public int KeyLength  => 2;

    public static ushort ComputeKey(ushort seed)
    {
        uint k = (uint)((seed >> 8) | ((seed & 0xFF) << 8));   // byteSwap16
        k = k + 0x7D58;
        k = ~k;
        k = k & 0xFFFF;
        k = k + 0x8001;
        return (ushort)(((k & 0xFF00) >> 8) | ((k & 0xFF) << 8));   // byteSwap16
    }
}
```

Test vectors (in `E38AlgorithmTests`):

| seed | key |
|---|---|
| `0x1234` | `0x96CE` |
| `0xA1B2` | `0x0750` |
| `0xDEAD` | `0xCA54` |
| `0xCAFE` | `0xDE03` |
| `0xFFFF` | `0xA902` |


### 3.2 `E67Algorithm` - 2-byte, GMLAN-0, algoId `0x89` -> E67 ECM

Registry id: `gm-e67-2byte`. Extracted from `KeyAlgoGm_$89` in PowerPCM_Flasher_0006 (`ildasm` -> `Managed TargetRVA = 0x6670`, native x86 at that file offset).

```csharp
public sealed class E67Algorithm : ISeedKeyAlgorithm
{
    public string Id => "gm-e67-2byte";
    public int SeedLength => 2;
    public int KeyLength  => 2;

    public static ushort ComputeKey(ushort seed)
    {
        uint v = Rol16(seed, 6);
        v = ((v & 0xFFFF) << 8) | ((v & 0xFFFF) >> 8);  // bswap16
        v = (v - 0x55E9) & 0xFFFFFFFF;
        v = Ror16((ushort)(v & 0xFFFF), 2);
        return (ushort)((v + 0x2A8E) & 0xFFFF);
    }

    private static uint Rol16(ushort x, int n) { n &= 15;
        return n == 0 ? x : (uint)(((x << n) | (x >> (16 - n))) & 0xFFFF); }
    private static uint Ror16(ushort x, int n) { n &= 15;
        return n == 0 ? x : (uint)(((x >> n) | (x << (16 - n))) & 0xFFFF); }
}
```

Test vectors (in `E67AlgorithmTests`):

| seed | key |
|---|---|
| `0x0000` | `0x1513` |
| `0x1234` | `0x5637` |
| `0xABCD` | `0xAFD0` |
| `0xDEAD` | `0xB2FE` |
| `0xCAFE` | `0xC1C3` |
| `0xFFFF` | `0xD513` |


### 3.3 `T43Algorithm` - 2-byte, T43 TCM (6T70 family)

Registry id: `gm-t43` (legacy alias). GM-assigned algoId is not yet pinned. For interpreter 3, opcode `$27` uses AC0 for the algorithm and AC2 for the table/source; a utility match and independent binding are needed. Decompiled from the FOSS 6Speed.T43 tester's `gett43key`.

```csharp
public sealed class T43Algorithm : ISeedKeyAlgorithm
{
    public string Id => "gm-t43";
    public int SeedLength => 2;
    public int KeyLength  => 2;

    public static ushort ComputeKey(ushort seed)
    {
        int n   = (seed + 0xB0D8) & 0xFFFF;
        n       = (-n) & 0xFFFF;                  // two's-complement negate
        int rol = ((n << 3) | (n >> 13)) & 0xFFFF;
        return (ushort)(((rol & 0xFF) << 8) | ((rol >> 8) & 0xFF));  // byteSwap16
    }
}
```

Test vectors (in `T43AlgorithmTests`):

| seed | key |
|---|---|
| `0x0000` | `0x4279` |
| `0x1234` | `0xA1E7` |
| `0xDEAD` | `0xDB83` |
| `0xCAFE` | `0x5421` |
| `0xFFFF` | `0x4A79` |


### 3.4 `Gm5ByteAlgorithm` - 5-byte Enhanced, GMLAN-0, every algoId -> E92 family and successors

Registry id: `gm-algo92-5byte`. One cipher parameterised by a per-algoId 32-byte payload (stored in `Gm5BytePasswords.Table`, 256 entries embedded in `Gm5BytePasswords.cs`). The cipher math is identical for every algoId; only the payload differs.

```csharp
public sealed class Gm5ByteAlgorithm : ISeedKeyAlgorithm
{
    public string Id => "gm-algo92-5byte";
    public int SeedLength => 5;
    public int KeyLength  => 5;

    public static void ComputeKey(ReadOnlySpan<byte> seed, int algoId, string password, Span<byte> outKey)
    {
        // password layout (62 ASCII chars):
        //   [0..1]    = decimal length marker "01" or "03"
        //   [2..61]   = 60 chars base64
        //
        // decoded blob (44 bytes after base64):
        //   blob[0..31]   = SHA-256 chain payload
        //   blob[32..33]  = WORD A (BE) - governs iteration count
        //   blob[34..35]  = WORD B (BE) - must equal algoId
        //   blob[36..43]  = 8-byte signature (not validated here)

        if (!int.TryParse(password.AsSpan(0, 2), out var marker) || (marker != 1 && marker != 3))
            throw new InvalidOperationException(@"password length marker must be ""01"" or ""03""");

        var blob    = Convert.FromBase64String(password.Substring(2, 60));
        var payload = blob.AsSpan(0, 32);
        int A       = (blob[32] << 8) | blob[33];
        int B       = (blob[34] << 8) | blob[35];

        if (B != algoId) throw new InvalidOperationException($"algoId mismatch: 0x{B:X4} vs 0x{algoId:X4}");
        if (A > 0xFF - seed[4]) throw new InvalidOperationException("A would underflow iteration count");

        int nIter = 0xFF - seed[4] - A;

        Span<byte> h   = stackalloc byte[32];
        Span<byte> tmp = stackalloc byte[32];
        payload.CopyTo(h);
        for (int i = 0; i < nIter; i++) { SHA256.HashData(h, tmp); tmp.CopyTo(h); }

        Span<byte> plaintext  = stackalloc byte[16];
        plaintext.Slice(0, 11).Fill(0xFF);
        seed.CopyTo(plaintext.Slice(11));

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.KeySize = 128;
        aes.Key = h.Slice(0, 16).ToArray();
        Span<byte> ciphertext = stackalloc byte[16];
        aes.EncryptEcb(plaintext, ciphertext, PaddingMode.None);

        ciphertext.Slice(0, 5).CopyTo(outKey);   // first 5 bytes of ciphertext = response key
    }
}
```

Table-wide invariants (verified across the 256 captured entries):

- `B == algoId` for every entry (password self-identifies).
- `A == 0x00F5` constant for every entry. So `N = 0xFF - seed[4] - 0xF5`. For `seed[4] = 0x06` (E92 family marker) `N == 4` for every algoId.

Defaults: `algoId = 0x92`, `password = Gm5BytePasswords.Table[0x92]`, `familyByte = 0x06`. All overridable via `SecurityModuleConfig`:

```json
{
  "algoId":     "0x92",
  "password":   "01...zQk=",
  "familyByte": "0x06",
  "fixedSeed":  "1122334406"
}
```

Corrected archive-import behavior: archive import no longer automatically selects
this model. `UtilitySecuritySelection` reads the documented interpreter-3
fields (algorithm AC0, access mode AC1, source/table AC2, zero-seed exception
AC3) and retains them in the prime report. These fields alone do not establish
seed width, cipher implementation or target compatibility. Imports default to
`gm-archive-unresolved`, whose `$27` handler returns NRC `$11` and never unlocks.
That response describes an unsupported simulator model, not measured ECU
behavior. Explicit model selection remains available. See
[the importer notes](../Dps/README.md#0e-utility-security-selectors-are-metadata-not-a-model-binding).

Test vectors (in `Gm5ByteAlgorithmTests`, all for `algoId = 0x92`):

| seed | key |
|---|---|
| `11 22 33 44 06` | `EC BF F7 87 A4` |
| `8A E5 39 F5 06` | `EC 42 A7 E9 F1` |
| `43 89 30 D3 06` | `CD CF 83 5F 22` |
| `91 81 4E B9 06` | `B0 7B 1E 76 BC` |
| `C0 CA D2 9E 06` | `1E 84 FD BC 03` |
| `DE F0 8C D3 06` | `F3 B2 B4 55 C8` |
| `9C 92 6F F5 06` | `0D 16 59 D3 B9` |
| `D8 B1 D5 40 06` | `23 B7 1F FC F4` |


### 3.5 Non-cipher modules

| Registry id | Class | What it does |
|---|---|---|
| `gm-bypass-5byte` | `Gmw3110_2010_Generic` + `RandomSeedCipher(5)` | Explicit bypass fixture: first `$27 01` returns a zero seed and sets simulated unlocked state. Never selected automatically by archive import. |
| `gm-archive-unresolved` | `UnresolvedArchiveSecurityModule` | Archive default: refuses `$27` with NRC `$11`; no seed generation, key acceptance or unlock. |
| `gm-permissive-5byte`       | `Gmw3110Permissive5Byte` | Emits a real non-zero 5-byte seed; accepts ANY key on `$27 02`. Use for new ECU families whose password we haven't captured yet. |
| `gmw3110-2010-not-implemented` | `Gmw3110_2010_Generic` + `NotImplementedAlgorithm` | Deterministic seed `[0x12, 0x34]`, refuses every key. Exercises every NRC path against any J2534 host without committing to real cipher math. |


## 4. Adding a new algorithm

1. Drop a `class Foo : ISeedKeyAlgorithm` next to the others. Five-line skeleton:
   ```csharp
   public sealed class FooAlgorithm : ISeedKeyAlgorithm
   {
       public string Id => "gm-foo";
       public int SeedLength => 2;     // or 5, or whatever
       public int KeyLength  => 2;
       public IEnumerable<byte> SupportedLevels { get; } = new byte[] { 1 };

       public void GenerateSeed(byte level, Span<byte> seedBuffer, out int seedLength) { /* ... */ }
       public bool ComputeExpectedKey(byte level, ReadOnlySpan<byte> seed, Span<byte> keyBuffer, out int keyLength) { /* ... */ }
       public void LoadConfig(JsonElement? config) { /* ... */ }
   }
   ```
2. Register it in `SecurityModuleRegistry`:
   ```csharp
   Register("gm-foo",
       () => new Gmw3110_2010_Generic(new FooAlgorithm(), id: "gm-foo"));
   ```
3. Add a `*AlgorithmTests` file with at least 4 known seed/key pairs (or a brute-force diff against the original tool that you derived from). Existing tests are good shape references.
4. If the algorithm is for a 5-byte family with a per-algoId payload, just call `new Gm5ByteAlgorithm()` with `algoId` set in `SecurityModuleConfig` - you do NOT need a new class. The cipher is universal.

Module-level concerns (NRC dispatch, attempt counting, lockout, BypassAll short-circuit) are handled by `Gmw3110_2010_Generic`. Algorithms only ever see the seed and produce a key.
