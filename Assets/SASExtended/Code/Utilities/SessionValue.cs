namespace SASExtended.Utilities
{
    // Mirrors ConfigValue<T>'s .Value accessor but holds the value purely in memory - never bound to
    // SWConfiguration, so it's never written to or read back from the on-disk mod config. Used for
    // state (attitude offsets, Hover vertical velocity) that should persist across mode/vessel
    // switches within a play session, but must reset to its default every time the game restarts.
    public class SessionValue<T>
    {
        public SessionValue(T value) => Value = value;

        public T Value { get; set; }
    }
}
