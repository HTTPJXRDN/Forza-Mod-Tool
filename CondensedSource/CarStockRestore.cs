using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FH6LocalCryptoTool;

// Row ownership, not a list of editable controls. Both the working DB and clean
// reference contribute ownership: a converted car must not claim its donor's
// engine, and a removed reference must not hide another car's use of a part.
internal static class CarStockRestore
{
    internal sealed record Result(int Tables, int ChangedGroups, IReadOnlyList<string> SharedTables);
    sealed record Column(string Name, int Key, bool Generated);
    sealed record Table(string Name, Column[] Columns, string Sql, bool WithoutRowid)
    {
        internal Column[] Keys => Columns.Where(c => c.Key > 0).OrderBy(c => c.Key).ToArray();
        internal int Index(string name) => Array.FindIndex(Columns, c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
    sealed record Row(Table Table, object[] Values, object? RowId)
    {
        internal object Get(string name) { int i = Table.Index(name); return i < 0 ? DBNull.Value : Values[i]; }
    }
    sealed class Node(string table, string key)
    {
        internal string Table = table, Key = key;
        internal List<Row> Current = [], Stock = [];
        internal HashSet<Node> Children = [];
        internal int Owners;
        internal bool Direct, EngineResource, MotorResource;
        internal long EngineId, MotorId;
    }
    sealed record Relation(string Child, string[] From, string Parent, string[] To, bool OwnsChildren);
    sealed record Change(Node Node, List<object[]> Expected);

    // These are component records, not global classification/catalog tables.
    // Declared FKs and semantic owner keys discover their remaining dependent
    // tables, including tables with no corresponding Car Editor control.
    static readonly HashSet<string> Components = new(StringComparer.OrdinalIgnoreCase)
    {
        "Data_CarBody", "Data_CarBody_DummyAxle", "Data_Engine", "Data_Motor", "Data_Drivetrain",
        "List_AeroPhysics", "List_SpringDamperPhysics", "List_AntiSwayPhysics", "List_TorqueCurve",
        "List_TireCompound", "List_TyreCurveDB", "List_TireFrictionCurve", "List_TireFrictionMultiCurve",
        "List_TireAffectCurve", "List_Wheels", "WheelNormOffsets", "WheelAnnotations", "AutoSteerOverrides",
        "List_BrakeProfile", "List_DamageModDef", "List_SteeringSettings", "List_RearSteeringSettings",
        "List_ThirdSpringElement", "List_PreloadAndDroopDamper", "List_VariableTiming",
        "List_TractionControl", "CarPartNames", "List_PartsStrings", "NewProfile_Career_Garage", "PaintableGroups"
    };
    static readonly HashSet<string> CarOrdinals = new(StringComparer.OrdinalIgnoreCase)
    {
        "CarInvalidDefaultParts", "CarInvalidUpgradePackage", "CarPartPositions", "CarVouchersExclusions",
        "CarsDropParts", "FreeRevOverrides", "InAirBehaviourOverride", "List_AeroPhysics",
        "List_AntiSwayPhysics", "List_DrivingMode", "List_SpringDamperPhysics", "LiveryStripes",
        "PaintableGroupNames", "PaintableGroups", "UnobtainableCars", "UpgradePresetPackages"
    };
    static string Q(string value) => '"' + value.Replace("\"", "\"\"") + '"';
    static string Schema(string value) => value is "main" or "stockref" ? value : throw new ArgumentException("Unsupported restore schema.");
    static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    static bool Number(object value, out long id) => long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);

    internal static Result Restore(SqliteConnection db, long carId)
    {
        if (carId <= 0) throw new InvalidOperationException("Select a real car to restore.");
        Execute(db, "SAVEPOINT restore_owned_car");
        try
        {
            var graph = new Graph(db, carId);
            var changes = graph.Plan();
            string schema = SchemaFingerprint(db);
            var foreignKeys = ForeignKeys(db);
            foreach (var change in changes) Apply(db, change);
            graph.Verify(ReadRows(db, "main"), changes);
            if (schema != SchemaFingerprint(db)) throw new InvalidDataException("Restore changed the database schema.");
            foreach (var pair in ForeignKeys(db))
                if (pair.Value > foreignKeys.GetValueOrDefault(pair.Key))
                    throw new InvalidDataException("Restore would introduce a foreign-key error: " + pair.Key);
            using (var check = db.CreateCommand())
            {
                check.CommandText = "PRAGMA main.quick_check";
                if (!Is(Convert.ToString(check.ExecuteScalar()) ?? "", "ok")) throw new InvalidDataException("Restored DB failed quick_check.");
            }
            var result = new Result(changes.Select(c => c.Node.Table).Distinct(StringComparer.OrdinalIgnoreCase).Count(), changes.Count,
                graph.Nodes.Values.Where(n => n.Owners == 3 && graph.Different(n)).Select(n => n.Table)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
            Execute(db, "RELEASE restore_owned_car");
            return result;
        }
        catch (Exception error)
        {
            try { Execute(db, "ROLLBACK TO restore_owned_car"); Execute(db, "RELEASE restore_owned_car"); }
            catch (Exception rollback) { throw new AggregateException("Restore failed and rollback could not be confirmed. Reload the DB before exporting.", error, rollback); }
            throw;
        }
    }

    sealed class Graph
    {
        internal Dictionary<string, Node> Nodes = new(StringComparer.Ordinal);
        readonly Dictionary<string, Table> currentTables, stockTables;
        readonly HashSet<long> originalEngines = [], originalMotors = [];
        readonly Dictionary<string, Dictionary<string, List<Node>>> indices = new(StringComparer.Ordinal);
        readonly Dictionary<string, List<Node>> tablesOfNodes = new(StringComparer.OrdinalIgnoreCase);
        readonly long carId;
        readonly List<Relation> relations = [];
        readonly List<Relation> foreignRelations = [];

        internal Graph(SqliteConnection db, long id)
        {
            carId = id;
            var current = ReadRows(db, "main"); var stock = ReadRows(db, "stockref");
            currentTables = current.Tables; stockTables = stock.Tables;
            if (!stock.Rows.Any(r => Is(r.Table.Name, "Data_Car") && Number(r.Get("Id"), out long value) && value == id))
                throw new InvalidOperationException("The selected car is not in the matching stock reference.");
            foreach (var row in stock.Rows)
            {
                if (!Number(row.Get("Ordinal"), out long owner) || owner != id || !Number(row.Get("IsStock"), out long flag) || flag != 1) continue;
                if (Is(row.Table.Name, "List_UpgradeEngine") && Number(row.Get("EngineID"), out long engine)) originalEngines.Add(engine);
                if (Is(row.Table.Name, "List_UpgradeMotor") && Number(row.Get("MotorID"), out long motor)) originalMotors.Add(motor);
            }
            AddRows(current.Rows, false); AddRows(stock.Rows, true);
            foreach (var group in Nodes.Values.GroupBy(n => n.Table, StringComparer.OrdinalIgnoreCase)) tablesOfNodes.Add(group.Key, group.ToList());
            ReadRelations(db, "main", currentTables); ReadRelations(db, "stockref", stockTables);
            ResolveForeignRelations();
            AddImplicitRelations();
            foreach (var relation in relations.DistinctBy(r => r.Child.ToUpperInvariant() + "/" + string.Join(',', r.From).ToUpperInvariant() + "/" + r.Parent.ToUpperInvariant() + "/" + string.Join(',', r.To).ToUpperInvariant() + "/" + r.OwnsChildren))
                Connect(relation);
            ConnectTrackDisplayNames();
            var queue = new Queue<Node>(Nodes.Values.Where(n => n.Owners != 0));
            while (queue.TryDequeue(out var parent))
                foreach (var child in parent.Children)
                {
                    int flags = parent.Owners;
                    if (child.EngineResource && !originalEngines.Contains(child.EngineId) || child.MotorResource && !originalMotors.Contains(child.MotorId)) flags &= ~1;
                    int added = flags & ~child.Owners;
                    if (added == 0) continue;
                    child.Owners |= added; queue.Enqueue(child);
                }
        }

        IEnumerable<Node> TableNodes(string table) => tablesOfNodes.TryGetValue(table, out var nodes) ? nodes : [];

        void AddRows(List<Row> rows, bool stock)
        {
            foreach (var row in rows)
            {
                string key = Identity(row);
                if (!Nodes.TryGetValue(key, out var node)) Nodes.Add(key, node = new(row.Table.Name, key));
                (stock ? node.Stock : node.Current).Add(row);
                var t = row.Table;
                string? ownerColumn = Is(t.Name, "Data_Car") || Is(t.Name, "CarTrackOffsets") ? "Id" :
                    t.Index("CarId") >= 0 ? "CarId" : t.Index("CarOrdinal") >= 0 ? "CarOrdinal" :
                    t.Index("Ordinal") >= 0 && (CarOrdinals.Contains(t.Name) || t.Name.StartsWith("List_Upgrade", StringComparison.OrdinalIgnoreCase)) ? "Ordinal" : null;
                if (Is(t.Name, "FeaturedTilesOfflineCars") || Is(t.Name, "FixedLiveryCars")) ownerColumn = "ModelId";
                if (Is(t.Name, "MidnightCars")) ownerColumn = "MidnightCarModelId";
                if ((Is(t.Name, "OnDiscContent") || Is(t.Name, "ContentOffersMapping")) && Number(row.Get("ContentType"), out long type) && type == 1) ownerColumn = "ContentId";
                if (ownerColumn != null) Seed(node, row.Get(ownerColumn));
                if (Is(t.Name, "Data_Engine") || t.Name.StartsWith("List_UpgradeEngine", StringComparison.OrdinalIgnoreCase) && t.Index("Ordinal") < 0)
                { node.EngineResource = true; if (Number(row.Get("EngineID"), out long value)) node.EngineId = value; }
                if (Is(t.Name, "Data_Motor") || t.Name.StartsWith("List_UpgradeMotor", StringComparison.OrdinalIgnoreCase) && t.Index("Ordinal") < 0)
                { node.MotorResource = true; if (Number(row.Get("MotorID"), out long value)) node.MotorId = value; }
            }
        }

        void Seed(Node node, object value)
        {
            node.Direct = true;
            // Global/default rows (Ordinal 0) and invalid/unknown ownership are
            // protected too; a coincident numeric component ID is not ownership.
            node.Owners |= Number(value, out long owner) && owner == carId ? 1 : 2;
        }

        void ReadRelations(SqliteConnection db, string schema, Dictionary<string, Table> tables)
        {
            foreach (var table in tables.Values)
            {
                var fks = new List<(long Id, long Seq, string Parent, string From, string? To)>();
                using (var cmd = db.CreateCommand())
                {
                    cmd.CommandText = $"PRAGMA {Schema(schema)}.foreign_key_list({Q(table.Name)})";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) fks.Add((r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
                }
                foreach (var group in fks.GroupBy(f => f.Id))
                {
                    var keys = group.OrderBy(f => f.Seq).ToArray(); string parent = keys[0].Parent;
                    if (Is(parent, "Data_Car"))
                    {
                        if (keys.Length != 1 || keys[0].To != null && !Is(keys[0].To!, "Id")) throw new InvalidDataException("Unsupported car ownership foreign key in " + table.Name);
                        foreach (var node in TableNodes(table.Name))
                            foreach (var row in node.Current.Concat(node.Stock)) Seed(node, row.Get(keys[0].From));
                        continue;
                    }
                    string[] to = keys.Select((k, i) => k.To ?? (tables.TryGetValue(parent, out var p) && p.Keys.Length == keys.Length ? p.Keys[i].Name : "")).ToArray();
                    if (to.Any(string.IsNullOrEmpty)) throw new InvalidDataException("Unsupported reference in " + table.Name);
                    foreignRelations.Add(new(table.Name, keys.Select(k => k.From).ToArray(), parent, to, false));
                }
            }
        }

        void ResolveForeignRelations()
        {
            // Resolve table-level descendants after BOTH schemas have seeded
            // direct car ownership. This also follows multi-level custom FK
            // tables without making alphabetical schema order significant.
            var related = new HashSet<string>(Components, StringComparer.OrdinalIgnoreCase);
            related.UnionWith(Nodes.Values.Where(n => n.Direct || n.Table.StartsWith("List_Upgrade", StringComparison.OrdinalIgnoreCase)).Select(n => n.Table));
            bool added;
            do
            {
                added = false;
                foreach (var r in foreignRelations)
                    if (related.Contains(r.Parent)) added |= related.Add(r.Child);
            } while (added);
            foreach (var r in foreignRelations.Where(r => related.Contains(r.Parent)))
            {
                bool knownOwner = r.From.Length == 1 && (Is(r.Parent, "Data_Engine") && Is(r.From[0], "EngineID") ||
                    Is(r.Parent, "Data_Motor") && Is(r.From[0], "MotorID") || Is(r.Parent, "Data_Drivetrain") && Is(r.From[0], "DrivetrainID") ||
                    Is(r.Parent, "Data_CarBody") && Is(r.From[0], "CarBodyID"));
                bool customChild = !Components.Contains(r.Child) && !r.Child.StartsWith("List_Upgrade", StringComparison.OrdinalIgnoreCase) &&
                    !TableNodes(r.Child).Any(n => n.Direct);
                relations.Add(r with { OwnsChildren = knownOwner || customChild });
            }
        }

        void AddImplicitRelations()
        {
            var tables = currentTables.Values.Concat(stockTables.Values).DistinctBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            void Link(string child, string from, string parent, string to, bool owns = false) => relations.Add(new(child, [from], parent, [to], owns));
            // Distinctive component keys are safe across renamed/custom tables;
            // generic Id columns are never joined by coincident numeric values.
            foreach (var parent in tables.Where(t => Components.Contains(t.Name) && t.Keys.Length == 1 && !Is(t.Keys[0].Name, "Id")))
                foreach (var child in tables.Where(t => !Is(t.Name, parent.Name) && t.Index(parent.Keys[0].Name) >= 0))
                {
                    string key = parent.Keys[0].Name;
                    bool owns = Is(parent.Name, "Data_Engine") || Is(parent.Name, "Data_Motor") || Is(parent.Name, "Data_Drivetrain");
                    Link(child.Name, key, parent.Name, key, owns);
                }
            foreach (var child in tables)
            {
                if (child.Index("CarBodyID") >= 0 && !Is(child.Name, "Data_CarBody")) Link(child.Name, "CarBodyID", "Data_CarBody", "Id", true);
                if (child.Index("PartsStringID") >= 0) Link(child.Name, "PartsStringID", "List_PartsStrings", "Id");
                if (child.Index("TorqueCurveFullThrottleID") >= 0) Link(child.Name, "TorqueCurveFullThrottleID", "List_TorqueCurve", "TorqueCurveID");
                if (child.Index("FrontSpringDamperPhysicsID") >= 0) Link(child.Name, "FrontSpringDamperPhysicsID", "List_SpringDamperPhysics", "SpringDamperPhysicsID");
                if (child.Index("RearSpringDamperPhysicsID") >= 0) Link(child.Name, "RearSpringDamperPhysicsID", "List_SpringDamperPhysics", "SpringDamperPhysicsID");
            }
            Link("Data_CarBody_DummyAxle", "Id", "Data_CarBody", "Id", true);
            Link("WheelNormOffsets", "Id", "List_Wheels", "ID", true);
            Link("WheelAnnotations", "WheelID", "List_Wheels", "ID", true);
            Link("Data_Car", "StockWheelID", "List_Wheels", "ID");
            Link("Data_Car", "AutoSteerOverrideID", "AutoSteerOverrides", "Id");
            Link("Data_Car", "BrakeProfile", "List_BrakeProfile", "BrakesProfileID");
            Link("List_UpgradeBrakes", "BrakesProfileID", "List_BrakeProfile", "BrakesProfileID");
            Link("List_UpgradeSpringDamper", "SteeringSettingsProfileID", "List_SteeringSettings", "SteeringSettingsID");
            Link("List_UpgradeSpringDamper", "RearSteeringSettingsID", "List_RearSteeringSettings", "Id");
            Link("List_UpgradeSpringDamper", "FrontThirdSpringID", "List_ThirdSpringElement", "ThirdSpringID");
            Link("List_UpgradeSpringDamper", "RearThirdSpringID", "List_ThirdSpringElement", "ThirdSpringID");
            Link("List_UpgradeSpringDamper", "SuspensionDamageModDefID", "List_DamageModDef", "DamageModDefID");
            Link("List_UpgradeSpringDamper", "TiresDamageModDefID", "List_DamageModDef", "DamageModDefID");
            Link("List_TyreCurveDB", "TireCompoundID", "List_TireCompound", "TireCompoundID", true);
            Link("List_UpgradeTireCompoundFictionModOverride", "PartId", "List_UpgradeTireCompound", "Id", true);
            Link("NewProfile_Career_PurchasedParts", "GarageId", "NewProfile_Career_Garage", "Id", true);
            foreach (var column in tables.FirstOrDefault(t => Is(t.Name, "List_TireCompound"))?.Columns ?? [])
            {
                if (column.Name.StartsWith("AffectCurve", StringComparison.OrdinalIgnoreCase)) Link("List_TireCompound", column.Name, "List_TireAffectCurve", "AffectCurveID");
                if (column.Name.StartsWith("FrictionMultiCurve", StringComparison.OrdinalIgnoreCase)) Link("List_TireCompound", column.Name, "List_TireFrictionMultiCurve", "FrictionMultiCurveID");
            }
            // IDs in the part-name catalog are enum IDs, not upgrade-row IDs.
            Link("CarsDropParts", "PartID", "CarPartNames", "ID");
            // The game's own part metadata also maps preset/template fields
            // which lack declared FKs (transmissions, differentials, etc.).
            foreach (var row in TableNodes("Data_UpgradePart").SelectMany(n => n.Stock.Concat(n.Current)))
            {
                string part = Convert.ToString(row.Get("PartName"), CultureInfo.InvariantCulture) ?? "";
                string target = Convert.ToString(row.Get("TableName"), CultureInfo.InvariantCulture) ?? "";
                if (!target.StartsWith("List_Upgrade", StringComparison.OrdinalIgnoreCase) && !Is(target, "List_Wheels")) continue;
                foreach (string source in new[] { "UpgradePresetPackages", "CarInvalidUpgradePackage", "NewProfile_Career_Garage" })
                    Link(source, part, target, "Id");
            }
            Link("List_UpgradeDrivetrainDifferential", "TCProfileID", "List_TractionControl", "TC_ProfileID");
            Link("List_DrivingMode", "TCProfileID_Override", "List_TractionControl", "TC_ProfileID");
            // AllIECars has its own unrelated Id sequence; its owner is MediaName.
            Link("AllIECars", "MediaName", "Data_Car", "MediaName", true);
        }

        void ConnectTrackDisplayNames()
        {
            // UI definitions use (TypeId, Level, IsException), not PartId.
            // Private size labels can be restored/removed; shared ones retain
            // the ownership flags of every car using that size.
            var definitions = new Dictionary<(long Type, long Level), HashSet<Node>>();
            foreach (var node in TableNodes("Upgrades"))
                foreach (var row in node.Current.Concat(node.Stock))
                    if (Number(row.Get("TypeId"), out long type) && Number(row.Get("Level"), out long level) &&
                        Number(row.Get("IsException"), out long exception) && exception == 0)
                    {
                        if (!definitions.TryGetValue((type, level), out var set)) definitions[(type, level)] = set = [];
                        set.Add(node);
                    }
            foreach (var (table, part) in new[] { ("List_UpgradeCarBodyTrackSpacingFront", "TrackSpacingFront"),
                         ("List_UpgradeCarBodyTrackSpacingRear", "TrackSpacingRear") })
                foreach (var typeNode in TableNodes("UpgradeTypes"))
                    foreach (var typeRow in typeNode.Current.Concat(typeNode.Stock))
                    {
                        bool isPart = Is(Convert.ToString(typeRow.Get("PartName"), CultureInfo.InvariantCulture) ?? "", part) ||
                            Number(typeRow.Get("PartEnum"), out long partEnum) && partEnum == (part == "TrackSpacingFront" ? 42 : 43);
                        if (!isPart ||
                            !Number(typeRow.Get("Id"), out long type) || !Number(typeRow.Get("IsException"), out long exception) || exception != 0) continue;
                        foreach (var node in TableNodes(table))
                            foreach (var row in node.Current.Concat(node.Stock))
                                if (Number(row.Get("Level"), out long level) && definitions.TryGetValue((type, level), out var parents))
                                    foreach (var parent in parents) { node.Children.Add(parent); parent.Children.Add(typeNode); }
                    }
        }

        void Connect(Relation relation)
        {
            string indexKey = relation.Parent.ToUpperInvariant() + "/" + string.Join('/', relation.To).ToUpperInvariant();
            if (!indices.TryGetValue(indexKey, out var index))
            {
                indices[indexKey] = index = new(StringComparer.Ordinal);
                foreach (var node in TableNodes(relation.Parent))
                    foreach (var row in node.Current.Concat(node.Stock))
                    {
                        if (relation.To.Any(c => row.Table.Index(c) < 0)) continue;
                        string key = ReferenceKey(relation.To.Select(row.Get));
                        if (!index.TryGetValue(key, out var list)) index.Add(key, list = []);
                        if (!list.Contains(node)) list.Add(node);
                    }
            }
            foreach (var child in TableNodes(relation.Child))
                foreach (var row in child.Current.Concat(child.Stock))
                {
                    if (relation.From.Any(c => row.Table.Index(c) < 0 || row.Get(c) is DBNull)) continue;
                    if (!index.TryGetValue(ReferenceKey(relation.From.Select(row.Get)), out var parents)) continue;
                    foreach (var parent in parents)
                    {
                        if (!Is(relation.Parent, "Data_Car")) child.Children.Add(parent);
                        if (relation.OwnsChildren && !child.Direct) parent.Children.Add(child);
                    }
                }
        }

        internal bool Different(Node node) => !Multiset(node.Current.Select(r => ValueKey(r.Values)))
            .SequenceEqual(Multiset(Expected(node).Select(ValueKey)));

        List<object[]> Expected(Node node)
        {
            var rows = node.Stock.Select(r => (object[])r.Values.Clone()).ToList();
            if ((Is(node.Table, "Data_Engine") || Is(node.Table, "Data_Motor")) && node.Current.Count == 1)
                foreach (var row in rows)
                {
                    string column = Is(node.Table, "Data_Engine") ? "EngineName" : "MotorName";
                    int name = node.Stock[0].Table.Index(column);
                    if (name >= 0) row[name] = node.Current[0].Get(column);
                }
            return rows;
        }

        internal List<Change> Plan()
        {
            var changes = new List<Change>();
            foreach (var node in Nodes.Values.Where(n => n.Owners == 1))
            {
                if (!Different(node)) continue;
                if (!currentTables.TryGetValue(node.Table, out var table)) throw new InvalidDataException(node.Table + ": a required table is missing. Restore schema changes separately first.");
                if (stockTables.TryGetValue(node.Table, out var stock) && !SameSchema(table, stock))
                    throw new InvalidDataException(node.Table + ": columns/keys differ from stock. Restore schema changes separately first.");
                changes.Add(new(node, Expected(node)));
            }
            return changes;
        }

        internal void Verify(Snapshot after, List<Change> changes)
        {
            var planned = changes.ToDictionary(c => c.Node.Key, StringComparer.Ordinal);
            var actual = after.Rows.GroupBy(Identity).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            foreach (string key in Nodes.Keys.Union(actual.Keys, StringComparer.Ordinal))
            {
                actual.TryGetValue(key, out var rows); rows ??= [];
                if (planned.TryGetValue(key, out var change))
                {
                    if (!Multiset(rows.Select(r => ValueKey(r.Values))).SequenceEqual(Multiset(change.Expected.Select(ValueKey))))
                        throw new InvalidDataException(change.Node.Table + ": restored records did not match the expected stock data.");
                }
                else
                {
                    Nodes.TryGetValue(key, out var node);
                    if (!Multiset(rows.Select(Fingerprint)).SequenceEqual(Multiset((node?.Current ?? []).Select(Fingerprint))))
                        throw new InvalidDataException((node?.Table ?? rows.First().Table.Name) + ": restore would affect shared/unrelated records. Nothing was kept.");
                }
            }
        }
    }

    sealed record Snapshot(Dictionary<string, Table> Tables, List<Row> Rows);
    static Snapshot ReadRows(SqliteConnection db, string schema)
    {
        var tables = new Dictionary<string, Table>(StringComparer.OrdinalIgnoreCase); var rows = new List<Row>();
        var definitions = new List<(string Name, string Sql)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = $"SELECT name,sql FROM {Schema(schema)}.sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var r = cmd.ExecuteReader(); while (r.Read()) definitions.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
        }
        foreach (var (name, sql) in definitions)
        {
            var columns = new List<Column>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA {Schema(schema)}.table_xinfo({Q(name)})";
                using var r = cmd.ExecuteReader(); while (r.Read()) columns.Add(new(r.GetString(1), r.GetInt32(5), r.GetInt32(6) != 0));
            }
            var table = new Table(name, columns.ToArray(), sql, sql.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase)); tables.Add(name, table);
            string? rowid = table.WithoutRowid ? null : new[] { "rowid", "_rowid_", "oid" }.FirstOrDefault(n => table.Index(n) < 0);
            using var query = db.CreateCommand(); query.CommandText = $"SELECT {(rowid == null ? "" : rowid + ",")}{string.Join(',', columns.Select(c => Q(c.Name)))} FROM {Schema(schema)}.{Q(name)}";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var values = new object[columns.Count]; int offset = rowid == null ? 0 : 1;
                for (int i = 0; i < values.Length; i++) values[i] = reader.GetValue(i + offset);
                rows.Add(new(table, values, rowid == null ? null : reader.GetValue(0)));
            }
        }
        return new(tables, rows);
    }

    static bool SameSchema(Table a, Table b) => a.WithoutRowid == b.WithoutRowid && a.Columns.Length == b.Columns.Length &&
        a.Columns.Zip(b.Columns).All(p => Is(p.First.Name, p.Second.Name) && p.First.Key == p.Second.Key && p.First.Generated == p.Second.Generated);
    static string Identity(Row row) => row.Table.Name.ToUpperInvariant() + ":" +
        (row.Table.Keys.Length == 0 ? ValueKey(row.Values) : ValueKey(row.Table.Keys.Select(c => row.Get(c.Name))));
    static string Fingerprint(Row row) => ValueKey(row.Values.Append(row.RowId ?? DBNull.Value));
    static string[] Multiset(IEnumerable<string> rows) => rows.Order(StringComparer.Ordinal).ToArray();
    static string ReferenceKey(IEnumerable<object> values) => ValueKey(values.Select(v => Number(v, out long id) ? id : v));
    static string ValueKey(IEnumerable<object> values)
    {
        using var memory = new MemoryStream(); using (var w = new BinaryWriter(memory, Encoding.UTF8, true))
            foreach (object value in values)
                switch (value)
                {
                    case DBNull: w.Write((byte)0); break;
                    case long n: w.Write((byte)1); w.Write(n); break;
                    case double n: w.Write((byte)2); w.Write(n); break;
                    case string text: w.Write((byte)3); w.Write(text); break;
                    case byte[] bytes: w.Write((byte)4); w.Write(bytes.Length); w.Write(bytes); break;
                    default: throw new InvalidDataException("Unsupported SQLite restore value.");
                }
        return Convert.ToHexString(SHA256.HashData(memory.GetBuffer().AsSpan(0, (int)memory.Length)));
    }

    static void Apply(SqliteConnection db, Change change)
    {
        var node = change.Node;
        var table = node.Current.FirstOrDefault()?.Table ?? node.Stock[0].Table;
        if (table.Columns.Any(c => c.Generated)) throw new InvalidDataException(table.Name + ": generated/hidden columns require manual review.");
        var keys = table.Keys;
        if (node.Current.Count == 1 && change.Expected.Count == 1 && keys.Length > 0 && keys.All(c => node.Current[0].Get(c.Name) is not DBNull))
        {
            var columns = table.Columns.Where(c => c.Key == 0).ToArray();
            if (columns.Length == 0) return;
            string set = string.Join(',', columns.Select((c, i) => Q(c.Name) + "=$v" + i));
            string where = string.Join(" AND ", keys.Select((c, i) => Q(c.Name) + " IS $k" + i));
            using var cmd = db.CreateCommand(); cmd.CommandText = $"UPDATE main.{Q(table.Name)} SET {set} WHERE {where}";
            for (int i = 0; i < columns.Length; i++) cmd.Parameters.AddWithValue("$v" + i, change.Expected[0][table.Index(columns[i].Name)]);
            for (int i = 0; i < keys.Length; i++) cmd.Parameters.AddWithValue("$k" + i, node.Current[0].Get(keys[i].Name));
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidDataException("Ambiguous restore row in " + table.Name);
            return;
        }
        if (node.Current.Count > 0)
        {
            var columns = keys.Length == 0 ? table.Columns : keys;
            string where = string.Join(" AND ", columns.Select((c, i) => Q(c.Name) + " IS $k" + i));
            using var cmd = db.CreateCommand(); cmd.CommandText = $"DELETE FROM main.{Q(table.Name)} WHERE {where}";
            for (int i = 0; i < columns.Length; i++) cmd.Parameters.AddWithValue("$k" + i, node.Current[0].Get(columns[i].Name));
            if (cmd.ExecuteNonQuery() != node.Current.Count) throw new InvalidDataException("Unexpected restore deletion in " + table.Name);
        }
        foreach (var row in change.Expected)
        {
            using var cmd = db.CreateCommand(); cmd.CommandText = $"INSERT INTO main.{Q(table.Name)} ({string.Join(',', table.Columns.Select(c => Q(c.Name)))}) VALUES ({string.Join(',', table.Columns.Select((c, i) => "$v" + i))})";
            for (int i = 0; i < row.Length; i++) cmd.Parameters.AddWithValue("$v" + i, row[i]);
            cmd.ExecuteNonQuery();
        }
    }

    static string SchemaFingerprint(SqliteConnection db)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT type,name,tbl_name,sql FROM main.sqlite_master ORDER BY type,name";
        var rows = new List<string>(); using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(ValueKey(Enumerable.Range(0, 4).Select(reader.GetValue)));
        return string.Join('\n', rows);
    }
    static Dictionary<string, int> ForeignKeys(SqliteConnection db)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA main.foreign_key_check";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string key = reader.GetString(0) + " -> " + reader.GetString(2) + " / " + reader.GetInt64(3);
            result[key] = result.GetValueOrDefault(key) + 1;
        }
        return result;
    }
    static void Execute(SqliteConnection db, string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
}
