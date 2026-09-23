using DbDelta.Core.Model;

namespace DbDelta.SqlServer;

// A foreign key on another table that points at the one being changed, as the target has it.
internal sealed record InboundForeignKey(ObjectIdentity Table, ForeignKeyDefinition Key);
