using System.Data;

namespace DbCowboy;

public sealed record VectorScopeHealth(VectorScope Scope,long Rows,long Embedded);
public sealed record VectorCoverage(string Relation,long Rows,long Covered);
public sealed record VectorHealthReport(long Rows,long Embedded,List<VectorScopeHealth> Spaces,List<VectorItem> Sample,VectorCoverage? Linked=null);

public sealed partial class Db
{
    public async Task<VectorHealthReport> VectorHealth(VectorSource source, VectorScope? scope, CancellationToken ct)
    {
        var v=source.Vector;string column=Q(v.Column);
        var totals=await Query($"SELECT count(*),count({column}) FROM {v.Qualified}",ct,true);
        string model=source.HasModel?"model::text":"NULL::text",revision=source.HasRevision?"model_revision::text":"NULL::text",pipeline=source.HasPipeline?"pipeline::text":"NULL::text";
        var groups=await Query($"SELECT {model},{revision},{pipeline},count(*),count({column}) FROM {v.Qualified} GROUP BY 1,2,3 ORDER BY 4 DESC LIMIT 100",ct,true);
        var spaces=groups.Table.Rows.Cast<DataRow>().Select(r=>new VectorScopeHealth(new(r[0]==DBNull.Value?null:(string)r[0],r[1]==DBNull.Value?null:(string)r[1],r[2]==DBNull.Value?null:(string)r[2]),long.Parse((string)r[3]),long.Parse((string)r[4]))).ToList();
        var sample=scope==null?new List<VectorItem>():await VectorRows(source,scope,"",null,0,ct);
        VectorCoverage? linked=null;
        if(source.Context is {} fk)
        {
            string filter="";var args=new List<(string Name,object Value)>();
            if(scope!=null)
            {
                if(source.HasModel){filter+=" AND t.model::text IS NOT DISTINCT FROM @model";args.Add(("model",(object?)scope.Model??DBNull.Value));}
                if(source.HasRevision){filter+=" AND t.model_revision::text IS NOT DISTINCT FROM @revision";args.Add(("revision",(object?)scope.Revision??DBNull.Value));}
                if(source.HasPipeline){filter+=" AND t.pipeline::text IS NOT DISTINCT FROM @pipeline";args.Add(("pipeline",(object?)scope.Pipeline??DBNull.Value));}
            }
            string relation=Q(fk.TargetSchema)+"."+Q(fk.TargetTable);
            string match=string.Join(" AND ",fk.SourceColumns.Zip(fk.TargetColumns).Select(k=>$"t.{Q(k.First)}=parent.{Q(k.Second)}"));
            var coverage=await Query($"SELECT count(*),count(*) FILTER(WHERE EXISTS(SELECT 1 FROM {v.Qualified} t WHERE {match} AND t.{column} IS NOT NULL{filter})) FROM {relation} parent",ct,true,args.ToArray());
            linked=new(fk.TargetSchema+"."+fk.TargetTable,long.Parse((string)coverage.Table.Rows[0][0]),long.Parse((string)coverage.Table.Rows[0][1]));
        }
        return new(long.Parse((string)totals.Table.Rows[0][0]),long.Parse((string)totals.Table.Rows[0][1]),spaces,sample,linked);
    }
}
