using System.Collections.Specialized;
using Microsoft.SqlServer.Management.Smo;
using Index = Microsoft.SqlServer.Management.Smo.Index;

namespace SqlClone;

/// <summary>
/// Produces the DDL for the clone. SMO scripts the declarative objects (types, tables, indexes, constraints);
/// programmable objects come straight from <c>sys.sql_modules</c> so their text is reproduced exactly.
/// </summary>
internal sealed class SchemaScripter(Database database, SourceCatalog catalog)
{
    private static readonly string[] PreDataModuleTypes = ["FN", "IF", "TF", "V", "P"];

    // Encrypted modules have no definition to copy; the cloner reports them.
    private IEnumerable<ModuleInfo> Modules => catalog.Modules.Where(m => m.Definition is not null);

    private readonly ScriptingOptions _tableOptions = new()
    {
        SchemaQualify = true,
        IncludeHeaders = false,
        AnsiPadding = true,
        DriPrimaryKey = true,
        DriDefaults = true,
        DriIncludeSystemNames = true,
        DriChecks = false,
        DriForeignKeys = false,
        DriUniqueKeys = false,
        Indexes = false,
        ClusteredIndexes = false,
        NonClusteredIndexes = false,
        XmlIndexes = false,
        Statistics = false,
        Triggers = false,
        FullTextIndexes = false,
        ExtendedProperties = false,
        Permissions = false,
        Bindings = false,
        NoFileGroup = true,
        ScriptDataCompression = true,
    };

    private readonly ScriptingOptions _objectOptions = new()
    {
        SchemaQualify = true,
        IncludeHeaders = false,
        DriIncludeSystemNames = true,
        NoFileGroup = true,
        ScriptDataCompression = true,
        ExtendedProperties = false,
        Permissions = false,
    };

    private readonly ScriptingOptions _tableTypeOptions = new()
    {
        SchemaQualify = true,
        IncludeHeaders = false,
        DriAll = true,
        ExtendedProperties = false,
        Permissions = false,
    };

    /// <summary>Everything that must exist before data is loaded: schemas, types, bare tables and modules.</summary>
    public IReadOnlyList<ScriptUnit> PreData()
    {
        var units = new List<ScriptUnit>();

        units.AddRange(catalog.Schemas.Select(s => new ScriptUnit($"schema {Sql.Quote(s)}", [$"CREATE SCHEMA {Sql.Quote(s)}"])));

        foreach (XmlSchemaCollection x in database.XmlSchemaCollections)
            if (x.Schema != "sys")
                units.Add(Unit($"xml schema collection {Sql.Quote(x.Schema, x.Name)}", x.Script(_objectOptions)));

        foreach (UserDefinedDataType t in database.UserDefinedDataTypes)
            units.Add(Unit($"type {Sql.Quote(t.Schema, t.Name)}", t.Script(_objectOptions)));

        // Legacy CREATE RULE / CREATE DEFAULT objects.
        units.AddRange(Modules.Where(m => m.Type is "R" || (m.Type == "D" && m.ParentName is null)).Select(ModuleUnit));

        foreach (UserDefinedTableType t in database.UserDefinedTableTypes)
            units.Add(Unit($"table type {Sql.Quote(t.Schema, t.Name)}", t.Script(_tableTypeOptions)));

        foreach (Sequence s in database.Sequences)
            units.Add(Unit($"sequence {Sql.Quote(s.Schema, s.Name)}", s.Script(_objectOptions)));

        database.PrefetchObjects(typeof(Table), _tableOptions);
        foreach (var t in catalog.Tables)
            units.Add(Unit($"table {t.QualifiedName}", GetTable(t).Script(_tableOptions)));

        units.AddRange(catalog.Synonyms.Select(s => new ScriptUnit($"synonym {Sql.Quote(s.Schema, s.Name)}",
            [$"CREATE SYNONYM {Sql.Quote(s.Schema, s.Name)} FOR {s.BaseObjectName}"])));

        foreach (var type in PreDataModuleTypes)
            units.AddRange(Modules.Where(m => m.Type == type).Select(ModuleUnit));

        return units;
    }

    /// <summary>Everything that is cheaper (or only possible) to create once the data is in place.</summary>
    public IReadOnlyList<ScriptUnit> PostData()
    {
        var indexes = new List<ScriptUnit>();
        var checks = new List<ScriptUnit>();
        var foreignKeys = new List<ScriptUnit>();

        foreach (var info in catalog.Tables)
        {
            var table = GetTable(info);
            indexes.AddRange(IndexUnits(info.QualifiedName, table.Indexes));

            foreach (Statistic s in table.Statistics)
                if (!s.IsAutoCreated && !s.IsFromIndexCreation)
                    indexes.Add(Unit($"statistics {Sql.Quote(s.Name)} on {info.QualifiedName}", s.Script(_objectOptions)));

            foreach (Check c in table.Checks)
                checks.Add(Unit($"check constraint {Sql.Quote(c.Name)} on {info.QualifiedName}", c.Script(_objectOptions)));

            foreach (ForeignKey fk in table.ForeignKeys)
                foreignKeys.Add(Unit($"foreign key {Sql.Quote(fk.Name)} on {info.QualifiedName}", fk.Script(_objectOptions)));
        }

        foreach (var view in Modules.Where(m => m.Type == "V"))
        {
            var smoView = database.Views[view.Name, view.Schema];
            if (smoView is not null && smoView.HasIndex)
                indexes.AddRange(IndexUnits(Sql.Quote(view.Schema, view.Name), smoView.Indexes));
        }

        var bindings = catalog.Bindings.Select(b => new ScriptUnit($"{b.Procedure} {b.BoundName} -> {b.ObjectName}",
            [$"EXEC {b.Procedure} {Sql.Literal(b.BoundName)}, {Sql.Literal(b.ObjectName)}"]));

        var triggers = Modules.Where(m => m.Type == "TR").Select(m =>
        {
            var unit = ModuleUnit(m);
            return m.IsDisabled
                ? unit with
                {
                    Batches = [.. unit.Batches,
                        $"DISABLE TRIGGER {Sql.Quote(m.Schema, m.Name)} ON {Sql.Quote(m.ParentSchema!, m.ParentName!)}"],
                }
                : unit;
        });

        var databaseTriggers = catalog.DatabaseTriggers.Where(t => t.Definition is not null).Select(t =>
        {
            List<string> batches = [AnsiNulls(t.AnsiNulls), QuotedIdentifier(t.QuotedIdentifier), t.Definition!];
            if (t.IsDisabled) batches.Add($"DISABLE TRIGGER {Sql.Quote(t.Name)} ON DATABASE");
            return new ScriptUnit($"database trigger {Sql.Quote(t.Name)}", batches);
        });

        return [.. indexes, .. checks, .. foreignKeys, .. bindings, .. triggers, .. databaseTriggers];
    }

    private IEnumerable<ScriptUnit> IndexUnits(string owner, IndexCollection indexes) =>
        indexes.Cast<Index>()
            .Where(i => i.IndexKeyType != IndexKeyType.DriPrimaryKey)
            .OrderBy(i => i.IndexType switch
            {
                IndexType.ClusteredIndex or IndexType.ClusteredColumnStoreIndex => 0,
                IndexType.PrimaryXmlIndex => 1,
                _ => 2,
            })
            .Select(i => Unit($"index {Sql.Quote(i.Name)} on {owner}", i.Script(_objectOptions)));

    private Table GetTable(TableInfo info) =>
        database.Tables[info.Name, info.Schema]
        ?? throw new CloneException($"SMO could not find table {info.QualifiedName}.");

    private static ScriptUnit ModuleUnit(ModuleInfo m) =>
        new($"{m.Type} {Sql.Quote(m.Schema, m.Name)}",
            [AnsiNulls(m.AnsiNulls), QuotedIdentifier(m.QuotedIdentifier), m.Definition!]);

    private static string AnsiNulls(bool on) => $"SET ANSI_NULLS {(on ? "ON" : "OFF")}";

    private static string QuotedIdentifier(bool on) => $"SET QUOTED_IDENTIFIER {(on ? "ON" : "OFF")}";

    private static ScriptUnit Unit(string description, StringCollection batches) =>
        new(description, batches.Cast<string>().ToList());
}
