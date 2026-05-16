namespace Common.Protocol;

/// <summary>
/// Provenance of a $1A identifier value on an ECU. Surfaced as a column in
/// the editor's Identifiers grid so the user can see where each row's value
/// came from at a glance, and persisted through ConfigStore so the
/// classification survives save / reload cycles.
///
/// <para>Rules the writers and stickiness behaviour enforce:</para>
/// <list type="bullet">
///   <item><description><see cref="Blank"/> - never written. Default for
///   every DID on a fresh ECU.</description></item>
///   <item><description><see cref="User"/> - typed in the grid. STICKY:
///   a value the user typed then later cleared still shows as User, not
///   Blank, so a deliberate hand-blank survives subsequent auto-populate
///   and merge-mode bin loads.</description></item>
///   <item><description><see cref="Bin"/> - written by "Load Info From Bin".
///   In Merge mode this only applies to DIDs the bin actually wrote; in
///   Replace-all mode EVERY well-known DID is marked Bin (including blank
///   ones), because Replace-all is the user explicitly saying "this ECU's
///   identifier set is now whatever the bin contained, full stop".</description></item>
///   <item><description><see cref="Auto"/> - written by Auto-populate from
///   <c>DefaultDidValues</c>.</description></item>
/// </list>
/// </summary>
public enum DidSource : byte
{
    Blank = 0,
    User  = 1,
    Bin   = 2,
    Auto  = 3,
}
