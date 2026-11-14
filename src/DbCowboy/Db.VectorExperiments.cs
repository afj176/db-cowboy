using System.Data;
using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace DbCowboy;

public sealed record VectorSearchRun(List<VectorItem> Rows,double Milliseconds,string Plan,string[] Indexes,bool Approximate);
public sealed record VectorSearchQuality(VectorSearchRun Exact,VectorSearchRun Planned,int Shared,int Denominator)
{
    public double Recall => Denominator==0?0:(double)Shared/Denominator;
}
public sealed record VectorRankChange(string Label,int LeftRank,int RightRank);
public sealed record VectorPipelineReport(List<VectorItem> Left,List<VectorItem> Right,List<VectorRankChange> Shared,List<VectorItem> LeftOnly,List<VectorItem> RightOnly)
{
    public static VectorPipelineReport Create(VectorSource source,VectorItem anchor,IEnumerable<VectorItem> left,IEnumerable<VectorItem> right)
    {
        string Key(VectorItem row)=>VectorExperimentIdentity.Key(source,row,true)??throw new InvalidOperationException("A record identity is missing. This comparison cannot reliably match neighbors.");
        var seed=Key(anchor);var a=left.DistinctBy(Key).Where(r=>Key(r)!=seed).ToList();var b=right.DistinctBy(Key).Where(r=>Key(r)!=seed).ToList();
        var ranks=b.Select((row,index)=>(Key:Key(row),Rank:index+1)).ToDictionary(p=>p.Key,p=>p.Rank);
        var shared=a.Select((row,index)=>(Row:row,Rank:index+1)).Where(p=>ranks.ContainsKey(Key(p.Row))).Select(p=>new VectorRankChange(p.Row.Label,p.Rank,ranks[Key(p.Row)])).ToList();
        var aKeys=a.Select(Key).ToHashSet();return new(a,b,shared,a.Where(r=>!ranks.ContainsKey(Key(r))).ToList(),b.Where(r=>!aKeys.Contains(Key(r))).ToList());
    }
}

public static class VectorExperimentIdentity
{
    public static string[] RecordColumns(VectorSource source)=>source.Context?.SourceColumns??(source.Keys??[]).Where(k=>k!="model"&&k!="model_revision"&&k!="pipeline").ToArray();
    public static VectorBookmark? RecordKey(VectorSource source,VectorItem row)
    {
        var keys=RecordColumns(source);
        if(keys is not {Length:>0})return null;
        using var doc=JsonDocument.Parse(row.Metadata);var values=new Dictionary<string,string>();
        foreach(var key in keys){if(!doc.RootElement.TryGetProperty(key,out var value)||value.ValueKind==JsonValueKind.Null)return null;values[key]=value.GetRawText();}
        return new(values);
    }
    public static string? Key(VectorSource source,VectorItem row,bool record)
    {
        var bookmark=record?RecordKey(source,row):VectorInvestigationState.Bookmark(source,row);
        return bookmark==null?null:JsonSerializer.Serialize(bookmark.Keys.OrderBy(p=>p.Key).Select(p=>new[]{p.Key,p.Value}));
    }
}

public sealed partial class Db
{
    public async Task<List<VectorItem>> VectorCounterparts(VectorSource source,VectorScope scope,VectorItem anchor,CancellationToken ct)
    {
        var key=VectorExperimentIdentity.RecordKey(source,anchor)??throw new InvalidOperationException("This source needs a primary key or declared relationship to match records across pipelines.");
        return await VectorRows(source,scope,"",null,0,ct,key,true);
    }

    // Both measured plans and their result rows share one repeatable-read snapshot.
    // EXPLAIN ANALYZE runs only this generated SELECT; no user SQL is accepted.
    public async Task<VectorSearchQuality> VectorSearchQuality(VectorSource source,VectorScope scope,VectorItem anchor,int metric,CancellationToken ct)
    {
        if(source.Keys is not {Length:>0})throw new InvalidOperationException("Search quality needs a primary key so matches can be counted reliably.");
        if(anchor.Values.Length==0||anchor.Values.Any(v=>!double.IsFinite(v))||(metric==0&&Embeddings.Norm(anchor.Values)==0))throw new InvalidOperationException("Select a non-zero, finite embedding first.");
        var v=source.Vector;string column="t."+Q(v.Column);
        string distance=$"{column} OPERATOR({Q(v.TypeSchema)}.{(metric==1?"<->":metric==2?"<#>":"<=>")}) CAST(@embedding AS {v.CastType})";
        var args=new List<(string,object)>{("embedding",Embeddings.Serialize(anchor.Values)),("column",v.Column)};
        string filter="";
        void Scope(bool has,string name,string? value){if(has){filter+=$" AND t.{Q(name)}::text IS NOT DISTINCT FROM @{name}";args.Add((name,(object?)value??DBNull.Value));}}
        Scope(source.HasModel,"model",scope.Model);Scope(source.HasRevision,"model_revision",scope.Revision);Scope(source.HasPipeline,"pipeline",scope.Pipeline);
        string sql=$"SELECT {source.LabelSql} AS label,{source.MetadataSql}::text AS metadata,{column}::text AS embedding,{distance} AS distance FROM {v.Qualified} t{source.JoinSql} WHERE {column} IS NOT NULL{filter} ORDER BY {distance} LIMIT 25";
        await using var connection=await this.source.OpenConnectionAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        async Task Execute(string statement){await using var cmd=new NpgsqlCommand(statement,connection,transaction);await cmd.ExecuteNonQueryAsync(ct);}
        await Execute("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='60s'; SET LOCAL lock_timeout='5s'");
        var annIndexes=new HashSet<string>();
        await using(var cmd=new NpgsqlCommand("SELECT c.relname FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid JOIN pg_am a ON a.oid=c.relam WHERE i.indrelid=to_regclass(@relation) AND a.amname IN ('hnsw','ivfflat')",connection,transaction)){cmd.Parameters.AddWithValue("relation",v.Qualified);await using var reader=await cmd.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))annIndexes.Add(reader.GetString(0));}
        async Task<VectorSearchRun> Run(bool exact)
        {
            await Execute(exact?"SET LOCAL enable_indexscan=off; SET LOCAL enable_indexonlyscan=off; SET LOCAL enable_bitmapscan=off":"SET LOCAL enable_indexscan=DEFAULT; SET LOCAL enable_indexonlyscan=DEFAULT; SET LOCAL enable_bitmapscan=DEFAULT");
            NpgsqlCommand Command(string text){var cmd=new NpgsqlCommand(text,connection,transaction){AllResultTypesAreUnknown=true};foreach(var arg in args)cmd.Parameters.AddWithValue(arg.Item1,arg.Item2);return cmd;}
            string plan;await using(var cmd=Command("EXPLAIN (ANALYZE, FORMAT JSON, TIMING OFF) "+sql))plan=(string)(await cmd.ExecuteScalarAsync(ct))!;
            using var doc=JsonDocument.Parse(plan);var top=doc.RootElement[0];var indexes=new List<string>();
            void Visit(JsonElement node){if(node.TryGetProperty("Index Name",out var name))indexes.Add(name.GetString()!);if(node.TryGetProperty("Plans",out var children))foreach(var child in children.EnumerateArray())Visit(child);}
            Visit(top.GetProperty("Plan"));
            var rows=new List<VectorItem>();await using(var cmd=Command(sql))await using(var reader=await cmd.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))rows.Add(new(reader.IsDBNull(0)?"Unlabeled":reader.GetString(0),reader.GetString(1),Embeddings.Parse(reader.GetString(2)),reader.IsDBNull(3)?null:double.Parse(reader.GetString(3),CultureInfo.InvariantCulture)));
            return new(rows,top.GetProperty("Execution Time").GetDouble(),plan,indexes.Distinct().ToArray(),indexes.Any(annIndexes.Contains));
        }
        // Planned first avoids always giving it the exact scan's warmed cache.
        var planned=await Run(false);var exact=await Run(true);
        var exactKeys=exact.Rows.Select(r=>VectorExperimentIdentity.Key(source,r,false)).ToHashSet();
        int shared=planned.Rows.Count(r=>exactKeys.Contains(VectorExperimentIdentity.Key(source,r,false)));
        await transaction.CommitAsync(ct);return new(exact,planned,shared,exact.Rows.Count);
    }
}
