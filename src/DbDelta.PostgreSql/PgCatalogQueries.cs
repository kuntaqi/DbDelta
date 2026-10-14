namespace DbDelta.PostgreSql;

// Catalog SQL in one place, mirroring the SQL Server provider's CatalogQueries. "char"-typed catalog columns
// (attidentity, attgenerated, contype, confupdtype, confdeltype, typtype) are cast to text so the client reads
// a plain string rather than wrestling with Npgsql's single-byte char mapping. System and temp schemas are
// excluded everywhere.
internal static class PgCatalogQueries
{
    private const string NonSystemSchema =
        "n.nspname not in ('pg_catalog', 'information_schema', 'pg_toast') and n.nspname not like 'pg_temp%'";

    public const string Database = """
        select current_database() as name,
               (select datcollate from pg_database where datname = current_database()) as collation
        """;

    public const string Tables = $"""
        select n.nspname as schema_name, c.relname as table_name
        from pg_class c
        join pg_namespace n on n.oid = c.relnamespace
        where c.relkind in ('r', 'p') and {NonSystemSchema}
        order by n.nspname, c.relname
        """;

    public const string Columns = $"""
        select n.nspname as schema_name, c.relname as table_name, a.attname as column_name, a.attnum::int as ordinal,
               format_type(a.atttypid, null) as type_name,
               ic.character_maximum_length as char_len,
               ic.numeric_precision as num_prec,
               ic.numeric_scale as num_scale,
               ic.datetime_precision as dt_prec,
               not a.attnotnull as is_nullable,
               a.attidentity::text as identity_kind,
               seq.seqstart as seq_start,
               seq.seqincrement as seq_inc,
               a.attgenerated::text as generated_kind,
               pg_get_expr(ad.adbin, ad.adrelid) as default_expr,
               ic.collation_name as collation_name,
               tp.typtype::text as type_kind,
               tn.nspname as type_schema,
               tp.typname as type_name_raw
        from pg_attribute a
        join pg_class c on c.oid = a.attrelid
        join pg_namespace n on n.oid = c.relnamespace
        join pg_type tp on tp.oid = a.atttypid
        join pg_namespace tn on tn.oid = tp.typnamespace
        left join pg_attrdef ad on ad.adrelid = a.attrelid and ad.adnum = a.attnum
        left join pg_depend dep on dep.classid = 'pg_class'::regclass and dep.refobjid = a.attrelid
             and dep.refobjsubid = a.attnum and dep.deptype = 'i'
        left join pg_sequence seq on seq.seqrelid = dep.objid
        left join information_schema.columns ic
             on ic.table_schema = n.nspname and ic.table_name = c.relname and ic.column_name = a.attname
        where c.relkind in ('r', 'p') and {NonSystemSchema}
          and a.attnum > 0 and not a.attisdropped
        order by n.nspname, c.relname, a.attnum
        """;

    public const string Constraints = $"""
        select n.nspname as schema_name, c.relname as table_name, con.conname as name,
               con.contype::text as kind,
               con.confupdtype::text as on_update, con.confdeltype::text as on_delete,
               con.convalidated as validated,
               pg_get_expr(con.conbin, con.conrelid) as check_expr,
               rn.nspname as ref_schema, rc.relname as ref_table,
               (select array_agg(att.attname order by k.ord)
                  from unnest(con.conkey) with ordinality k(attnum, ord)
                  join pg_attribute att on att.attrelid = con.conrelid and att.attnum = k.attnum) as cols,
               (select array_agg(att.attname order by k.ord)
                  from unnest(con.confkey) with ordinality k(attnum, ord)
                  join pg_attribute att on att.attrelid = con.confrelid and att.attnum = k.attnum) as ref_cols
        from pg_constraint con
        join pg_class c on c.oid = con.conrelid
        join pg_namespace n on n.oid = c.relnamespace
        left join pg_class rc on rc.oid = con.confrelid
        left join pg_namespace rn on rn.oid = rc.relnamespace
        where con.contype in ('p', 'u', 'f', 'c') and {NonSystemSchema}
        order by n.nspname, c.relname, con.contype, con.conname
        """;

    // Indexes that back a primary key, unique, or exclusion constraint are modelled on the constraint, not
    // here, so they are filtered out — the same split the SQL Server reader makes between KeyConstraints and
    // Indexes.
    public const string Indexes = $"""
        select n.nspname as schema_name, c.relname as table_name, ix.relname as index_name,
               am.amname as method, i.indisunique as is_unique,
               pg_get_expr(i.indpred, i.indrelid) as predicate,
               ix.reloptions as reloptions
        from pg_index i
        join pg_class c on c.oid = i.indrelid
        join pg_class ix on ix.oid = i.indexrelid
        join pg_am am on am.oid = ix.relam
        join pg_namespace n on n.oid = c.relnamespace
        where c.relkind in ('r', 'p') and {NonSystemSchema}
          and not i.indisprimary
          and not exists (select 1 from pg_constraint con
                          where con.conindid = i.indexrelid and con.contype in ('p', 'u', 'x'))
        order by n.nspname, c.relname, ix.relname
        """;

    // indkey/indoption are 0-based int2vectors; a column at attnum 0 is an expression, rendered from the index
    // definition. Positions past indnkeyatts are INCLUDE columns.
    public const string IndexColumns = $"""
        select n.nspname as schema_name, c.relname as table_name, ix.relname as index_name, k.ord::int as ordinal,
               case when k.attnum = 0 then pg_get_indexdef(i.indexrelid, k.ord::int, true)
                    else a.attname end as column_name,
               (i.indoption[k.ord - 1] & 1) = 1 as is_descending,
               k.ord > i.indnkeyatts as is_included
        from pg_index i
        join pg_class c on c.oid = i.indrelid
        join pg_class ix on ix.oid = i.indexrelid
        join pg_namespace n on n.oid = c.relnamespace
        cross join lateral unnest(i.indkey) with ordinality k(attnum, ord)
        left join pg_attribute a on a.attrelid = i.indrelid and a.attnum = k.attnum
        where c.relkind in ('r', 'p') and {NonSystemSchema}
          and not i.indisprimary
          and not exists (select 1 from pg_constraint con
                          where con.conindid = i.indexrelid and con.contype in ('p', 'u', 'x'))
        order by n.nspname, c.relname, ix.relname, k.ord
        """;

    public const string Views = $"""
        select n.nspname as schema_name, c.relname as view_name, pg_get_viewdef(c.oid, true) as definition
        from pg_class c
        join pg_namespace n on n.oid = c.relnamespace
        where c.relkind = 'v' and {NonSystemSchema}
        order by n.nspname, c.relname
        """;

    // Overloaded functions share a name, so the identity carries the argument signature the way PostgreSQL
    // itself disambiguates. prokind is filtered to plain functions and procedures; aggregates and window
    // functions are out of scope and pg_get_functiondef does not support them anyway.
    public const string Routines = $"""
        select n.nspname as schema_name,
               p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')' as routine_name,
               p.prokind::text as kind, p.proretset as returns_set,
               pg_get_functiondef(p.oid) as definition
        from pg_proc p
        join pg_namespace n on n.oid = p.pronamespace
        where p.prokind in ('f', 'p') and {NonSystemSchema}
        order by n.nspname, p.proname
        """;

    // tgisinternal excludes the triggers PostgreSQL creates to enforce foreign keys and constraints. A
    // trigger name is unique per table, not per schema, so the table qualifies it below.
    public const string Triggers = $"""
        select n.nspname as schema_name, c.relname as table_name, t.tgname as trigger_name,
               t.tgenabled::text as enabled,
               pg_get_triggerdef(t.oid, true) as definition
        from pg_trigger t
        join pg_class c on c.oid = t.tgrelid
        join pg_namespace n on n.oid = c.relnamespace
        where not t.tgisinternal and {NonSystemSchema}
        order by n.nspname, c.relname, t.tgname
        """;
}
