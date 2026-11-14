using System.Data;

namespace DbCowboy;

public sealed record VectorScope(string? Model, string? Revision, string? Pipeline = null)
{
    public override string ToString()
    {
        static string Short(string value)=>value.Length>12?value[..12]+"…":value;
        return (Model?.Split('/')[^1]??"All vectors in this column")+(Revision==null?"":" · rev "+Short(Revision))+(Pipeline==null?"":" · pipeline "+Short(Pipeline));
    }
}
public sealed record VectorLabelOption(string Key,string Title);
public sealed record VectorSource(VectorColumn Vector, string LabelSql, string JoinSql, string MetadataSql, string OrderSql, bool HasModel, bool HasRevision, bool HasPipeline, string Description, string[]? Keys = null, VectorLabelOption[]? Labels = null, string? LabelKey = null, string? DeclaredType = null, ForeignKeyInfo? Context = null);
public sealed record VectorItem(string Label, string Metadata, double[] Values, double? Distance = null);

public sealed partial class Db
{
    public async Task<VectorSource> VectorSource(VectorColumn vector, CancellationToken ct, string? labelChoice = null)
    {
        var columns = await Columns(new(vector.Schema, vector.Table, "r", 0), ct);
        string? label = new[] { "word", "title", "name", "label", "sense_text", "content", "text", "caption", "description", "body" }.FirstOrDefault(n => columns.Any(c => c.Name == n))
            ?? columns.FirstOrDefault(c=>(c.Type=="text"||c.Type.StartsWith("character"))&&!new[]{"model","model_revision","pipeline","input_sha256","sense_sha256"}.Contains(c.Name))?.Name
            ?? columns.FirstOrDefault(c=>c.Name=="id")?.Name;
        string labelSql = label == null ? "'Embedding'" : $"left(t.{Q(label)}::text,160)";
        string join = "", description = "Labels from " + (label ?? "row");
        ForeignKeyInfo? context=null;
        var choices=columns.Where(c=>!c.Type.StartsWith("vector")&&!c.Type.StartsWith("halfvec")&&!c.Type.Contains("json")).Select(c=>new VectorLabelOption("row:"+c.Name,vector.Table+"."+c.Name)).ToList();
        string? chosen=label==null?null:"row:"+label;
        static string Value(string alias, string name) => $"CASE WHEN octet_length(to_jsonb({alias}.{Q(name)})::text)>8192 THEN to_jsonb(left({alias}.{Q(name)}::text,1500)) ELSE to_jsonb({alias}.{Q(name)}) END";
        var metadata = columns.Where(c => c.Name != vector.Column && !c.Type.StartsWith("vector") && !c.Type.StartsWith("halfvec")).Take(20).Select(c => $"'{c.Name.Replace("'", "''")}',{Value("t", c.Name)}").ToList();
        foreach (var fk in (await ForeignKeys(ct)).Where(f => f.SourceSchema == vector.Schema && f.SourceTable == vector.Table))
        {
            var target = await Columns(new(fk.TargetSchema, fk.TargetTable, "r", 0), ct);
            string? name = new[] { "word", "title", "name", "label" }.FirstOrDefault(n => target.Any(c => c.Name == n));
            if (name == null) continue;
            context=fk;
            choices.AddRange(target.Where(c=>!c.Type.Contains("json")&&!c.Type.StartsWith("vector")&&!c.Type.StartsWith("halfvec")).Select(c=>new VectorLabelOption("linked:"+c.Name,fk.TargetTable+"."+c.Name)));
            join = $" LEFT JOIN {Q(fk.TargetSchema)}.{Q(fk.TargetTable)} linked ON " + string.Join(" AND ", fk.SourceColumns.Zip(fk.TargetColumns).Select(p => $"t.{Q(p.First)}=linked.{Q(p.Second)}"));
            labelSql = $"COALESCE(left(linked.{Q(name)}::text,160),{labelSql})";
            foreach (var c in target.Where(c => new[] { "word", "title", "name", "definition", "parts_of_speech", "topics", "tags" }.Contains(c.Name))) metadata.Add($"'linked.{c.Name}',{Value("linked", c.Name)}");
            description = $"Labels via declared FK → {fk.TargetSchema}.{fk.TargetTable}.{name}"; break;
        }
        if(join.Length>0)chosen="linked:"+new[]{"word","title","name","label"}.First(n=>choices.Any(c=>c.Key=="linked:"+n));
        if(labelChoice!=null&&choices.Any(c=>c.Key==labelChoice))
        {
            chosen=labelChoice;string name=labelChoice[(labelChoice.IndexOf(':')+1)..];
            labelSql=$"COALESCE(left({(labelChoice.StartsWith("linked:")?"linked":"t")}.{Q(name)}::text,160),'Unlabeled')";
            description="Labels from "+choices.Single(c=>c.Key==labelChoice).Title;
        }
        var pk = await Query("SELECT a.attname FROM pg_index i JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=ANY(i.indkey) WHERE i.indrelid=to_regclass(@r) AND i.indisprimary ORDER BY a.attnum", ct, true, ("r", vector.Qualified));
        foreach(string key in pk.Table.Rows.Cast<DataRow>().Select(r=>(string)r[0]).Where(k=>!columns.Take(20).Any(c=>c.Name==k)))metadata.Add($"'{key.Replace("'","''")}',to_jsonb(t.{Q(key)} )");
        foreach(string key in (context?.SourceColumns??[]).Where(k=>!columns.Take(20).Any(c=>c.Name==k)&&!pk.Table.Rows.Cast<DataRow>().Any(r=>(string)r[0]==k)))metadata.Add($"'{key.Replace("'","''")}',to_jsonb(t.{Q(key)})");
        string order = pk.Table.Rows.Count > 0 ? string.Join(",", pk.Table.Rows.Cast<DataRow>().Select(r => "t." + Q((string)r[0]))) : "md5((to_jsonb(t)-@column)::text)";
        return new(vector, labelSql, join, metadata.Count == 0 ? "'{}'::jsonb" : "jsonb_build_object(" + string.Join(",", metadata) + ")", order, columns.Any(c => c.Name == "model"), columns.Any(c => c.Name == "model_revision"), columns.Any(c => c.Name == "pipeline"), description,pk.Table.Rows.Cast<DataRow>().Select(r=>(string)r[0]).ToArray(),choices.ToArray(),chosen,columns.FirstOrDefault(c=>c.Name==vector.Column)?.Type,context);
    }
    public async Task<List<VectorScope>> VectorScopes(VectorSource source, CancellationToken ct)
    {
        if (!source.HasModel && !source.HasRevision && !source.HasPipeline) return [new(null, null)];
        string model = source.HasModel ? "model::text" : "NULL::text";
        string revision = source.HasRevision ? "model_revision::text" : "NULL::text";
        string pipeline = source.HasPipeline ? "pipeline::text" : "NULL::text";
        var rows = await Query($"SELECT {model} AS model,{revision} AS revision,{pipeline} AS pipeline FROM {source.Vector.Qualified} WHERE {Q(source.Vector.Column)} IS NOT NULL GROUP BY 1,2,3 ORDER BY count(*) DESC,1,2,3 LIMIT 100", ct, true);
        return rows.Table.Rows.Cast<DataRow>().Select(r => new VectorScope(r[0] == DBNull.Value ? null : (string)r[0], r[1] == DBNull.Value ? null : (string)r[1], r[2] == DBNull.Value ? null : (string)r[2])).ToList();
    }
    public async Task<List<VectorItem>> VectorRows(VectorSource source, VectorScope scope, string search, double[]? seed, int metric, CancellationToken ct, VectorBookmark? bookmark = null, bool relatedKey = false)
    {
        if(seed!=null&&(seed.Length==0||seed.Any(v=>!double.IsFinite(v))||(metric==0&&Embeddings.Norm(seed)==0)))throw new InvalidOperationException("Cosine search needs a non-zero vector; all search vectors must have finite components. Select another embedding or use L2 for a zero vector.");
        var v = source.Vector; string column = "t." + Q(v.Column);
        string filter = source.HasModel ? " AND t.model::text IS NOT DISTINCT FROM @model" : "";
        if (source.HasRevision) filter += " AND t.model_revision::text IS NOT DISTINCT FROM @revision";
        if (source.HasPipeline) filter += " AND t.pipeline::text IS NOT DISTINCT FROM @pipeline";
        string distance = seed == null ? "NULL::double precision" : $"{column} OPERATOR({Q(v.TypeSchema)}.{(metric == 1 ? "<->" : metric == 2 ? "<#>" : "<=>")}) CAST(@embedding AS {v.CastType})";
        string order = seed == null ? source.OrderSql : distance;
        var args = new List<(string Name, object Value)> { ("column", v.Column), ("search", search), ("limit", seed == null ? 500 : 25) };
        if (source.HasModel) args.Add(("model", (object?)scope.Model ?? DBNull.Value));
        if (source.HasRevision) args.Add(("revision", (object?)scope.Revision ?? DBNull.Value));
        if (source.HasPipeline) args.Add(("pipeline", (object?)scope.Pipeline ?? DBNull.Value));
        if (seed != null) args.Add(("embedding", Embeddings.Serialize(seed)));
        if(bookmark!=null)
        {
            var keys=relatedKey?VectorExperimentIdentity.RecordColumns(source):source.Keys;
            if(keys is not {Length:>0} || !keys.Order().SequenceEqual(bookmark.Keys.Keys.Order()))throw new InvalidOperationException("The saved row key no longer matches this table. Explore a fresh sample.");
            int i=0;foreach(var key in keys){string parameter="key"+i++;filter+=$" AND to_jsonb(t.{Q(key)}) IS NOT DISTINCT FROM CAST(@{parameter} AS jsonb)";args.Add((parameter,bookmark.Keys[key]));}
        }
        // Every entry point forces a server READ ONLY transaction, independent of
        // the connection's ordinary SQL editor preference. No generated DDL/DML.
        // Filtered ANN scans can exhaust their candidate list before LIMIT 25.
        // Exact retrieval is the lab default: disable index scans only within
        // this read-only transaction. No index/schema/global setting is changed.
        string exact = seed == null ? "" : "SET LOCAL enable_indexscan = off; ";
        var result = await Query(exact + $"SELECT {source.LabelSql} AS label,{source.MetadataSql}::text AS metadata,{column}::text AS embedding,{distance} AS distance FROM {v.Qualified} t{source.JoinSql} WHERE {column} IS NOT NULL{filter} AND (@search='' OR strpos(lower({source.LabelSql}),lower(@search))>0) ORDER BY {order} LIMIT @limit", ct, true, args.ToArray());
        return result.Table.Rows.Cast<DataRow>().Select(r => new VectorItem(r[0] == DBNull.Value ? "Unlabeled" : (string)r[0], (string)r[1], Embeddings.Parse((string)r[2]), r[3] == DBNull.Value ? null : double.Parse((string)r[3], System.Globalization.CultureInfo.InvariantCulture))).ToList();
    }
}
