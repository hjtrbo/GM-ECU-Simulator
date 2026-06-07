namespace Core.Ecu.Personas;

// Resolve a persistence-side string id (`gmw3110`, `uds-kernel`, `ford-uds`)
// to the corresponding singleton IDiagnosticPersona. Used by ConfigStore on
// load so users can pick the dispatch table per ECU in JSON without touching
// code. Missing / unknown ids fall back to Gmw3110Persona - the default for
// every GM ECU.
public static class PersonaRegistry
{
    public static IDiagnosticPersona Resolve(string? id) => id switch
    {
        null                                       => Gmw3110Persona.Instance,
        ""                                         => Gmw3110Persona.Instance,
        "gmw3110"                                  => Gmw3110Persona.Instance,
        "uds-kernel"                               => UdsKernelPersona.Instance,
        "ford-uds"                                 => FordUdsPersona.Instance,
        _                                          => Gmw3110Persona.Instance,
    };
}
