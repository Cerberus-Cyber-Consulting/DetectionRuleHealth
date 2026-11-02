using Kusto.Language;
using Kusto.Language.Symbols;
using RuleHealth.Core.Model;

namespace RuleHealth.Core.Extraction;

/// <summary>
/// Turns a table schema into the parser's GlobalState. Binding against the right schema is what
/// makes lineage work: without it the parser cannot tell a table column from a computed one.
/// </summary>
public static class SchemaBuilder
{
    // FileProfile/DeviceFromIP are Defender "magic" functions. These stubs return the input rows plus
    // the documented output columns, so queries using them bind. The output columns have no table
    // lineage, which is correct: their values come from Microsoft's file/device data, not a table.
    private const string FileProfileBody = """
        {
            (T | extend _TmpJoinKey=123)
            | join (
                print SHA1='', SHA256='', MD5='', FileSize=0, GlobalPrevalence=0, GlobalFirstSeen=now(),
                GlobalLastSeen=now(), Signer='', Issuer='', SignerHash='', IsCertificateValid=false,
                IsRootSignerMicrosoft=false, SignatureState='', IsExecutable=false, ThreatName='',
                Publisher='', SoftwareName='', ProfileAvailability='',_TmpJoinKey=123
            ) on _TmpJoinKey | project-away _TmpJoinKey, _TmpJoinKey1
        }
        """;

    private const string DeviceFromIpBody = """
        {
            (T | extend _TmpJoinKey=123)
            | join (print IP='', DeviceId='', _TmpJoinKey=123) on _TmpJoinKey | project-away _TmpJoinKey, _TmpJoinKey1
        }
        """;

    // Watchlists are tenant data; the stub keeps queries bindable and its columns stay "computed".
    private const string WatchlistBody = "{ print SearchKey='', _DTItemId='', LastUpdatedTimeUTC=now() }";

    public static GlobalState Build(SchemaDocument schema)
    {
        var members = new List<Symbol>();
        foreach (var (table, columns) in schema.Tables)
        {
            var columnSymbols = columns.Select(c => new ColumnSymbol(c.Key, ToScalarType(c.Value)));
            members.Add(new TableSymbol(table, columnSymbols));
        }

        // schema.json from a live tenant has no function list; the unified context always has both.
        var functions = schema.Functions ?? SchemaFileReader.UnifiedFunctions;
        if (functions.Contains("FileProfile"))
            members.Add(new FunctionSymbol("FileProfile", "(T:(*), x:string='',y:string='')", FileProfileBody));
        if (functions.Contains("DeviceFromIP"))
            members.Add(new FunctionSymbol("DeviceFromIP", "(T:(*), x:string='',y:datetime='')", DeviceFromIpBody));
        members.Add(new FunctionSymbol("_GetWatchlist", "(alias:string)", WatchlistBody));

        return GlobalState.Default.WithDatabase(new DatabaseSymbol("db", members));
    }

    /// <summary>Kusto type name → parser type. Unknown names (and odd spellings) become dynamic, like the spike.</summary>
    private static TypeSymbol ToScalarType(string type) =>
        (TypeSymbol?)ScalarTypes.GetSymbol(type) ?? ScalarTypes.Dynamic;
}
