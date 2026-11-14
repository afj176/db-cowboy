# Restricted database connection discovery

When DB Cowboy connects, it reads the catalog once to build the table list, SQL completion, the relationship diagram and the list of vector columns (`PreparedConnection.Open` in `src/DbCowboy/ConnectionProfiles.cs`). A role that can sign in is often not allowed to use every schema, so this discovery only lists what the role may read.

The defect this guards against: authentication succeeded, but vector discovery included tables in schemas the role could not use. Loading the metadata of such a table called `to_regclass` on an inaccessible qualified name, PostgreSQL returned SQLSTATE 42501, and the connection never finished preparing.

The rules:

- Tables and completion entries come only from schemas on which the role has `USAGE`.
- Vector columns additionally need `SELECT` on the table, because Vector lab reads complete rows. A grant on single columns is not enough.
- Foreign keys are listed only when both ends are in schemas with `USAGE` and both tables have `SELECT`, so a link to an inaccessible table cannot break preparation or appear in the relationship diagram.

None of this grants privileges or bypasses PostgreSQL permissions. SQL you type yourself is still subject to the server's permissions and row-level security policies.
