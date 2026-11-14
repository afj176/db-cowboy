using DbCowboy;
using System.Data;
using System.Text.Json;

int checks=0;void Check(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);checks++;Console.WriteLine("PASS: "+name);}
var (original,password)=CheckTarget.Resolve();
var saved=new Profile{Name="Local words",Host="localhost",Port=14321,Database="postgres",Username="postgres",Ssl="Disable"};
var favorite=saved with{Id=Guid.NewGuid(),Name="Production reference",Favorite=true,Production=true};var recent=saved with{Id=Guid.NewGuid(),Name="Recent",LastConnected=DateTimeOffset.UtcNow};
Check(ConnectionProfiles.Ordered(new[]{saved,recent,favorite}).Select(p=>p.Id).SequenceEqual(new[]{favorite.Id,recent.Id,saved.Id}),"favorites and recent connections sort predictably");
Check(ConnectionProfiles.Ordered(new[]{saved,favorite},"production").Single().Id==favorite.Id,"search finds production profiles");
Check(ConnectionProfiles.Ordered(new[]{saved},"POSTGRES").Count()==1&&ConnectionProfiles.Ordered(new[]{saved},"  localhost ").Count()==1,"search matches user database host case-insensitively");
Check(ConnectionProfiles.Ordered(new[]{saved},"absent").Count()==0,"search supports honest empty state");
var edited=saved with{Name="Renamed"};var list=ConnectionProfiles.Upsert(new[]{saved,recent},edited);
Check(list.Count==2&&list[0].Name=="Renamed"&&list[0].Id==saved.Id,"editing preserves profile identity and list position");
Check(ConnectionProfiles.Upsert(list,favorite).Count==3,"saving a new profile adds it once");
var copy=ConnectionProfiles.Duplicate(favorite);Check(copy.Id!=favorite.Id&&copy.Name.EndsWith(" copy")&&!copy.Favorite&&copy.LastConnected==null&&copy.Host==favorite.Host,"duplicate retains endpoint with independent identity and history");
var key=ConnectionProfiles.SessionKey(saved,"example");
Check(key==ConnectionProfiles.SessionKey(saved with{Name="New name",Avatar="Dot",Favorite=true,Production=true,LastConnected=DateTimeOffset.Now},"example"),"presentation edits do not require reconnect");
foreach(var changed in new[]{saved with{Host="127.0.0.1"},saved with{Port=9999},saved with{Database="another"},saved with{Username="other"},saved with{Ssl="Require"},saved with{RootCertificate="root.pem"},saved with{ReadOnly=false}})Check(key!=ConnectionProfiles.SessionKey(changed,"example"),"session key detects changed "+(changed.Host!=saved.Host?"host":changed.Port!=saved.Port?"port":changed.Database!=saved.Database?"database":changed.Username!=saved.Username?"user":changed.Ssl!=saved.Ssl?"TLS":changed.RootCertificate!=saved.RootCertificate?"certificate":"read-only default"));
Check(key!=ConnectionProfiles.SessionKey(saved,"different"),"password changes require reconnect");
Check(!JsonSerializer.Serialize(saved).Contains("Endpoint"),"derived endpoint display is not persisted");
var remembered=ConnectionProfiles.WithCredentials(saved,"example",true);
Check(Store.Unprotect(remembered.Secret)=="example"&&!JsonSerializer.Serialize(remembered).Contains("example"),"remembered passwords use Windows protection, not plain profile JSON");
Check(ConnectionProfiles.WithCredentials(remembered,"new",null).Secret==remembered.Secret,"ordinary reuse preserves saved ciphertext");
Check(ConnectionProfiles.WithCredentials(remembered,"session",false).Secret=="","explicit session-only password clears obsolete saved ciphertext");
Check(ConnectionProfiles.DefaultTls("localhost")=="Disable"&&ConnectionProfiles.DefaultTls("127.0.0.1")=="Disable"&&ConnectionProfiles.DefaultTls("::1")=="Disable","new local containers get a suitable TLS default");
Check(ConnectionProfiles.DefaultTls("db.example.com")=="VerifyFull","new remote hosts default to validated TLS");
Check(!JsonSerializer.Deserialize<Profile>("{\"Name\":\"Legacy\"}")!.Favorite,"legacy profiles load with compatible defaults");
var workspace=new WorkspaceState{LastSelectedConnection=recent.Id};workspace.Connections[saved.Id]=new([new(Guid.NewGuid(),"First","SELECT 1")],Guid.Empty,View:"Vector lab");workspace.Connections[recent.Id]=new([new(Guid.NewGuid(),"Second","SELECT 2")],Guid.Empty,View:"Query");
var restored=JsonSerializer.Deserialize<WorkspaceState>(JsonSerializer.Serialize(workspace))!;
Check(restored.LastSelectedConnection==recent.Id&&restored.Connections[saved.Id].View=="Vector lab"&&restored.Connections[recent.Id].Documents[0].Sql=="SELECT 2","selected profile and independent workspace views survive restart");
Check(JsonSerializer.Deserialize<ConnectionWorkspace>("{\"Documents\":[],\"ActiveDocument\":\"00000000-0000-0000-0000-000000000000\"}")!.View=="Query","legacy workspace defaults to query view");
await using var db=new Db(original,password);var ct=CancellationToken.None;
async Task<string> Snapshot(){var r=await db.Query("SELECT count(*)::text AS rows,md5(string_agg(md5(to_jsonb(t)::text),'' ORDER BY md5(to_jsonb(t)::text))) AS fingerprint FROM public.word_embeddings t; SELECT count(*)::text AS rows,md5(string_agg(md5(to_jsonb(t)::text),'' ORDER BY id)) AS fingerprint FROM public.words t",ct,true);return string.Join("|",r.Results.SelectMany(t=>t.Rows.Cast<DataRow>()).Select(r=>string.Join(",",r.ItemArray)));}
var before=await Snapshot();
var first=await PreparedConnection.Open(original,password,ct);await using var firstDb=first.Database;
Check(first.Relations.Count>0&&first.Vectors.Count>0&&first.Sources.Count>0,"connection preparation validates catalogs before session replacement");
var alias=original with{Id=Guid.NewGuid(),Name="Read-only alias",ReadOnly=true};var second=await PreparedConnection.Open(alias,password,ct);await using var secondDb=second.Database;
Check(first.Database.Profile.Id!=second.Database.Profile.Id&&second.Relations.Count==first.Relations.Count,"multiple saved profiles prepare independent sessions on the existing database");
bool failed=false;try{var invalid=await PreparedConnection.Open(original with{Port=1},password,ct);await invalid.Database.DisposeAsync();}catch{failed=true;}
Check(failed,"unreachable next connection fails before replacement");
Check((await first.Database.Query("SELECT current_database()",ct,true)).Table.Rows[0][0].ToString()==original.Database,"previous session remains usable after failed switch preparation");
using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();bool stopped=false;try{var invalid=await PreparedConnection.Open(original,password,cancelled.Token);await invalid.Database.DisposeAsync();}catch(OperationCanceledException){stopped=true;}Check(stopped,"cancelled connection preparation does not replace active session");}
Check(await Snapshot()==before,"words and word_embeddings full-row fingerprints unchanged during read-only connection validation");
Console.WriteLine($"FINAL {checks} checks passed. No DDL/DML was issued. Existing database was read only.");
