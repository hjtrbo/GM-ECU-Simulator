# Archive security regressions

These test executables create their own temporary utility/archive fixtures.
They require no saved DPS archives, vehicle firmware, VINs, adapters, J2534
registration or hardware drivers. A failed case returns a nonzero exit code;
the output JSON lists every case and failure.

## Core tests

With the .NET9 SDK, from the repository root:

```sh
dotnet run --project Tests.ArchiveSecurity/Tests.ArchiveSecurity.csproj --configuration Debug
```

The 27 cases cover interpreter-3 selector decoding, malformed/unsupported fields,
access-mode overflow, multiple steps, and three complete synthetic archive
imports. They check the registered unresolved module's responses, unchanged
locked state, and report output. No algorithm or target family is inferred from
an archive's algorithm selector alone.

For example, actions `92 00 00 00` declare algorithm92 and access mode0 for the
reviewed interpreter; the previous AC1-based selection treated this as zero and
selected a bypass. The corrected report retains that metadata without claiming
that it identifies an E92 cipher, seed width or supported real ECU.

Format source: General Motors, *Service Programming System Interpreter
Programmers Reference Manual*, April1,2011, interpreter3 opcode27, pp163–164.
The [public mirror](https://www.scribd.com/doc/85336952/Interpreters-April-01-2011)
is not an official GM hosting service. The parser implements only the documented
fields; raw values and unsupported cases remain visible. It does not execute
utility control flow or calculate keys.

## Windows wizard tests

With the .NET9 SDK on Windows:

```sh
dotnet run --project Tests.ArchiveWizard/Tests.ArchiveWizard.csproj --configuration Debug
```

This builds the actual WPF application through a project reference, then runs
six cases against its view models. It checks unresolved defaults, preservation
of an explicit choice on re-entry/same-archive restore, and clearing stale model
and seed overrides on archive changes or failed loads. The test drives the
private post-picker load method by reflection so no file dialog is opened and
no production API needs to be exposed solely for testing.

The application is compiled, not launched: these checks do not validate window
rendering, interactive navigation, drivers or real vehicle behavior. Existing
explicit security models and bypass fixtures remain available for deliberate
simulation use. Unresolved imports return NRC11 as a simulator limitation, not
as a prediction of a real controller's response.

The [CI workflow](../.github/workflows/archive-security.yml) runs core cases on
Linux and wizard cases on Windows. The broader upstream `Tests.Unit` suite and
its external archive fixtures are separate; they are not counted in these runs.
