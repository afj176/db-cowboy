using DbCowboy;
using System.Text.Json;
using System.Data;
int checks=0; void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);checks++;Console.WriteLine("PASS: "+name);}
static string? Pipeline(VectorItem row){using var doc=JsonDocument.Parse(row.Metadata);return doc.RootElement.GetProperty("pipeline").GetString();}
var vectors=new[]{new[]{1d,0,0},new[]{.8,.2,0},new[]{-1d,0,0},new[]{0d,0,0}};
var near=VectorLens.Neighbors(vectors,vectors[0]);Check(near.Select(x=>x.Index).SequenceEqual(new[]{0,1,2}),"cosine ranking excludes zero vectors and orders opposing direction last");
Check(VectorLens.Fingerprint(new[]{1d,3d,-2d,-4d},2).SequenceEqual(new[]{2d,-3d}),"fingerprint preserves signed contiguous means");
Check(VectorLens.Fingerprint(new[]{2d,-2d},64).SequenceEqual(new[]{2d,-2d}),"short vectors do not create empty bands");
Check(VectorLens.Neighbors(new[]{new[]{0d,0d}},new[]{1d,0d}).Count==0,"all-zero neighborhood safely empty");
Check(Math.Abs(Embeddings.Cosine(vectors[0],vectors[2])+1)<1e-12,"opposite vectors retain negative cosine");
var (profile,password)=CheckTarget.Resolve();
await using var db=new Db(profile,password);var ct=CancellationToken.None;
async Task<string> Snapshot(){var r=await db.Query("SELECT count(*)::text AS rows,md5(string_agg(md5(to_jsonb(t)::text),'' ORDER BY md5(to_jsonb(t)::text))) AS fingerprint FROM public.word_embeddings t; SELECT count(*)::text AS rows,md5(string_agg(md5(to_jsonb(t)::text),'' ORDER BY id)) AS fingerprint FROM public.words t",ct,true);return string.Join("|",r.Results.SelectMany(t=>t.Rows.Cast<DataRow>()).Select(r=>string.Join(",",r.ItemArray)));}
var before=await Snapshot();
var vector=(await db.Vectors(ct)).Single(v=>v.Schema=="public"&&v.Table=="word_embeddings"&&v.Column=="embedding");
var source=await db.VectorSource(vector,ct);Check(source.JoinSql.Contains("\"words\"")&&source.Description.Contains("word"),"declared FK resolves human word labels");
var scopes=await db.VectorScopes(source,ct);Check(scopes.Count>0&&scopes[0].Model!=null,"model/revision discovery");
var sample=await db.VectorRows(source,scopes[0],"",null,0,ct);Check(sample.Count==500&&sample.All(r=>r.Values.Length==384),"bounded 500-row 384D real sample");
Check(sample.All(r=>r.Label.Length>0)&&sample.Any(r=>r.Metadata.Contains("linked.definition")),"labels and linked word context retained");
using(var metadata=JsonDocument.Parse(sample[0].Metadata)){Check(metadata.RootElement.GetProperty("linked.tags").ValueKind==JsonValueKind.Array,"linked arrays preserve JSON types");Check(metadata.RootElement.GetProperty("truncated").ValueKind is JsonValueKind.True or JsonValueKind.False,"boolean metadata preserves JSON types");}
Check(sample.All(r=>Pipeline(r)==scopes[0].Pipeline),"sampling isolates pipeline within model/revision");
var repeat=await db.VectorRows(source,scopes[0],"",null,0,ct);Check(sample.Select(r=>r.Metadata).SequenceEqual(repeat.Select(r=>r.Metadata)),"stable primary-key sample");
var found=await db.VectorRows(source,scopes[0],sample[0].Label,null,0,ct);Check(found.Count>0&&found.All(r=>r.Label.Contains(sample[0].Label,StringComparison.OrdinalIgnoreCase)),"server label search");
var escaped=await db.VectorRows(source,scopes[0],"' ; DROP TABLE words; --",null,0,ct);Check(escaped.Count==0,"label search is parameterized text, never SQL");
foreach(int metric in new[]{0,1,2}){var neighbors=await db.VectorRows(source,scopes[0],"",sample[0].Values,metric,ct);Check(neighbors.All(r=>Pipeline(r)==scopes[0].Pipeline),"neighbor pipeline scope metric "+metric);Check(neighbors.Count==25&&neighbors.All(r=>r.Distance.HasValue),"read-only server neighbors metric "+metric);Check(neighbors.Zip(neighbors.Skip(1)).All(p=>p.First.Distance<=p.Second.Distance),"ascending server distance metric "+metric);}
var projection=Embeddings.Pca(sample.Select(r=>r.Values).ToArray());Check(projection.Points.Length==500&&projection.Dimensions==384&&double.IsFinite(projection.Explained),"real-data PCA finite and bounded");
var bookmark=VectorInvestigationState.Bookmark(source,sample[0]);Check(bookmark!=null&&bookmark.Keys.Count==source.Keys!.Length,"bookmark captures complete catalog primary key");
Check(!VectorInvestigationState.Compatible(source,new(new Dictionary<string,string>{{"removed_key","1"}})),"schema-changed bookmark is safely ignored");Check(!VectorInvestigationState.Compatible(source,new(source.Keys!.ToDictionary(k=>k,k=>"not JSON"))),"malformed bookmark values are safely ignored");
var restored=await db.VectorRows(source,scopes[0],"",null,0,ct,bookmark);Check(restored.Count==1&&VectorInvestigationState.Matches(source,bookmark,restored[0]),"primary-key bookmark restores exactly one row in its model scope");
var wrongScope=scopes.FirstOrDefault(x=>x!=scopes[0]);if(wrongScope!=null)Check((await db.VectorRows(source,wrongScope,"",null,0,ct,bookmark)).Count==0,"bookmark cannot escape embedding-space scope");
var health=await db.VectorHealth(source,scopes[0],ct);Check(health.Rows>=health.Embedded&&health.Embedded>=500,"whole-source embedding coverage counts");Check(health.Sample.Count==500&&health.Sample.All(r=>Pipeline(r)==scopes[0].Pipeline),"health diagnostics use bounded selected-space sample");Check(health.Spaces.Count>0&&health.Spaces.Count<100&&health.Spaces.Sum(x=>x.Rows)==health.Rows,"space distribution counts reconcile with source total");
Check(health.Spaces.Single(x=>x.Scope==scopes[0]).Embedded==health.Spaces.Max(x=>x.Embedded),"automatic default chooses most populated non-NULL embedding space");
Check(health.Linked is {Rows:>0}&&health.Linked.Covered<=health.Linked.Rows&&health.Linked.Relation=="public.words","declared-FK coverage counts linked words once per selected space");
var labelSource=await db.VectorSource(vector,ct,"row:id");var labels=await db.VectorRows(labelSource,scopes[0],"",null,0,ct,bookmark);Check(labels.Count==1&&Guid.TryParse(labels[0].Label,out _),"catalog-validated label override works without arbitrary SQL");
var view=new VectorInvestigation(vector.Schema,vector.Table,vector.Column,scopes[0],"hedgehog",2,0,"Single color",source.LabelKey,bookmark,bookmark,bookmark,"My view");var state=new WorkspaceState();state.VectorLast[profile.Id]=view;state.VectorSaved[profile.Id]=[view];var serialized=JsonSerializer.Serialize(state);var restoredState=JsonSerializer.Deserialize<WorkspaceState>(serialized)!;
Check(restoredState.VectorSaved[profile.Id][0].Scope==scopes[0]&&restoredState.VectorLast[profile.Id].Mode==2,"investigation source/scope/view survives JSON round trip");Check(!serialized.Contains("Values")&&!serialized.Contains("[0.1,0.2"),"investigation state stores keys, not embedding components");Check(JsonSerializer.Deserialize<WorkspaceState>("{\"Connections\":{}}")!.VectorLast.Count==0,"old workspace files remain compatible");
VectorItem Example(int id,int sense,string label)=>new(label,JsonSerializer.Serialize(new{id,sense_index=sense}),[1,0],0);
var fakeSource=source with{Context=new("test","public","test",["id"],"public","records",["id"]),Keys=["id","sense_index"]};
var anchorExample=Example(1,0,"seed");var aExample=Example(2,0,"same label");var bExample=Example(3,0,"same label");var cExample=Example(4,0,"other");
var comparison=VectorPipelineReport.Create(fakeSource,anchorExample,[anchorExample,aExample,Example(2,1,"same label"),bExample],[anchorExample,cExample,aExample]);
Check(comparison.Left.Count==2&&comparison.Right.Count==2,"pipeline comparison deduplicates senses and excludes anchor record");
Check(comparison.Shared.Count==1&&comparison.Shared[0].LeftRank==1&&comparison.Shared[0].RightRank==2,"pipeline shared ranks match record keys, not duplicate labels");
Check(comparison.LeftOnly.Count==1&&comparison.RightOnly.Count==1,"pipeline comparison separates unique neighbors");
Check(VectorPipelineReport.Create(fakeSource,anchorExample,[],[]).Shared.Count==0,"empty neighbor comparison is safe");
var scopeKeySource=fakeSource with{Context=null,Keys=["id","pipeline"]};
Check(VectorExperimentIdentity.RecordColumns(scopeKeySource).SequenceEqual(new[]{"id"}),"cross-pipeline primary-key matching excludes scope discriminator");
var counterparts=await db.VectorCounterparts(source,scopes[0],sample[0],ct);
Check(counterparts.Count>0&&counterparts.All(r=>VectorExperimentIdentity.Key(source,r,true)==VectorExperimentIdentity.Key(source,sample[0],true)),"pipeline counterpart discovery uses declared record identity, not label text");
Check(counterparts.All(r=>Pipeline(r)==scopes[0].Pipeline),"counterpart discovery stays inside selected pipeline");
if(wrongScope!=null){var alternate=await db.VectorCounterparts(source,wrongScope,sample[0],ct);Check(alternate.All(r=>Pipeline(r)==wrongScope.Pipeline&&VectorExperimentIdentity.Key(source,r,true)==VectorExperimentIdentity.Key(source,sample[0],true)),"other pipeline returns matching record or honest empty result");}
var quality=await db.VectorSearchQuality(source,scopes[0],sample[0],0,ct);
bool zeroRejected=false;try{await db.VectorRows(source,scopes[0],"",new double[384],0,ct);}catch(InvalidOperationException){zeroRejected=true;}Check(zeroRejected,"undefined zero-vector cosine search is rejected before querying");
Check(quality.Exact.Rows.Count==25&&quality.Denominator==25,"quality check gets exact top-25 baseline");
Check(!quality.Exact.Approximate,"exact quality baseline does not use ANN indexes");
Check(quality.Exact.Rows.All(r=>Pipeline(r)==scopes[0].Pipeline)&&quality.Planned.Rows.All(r=>Pipeline(r)==scopes[0].Pipeline),"both quality searches enforce identical pipeline scope");
Check(quality.Shared>=0&&quality.Shared<=quality.Denominator&&quality.Recall>=0&&quality.Recall<=1,"quality overlap counts primary-key rows within exact baseline");
Check(quality.Exact.Milliseconds>=0&&quality.Planned.Milliseconds>=0&&quality.Exact.Plan.Contains("Execution Time")&&quality.Planned.Plan.Contains("Execution Time"),"quality timings come from executed server plans");
Console.WriteLine($"QUALITY planned ANN={quality.Planned.Approximate}, rows={quality.Planned.Rows.Count}, shared={quality.Shared}/{quality.Denominator}; exact={quality.Exact.Milliseconds:0.00}ms, planned={quality.Planned.Milliseconds:0.00}ms");
Check(await Snapshot()==before,"words and word_embeddings full-row fingerprints unchanged during read-only validation");
Console.WriteLine($"FINAL {checks} checks passed. No DDL/DML was issued. Existing database was read only.");
