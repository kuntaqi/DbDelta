# ALTER COLUMN is emitted for changes it cannot make: identity, and columns a default depends on

Status: fixed on branch `fix/alter-column-dependencies`, not yet filed on GitHub. This file is written to be
pasted into an issue as it stands; the design of the fix is in `docs/PLAN.md`, *A column difference is one of
four statements*.

## Summary

When a table's columns differ only in type, collation or identity, schema sync emits one
`ALTER TABLE … ALTER COLUMN` per column. Two kinds of difference cannot be made that way, and the script
does not know it:

1. **Identity.** `TSqlWriter.AlterColumn` renders the column through `ColumnDefinition`, which appends
   `IDENTITY(seed,increment)` whenever the source column has one. `ALTER COLUMN … IDENTITY` is not T-SQL.
   The batch fails to compile with error 156, so the whole script is rejected before anything runs.
2. **A column a default constraint depends on.** `EmitColumnChange` drops and recreates the indexes that
   depend on the column (`DependentIndexes`), but not the default constraint. Changing the type under a
   default fails with error 4922, and the transaction rolls back.

Both fail safe, because the script is one transaction and nothing is applied. But the review screen shows
the script as ready. Its step labels read like ordinary alters, and the only warning on screen is the
narrowing note on the `datetime2` → `datetime` steps, which is about something else.

## How it was found

A UAT table had been recreated with `SELECT * INTO … FROM <linked server>…` through an MSDASQL linked server
on the legacy "SQL Server" ODBC driver. That leaves the following shape, all of it documented behaviour
rather than a bug:

- `nvarchar(max)` → `ntext`, `varchar(max)` → `text`, `varbinary(max)` → `image`
- `datetime` → `datetime2(7)`
- string columns carry the collation the provider reports, not the database default
- the identity property is dropped (`SELECT INTO` never inherits identity from a remote source)
- constraints and indexes are dropped, and were then re-added by hand

Syncing PROD → UAT for that one table should be DbDelta's easiest case: one table, no data to move, every
difference a column definition. The script it produced could not run.

## Repro

Two databases on one instance, run on LocalDB or any SQL Server with compatibility level 130 or later.
`AppProd` holds the intended shape, and `AppUat` the shape the linked-server copy leaves.

```sql
CREATE DATABASE AppProd COLLATE Latin1_General_CI_AS;
CREATE DATABASE AppUat  COLLATE Latin1_General_CI_AS;
GO

USE AppProd;
CREATE TABLE dbo.Category
(
    CategoryId  int IDENTITY(1,2) NOT NULL,
    Name        varchar(50) COLLATE Latin1_General_CI_AS NULL,
    Notes       nvarchar(max) COLLATE Latin1_General_CI_AS NULL,
    LastUpdated datetime NULL CONSTRAINT DF_Category_LastUpdated DEFAULT (getdate()),
    ReviewedOn  datetime NULL,
    CONSTRAINT PK_Category PRIMARY KEY CLUSTERED (CategoryId)
);
GO

USE AppUat;
CREATE TABLE dbo.Category
(
    CategoryId  int NOT NULL,
    Name        varchar(50) COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
    Notes       ntext COLLATE SQL_Latin1_General_CP1_CI_AS NULL,
    LastUpdated datetime2 NULL CONSTRAINT DF_Category_LastUpdated DEFAULT (getdate()),
    ReviewedOn  datetime2 NULL,
    CONSTRAINT PK_Category PRIMARY KEY CLUSTERED (CategoryId)
);
INSERT dbo.Category (CategoryId, Name, Notes, LastUpdated, ReviewedOn)
VALUES (1, 'a', N'one', '2026-01-01 10:00:00.123', NULL),
       (3, 'b', N'two', '2026-01-02 11:00:00.457', '2026-01-03');
GO
```

Then in DbDelta:

1. Connect `AppProd` as the source and `AppUat` as the target, and compare.
2. On Schema compare, open `dbo.Category`. It lists five column differences: `CategoryId` (identity),
   `Name` (collation), `Notes` (type), `LastUpdated` (type) and `ReviewedOn` (type).
3. Tick `dbo.Category` and open the script review.
4. Apply, or run the script by hand.

## Actual

The review shows five alter steps, in this shape:

```sql
-- 1/5  alter column dbo.Category.CategoryId
ALTER TABLE [dbo].[Category] ALTER COLUMN [CategoryId] INT IDENTITY(1,2) NOT NULL;

-- 2/5  alter column dbo.Category.Name
ALTER TABLE [dbo].[Category] ALTER COLUMN [Name] VARCHAR(50) NULL;

-- 3/5  alter column dbo.Category.Notes
ALTER TABLE [dbo].[Category] ALTER COLUMN [Notes] NVARCHAR(MAX) NULL;

-- 4/5  alter column dbo.Category.LastUpdated — LastUpdated DATETIME2(7) becomes DATETIME, …
ALTER TABLE [dbo].[Category] ALTER COLUMN [LastUpdated] DATETIME NULL;

-- 5/5  alter column dbo.Category.ReviewedOn — ReviewedOn DATETIME2(7) becomes DATETIME, …
ALTER TABLE [dbo].[Category] ALTER COLUMN [ReviewedOn] DATETIME NULL;
```

Run as emitted, the batch is rejected at compile:

```
Msg 156, Level 15, State 1
Incorrect syntax near the keyword 'IDENTITY'.
```

With step 1 removed, step 4 fails in its turn:

```
Msg 4922, Level 16
ALTER TABLE ALTER COLUMN LastUpdated failed because one or more objects access this column.
```

`Name`, `Notes` and `ReviewedOn` alter cleanly on their own. `Name` lands on `Latin1_General_CI_AS`
correctly, because the target's database default is the source column's collation and `ALTER COLUMN`
without `COLLATE` resets to the database default. That is the rule already written down in
`TSqlWriter.CollationClause`, and it holds here.

What has been verified, and what has not. The emitted statements above are the shape DbDelta produced for
the original table, with the names swapped for stand-ins; the step labels and the narrowing note are its
own. Both errors were then reproduced from scratch, by running those exact statements against a replica of
the table's shape.

Verified end to end on 2026-09-23, on LocalDB with the repro above: compare reports `5 columns differ`,
the script DbDelta emits is byte-for-byte the one shown, `notEmitted` is empty, and running it gives
error 156; with step 1 removed, 5074 + 4922 on `DF_Category_LastUpdated`. The PK case was checked
separately — a type change on `CategoryId` fails with 5074 + 4922 on `PK_Category` — and it is a real gap,
because the catalog reader excludes `is_primary_key` and `is_unique_constraint` indexes from
`TableDefinition.Indexes`, so `DependentIndexes` can never see them.

## Expected

**An identity difference is a table rebuild, or a refusal, never an `ALTER COLUMN`.** SQL Server has no
statement that adds identity to, or removes it from, an existing column. There are two acceptable outcomes:

- *Rebuild* (preferred, and what was done by hand to fix the original table):
  1. rename the target table aside, together with its PK and default constraints, so their names are free;
  2. `CREATE TABLE` from the source definition, with the source's constraint names, collations, seed and
     increment, and the PK's fill factor;
  3. `SET IDENTITY_INSERT … ON`, copy every row with explicit casts
     (`ntext` → `nvarchar(max)`, `datetime2` → `datetime`), then `SET IDENTITY_INSERT … OFF`;
  4. assert the row counts match inside the transaction, then drop the set-aside table;
  5. recreate indexes, foreign keys in either direction, triggers and permissions on the new table;
  6. `sp_refreshview` / `sp_refreshsqlmodule` for the dependent non-schema-bound modules, which bind
     column metadata when they are created;
  7. leave the identity current value at the copied maximum. `IDENTITY_INSERT` already does that, but
     assert it, because the next id is `max + increment` and the increment here is 2.
- *Refuse*, in the same posture already taken for a hash index or a keyless table: do not emit the
  step, and say on the review screen that the identity difference needs a table rebuild the tool does not
  perform yet. Emitting SQL the server rejects is worse than either option.

**A column a default depends on has its default dropped and recreated around the alter**, the same way
dependent indexes already are: `DROP CONSTRAINT <default>` before the `ALTER COLUMN`, and
`ADD CONSTRAINT <default> DEFAULT <expr> FOR <column>` after it, with the source's name and expression.
Check constraints and computed columns that reference the column raise the same 4922 and need the same
treatment.

**One case to check while in there.** In the original table the identity column was also the PK column.
Altering a PK column's type fails for the same reason as the default, because the PK depends on it, and the
PK is not in `DependentIndexes` if key constraints are modelled apart from indexes. The repro above does not
reach that case, because error 156 masks everything after it. A rebuild sidesteps it altogether.

## Where in the code

- `src/DbDelta.SqlServer/TSqlWriter.cs`, `AlterColumn` → `ColumnDefinition(column, includeDefault: false, …)`:
  the identity clause is added unconditionally when `column.Identity` is set. `includeDefault: false` already
  shows that `ALTER` needs a narrower rendering than `CREATE`. Identity belongs on the `CREATE`-only side of
  that line too.
- `src/DbDelta.SqlServer/TSqlEmitter.cs`, `EmitColumnChange`: `DependentIndexes(target, name)` is the only
  dependency walk before the alter. Defaults (`ColumnDefinition.DefaultConstraintName` on the *target*
  column), check constraints and computed columns are not considered.
- A test that would have caught both: target and source differ only by identity on one column and type on
  a defaulted column, and the emitted script must execute against a real server, not just match a snapshot.
