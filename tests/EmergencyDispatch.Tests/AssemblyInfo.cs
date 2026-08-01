// Integration-style tests share SQLite in-memory databases and some hold a serializable
// transaction open briefly to exercise mid-solve isolation. Disabling cross-collection
// parallelism keeps those from contending on SQLite's single-writer lock and makes the
// suite deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
